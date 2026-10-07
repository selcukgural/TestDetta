using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace TestDetta.Core;

/// <summary>
/// The Visual Basic side of <see cref="SourceIndexBuilder"/>: types and members of a file with the names
/// they mention, by the same rules as C#. Names are kept as written and compared ignoring case
/// (<see cref="IndexedMember.IgnoreCase"/>), as the compiler binds them (docs/design/vb-fsharp-support.md).
/// </summary>
internal static class VisualBasicIndexer
{
    /// <summary>Indexes the declarations of one parse of a Visual Basic file.</summary>
    /// <param name="path">Normalized file path.</param>
    /// <param name="root">The parse.</param>
    /// <param name="owner">Project that compiles the file.</param>
    /// <param name="members">Members of the file; updated in place.</param>
    /// <param name="types">Types of the file; updated in place.</param>
    public static void IndexRoot(string path, SyntaxNode root, ProjectNode owner, List<IndexedMember> members, List<IndexedType> types)
    {
        // An Imports of a type or module brings its members into scope without its name, like C#'s using
        // static: every member of the file may reach them.
        var imported = ((CompilationUnitSyntax)root).Imports
            .SelectMany(static i => i.ImportsClauses.OfType<SimpleImportsClauseSyntax>())
            .Select(static c => Rightmost(c.Name))
            .OfType<string>()
            .ToList();
        var aliases = ImportAliases((CompilationUnitSyntax)root);
        foreach (var declaration in VisualBasicLanguage.Instance.Collect(root, owner.RootNamespace).Values)
        {
            var node = declaration.Node;
            if (declaration.MemberName is null)
            {
                var baseNames = BaseTypeNames(node);
                types.Add(new IndexedType(
                    declaration.TypeName,
                    declaration.RuntimeTypeName,
                    owner.Path,
                    owner.IsTestProject,
                    IsRunnableClass(node),
                    HasTestMethods(node),
                    baseNames,
                    CanContainTests: false,
                    VisualBasicLanguage.Attributes(node).Count > 0 || baseNames.Count > 0)
                {
                    Spans = owner.IsTestProject ? [Span(path, root, node)] : [],
                });
            }

            members.Add(new IndexedMember(
                owner.Path,
                owner.IsTestProject,
                declaration.TypeName,
                declaration.MemberName,
                declaration.MemberName is null ? [] : DeclarationWalker.ReferenceNames(declaration.MemberName),
                declaration.MemberName is null || MayBeCalledImplicitly(declaration),
                declaration.MemberName is not null && IsPrivateToType(node),
                declaration.MemberName is not null && IsStaticAccess(node),
                References(node, declaration.Descend, imported, aliases))
            {
                MetadataReferences = References(VisualBasicLanguage.Instance.MetadataNodes(node, declaration.Descend), []),
                IgnoreCase = true,
            });
        }
    }

    /// <summary>Names mentioned below <paramref name="node"/> (as <see cref="DeclarationWalker.Collect"/>'s descend allows), plus <paramref name="extra"/>.</summary>
    /// <param name="node">Declaration node.</param>
    /// <param name="descend">Which child nodes belong to the declaration.</param>
    /// <param name="extra">Names every member of the file may reach.</param>
    /// <param name="aliases">The file's import aliases and the type names their targets hold (<see cref="ImportAliases"/>).</param>
    private static FrozenSet<string> References(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend, List<string> extra, Dictionary<string, List<string>> aliases)
    {
        var references = new HashSet<string>(extra, StringComparer.OrdinalIgnoreCase);
        Add(node.DescendantNodes(n => descend(n, node)), references);
        foreach (var (alias, targets) in aliases)
        {
            if (references.Contains(alias))
            {
                references.UnionWith(targets);
            }
        }

        return references.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file's import aliases (<c>Imports Verifier = CodeFixTest(Of MyAnalyzer, MyFixer)</c>) and the type names in
    /// each target (the last part of a qualified name, and type arguments): a declaration that names the alias uses
    /// them, as with C#'s using aliases. Project-level imports are not in the file and are left as before.
    /// </summary>
    /// <param name="root">Compilation unit.</param>
    private static Dictionary<string, List<string>> ImportAliases(CompilationUnitSyntax root)
    {
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var clause in root.Imports.SelectMany(static i => i.ImportsClauses.OfType<SimpleImportsClauseSyntax>()))
        {
            if (clause.Alias is not { } alias)
            {
                continue;
            }

            var names = clause.Name.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
                .Where(n => NamesType(n, clause.Name))
                .Select(static n => n.Identifier.ValueText)
                .ToList();
            var name = alias.Identifier.ValueText;
            aliases[name] = aliases.TryGetValue(name, out var known) ? [.. known.Union(names, StringComparer.OrdinalIgnoreCase)] : names;
        }

        return aliases;
    }

    /// <summary>True when <paramref name="name"/> in an import target names a type: not a namespace or outer part before a dot.</summary>
    /// <param name="name">A simple name in the target.</param>
    /// <param name="target">The import target.</param>
    private static bool NamesType(SimpleNameSyntax name, SyntaxNode target)
    {
        SyntaxNode child = name;
        for (var parent = name.Parent; parent is not null && child != target; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case TypeArgumentListSyntax:
                    return true;
                case QualifiedNameSyntax qualified when qualified.Left == child:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Names mentioned below <paramref name="nodes"/>.</summary>
    /// <param name="nodes">Nodes to scan.</param>
    /// <param name="extra">Names to start with.</param>
    private static FrozenSet<string> References(IEnumerable<SyntaxNode> nodes, List<string> extra)
    {
        var references = new HashSet<string>(extra, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            Add(node.DescendantNodesAndSelf(), references);
        }

        return references.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds every simple name among <paramref name="nodes"/>: identifiers and generic names where they are
    /// used, attribute names also with the <c>Attribute</c> suffix, and the object of a <c>Handles</c> clause.
    /// </summary>
    /// <param name="nodes">Nodes to scan.</param>
    /// <param name="references">Result; updated in place.</param>
    private static void Add(IEnumerable<SyntaxNode> nodes, HashSet<string> references)
    {
        foreach (var child in nodes)
        {
            switch (child)
            {
                case SimpleNameSyntax name:
                    references.Add(name.Identifier.ValueText);
                    break;
                case AttributeSyntax attribute when Rightmost(attribute.Name) is { } attributeName:
                    references.Add(attributeName + "Attribute");
                    break;
                case WithEventsEventContainerSyntax container:
                    references.Add(container.Identifier.ValueText);
                    break;
            }
        }
    }

    /// <summary>The last identifier of a possibly qualified name.</summary>
    /// <param name="name">A name or type.</param>
    private static string? Rightmost(TypeSyntax name) => name switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// True when code may run the member without naming it: an initializer (it runs in the type
    /// initializer or every constructor), attributes, <c>Overrides</c>, a <c>Handles</c> or
    /// <c>Implements</c> clause (an event or an interface calls it), a <c>Default</c> property (called as
    /// <c>x(i)</c>), a name the language or frameworks call by convention, or a non-private member of a
    /// type that inherits or implements something.
    /// </summary>
    /// <param name="declaration">A member declaration.</param>
    private static bool MayBeCalledImplicitly(Declaration declaration)
    {
        var node = declaration.Node;
        var header = node is EnumMemberDeclarationSyntax ? null : VisualBasicLanguage.Header(node);
        var names = DeclarationWalker.ReferenceNames(declaration.MemberName!);
        var modifiers = VisualBasicLanguage.Modifiers(node);
        if (header is FieldDeclarationSyntax field && field.Declarators.Any(static d => d.Initializer is not null || d.AsClause is AsNewClauseSyntax)
            || header is PropertyStatementSyntax { Initializer: not null } or PropertyStatementSyntax { AsClause: AsNewClauseSyntax })
        {
            return true;
        }

        if (names.Count == 0 || VisualBasicLanguage.Attributes(node).Count > 0
            || modifiers.Any(SyntaxKind.OverridesKeyword) || modifiers.Any(SyntaxKind.DefaultKeyword))
        {
            return true;
        }

        if (header is MethodStatementSyntax { HandlesClause: not null } or MethodStatementSyntax { ImplementsClause: not null }
            or PropertyStatementSyntax { ImplementsClause: not null } or EventStatementSyntax { ImplementsClause: not null })
        {
            return true;
        }

        if (names.Any(static n => SourceIndexBuilder.ImplicitlyCalledNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        return node.Parent is TypeBlockSyntax type
               && (type is InterfaceBlockSyntax || ((type.Inherits.Count > 0 || type.Implements.Count > 0) && !IsPrivate(node)));
    }

    /// <summary>True for a private member of a class, structure or module.</summary>
    /// <param name="node">A member node.</param>
    private static bool IsPrivateToType(SyntaxNode node) =>
        node.Parent is ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax && IsPrivate(node);

    /// <summary>
    /// True when the member is private: <c>Private</c>, or a field declared with <c>Dim</c> alone in a
    /// class or module (Visual Basic's default there; methods, properties and events default to public).
    /// </summary>
    /// <param name="node">A member node.</param>
    private static bool IsPrivate(SyntaxNode node)
    {
        var modifiers = VisualBasicLanguage.Modifiers(node);
        if (modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            return true;
        }

        var access = modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword) || modifiers.Any(SyntaxKind.FriendKeyword);
        return node is FieldDeclarationSyntax && !access && node.Parent is ClassBlockSyntax or ModuleBlockSyntax;
    }

    /// <summary>
    /// True for members other code reaches through the type's name: <c>Shared</c> members and constants
    /// of classes and structures, and enum members. A module's members are reachable without its name
    /// (and extension methods are called like instance members), so they count as instance-like here.
    /// </summary>
    /// <param name="node">A member node.</param>
    private static bool IsStaticAccess(SyntaxNode node)
    {
        if (node is EnumMemberDeclarationSyntax)
        {
            return true;
        }

        if (node.Parent is not (ClassBlockSyntax or StructureBlockSyntax))
        {
            return false;
        }

        var modifiers = VisualBasicLanguage.Modifiers(node);
        if (node is FieldDeclarationSyntax && modifiers.Any(SyntaxKind.ConstKeyword))
        {
            return true;
        }

        return VisualBasicLanguage.Shared(modifiers)
               && !VisualBasicLanguage.Attributes(node).SelectMany(static l => l.Attributes).Any(static a => Rightmost(a.Name) is "Extension" or "ExtensionAttribute");
    }

    /// <summary>True when a method directly inside the type has a test attribute.</summary>
    /// <param name="node">Type node.</param>
    private static bool HasTestMethods(SyntaxNode node) =>
        node is TypeBlockSyntax type
        && type.Members.OfType<MethodBlockSyntax>().Any(static m => m.SubOrFunctionStatement.AttributeLists
            .SelectMany(static l => l.Attributes)
            .Any(static a => Rightmost(a.Name) is { } name && SourceIndexBuilder.IsTestAttribute(name)));

    /// <summary>Simple names of the <c>Inherits</c> and <c>Implements</c> types.</summary>
    /// <param name="node">Type node.</param>
    private static List<string> BaseTypeNames(SyntaxNode node) =>
        node is TypeBlockSyntax type
            ? [.. type.Inherits.SelectMany(static i => i.Types).Concat(type.Implements.SelectMany(static i => i.Types))
                .Select(static t => Rightmost(t))
                .OfType<string>()]
            : [];

    /// <summary>A class that is not <c>MustInherit</c>, or a module (its test methods are shared).</summary>
    /// <param name="node">Type node.</param>
    private static bool IsRunnableClass(SyntaxNode node) =>
        node is ModuleBlockSyntax
        || node is ClassBlockSyntax @class && !@class.ClassStatement.Modifiers.Any(SyntaxKind.MustInheritKeyword);

    /// <summary>Lines of <paramref name="node"/>, 1-based.</summary>
    /// <param name="path">File path.</param>
    /// <param name="root">The parse.</param>
    /// <param name="node">A type node.</param>
    private static DeclarationSpan Span(string path, SyntaxNode root, SyntaxNode node)
    {
        var lines = root.SyntaxTree.GetLineSpan(node.Span);
        return new DeclarationSpan(path, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1);
    }
}
