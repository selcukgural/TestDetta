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
    /// <param name="generated">
    /// Code source generators wrote, from coverage maps; each type becomes one member of its project
    /// that mentions the generated names, so name tracing reaches generated callers.
    /// </param>
    public SourceIndex Build(ProjectGraph graph, string repoRoot, IReadOnlySet<string> projects, IEnumerable<GeneratedType>? generated = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = new List<(string Path, ProjectNode Owner)>();
        foreach (var file in EnumerateSourceFiles(Paths.Normalize(repoRoot)))
        {
            // A linked file is compiled by every project that includes it, so it is indexed for each.
            foreach (var owner in graph.ResolveFile(file))
            {
                if (owner.Role == FileRole.Source && projects.Contains(owner.Project.Path))
                {
                    files.Add((file, owner.Project));
                }
            }
        }

        var perFile = new ConcurrentBag<FileIndex>();
        Parallel.ForEach(files, file => perFile.Add(IndexFile(file.Path, file.Owner)));

        // Merge in a fixed order so the result, and anything logged from it, is deterministic.
        var members = new List<IndexedMember>();
        var types = new Dictionary<string, IndexedType>(StringComparer.Ordinal);
        foreach (var index in perFile.OrderBy(static f => f.Path, StringComparer.Ordinal).ThenBy(static f => f.Project, StringComparer.Ordinal))
        {
            members.AddRange(index.Members);
            foreach (var type in index.Types)
            {
                // Per project: a source file linked into several test projects (a shared .projitems)
                // declares its test classes once in each, and each project runs them.
                var key = TypeKey(type);
                types[key] = types.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        IsRunnableClass = existing.IsRunnableClass && type.IsRunnableClass,
                        HasTestMethods = existing.HasTestMethods || type.HasTestMethods,
                        BaseTypeNames = [.. existing.BaseTypeNames, .. type.BaseTypeNames],
                        MayBeFoundByReflection = existing.MayBeFoundByReflection || type.MayBeFoundByReflection,
                        Spans = type.Spans.Count == 0 ? existing.Spans : [.. existing.Spans.Union(type.Spans)],
                    }
                    : type;
            }
        }

        MarkTestClasses(types);
        AddGenerated(graph, repoRoot, projects, generated ?? [], members);

        LogIndexed(files.Count, projects.Count, members.Count, stopwatch.ElapsedMilliseconds);
        return new SourceIndex(members, types);
    }

    /// <summary>Name of the member that stands for a type's generated code.</summary>
    public const string GeneratedMember = "<generated>";

    /// <summary>
    /// Adds one member per generated type of an indexed project. It may run without being named
    /// (generated registrations run from attributes and module initializers), so a change reaching
    /// it spreads to its type.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="projects">Indexed projects.</param>
    /// <param name="generated">Generated types from coverage maps; the same type may come from several maps.</param>
    /// <param name="members">Indexed members; updated in place.</param>
    private static void AddGenerated(
        ProjectGraph graph,
        string repoRoot,
        IReadOnlySet<string> projects,
        IEnumerable<GeneratedType> generated,
        List<IndexedMember> members)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var type in generated)
        {
            var project = Paths.Normalize(Path.Combine(repoRoot, type.Project));
            if (!projects.Contains(project) || !seen.Add((project, type.TypeName)) || graph.Find(project) is not { } owner)
            {
                continue;
            }

            members.Add(new IndexedMember(
                project,
                owner.IsTestProject,
                type.TypeName,
                GeneratedMember,
                [],
                MayBeCalledImplicitly: true,
                IsPrivate: false,
                IsStatic: false,
                type.References.ToFrozenSet(StringComparer.Ordinal)));
        }
    }

    /// <summary>Key of a type in the index: the same source type compiled by two projects is two entries.</summary>
    /// <param name="type">Indexed type.</param>
    private static string TypeKey(IndexedType type) => type.ProjectPath + "|" + type.TypeName;

    /// <summary>
    /// Sets <see cref="IndexedType.CanContainTests"/> for concrete classes that declare test methods
    /// or inherit them, directly or through other classes of the same test project.
    /// </summary>
    /// <param name="types">All merged types; updated in place.</param>
    private static void MarkTestClasses(Dictionary<string, IndexedType> types)
    {
        // Test-project types by (project, simple name of a base they list), to walk base lists backwards.
        var byBaseName = new Dictionary<(string Project, string Name), List<IndexedType>>();
        var marked = new HashSet<IndexedType>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<IndexedType>();
        foreach (var type in types.Values)
        {
            if (!type.InTestProject)
            {
                continue;
            }

            foreach (var baseName in type.BaseTypeNames)
            {
                if (!byBaseName.TryGetValue((type.ProjectPath, baseName), out var list))
                {
                    list = [];
                    byBaseName[(type.ProjectPath, baseName)] = list;
                }

                list.Add(type);
            }

            if (type.HasTestMethods && marked.Add(type))
            {
                queue.Enqueue(type);
            }
        }

        // A type inherits tests when a base it names, by simple name in the same project, declares or
        // inherits them. Walking from the types with tests to the types that name them visits each type
        // once: following base lists forwards tried every type sharing a simple name at every level,
        // which never finished on Swashbuckle's snapshot folders of generated clients.
        while (queue.TryDequeue(out var withTests))
        {
            if (!byBaseName.TryGetValue((withTests.ProjectPath, DeclarationWalker.SimpleTypeName(withTests.TypeName)), out var derived))
            {
                continue;
            }

            foreach (var type in derived)
            {
                if (marked.Add(type))
                {
                    queue.Enqueue(type);
                }
            }
        }

        foreach (var type in types.Values.ToList())
        {
            if (type.InTestProject && type.IsRunnableClass && marked.Contains(type))
            {
                types[TypeKey(type)] = type with { CanContainTests = true };
            }
        }
    }

    /// <summary>Members and types of one file as compiled by one project.</summary>
    /// <param name="Path">Normalized file path.</param>
    /// <param name="Project">Project that compiles it.</param>
    /// <param name="Members">Indexed members of the file.</param>
    /// <param name="Types">Types declared in the file.</param>
    private sealed record FileIndex(string Path, string Project, List<IndexedMember> Members, List<IndexedType> Types);

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

        var members = new List<IndexedMember>();
        var types = new List<IndexedType>();

        // Conditional code is indexed as if its symbols were both undefined and defined; types seen in
        // both are merged like partial types, and a member seen twice only widens its references.
        foreach (var root in DeclarationWalker.ParseVariants(text, owner.SymbolSets))
        {
            IndexRoot(new ParsedFile(path, root), owner, members, types);
        }

        return new FileIndex(path, owner.Path, members, types);
    }

    /// <summary>One parse of a file.</summary>
    /// <param name="Path">Normalized file path.</param>
    /// <param name="Root">Compilation unit.</param>
    private sealed record ParsedFile(string Path, CompilationUnitSyntax Root);

    /// <summary>Indexes the declarations and top-level statements of one parse of a file.</summary>
    /// <param name="file">The parse.</param>
    /// <param name="owner">Project that compiles the file.</param>
    /// <param name="members">Members of the file; updated in place.</param>
    /// <param name="types">Types of the file; updated in place.</param>
    private static void IndexRoot(ParsedFile file, ProjectNode owner, List<IndexedMember> members, List<IndexedType> types)
    {
        var root = file.Root;
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
                    IsRunnableClass(declaration.Node),
                    HasTestMethods(declaration.Node),
                    BaseTypeNames(declaration.Node),
                    CanContainTests: false,
                    declaration.Node.AttributeLists.Count > 0 || declaration.Node is BaseTypeDeclarationSyntax { BaseList: not null })
                {
                    Spans = owner.IsTestProject ? [Span(file, declaration.Node)] : [],
                });
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
                CollectReferences(declaration.Node, declaration.Descend, usingStatic))
            {
                MetadataReferences = CollectMetadataReferences(declaration.Node, declaration.Descend),
            });
        }

        AddTopLevelStatements(root, owner, members, types);
    }

    /// <summary>Lines of <paramref name="node"/> in <paramref name="file"/>, 1-based.</summary>
    /// <param name="file">The parse the node belongs to.</param>
    /// <param name="node">A type declaration.</param>
    private static DeclarationSpan Span(ParsedFile file, SyntaxNode node)
    {
        var lines = file.Root.SyntaxTree.GetLineSpan(node.Span);
        return new DeclarationSpan(file.Path, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1);
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
            program, program, owner.Path, owner.IsTestProject, IsRunnableClass: false, HasTestMethods: false, [],
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
    /// Names in the attributes and parameter default values of a declaration, excluding nested
    /// declarations: the compiler stores them as metadata, which reflection reads without running code.
    /// </summary>
    /// <param name="node">Declaration node.</param>
    /// <param name="descend">Which child nodes belong to the declaration.</param>
    private static FrozenSet<string> CollectMetadataReferences(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        HashSet<string>? references = null;
        foreach (var metadata in DeclarationWalker.MetadataNodes(node, descend))
        {
            references ??= new HashSet<string>(StringComparer.Ordinal);
            AddReferences(metadata, DeclarationWalker.DescendAll, references);
        }

        return references?.ToFrozenSet(StringComparer.Ordinal) ?? [];
    }

    /// <summary>Every simple name below <paramref name="node"/>, as <see cref="AddReferences"/> collects them.</summary>
    /// <param name="node">Node to scan.</param>
    /// <param name="descend">Which child nodes to scan.</param>
    internal static HashSet<string> References(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        AddReferences(node, descend, references);
        return references;
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

        // An initializer runs in the type initializer (static) or every constructor (instance), which
        // any use of the type triggers, not only code that names the field or property.
        if (member is BaseFieldDeclarationSyntax field && field.Declaration.Variables.Any(static v => v.Initializer is not null)
            || member is PropertyDeclarationSyntax { Initializer: not null })
        {
            return true;
        }

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
    private static bool IsTestAttribute(string name)
    {
        var shortName = name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
        if (TestAttributes.Contains(shortName))
        {
            return true;
        }

        // Custom test attributes derive from the framework's and keep its name as a suffix:
        // [UIFact], [WpfFact], [RetryFact], [CulturedTheory], [NonParallelTest].
        foreach (var suffix in TestAttributeSuffixes)
        {
            if (shortName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Endings of custom test attribute names; see <see cref="IsTestAttribute"/>.</summary>
    private static readonly string[] TestAttributeSuffixes = ["Fact", "Theory", "Test", "TestMethod", "TestCase"];

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

    /// <summary>
    /// True for a class whose tests a framework runs: any non-abstract class. Static classes count:
    /// xUnit and NUnit run the static test methods of a static class without instantiating it.
    /// </summary>
    /// <param name="node">Type declaration.</param>
    private static bool IsRunnableClass(MemberDeclarationSyntax node) =>
        node is ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: 0 or (int)SyntaxKind.ClassKeyword }
        && !node.Modifiers.Any(SyntaxKind.AbstractKeyword);

    /// <summary>All .cs files below <paramref name="root"/>, skipping build output and tool folders.</summary>
    /// <param name="root">Normalized repository root.</param>
    internal static IEnumerable<string> EnumerateSourceFiles(string root)
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
