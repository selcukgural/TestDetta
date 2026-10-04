using System.Collections.Frozen;

namespace TestDetta.Core;

/// <summary>A member (or a type's own declaration) and the identifiers its code mentions.</summary>
/// <param name="ProjectPath">Normalized path of the project that compiles the file.</param>
/// <param name="InTestProject">True when <see cref="ProjectPath"/> is a test project.</param>
/// <param name="TypeName">Qualified name of the containing type.</param>
/// <param name="MemberName">Member signature, or null for the type declaration (attributes, base list, primary constructor).</param>
/// <param name="DeclaredNames">Identifiers other code uses to refer to this member; empty for constructors, operators and type shells.</param>
/// <param name="MayBeCalledImplicitly">
/// True when code can run this member without naming it: constructors, operators, overrides,
/// attributed members, explicit or likely interface implementations, and well-known pattern
/// members such as <c>Dispose</c> or <c>GetEnumerator</c>.
/// </param>
/// <param name="IsPrivate">
/// True for private members of classes, structs and records. Only code inside the same type (and
/// its nested types) can name them, so their names are not traced anywhere else.
/// </param>
/// <param name="IsStatic">
/// True for static members other than extension methods, constants and enum members. Code
/// outside the type reaches them as <c>Type.Member</c> (or through <c>using static Type</c>), so a
/// reference only counts when the referencing type also names the declaring type.
/// </param>
/// <param name="References">
/// Every simple identifier that appears in the member's code, plus the targets of the file's
/// <c>using static</c> directives.
/// </param>
internal sealed record IndexedMember(
    string ProjectPath,
    bool InTestProject,
    string TypeName,
    string? MemberName,
    IReadOnlyList<string> DeclaredNames,
    bool MayBeCalledImplicitly,
    bool IsPrivate,
    bool IsStatic,
    FrozenSet<string> References)
{
    /// <summary>
    /// The part of <see cref="References"/> that is metadata: names in the member's attributes (its
    /// parameters' and accessors' included) and in parameter default values. Reflection reads these
    /// without running the member, so a coverage map cannot say who depends on them.
    /// </summary>
    public FrozenSet<string> MetadataReferences { get; init; } = [];
}

/// <summary>A type declared in an indexed project.</summary>
/// <param name="TypeName">Qualified name, nested types joined with '.'.</param>
/// <param name="RuntimeTypeName">Name as <c>dotnet test --filter</c> sees it, nested types joined with '+'.</param>
/// <param name="ProjectPath">Normalized path of the declaring project.</param>
/// <param name="InTestProject">True when the declaring project is a test project.</param>
/// <param name="IsRunnableClass">
/// True for non-abstract classes, the ones whose tests a framework runs (static classes included:
/// xUnit and NUnit run their static test methods). For a partial type, false as soon as one part
/// says otherwise.
/// </param>
/// <param name="HasTestMethods">True when a method carries a test attribute such as <c>[Fact]</c>, <c>[Test]</c> or <c>[TestMethod]</c>.</param>
/// <param name="BaseTypeNames">Simple names in the base list, used to find classes that inherit tests.</param>
/// <param name="CanContainTests">
/// True for a concrete class that declares test methods or inherits them from a class of the same
/// project. Only these go into a test filter.
/// </param>
/// <param name="MayBeFoundByReflection">
/// True when the type has a base list or attributes. Frameworks that discover types at run time
/// (controllers, message handlers, hosted services, validators) find them through exactly these.
/// For a partial type, true as soon as one part has either.
/// </param>
internal sealed record IndexedType(
    string TypeName,
    string RuntimeTypeName,
    string ProjectPath,
    bool InTestProject,
    bool IsRunnableClass,
    bool HasTestMethods,
    IReadOnlyList<string> BaseTypeNames,
    bool CanContainTests,
    bool MayBeFoundByReflection)
{
    /// <summary>
    /// Where the type is declared, one span per declaration (partial parts, and each parsed variant of
    /// conditional code once). Kept for types of test projects only: selection matches a test class
    /// the build names differently from its coverage map by the place the map recorded it at
    /// (docs/design/unrecorded-classes.md). Empty for other types.
    /// </summary>
    public IReadOnlyList<DeclarationSpan> Spans { get; init; } = [];
}

/// <summary>The lines a type declaration covers in one file, attributes and braces included.</summary>
/// <param name="File">Normalized file path.</param>
/// <param name="StartLine">First line, 1-based like PDB sequence points.</param>
/// <param name="EndLine">Last line, 1-based.</param>
internal sealed record DeclarationSpan(string File, int StartLine, int EndLine)
{
    /// <summary>True when <paramref name="line"/> of <paramref name="file"/> lies inside the declaration.</summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="line">1-based line.</param>
    public bool Contains(string file, int line) => line >= StartLine && line <= EndLine && Paths.Comparer.Equals(file, File);
}

/// <summary>
/// Name-based view of the source code of a set of projects: which members mention which
/// identifiers. Built by <see cref="SourceIndexBuilder"/>; read by <see cref="ImpactPropagation"/>.
/// </summary>
internal sealed class SourceIndex
{
    private readonly Dictionary<string, List<IndexedMember>> _referencesTo;
    private readonly Dictionary<string, List<IndexedMember>> _membersByType;
    private readonly Dictionary<string, IndexedType> _types;

    /// <summary>
    /// Types by name alone, for questions about the source type rather than one project's copy of
    /// it: when projects share the file, <see cref="IndexedType.MayBeFoundByReflection"/> is true if
    /// any copy says so.
    /// </summary>
    private readonly Dictionary<string, IndexedType> _typesByName = new(StringComparer.Ordinal);

    /// <summary>Union of the references of all members of a type, including its declaration and every partial part.</summary>
    private readonly Dictionary<string, HashSet<string>> _typeReferences;

    /// <summary>Builds the lookup tables from already collected members and types.</summary>
    /// <param name="members">All indexed members.</param>
    /// <param name="types">All indexed types, one entry per type and project even when it is partial.</param>
    public SourceIndex(IReadOnlyList<IndexedMember> members, Dictionary<string, IndexedType> types)
    {
        Members = members;
        _types = types;
        foreach (var type in types.Values)
        {
            _typesByName[type.TypeName] = _typesByName.TryGetValue(type.TypeName, out var existing)
                ? existing with { MayBeFoundByReflection = existing.MayBeFoundByReflection || type.MayBeFoundByReflection }
                : type;
        }

        _referencesTo = new Dictionary<string, List<IndexedMember>>(StringComparer.Ordinal);
        _membersByType = new Dictionary<string, List<IndexedMember>>(StringComparer.Ordinal);
        _typeReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            GetOrAdd(_membersByType, member.TypeName).Add(member);

            if (!_typeReferences.TryGetValue(member.TypeName, out var typeReferences))
            {
                typeReferences = new HashSet<string>(StringComparer.Ordinal);
                _typeReferences[member.TypeName] = typeReferences;
            }

            typeReferences.UnionWith(member.References);

            foreach (var name in member.References)
            {
                GetOrAdd(_referencesTo, name).Add(member);
            }
        }
    }

    /// <summary>All indexed members.</summary>
    public IReadOnlyList<IndexedMember> Members { get; }

    /// <summary>All indexed types.</summary>
    public IReadOnlyCollection<IndexedType> Types => _types.Values;

    /// <summary>Members whose code mentions the identifier <paramref name="name"/>.</summary>
    /// <param name="name">Simple identifier.</param>
    public IReadOnlyList<IndexedMember> ReferencesTo(string name) =>
        _referencesTo.TryGetValue(name, out var list) ? list : [];

    /// <summary>True when any member of <paramref name="typeName"/>, or its declaration, mentions <paramref name="name"/>.</summary>
    /// <param name="typeName">Qualified name of the referencing type.</param>
    /// <param name="name">Simple identifier.</param>
    public bool TypeMentions(string typeName, string name) =>
        _typeReferences.TryGetValue(typeName, out var references) && references.Contains(name);

    /// <summary>Indexed types (every part and project) by their simple name, built on first use.</summary>
    private Dictionary<string, List<IndexedType>>? _typesBySimpleName;

    /// <summary>
    /// True when <paramref name="typeName"/> certainly derives from <c>System.Attribute</c>: a part of
    /// it lists <c>Attribute</c> as its base and no indexed type is called <c>Attribute</c>, or every
    /// indexed type its base names is such a class. Instances of an attribute class come only from its
    /// uses (<c>[Name]</c>) and <c>new</c>, which both name it, so one that nothing names has none,
    /// whatever reflection does. A base outside the repository with another name (an
    /// <c>XmlAttribute</c>) gives false.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    public bool IsAttributeClass(string typeName) => IsAttributeClass(typeName, []);

    /// <summary>See <see cref="IsAttributeClass(string)"/>; <paramref name="visited"/> stops cycles.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="visited">Types already on the path.</param>
    private bool IsAttributeClass(string typeName, HashSet<string> visited)
    {
        if (!visited.Add(typeName))
        {
            return false;
        }

        _typesBySimpleName ??= _types.Values
            .GroupBy(static t => DeclarationWalker.SimpleTypeName(t.TypeName), StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        foreach (var part in _types.Values)
        {
            if (part.TypeName != typeName)
            {
                continue;
            }

            foreach (var baseName in part.BaseTypeNames)
            {
                if (_typesBySimpleName.TryGetValue(baseName, out var candidates))
                {
                    // A base the repository declares counts when every type of that name is an attribute class.
                    if (candidates.TrueForAll(c => IsAttributeClass(c.TypeName, [.. visited])))
                    {
                        return true;
                    }

                    continue;
                }

                if (baseName == "Attribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The type called <paramref name="typeName"/>, or null when no indexed file declares it.</summary>
    /// <param name="typeName">Qualified type name.</param>
    public IndexedType? FindType(string typeName) => _typesByName.GetValueOrDefault(typeName);

    /// <summary>The member with signature <paramref name="memberName"/> in <paramref name="typeName"/>, or null.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="memberName">Member signature, or null for the type declaration.</param>
    public IndexedMember? FindMember(string typeName, string? memberName)
    {
        if (!_membersByType.TryGetValue(typeName, out var members))
        {
            return null;
        }

        foreach (var member in members)
        {
            if (member.MemberName == memberName)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    /// The member with signature <paramref name="memberName"/> in <paramref name="typeName"/> as
    /// <paramref name="projectPath"/> compiles it: a file in a folder several projects compile is
    /// indexed once per project, and each copy spreads to that project's dependents. Falls back to any
    /// copy when the project has none.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="memberName">Member signature, or null for the type declaration.</param>
    /// <param name="projectPath">Project that owns the changed file.</param>
    public IndexedMember? FindMember(string typeName, string? memberName, string projectPath)
    {
        if (!_membersByType.TryGetValue(typeName, out var members))
        {
            return null;
        }

        IndexedMember? any = null;
        foreach (var member in members)
        {
            if (member.MemberName != memberName)
            {
                continue;
            }

            if (Paths.Comparer.Equals(member.ProjectPath, projectPath))
            {
                return member;
            }

            any ??= member;
        }

        return any;
    }

    /// <summary>Every indexed copy of the member: one per project compiling its file.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="memberName">Member signature, or null for the type declaration.</param>
    public IEnumerable<IndexedMember> FindMembers(string typeName, string? memberName) =>
        _membersByType.TryGetValue(typeName, out var members) ? members.Where(m => m.MemberName == memberName) : [];

    /// <summary>
    /// True when a member of another type than <paramref name="typeName"/> mentions <paramref name="name"/>.
    /// A type that nothing else names is probably found through reflection (controllers, handlers, hosted services).
    /// </summary>
    /// <param name="name">Simple identifier.</param>
    /// <param name="typeName">Qualified name of the type whose own references are ignored.</param>
    public bool IsReferencedOutside(string name, string typeName)
    {
        foreach (var member in ReferencesTo(name))
        {
            if (member.TypeName != typeName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a member other than <paramref name="self"/> mentions <paramref name="name"/>.</summary>
    /// <param name="name">Simple identifier.</param>
    /// <param name="self">Member whose own references, such as recursion, are ignored.</param>
    public bool IsReferencedByOthers(string name, IndexedMember self)
    {
        foreach (var member in ReferencesTo(name))
        {
            if (!ReferenceEquals(member, self))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the list stored under <paramref name="key"/>, creating it when missing.</summary>
    /// <param name="map">Dictionary of lists.</param>
    /// <param name="key">Lookup key.</param>
    private static List<IndexedMember> GetOrAdd(Dictionary<string, List<IndexedMember>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }
}
