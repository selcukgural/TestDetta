using Mono.Cecil;
using Sift.Core;

namespace Sift.Coverage;

/// <summary>
/// Maps compiled methods back to the source-level type and member that <see cref="SymbolChange"/>
/// uses, so recorded coverage can be matched against a diff.
/// </summary>
internal static class MethodNames
{
    /// <summary>
    /// The source member a compiled method belongs to. Compiler-generated code is attributed to the
    /// member it came from: <c>&lt;Run&gt;d__3.MoveNext</c> (async/iterator), <c>&lt;&gt;c.&lt;Run&gt;b__0_0</c>
    /// (lambda) and <c>&lt;Run&gt;g__Local|0_0</c> (local function) all map to <c>Run</c>; accessors map to
    /// their property or event.
    /// </summary>
    /// <param name="method">A method with a body.</param>
    public static CoveredMethod Map(MethodDefinition method)
    {
        var type = method.DeclaringType;
        string? member = null;

        // Walk out of compiler-generated nested types; a state machine's name carries the member.
        while (IsCompilerGenerated(type) && type.DeclaringType is not null)
        {
            member ??= GeneratedMemberName(type.Name);
            type = type.DeclaringType;
        }

        member ??= GeneratedMemberName(method.Name) ?? SourceMemberName(method);
        return new CoveredMethod(TypeName(type), member);
    }

    /// <summary>
    /// True for types that belong to no source type because the outermost type containing them was
    /// generated: <c>&lt;PrivateImplementationDetails&gt;</c>, <c>&lt;&gt;z__ReadOnlyArray`1</c> and their nested types.
    /// </summary>
    /// <param name="type">Type definition.</param>
    public static bool IsCompilerInfrastructure(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return IsCompilerGenerated(type);
    }

    /// <summary>Qualified source name of a type: <c>Ns.Outer/Inner`1</c> becomes <c>Ns.Outer.Inner`1</c>.</summary>
    /// <param name="type">Type definition.</param>
    public static string TypeName(TypeDefinition type) => type.FullName.Replace('/', '.');

    /// <summary>
    /// Name of a non-generated method as source spells it: accessors become their property or event,
    /// explicit interface implementations lose the interface prefix.
    /// </summary>
    /// <param name="method">A method written in source.</param>
    private static string SourceMemberName(MethodDefinition method)
    {
        var name = method.Name;
        if (name.StartsWith('.'))
        {
            return name;
        }

        // Explicit implementations are named "Ns.IFoo.Run" (or "Ns.IFoo.get_Name").
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        if (!method.IsSpecialName)
        {
            return name;
        }

        foreach (var prefix in (ReadOnlySpan<string>)["get_", "set_", "add_", "remove_"])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    /// <summary>The member between angle brackets of a generated name (<c>&lt;Run&gt;b__0_0</c> gives <c>Run</c>), or null.</summary>
    /// <param name="name">Type or method name.</param>
    private static string? GeneratedMemberName(string name)
    {
        if (!name.StartsWith('<'))
        {
            return null;
        }

        // Names may nest, as in "<<Main>$>g__Local|0_0": match the bracket that closes the first one.
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '<')
            {
                depth++;
            }
            else if (name[i] == '>' && --depth == 0)
            {
                return i > 1 ? name[1..i] : null;
            }
        }

        return null;
    }

    /// <summary>True for types the compiler generates: closures, lambda caches and state machines.</summary>
    /// <param name="type">Type definition.</param>
    private static bool IsCompilerGenerated(TypeDefinition type) => type.Name.StartsWith('<');
}
