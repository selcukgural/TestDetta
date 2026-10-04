using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TestDetta.Eval;

/// <summary>
/// A hash of the code that decides a coverage recording, so cached maps survive TestDetta changes that
/// recording never runs (docs/design/eval-result-cache.md, part 2). The root assemblies (coverage and
/// tracker) count in full. Of the shared assembly (core), only the types the roots can reach count:
/// every reference from signatures, base types, attributes, fields and method bodies is followed,
/// type by type, and an interface or abstract type in the set brings every type that implements or
/// derives from it. Reached types are hashed by their members and IL, with operands written as names
/// and offsets rather than metadata tokens, which shift when unrelated code is added.
/// </summary>
internal static class RecordingFingerprint
{
    /// <summary>
    /// Hashes <paramref name="roots"/> in full and the part of <paramref name="shared"/> they, or the
    /// <paramref name="sharedRoots"/>, can reach.
    /// </summary>
    /// <param name="roots">Assemblies whose every type counts, such as the coverage recorder.</param>
    /// <param name="shared">Assembly whose reached types count, such as TestDetta.Core.</param>
    /// <param name="sharedRoots">Full names of types in <paramref name="shared"/> the caller uses directly.</param>
    public static string Compute(IReadOnlyList<string> roots, string shared, IReadOnlyList<string> sharedRoots)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var root in roots)
        {
            hash.AppendData(File.ReadAllBytes(root));
        }

        using var module = ModuleDefinition.ReadModule(shared);
        var reached = Reach(roots, module, sharedRoots);
        var text = new StringBuilder();
        foreach (var type in reached.OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            Describe(type, text);
        }

        hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Full names of the outermost shared types that count, for tests and diagnostics.</summary>
    /// <param name="roots">Assemblies whose every type counts.</param>
    /// <param name="shared">Assembly whose reached types count.</param>
    /// <param name="sharedRoots">Full names of types in <paramref name="shared"/> the caller uses directly.</param>
    internal static IReadOnlySet<string> ReachedTypes(IReadOnlyList<string> roots, string shared, IReadOnlyList<string> sharedRoots)
    {
        using var module = ModuleDefinition.ReadModule(shared);
        return Reach(roots, module, sharedRoots).Select(static t => t.FullName).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The outermost types of <paramref name="module"/> the roots can reach.</summary>
    /// <param name="roots">Assemblies to start from.</param>
    /// <param name="module">The shared assembly's module.</param>
    /// <param name="sharedRoots">Types of the module used directly.</param>
    private static HashSet<TypeDefinition> Reach(IReadOnlyList<string> roots, ModuleDefinition module, IReadOnlyList<string> sharedRoots)
    {
        var byName = AllTypes(module.Types).ToDictionary(static t => t.FullName, StringComparer.Ordinal);
        var reached = new HashSet<TypeDefinition>();
        var pending = new Queue<TypeDefinition>();
        void Add(TypeReference reference)
        {
            var element = reference.GetElementType();
            if (byName.TryGetValue(element.FullName, out var definition) && !IsElsewhere(reference, module))
            {
                var outer = Outermost(definition);
                if (reached.Add(outer))
                {
                    pending.Enqueue(outer);
                }
            }
        }

        foreach (var name in sharedRoots)
        {
            if (byName.TryGetValue(name, out var definition))
            {
                var outer = Outermost(definition);
                if (reached.Add(outer))
                {
                    pending.Enqueue(outer);
                }
            }
        }

        foreach (var root in roots)
        {
            using var rootModule = ModuleDefinition.ReadModule(root);
            foreach (var type in AllTypes(rootModule.Types))
            {
                References(type, Add);
            }
        }

        while (true)
        {
            while (pending.TryDequeue(out var type))
            {
                foreach (var nested in AllTypes([type]))
                {
                    References(nested, Add);
                }
            }

            // Any implementation of a reached interface or abstract type may be the one handed in.
            var open = reached.SelectMany(static t => AllTypes([t])).Where(static t => t.IsInterface || t.IsAbstract)
                .Select(static t => t.FullName).ToHashSet(StringComparer.Ordinal);
            var implementations = byName.Values.Where(t => !reached.Contains(Outermost(t))
                && (t.BaseType is { } baseType && open.Contains(baseType.GetElementType().FullName)
                    || t.Interfaces.Any(i => open.Contains(i.InterfaceType.GetElementType().FullName)))).ToList();
            if (implementations.Count == 0)
            {
                return reached;
            }

            foreach (var implementation in implementations)
            {
                Add(implementation);
            }
        }
    }

    /// <summary>True when <paramref name="reference"/> names a type of another assembly that happens to share a full name.</summary>
    /// <param name="reference">Referenced type.</param>
    /// <param name="module">The shared assembly's module.</param>
    private static bool IsElsewhere(TypeReference reference, ModuleDefinition module) =>
        reference.GetElementType().Scope is AssemblyNameReference name && name.Name != module.Assembly.Name.Name;

    /// <summary>Every type in <paramref name="types"/> and, recursively, their nested types.</summary>
    /// <param name="types">Top-level or outer types.</param>
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The type itself, or the type it is nested in, all the way out.</summary>
    /// <param name="type">A type.</param>
    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    /// <summary>Calls <paramref name="add"/> with every type <paramref name="type"/> mentions.</summary>
    /// <param name="type">Type to scan; its nested types are scanned separately.</param>
    /// <param name="add">Receives each referenced type.</param>
    private static void References(TypeDefinition type, Action<TypeReference> add)
    {
        void Type(TypeReference? reference)
        {
            switch (reference)
            {
                case null or GenericParameter:
                    return;
                case GenericInstanceType generic:
                    Type(generic.ElementType);
                    foreach (var argument in generic.GenericArguments)
                    {
                        Type(argument);
                    }

                    return;
                case TypeSpecification specification:
                    Type(specification.ElementType);
                    return;
                default:
                    add(reference);
                    return;
            }
        }

        void Attributes(ICustomAttributeProvider provider)
        {
            foreach (var attribute in provider.CustomAttributes)
            {
                Type(attribute.AttributeType);
            }
        }

        void Method(MethodReference method)
        {
            Type(method.DeclaringType);
            Type(method.ReturnType);
            foreach (var parameter in method.Parameters)
            {
                Type(parameter.ParameterType);
            }

            if (method is GenericInstanceMethod generic)
            {
                foreach (var argument in generic.GenericArguments)
                {
                    Type(argument);
                }
            }
        }

        Type(type.BaseType);
        Attributes(type);
        foreach (var implemented in type.Interfaces)
        {
            Type(implemented.InterfaceType);
        }

        foreach (var field in type.Fields)
        {
            Type(field.FieldType);
            Attributes(field);
        }

        foreach (var property in type.Properties)
        {
            Type(property.PropertyType);
        }

        foreach (var method in type.Methods)
        {
            Method(method);
            Attributes(method);
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var variable in method.Body.Variables)
            {
                Type(variable.VariableType);
            }

            foreach (var handler in method.Body.ExceptionHandlers)
            {
                Type(handler.CatchType);
            }

            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case TypeReference referenced:
                        Type(referenced);
                        break;
                    case MethodReference called:
                        Method(called);
                        break;
                    case FieldReference field:
                        Type(field.DeclaringType);
                        Type(field.FieldType);
                        break;
                }
            }
        }
    }

    /// <summary>Writes a stable description of <paramref name="type"/> and its nested types: members and IL.</summary>
    /// <param name="type">Reached type.</param>
    /// <param name="text">Receives the description.</param>
    private static void Describe(TypeDefinition type, StringBuilder text)
    {
        text.Append("type ").Append(type.FullName).Append(' ').Append((int)type.Attributes).Append(' ').AppendLine(type.BaseType?.FullName);
        foreach (var implemented in type.Interfaces)
        {
            text.Append(" : ").AppendLine(implemented.InterfaceType.FullName);
        }

        DescribeAttributes(type, text);
        foreach (var field in type.Fields)
        {
            text.Append("field ").Append(field.FullName).Append(' ').Append((int)field.Attributes).Append(' ')
                .Append(Convert.ToString(field.Constant, CultureInfo.InvariantCulture)).Append(' ')
                .AppendLine(Convert.ToHexString(field.InitialValue ?? []));
            DescribeAttributes(field, text);
        }

        foreach (var property in type.Properties)
        {
            text.Append("property ").AppendLine(property.FullName);
        }

        foreach (var method in type.Methods)
        {
            text.Append("method ").Append(method.FullName).Append(' ').AppendLine(((int)method.Attributes).ToString(CultureInfo.InvariantCulture));
            DescribeAttributes(method, text);
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var variable in method.Body.Variables)
            {
                text.Append(" local ").AppendLine(variable.VariableType.FullName);
            }

            foreach (var handler in method.Body.ExceptionHandlers)
            {
                text.Append(" handler ").Append(handler.HandlerType).Append(' ').Append(handler.TryStart.Offset).Append(' ')
                    .Append(handler.HandlerStart.Offset).Append(' ').AppendLine(handler.CatchType?.FullName);
            }

            foreach (var instruction in method.Body.Instructions)
            {
                text.Append(' ').Append(instruction.Offset).Append(' ').Append(instruction.OpCode.Name).Append(' ').AppendLine(Operand(instruction.Operand));
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            Describe(nested, text);
        }
    }

    /// <summary>Writes the attributes on <paramref name="provider"/> with their arguments.</summary>
    /// <param name="provider">Type, field or method.</param>
    /// <param name="text">Receives the description.</param>
    private static void DescribeAttributes(ICustomAttributeProvider provider, StringBuilder text)
    {
        foreach (var attribute in provider.CustomAttributes)
        {
            text.Append(" [").Append(attribute.AttributeType.FullName);
            foreach (var argument in attribute.ConstructorArguments)
            {
                text.Append(' ').Append(Convert.ToString(argument.Value, CultureInfo.InvariantCulture));
            }

            foreach (var named in attribute.Properties.Concat(attribute.Fields))
            {
                text.Append(' ').Append(named.Name).Append('=').Append(Convert.ToString(named.Argument.Value, CultureInfo.InvariantCulture));
            }

            text.AppendLine("]");
        }
    }

    /// <summary>An instruction operand as text that does not depend on metadata tokens.</summary>
    /// <param name="operand">The operand, or null.</param>
    private static string Operand(object? operand) => operand switch
    {
        null => string.Empty,
        MemberReference member => member.FullName,
        Instruction target => target.Offset.ToString(CultureInfo.InvariantCulture),
        Instruction[] targets => string.Join(",", targets.Select(static t => t.Offset.ToString(CultureInfo.InvariantCulture))),
        VariableDefinition variable => "v" + variable.Index.ToString(CultureInfo.InvariantCulture),
        ParameterDefinition parameter => "p" + parameter.Index.ToString(CultureInfo.InvariantCulture),
        CallSite site => site.FullName,
        _ => Convert.ToString(operand, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
