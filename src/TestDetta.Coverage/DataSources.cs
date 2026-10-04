using System.Collections.Frozen;
using Mono.Cecil;

namespace TestDetta.Coverage;

/// <summary>
/// Finds the members that supply test data: those named by <c>[MemberData]</c>, <c>[TestCaseSource]</c>,
/// <c>[DynamicData]</c>, <c>[MethodDataSource]</c> and similar attributes, and the types named by
/// <c>[ClassData]</c>-like ones.
/// </summary>
/// <remarks>
/// Frameworks run these before the test class is created: while discovering tests, or when a theory
/// starts and before its constructor runs. No entry marker of the class has run by then, so without
/// a marker of their own their code went to whichever class was entered last, and a change to code
/// that only the data needs selected the wrong class (Newtonsoft.Json's <c>JsonSchemaSpecTests</c>
/// reads its cases through <c>JArray.Parse</c>).
/// <para>
/// The marker is not undone when the member returns: rows built lazily (LINQ, iterators) are
/// produced after it, and the framework creates the class right afterwards anyway. A test that
/// calls another class's data member itself keeps its bucket: the tracker sees the test's frame
/// below the data member and does not switch (<c>TestDettaTracker.CalledFromRunningTest</c>).
/// </para>
/// <para>
/// A static field named as a data source has no code of its own: reading it runs its type's static
/// constructor, which is the data member then. A concrete test class's own static constructor is
/// marked for the class too: it builds the values its tests receive through such fields, or that a
/// framework reads by reflection, and it runs whenever the framework first touches the class, which
/// is often while another class is current (NodaTime's <c>TzdbDateTimeZoneTest.AllTzdbZones</c> was
/// credited to <c>TzdbDateTimeZoneSourceTest</c>, so a change to the time zone data it loads missed
/// it). A shared base class's static constructor keeps no marker: its values serve every derived
/// class, and the field readers already account for them.
/// </para>
/// </remarks>
internal static class DataSources
{
    /// <summary>Attribute names (without the <c>Attribute</c> suffix and generic arity) that name a data source.</summary>
    private static readonly FrozenSet<string> Attributes = FrozenSet.Create(
        StringComparer.Ordinal,
        "MemberData", "ClassData", "TestCaseSource", "ValueSource", "TestFixtureSource", "DynamicData",
        "MethodDataSource", "InstanceMethodDataSource", "ClassDataSource");

    /// <summary>
    /// Data source methods of <paramref name="module"/> and the test class each one serves: the runtime
    /// name of the only concrete class that uses it, or null when several classes, an abstract base or
    /// a generic class use it. The data then counts as code outside every test, which coverage does not
    /// answer for.
    /// </summary>
    /// <param name="module">Test module.</param>
    /// <param name="isTestClass">True for the module's test classes.</param>
    public static Dictionary<MethodDefinition, string?> Find(ModuleDefinition module, Func<TypeDefinition, bool> isTestClass)
    {
        var users = new Dictionary<MethodDefinition, HashSet<TypeDefinition>>();
        var bases = new HashSet<TypeDefinition>();
        var initializers = new List<(MethodDefinition Initializer, TypeDefinition Type)>();
        foreach (var type in module.GetTypes())
        {
            if (type.BaseType is { } baseType && baseType.Module == module && baseType.Resolve() is { } resolved)
            {
                bases.Add(resolved);
            }

            if (!isTestClass(type))
            {
                continue;
            }

            Collect(type.CustomAttributes, type, users);
            if (type is { IsAbstract: false, HasGenericParameters: false } && StaticConstructor(type) is { } initializer)
            {
                initializers.Add((initializer, type));
            }

            foreach (var method in type.Methods)
            {
                Collect(method.CustomAttributes, type, users);
                foreach (var parameter in method.Parameters)
                {
                    Collect(parameter.CustomAttributes, type, users);
                }
            }
        }

        // Known only once every type was seen: a class others derive from serves them all.
        foreach (var (initializer, type) in initializers)
        {
            if (!bases.Contains(type))
            {
                Add(initializer, type, users);
            }
        }

        var sources = new Dictionary<MethodDefinition, string?>();
        foreach (var (method, classes) in users)
        {
            var only = classes.Count == 1 ? classes.First() : null;
            var user = only is { IsAbstract: false, HasGenericParameters: false } && !bases.Contains(only)
                ? only.FullName.Replace('/', '+')
                : null;
            sources[method] = user;

            // An iterator's body runs in its state machine, after the method has returned.
            foreach (var machine in StateMachines(method))
            {
                foreach (var body in machine.Methods)
                {
                    sources.TryAdd(body, user);
                }
            }
        }

        return sources;
    }

    /// <summary>Adds the data sources named by <paramref name="attributes"/> with <paramref name="owner"/> as a user.</summary>
    /// <param name="attributes">Attributes of a test class, test method or parameter.</param>
    /// <param name="owner">Test class declaring them.</param>
    /// <param name="users">Data source methods and their users; updated in place.</param>
    private static void Collect(IEnumerable<CustomAttribute> attributes, TypeDefinition owner, Dictionary<MethodDefinition, HashSet<TypeDefinition>> users)
    {
        foreach (var attribute in attributes)
        {
            if (!IsDataAttribute(attribute.AttributeType))
            {
                continue;
            }

            var types = new List<TypeDefinition>();
            var names = new List<string>();
            Read(attribute, types, names);

            if (names.Count == 0)
            {
                // A data class: everything it declares runs to produce the rows.
                foreach (var type in types)
                {
                    AddType(type, owner, users);
                }

                continue;
            }

            if (types.Count == 0)
            {
                types.Add(owner);
            }

            foreach (var type in types)
            {
                foreach (var name in names)
                {
                    AddMember(type, name, owner, users);
                }
            }
        }
    }

    /// <summary>
    /// Reads the types (<c>typeof</c> arguments, <c>MemberType</c>-like properties, generic arguments of
    /// the attribute) and member names (string constructor arguments) of a data attribute. Only types
    /// of the test module are kept: other code is not instrumented.
    /// </summary>
    /// <param name="attribute">Data attribute.</param>
    /// <param name="types">Receives the named types.</param>
    /// <param name="names">Receives the member names.</param>
    private static void Read(CustomAttribute attribute, List<TypeDefinition> types, List<string> names)
    {
        var module = attribute.AttributeType.Module;
        foreach (var argument in attribute.ConstructorArguments)
        {
            switch (argument.Value)
            {
                case string name:
                    names.Add(name);
                    break;

                case TypeReference type:
                    AddLocal(type, module, types);
                    break;
            }
        }

        foreach (var named in attribute.Properties.Concat(attribute.Fields))
        {
            if (named.Argument.Value is TypeReference type)
            {
                AddLocal(type, module, types);
            }
        }

        if (attribute.AttributeType is GenericInstanceType generic)
        {
            foreach (var argument in generic.GenericArguments)
            {
                AddLocal(argument, module, types);
            }
        }
    }

    /// <summary>Adds the definition of <paramref name="type"/> when the module declares it.</summary>
    /// <param name="type">Referenced type.</param>
    /// <param name="module">Test module.</param>
    /// <param name="types">Receives the definition.</param>
    private static void AddLocal(TypeReference type, ModuleDefinition module, List<TypeDefinition> types)
    {
        var element = type is GenericInstanceType generic ? generic.ElementType : type;
        if (Resolve(element) is { } definition && definition.Module == module)
        {
            types.Add(definition);
        }
    }

    /// <summary>Adds every method of <paramref name="type"/>, its nested types and its bases in the module.</summary>
    /// <param name="type">Data class.</param>
    /// <param name="owner">Test class using it.</param>
    /// <param name="users">Data source methods and their users; updated in place.</param>
    private static void AddType(TypeDefinition type, TypeDefinition owner, Dictionary<MethodDefinition, HashSet<TypeDefinition>> users)
    {
        for (var (current, depth) = (type, 0); current is not null && depth < 16; depth++)
        {
            foreach (var method in current.Methods)
            {
                Add(method, owner, users);
            }

            foreach (var nested in current.NestedTypes)
            {
                foreach (var method in nested.Methods)
                {
                    Add(method, owner, users);
                }
            }

            current = current.BaseType is { } baseType && baseType.Module == type.Module ? Resolve(baseType) : null;
        }
    }

    /// <summary>
    /// Adds the methods and property getters called <paramref name="name"/> of <paramref name="type"/>
    /// or a base class in the module, as frameworks look the member up, and for a static field of
    /// that name the static constructor of the type declaring it.
    /// </summary>
    /// <param name="type">Type that declares the member.</param>
    /// <param name="name">Member name from the attribute.</param>
    /// <param name="owner">Test class using it.</param>
    /// <param name="users">Data source methods and their users; updated in place.</param>
    private static void AddMember(TypeDefinition type, string name, TypeDefinition owner, Dictionary<MethodDefinition, HashSet<TypeDefinition>> users)
    {
        for (var (current, depth) = (type, 0); current is not null && depth < 16; depth++)
        {
            foreach (var method in current.Methods)
            {
                if (method.Name == name || (method.IsGetter && IsGetterOf(method.Name, name)))
                {
                    Add(method, owner, users);
                }
            }

            if (current.Fields.Any(field => field.IsStatic && field.Name == name) && StaticConstructor(current) is { } initializer)
            {
                Add(initializer, owner, users);
            }

            current = current.BaseType is { } baseType && baseType.Module == type.Module ? Resolve(baseType) : null;
        }
    }

    /// <summary>The static constructor of <paramref name="type"/>, when it has one with a body.</summary>
    /// <param name="type">Type definition.</param>
    private static MethodDefinition? StaticConstructor(TypeDefinition type) =>
        type.Methods.FirstOrDefault(static method => method is { IsConstructor: true, IsStatic: true, HasBody: true });

    /// <summary>True when <paramref name="getter"/> is <c>get_</c> followed by <paramref name="property"/>.</summary>
    /// <param name="getter">Method name.</param>
    /// <param name="property">Property name.</param>
    private static bool IsGetterOf(string getter, string property) =>
        getter.Length == property.Length + 4
        && getter.StartsWith("get_", StringComparison.Ordinal)
        && getter.AsSpan(4).SequenceEqual(property);

    /// <summary>Records <paramref name="owner"/> as a user of <paramref name="method"/>.</summary>
    /// <param name="method">Data source method.</param>
    /// <param name="owner">Test class.</param>
    /// <param name="users">Data source methods and their users; updated in place.</param>
    private static void Add(MethodDefinition method, TypeDefinition owner, Dictionary<MethodDefinition, HashSet<TypeDefinition>> users)
    {
        if (!users.TryGetValue(method, out var classes))
        {
            classes = [];
            users[method] = classes;
        }

        classes.Add(owner);
    }

    /// <summary>State machine types of an iterator or async iterator method, named by its compiler attribute.</summary>
    /// <param name="method">Data source method.</param>
    private static IEnumerable<TypeDefinition> StateMachines(MethodDefinition method)
    {
        foreach (var attribute in method.CustomAttributes)
        {
            if (attribute.AttributeType.Name is not ("IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute")
                || attribute.ConstructorArguments is not [{ Value: TypeReference machine }])
            {
                continue;
            }

            if (Resolve(machine) is { } definition)
            {
                yield return definition;
            }
        }
    }

    /// <summary>True when the attribute, or one it derives from, names a data source.</summary>
    /// <param name="type">Attribute type.</param>
    private static bool IsDataAttribute(TypeReference type)
    {
        for (var (current, depth) = (type, 0); current is not null && depth < 8; depth++)
        {
            var name = current.Name;
            var arity = name.IndexOf('`');
            if (arity >= 0)
            {
                name = name[..arity];
            }

            if (name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name = name[..^"Attribute".Length];
            }

            if (Attributes.Contains(name))
            {
                return true;
            }

            current = Resolve(current)?.BaseType;
        }

        return false;
    }

    /// <summary>Resolves a type reference, or null when its assembly cannot be found.</summary>
    /// <param name="type">Type reference.</param>
    private static TypeDefinition? Resolve(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }
}
