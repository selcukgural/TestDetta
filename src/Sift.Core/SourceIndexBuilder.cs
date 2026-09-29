using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Parses every C# file of the given projects and records, per member, the identifiers it mentions.
/// Files are parsed in parallel; nothing is compiled.
/// </summary>
/// <param name="logger">Receives file counts and timing.</param>
internal sealed partial class SourceIndexBuilder(ILogger logger)
{
    /// <summary>Directories that never contain compiled sources.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bin", "obj", ".git", ".vs", ".idea", "node_modules");

    /// <summary>
    /// Members the language or common frameworks call by convention rather than by name:
    /// <c>using</c>, <c>foreach</c>, <c>await</c>, deconstruction, collection initializers,
    /// string interpolation, equality in collections, and test lifecycle hooks.
    /// </summary>
    private static readonly FrozenSet<string> ImplicitlyCalledNames = FrozenSet.Create(
        StringComparer.Ordinal,
        "Dispose", "DisposeAsync", "GetEnumerator", "GetAsyncEnumerator", "MoveNext", "MoveNextAsync", "Current",
        "Deconstruct", "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted", "Add", "ToString", "Equals",
        "GetHashCode", "CompareTo", "Invoke", "Main", "InitializeAsync", "Clone", "PrintMembers", "EqualityContract");

    /// <summary>Attributes that mark a test method in xUnit, NUnit and MSTest.</summary>
    private static readonly FrozenSet<string> TestAttributes = FrozenSet.Create(
        StringComparer.Ordinal,
        "Fact", "Theory", "SkippableFact", "SkippableTheory", "Test", "TestCase", "TestCaseSource",
        "TestMethod", "DataTestMethod");

    /// <summary>Indexes the C# files owned by <paramref name="projects"/>.</summary>
    /// <param name="graph">Projects of the repository, used to find which project owns a file.</param>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="projects">Normalized .csproj paths whose files are indexed.</param>
    public SourceIndex Build(ProjectGraph graph, string repoRoot, IReadOnlySet<string> projects)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = new List<(string Path, ProjectNode Owner)>();
        foreach (var file in EnumerateSourceFiles(Paths.Normalize(repoRoot)))
        {
            if (graph.FindOwner(file) is { } owner && projects.Contains(owner.Path))
            {
                files.Add((file, owner));
            }
        }

        var perFile = new ConcurrentBag<FileIndex>();
        Parallel.ForEach(files, file => perFile.Add(IndexFile(file.Path, file.Owner)));

        // Merge in a fixed order so the result, and anything logged from it, is deterministic.
        var members = new List<IndexedMember>();
        var types = new Dictionary<string, IndexedType>(StringComparer.Ordinal);
        foreach (var index in perFile.OrderBy(static f => f.Path, StringComparer.Ordinal))
        {
            members.AddRange(index.Members);
            foreach (var type in index.Types)
            {
                types[type.TypeName] = types.TryGetValue(type.TypeName, out var existing)
                    ? existing with
                    {
                        IsConcreteClass = existing.IsConcreteClass && type.IsConcreteClass,
                        HasTestMethods = existing.HasTestMethods || type.HasTestMethods,
                        BaseTypeNames = [.. existing.BaseTypeNames, .. type.BaseTypeNames],
                        MayBeFoundByReflection = existing.MayBeFoundByReflection || type.MayBeFoundByReflection,
                    }
                    : type;
            }
        }

        MarkTestClasses(types);

        LogIndexed(files.Count, projects.Count, members.Count, stopwatch.ElapsedMilliseconds);
        return new SourceIndex(members, types);
    }

    /// <summary>
    /// Sets <see cref="IndexedType.CanContainTests"/> for concrete classes that declare test methods
    /// or inherit them, directly or through other classes of the same test project.
    /// </summary>
    /// <param name="types">All merged types; updated in place.</param>
    private static void MarkTestClasses(Dictionary<string, IndexedType> types)
    {
        // Simple name -> types of test projects with that name, to follow base lists by name.
        var bySimpleName = new Dictionary<string, List<IndexedType>>(StringComparer.Ordinal);
        foreach (var type in types.Values)
        {
            if (!type.InTestProject)
            {
                continue;
            }

            var simple = DeclarationWalker.SimpleTypeName(type.TypeName);
            if (!bySimpleName.TryGetValue(simple, out var list))
            {
                list = [];
                bySimpleName[simple] = list;
            }

            list.Add(type);
        }

        foreach (var type in types.Values.ToList())
        {
            if (type.InTestProject && type.IsConcreteClass && DeclaresOrInheritsTests(type, bySimpleName, depth: 0))
            {
                types[type.TypeName] = type with { CanContainTests = true };
            }
        }
    }

    /// <summary>True when <paramref name="type"/> or a base class of the same project declares test methods.</summary>
    /// <param name="type">Type to check.</param>
    /// <param name="bySimpleName">Test-project types by simple name.</param>
    /// <param name="depth">Recursion depth; stops at 16 so a cyclic base list (invalid code) cannot loop.</param>
    private static bool DeclaresOrInheritsTests(
        IndexedType type,
        Dictionary<string, List<IndexedType>> bySimpleName,
        int depth)
    {
        if (type.HasTestMethods)
        {
            return true;
        }

        if (depth >= 16)
        {
            return false;
        }

        foreach (var baseName in type.BaseTypeNames)
        {
            if (!bySimpleName.TryGetValue(baseName, out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.ProjectPath == type.ProjectPath && DeclaresOrInheritsTests(candidate, bySimpleName, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Members and types of one file.</summary>
    /// <param name="Path">Normalized file path.</param>
    /// <param name="Members">Indexed members of the file.</param>
    /// <param name="Types">Types declared in the file.</param>
    private sealed record FileIndex(string Path, List<IndexedMember> Members, List<IndexedType> Types);

    /// <summary>Parses one file and indexes its declarations and top-level statements.</summary>
    /// <param name="path">Normalized file path.</param>
    /// <param name="owner">Project that compiles the file.</param>
    private static FileIndex IndexFile(string path, ProjectNode owner)
    {
        SourceText text;
        using (var stream = File.OpenRead(path))
        {
            text = SourceText.From(stream);
        }

        var root = DeclarationWalker.Parse(text);
        var members = new List<IndexedMember>();
        var types = new List<IndexedType>();
        var usingStatic = UsingStaticTargets(root);

        foreach (var declaration in DeclarationWalker.Collect(root).Values)
        {
            if (declaration.MemberName is null)
            {
                types.Add(new IndexedType(
                    declaration.TypeName,
                    declaration.RuntimeTypeName,
                    owner.Path,
                    owner.IsTestProject,
                    IsConcreteClass(declaration.Node),
                    HasTestMethods(declaration.Node),
                    BaseTypeNames(declaration.Node),
                    CanContainTests: false,
                    declaration.Node.AttributeLists.Count > 0 || declaration.Node is BaseTypeDeclarationSyntax { BaseList: not null }));
            }

            members.Add(new IndexedMember(
                owner.Path,
                owner.IsTestProject,
                declaration.TypeName,
                declaration.MemberName,
                declaration.MemberName is null ? [] : DeclarationWalker.ReferenceNames(declaration.MemberName),
                declaration.MemberName is null || MayBeCalledImplicitly(declaration),
                declaration.MemberName is not null && IsPrivateToType(declaration.Node),
                declaration.MemberName is not null && IsStaticAccess(declaration.Node),
                CollectReferences(declaration.Node, declaration.Descend, usingStatic)));
        }

        AddTopLevelStatements(root, owner, members, types);
        return new FileIndex(path, members, types);
    }

    /// <summary>Indexes top-level statements as the generated <c>Program</c> type's entry point.</summary>
    /// <param name="root">Compilation unit.</param>
    /// <param name="owner">Project that compiles the file.</param>
    /// <param name="members">Members of the file; updated in place.</param>
    /// <param name="types">Types of the file; updated in place.</param>
    private static void AddTopLevelStatements(
        CompilationUnitSyntax root,
        ProjectNode owner,
        List<IndexedMember> members,
        List<IndexedType> types)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in root.Members)
        {
            if (member is GlobalStatementSyntax statement)
            {
                AddReferences(statement, DeclarationWalker.DescendAll, references);
            }
        }

        if (references.Count == 0)
        {
            return;
        }

        const string program = DeclarationWalker.TopLevelProgramType;
        members.Add(new IndexedMember(
            owner.Path, owner.IsTestProject, program, "<Main>$", [], MayBeCalledImplicitly: true, IsPrivate: false,
            IsStatic: false, references.ToFrozenSet()));
        // The runtime, not reflection, starts the entry point. A test that runs it in-process has to
        // name Program (WebApplicationFactory<Program>); one that starts the app as a separate process
        // is invisible to name tracing for every change, not just this one.
        types.Add(new IndexedType(
            program, program, owner.Path, owner.IsTestProject, IsConcreteClass: false, HasTestMethods: false, [],
            CanContainTests: false, MayBeFoundByReflection: false));
    }

    /// <summary>
    /// Simple identifiers mentioned inside a declaration, excluding nested declarations, plus the
    /// file's <c>using static</c> targets: those let the code reach static members without naming the type.
    /// </summary>
    /// <param name="node">Declaration node.</param>
    /// <param name="descend">Which child nodes belong to the declaration.</param>
    /// <param name="usingStatic">Simple names of the types imported with <c>using static</c> in the file.</param>
    private static FrozenSet<string> CollectReferences(
        SyntaxNode node,
        Func<SyntaxNode, SyntaxNode, bool> descend,
        List<string> usingStatic)
    {
        var references = new HashSet<string>(usingStatic, StringComparer.Ordinal);
        AddReferences(node, descend, references);
        return references.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Simple names of the types named in the file's <c>using static</c> directives, including those inside namespaces.</summary>
    /// <param name="root">Compilation unit.</param>
    private static List<string> UsingStaticTargets(CompilationUnitSyntax root)
    {
        var targets = new List<string>();
        foreach (var directive in root.DescendantNodes(static n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                     .OfType<UsingDirectiveSyntax>())
        {
            if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && directive.NamespaceOrType is NameSyntax name
                && RightmostName(name) is { } simple)
            {
                targets.Add(simple.Identifier.ValueText);
            }
        }

        return targets;
    }

    /// <summary>
    /// True for members that code outside the type reaches through the type name: static fields,
    /// properties, events and methods, constants and enum members. Extension methods and members
    /// of extension blocks are excluded because they are called like instance members.
    /// </summary>
    /// <param name="member">Member declaration.</param>
    private static bool IsStaticAccess(MemberDeclarationSyntax member)
    {
        if (member is EnumMemberDeclarationSyntax)
        {
            return true;
        }

        if (member.Parent is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
        {
            return false;
        }

        if (member is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return true;
        }

        if (!member.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return false;
        }

        return member is not MethodDeclarationSyntax method
               || method.ParameterList.Parameters is not [{ Modifiers: var modifiers }, ..]
               || !modifiers.Any(SyntaxKind.ThisKeyword);
    }

    /// <summary>
    /// Adds every simple name below <paramref name="node"/>. Attributes are also added with the
    /// <c>Attribute</c> suffix, because <c>[Foo]</c> refers to <c>FooAttribute</c>.
    /// </summary>
    /// <param name="node">Node to scan.</param>
    /// <param name="descend">Which child nodes to scan.</param>
    /// <param name="references">Result set; updated in place.</param>
    private static void AddReferences(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend, HashSet<string> references)
    {
        foreach (var child in node.DescendantNodes(n => descend(n, node)))
        {
            switch (child)
            {
                case SimpleNameSyntax name:
                    references.Add(name.Identifier.ValueText);
                    break;

                case AttributeSyntax attribute when RightmostName(attribute.Name) is { } attributeName:
                    references.Add(attributeName.Identifier.ValueText + "Attribute");
                    break;
            }
        }
    }

    /// <summary>The last identifier of a possibly qualified name: <c>Xunit.Fact</c> gives <c>Fact</c>.</summary>
    /// <param name="name">Name syntax.</param>
    private static SimpleNameSyntax? RightmostName(NameSyntax name) => name switch
    {
        SimpleNameSyntax simple => simple,
        QualifiedNameSyntax qualified => qualified.Right,
        AliasQualifiedNameSyntax alias => alias.Name,
        _ => null,
    };

    /// <summary>
    /// True when code may run the member without naming it, so a change to it must be traced
    /// through its type instead of its name. See <see cref="IndexedMember.MayBeCalledImplicitly"/>.
    /// </summary>
    /// <param name="declaration">A member declaration.</param>
    private static bool MayBeCalledImplicitly(Declaration declaration)
    {
        var member = declaration.Node;
        var names = DeclarationWalker.ReferenceNames(declaration.MemberName!);

        if (names.Count == 0 || member.AttributeLists.Count > 0 || member.Modifiers.Any(SyntaxKind.OverrideKeyword))
        {
            return true;
        }

        if (member is BasePropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null }
            or MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null })
        {
            return true;
        }

        foreach (var name in names)
        {
            if (ImplicitlyCalledNames.Contains(name))
            {
                return true;
            }
        }

        // A non-private member of a type with a base list may implement an interface or override a
        // framework hook, and then be called through that contract from code outside the repository.
        return member.Parent is TypeDeclarationSyntax { BaseList: not null } type
               && (type is InterfaceDeclarationSyntax || !IsPrivate(member));
    }

    /// <summary>
    /// True for a private member of a class, struct or record. Interface and enum members are
    /// public whatever their modifiers say.
    /// </summary>
    /// <param name="member">Member declaration.</param>
    private static bool IsPrivateToType(MemberDeclarationSyntax member) =>
        member.Parent is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax && IsPrivate(member);

    /// <summary>True when the member is private, which is also the default for class and struct members.</summary>
    /// <param name="member">Member declaration.</param>
    private static bool IsPrivate(MemberDeclarationSyntax member) =>
        !member.Modifiers.Any(SyntaxKind.PublicKeyword)
        && !member.Modifiers.Any(SyntaxKind.ProtectedKeyword)
        && !member.Modifiers.Any(SyntaxKind.InternalKeyword);

    /// <summary>True when a method directly inside the type has a test attribute.</summary>
    /// <param name="node">Type declaration.</param>
    private static bool HasTestMethods(MemberDeclarationSyntax node)
    {
        if (node is not TypeDeclarationSyntax type)
        {
            return false;
        }

        foreach (var member in type.Members)
        {
            if (member is not MethodDeclarationSyntax method)
            {
                continue;
            }

            foreach (var list in method.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    if (RightmostName(attribute.Name) is { } name && IsTestAttribute(name.Identifier.ValueText))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>True for <c>Fact</c>, <c>FactAttribute</c> and the other names in <see cref="TestAttributes"/>.</summary>
    /// <param name="name">Attribute name as written.</param>
    private static bool IsTestAttribute(string name) =>
        TestAttributes.Contains(name)
        || (name.EndsWith("Attribute", StringComparison.Ordinal) && TestAttributes.Contains(name[..^"Attribute".Length]));

    /// <summary>Simple names of the base list entries: <c>: TestBase, IAsyncLifetime</c> gives both.</summary>
    /// <param name="node">Type declaration.</param>
    private static List<string> BaseTypeNames(MemberDeclarationSyntax node)
    {
        if (node is not BaseTypeDeclarationSyntax { BaseList: { } baseList })
        {
            return [];
        }

        var names = new List<string>(baseList.Types.Count);
        foreach (var baseType in baseList.Types)
        {
            var name = baseType.Type switch
            {
                SimpleNameSyntax simple => simple,
                QualifiedNameSyntax qualified => qualified.Right,
                AliasQualifiedNameSyntax alias => alias.Name,
                _ => null,
            };

            if (name is not null)
            {
                names.Add(name.Identifier.ValueText);
            }
        }

        return names;
    }

    /// <summary>True for a class a test framework could instantiate: not abstract, not static.</summary>
    /// <param name="node">Type declaration.</param>
    private static bool IsConcreteClass(MemberDeclarationSyntax node) =>
        node is ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: 0 or (int)SyntaxKind.ClassKeyword }
        && !node.Modifiers.Any(SyntaxKind.AbstractKeyword)
        && !node.Modifiers.Any(SyntaxKind.StaticKeyword);

    /// <summary>All .cs files below <paramref name="root"/>, skipping build output and tool folders.</summary>
    /// <param name="root">Normalized repository root.</param>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                yield return Paths.Normalize(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Indexed {FileCount} C# file(s) of {ProjectCount} project(s), {MemberCount} member(s), in {ElapsedMs} ms")]
    private partial void LogIndexed(int fileCount, int projectCount, int memberCount, long elapsedMs);
}
