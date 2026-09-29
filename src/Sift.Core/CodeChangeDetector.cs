using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Compares two versions of a C# file by syntax and reports which types and members changed.
/// Only tokens and preprocessor trivia are compared, so whitespace and comment edits do not count.
/// No compilation is created: this is fast but works on names, not resolved symbols.
/// </summary>
/// <param name="logger">Receives one debug line per changed symbol.</param>
public sealed partial class CodeChangeDetector(ILogger<CodeChangeDetector> logger)
{
    /// <summary>
    /// Preview language version so that any syntax the SDK accepts parses; doc comments are read as
    /// plain comments because they never influence behaviour.
    /// </summary>
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithDocumentationMode(DocumentationMode.None);

    /// <summary>Name used for the type the compiler generates for top-level statements.</summary>
    private const string TopLevelProgramType = "Program";

    /// <summary>Compares the two versions of <paramref name="path"/>.</summary>
    /// <param name="path">File path relative to the repository root; only used in the result and logs.</param>
    /// <param name="oldText">Content at the base revision, or null when the file was added.</param>
    /// <param name="newText">Content in the working tree, or null when the file was deleted.</param>
    public CodeChange Detect(string path, SourceText? oldText, SourceText? newText)
    {
        var oldRoot = Parse(oldText);
        var newRoot = Parse(newText);

        if (SignificantEquals(oldRoot, newRoot, DescendAll))
        {
            LogCosmetic(path);
            return new CodeChange(path, IsCosmetic: true, AffectsWholeProject: false, []);
        }

        var affectsWholeProject = !SignificantEquals(ProjectWideNodes(oldRoot), ProjectWideNodes(newRoot));
        var symbols = new List<SymbolChange>();
        var oldDeclarations = Collect(oldRoot);
        var newDeclarations = Collect(newRoot);

        // Types first: a type that only exists on one side is reported once, without its members.
        var wholeTypeChanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, declaration) in oldDeclarations)
        {
            if (declaration.MemberName is null && !newDeclarations.ContainsKey(key))
            {
                wholeTypeChanges.Add(declaration.TypeName);
                symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Removed));
            }
        }

        foreach (var (key, declaration) in newDeclarations)
        {
            if (declaration.MemberName is null && !oldDeclarations.ContainsKey(key))
            {
                wholeTypeChanges.Add(declaration.TypeName);
                symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Added));
            }
        }

        foreach (var (key, oldDeclaration) in oldDeclarations)
        {
            if (wholeTypeChanges.Contains(oldDeclaration.TypeName))
            {
                continue;
            }

            if (!newDeclarations.TryGetValue(key, out var newDeclaration))
            {
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Removed));
                continue;
            }

            if (!SignificantEquals(oldDeclaration.Node, newDeclaration.Node, oldDeclaration.Descend))
            {
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Modified));
            }
        }

        foreach (var (key, newDeclaration) in newDeclarations)
        {
            if (!wholeTypeChanges.Contains(newDeclaration.TypeName) && !oldDeclarations.ContainsKey(key))
            {
                symbols.Add(new SymbolChange(newDeclaration.TypeName, newDeclaration.MemberName, SymbolChangeKind.Added));
            }
        }

        // Usings, namespace lines and top-level statements belong to no declaration. When they change,
        // every type in the file may bind differently, so all of them count as modified.
        if (!SignificantEquals(oldRoot, newRoot, DescendFileShell))
        {
            AddFileLevelChanges(oldRoot, newRoot, newDeclarations, wholeTypeChanges, symbols);
        }

        foreach (var symbol in symbols)
        {
            LogSymbol(path, symbol.Kind, symbol.TypeName, symbol.MemberName ?? "(type)");
        }

        LogDetected(path, symbols.Count, affectsWholeProject);
        return new CodeChange(path, IsCosmetic: false, affectsWholeProject, symbols);
    }

    /// <summary>
    /// Adds a type-level <see cref="SymbolChangeKind.Modified"/> entry for every type declared in the
    /// file that is not already reported as a whole, plus the generated program type when the file
    /// has top-level statements.
    /// </summary>
    /// <param name="oldRoot">Old compilation unit.</param>
    /// <param name="newRoot">New compilation unit.</param>
    /// <param name="newDeclarations">Declarations of the new version.</param>
    /// <param name="wholeTypeChanges">Types already reported as added or removed.</param>
    /// <param name="symbols">Result list; updated in place.</param>
    private static void AddFileLevelChanges(
        CompilationUnitSyntax oldRoot,
        CompilationUnitSyntax newRoot,
        Dictionary<string, Declaration> newDeclarations,
        HashSet<string> wholeTypeChanges,
        List<SymbolChange> symbols)
    {
        var alreadyModified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol is { MemberName: null, Kind: SymbolChangeKind.Modified })
            {
                alreadyModified.Add(symbol.TypeName);
            }
        }

        foreach (var declaration in newDeclarations.Values)
        {
            if (declaration.MemberName is not null
                || wholeTypeChanges.Contains(declaration.TypeName)
                || !alreadyModified.Add(declaration.TypeName))
            {
                continue;
            }

            symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Modified));
        }

        var hasTopLevelStatements =
            oldRoot.Members.Any(static m => m is GlobalStatementSyntax) ||
            newRoot.Members.Any(static m => m is GlobalStatementSyntax);

        if (hasTopLevelStatements && alreadyModified.Add(TopLevelProgramType))
        {
            symbols.Add(new SymbolChange(TopLevelProgramType, null, SymbolChangeKind.Modified));
        }
    }

    /// <summary>Parses <paramref name="text"/>; a missing file parses as an empty compilation unit.</summary>
    /// <param name="text">File content, or null.</param>
    private static CompilationUnitSyntax Parse(SourceText? text) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text ?? SourceText.From(string.Empty), ParseOptions).GetRoot();

    /// <summary>The <c>global using</c> directives and assembly/module attributes, which apply project-wide.</summary>
    /// <param name="root">Compilation unit.</param>
    private static IEnumerable<SyntaxNode> ProjectWideNodes(CompilationUnitSyntax root)
    {
        foreach (var directive in root.Usings)
        {
            if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            {
                yield return directive;
            }
        }

        foreach (var attributes in root.AttributeLists)
        {
            yield return attributes;
        }
    }

    /// <summary>A type or member declaration and how to compare it.</summary>
    /// <param name="TypeName">Qualified name of the type, or of the containing type for a member.</param>
    /// <param name="MemberName">Member signature, or null for the type declaration itself.</param>
    /// <param name="Node">The declaration node.</param>
    /// <param name="Descend">
    /// Which child nodes the comparison looks into, given the node and the declaration being
    /// compared. For a type this skips its members, which are compared on their own; for a member it
    /// is everything.
    /// </param>
    private sealed record Declaration(
        string TypeName,
        string? MemberName,
        SyntaxNode Node,
        Func<SyntaxNode, SyntaxNode, bool> Descend);

    /// <summary>Collects every type, delegate and member declaration of a file, keyed by identity.</summary>
    /// <param name="root">Compilation unit.</param>
    private static Dictionary<string, Declaration> Collect(CompilationUnitSyntax root)
    {
        var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        Visit(root, containingType: null, currentNamespace: string.Empty, result);
        return result;
    }

    /// <summary>Recursively collects declarations below <paramref name="container"/>.</summary>
    /// <param name="container">Compilation unit, namespace or type.</param>
    /// <param name="containingType">Qualified name of the enclosing type, or null outside types.</param>
    /// <param name="currentNamespace">Enclosing namespace, empty for the global namespace.</param>
    /// <param name="result">Collected declarations; updated in place.</param>
    private static void Visit(
        SyntaxNode container,
        string? containingType,
        string currentNamespace,
        Dictionary<string, Declaration> result)
    {
        foreach (var child in container.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    Visit(ns, containingType: null, Qualify(currentNamespace, ns.Name.ToString()), result);
                    break;

                case BaseTypeDeclarationSyntax type:
                    var typeName = Qualify(containingType ?? currentNamespace, TypeIdentity(type));
                    Add(result, typeName, new Declaration(typeName, null, type, DescendTypeShell));
                    Visit(type, typeName, currentNamespace, result);
                    break;

                case DelegateDeclarationSyntax @delegate:
                    var delegateName = Qualify(
                        containingType ?? currentNamespace,
                        @delegate.Identifier.ValueText + Arity(@delegate.TypeParameterList));
                    Add(result, delegateName, new Declaration(delegateName, null, @delegate, DescendAll));
                    break;

                case MemberDeclarationSyntax member when containingType is not null:
                    var memberName = MemberIdentity(member);
                    Add(result, $"{containingType}::{memberName}", new Declaration(containingType, memberName, member, DescendAll));
                    break;
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="declaration"/> under <paramref name="key"/>, numbering duplicates such as
    /// two parts of a partial type in the same file. Both versions number in source order, so the
    /// same duplicate gets the same key on both sides.
    /// </summary>
    /// <param name="result">Collected declarations.</param>
    /// <param name="key">Identity of the declaration.</param>
    /// <param name="declaration">The declaration to add.</param>
    private static void Add(Dictionary<string, Declaration> result, string key, Declaration declaration)
    {
        var unique = key;
        for (var n = 2; !result.TryAdd(unique, declaration); n++)
        {
            unique = $"{key}#{n}";
        }
    }

    /// <summary>Type name with generic arity, such as <c>Box`1</c>.</summary>
    /// <param name="type">Type declaration.</param>
    private static string TypeIdentity(BaseTypeDeclarationSyntax type) =>
        type.Identifier.ValueText + (type is TypeDeclarationSyntax t ? Arity(t.TypeParameterList) : string.Empty);

    /// <summary>Signature that tells overloads apart: name, generic arity and parameter types.</summary>
    /// <param name="member">Member declaration inside a type.</param>
    private static string MemberIdentity(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m =>
            $"{Explicit(m.ExplicitInterfaceSpecifier)}{m.Identifier.ValueText}{Arity(m.TypeParameterList)}({Parameters(m.ParameterList)})",
        ConstructorDeclarationSyntax c =>
            $"{(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}({Parameters(c.ParameterList)})",
        DestructorDeclarationSyntax => "Finalize()",
        PropertyDeclarationSyntax p => Explicit(p.ExplicitInterfaceSpecifier) + p.Identifier.ValueText,
        IndexerDeclarationSyntax i => $"{Explicit(i.ExplicitInterfaceSpecifier)}this[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => Explicit(e.ExplicitInterfaceSpecifier) + e.Identifier.ValueText,
        BaseFieldDeclarationSyntax f => string.Join(',', f.Declaration.Variables.Select(static v => v.Identifier.ValueText)),
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}({Parameters(o.ParameterList)})",
        ConversionOperatorDeclarationSyntax c =>
            $"operator {c.ImplicitOrExplicitKeyword.Text} {Compact(c.Type)}({Parameters(c.ParameterList)})",
        EnumMemberDeclarationSyntax e => e.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };

    /// <summary><c>`n</c> for a generic declaration, empty otherwise.</summary>
    /// <param name="parameters">Type parameter list, or null.</param>
    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is { Parameters.Count: > 0 } ? $"`{parameters.Parameters.Count}" : string.Empty;

    /// <summary>Interface prefix of an explicit implementation, such as <c>IDisposable.</c>.</summary>
    /// <param name="specifier">Explicit interface specifier, or null.</param>
    private static string Explicit(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier is null ? string.Empty : Compact(specifier.Name) + ".";

    /// <summary>Comma-separated parameter types including <c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c>.</summary>
    /// <param name="list">Parameter list.</param>
    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(',', list.Parameters.Select(static p =>
            p.Modifiers.Count == 0 ? Compact(p.Type) : $"{p.Modifiers.ToString()} {Compact(p.Type)}"));

    /// <summary>Source text of <paramref name="node"/> without any whitespace, so formatting does not change identities.</summary>
    /// <param name="node">Type syntax or name, or null.</param>
    private static string Compact(SyntaxNode? node) =>
        node is null ? string.Empty : string.Concat(node.DescendantTokens().Select(static t => t.Text));

    /// <summary>Joins a namespace or type with a nested name.</summary>
    /// <param name="prefix">Enclosing namespace or type; empty for the global namespace.</param>
    /// <param name="name">Nested name.</param>
    private static string Qualify(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";

    /// <summary>Compares everything below a node.</summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="root">Node being compared.</param>
    private static bool DescendAll(SyntaxNode node, SyntaxNode root) => true;

    /// <summary>Compares a type's own declaration but not its members, which are compared on their own.</summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="type">The type being compared.</param>
    /// <remarks>Takes the type as an argument so one delegate works for the old and the new tree.</remarks>
    private static bool DescendTypeShell(SyntaxNode node, SyntaxNode type) =>
        node == type || node.Parent != type || node is not MemberDeclarationSyntax;

    /// <summary>
    /// Compares what lies outside every type and delegate: usings, namespace lines and top-level statements.
    /// </summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="root">Compilation unit being compared.</param>
    private static bool DescendFileShell(SyntaxNode node, SyntaxNode root) =>
        node is not MemberDeclarationSyntax or BaseNamespaceDeclarationSyntax or GlobalStatementSyntax;

    /// <summary>True when both nodes have the same tokens and preprocessor trivia in the same order.</summary>
    /// <param name="left">First node.</param>
    /// <param name="right">Second node.</param>
    /// <param name="descend">Which child nodes to compare.</param>
    private static bool SignificantEquals(SyntaxNode left, SyntaxNode right, Func<SyntaxNode, SyntaxNode, bool> descend) =>
        SignificantText(left, descend).SequenceEqual(SignificantText(right, descend), StringComparer.Ordinal);

    /// <summary>True when both node sequences have the same tokens and preprocessor trivia in the same order.</summary>
    /// <param name="left">First sequence.</param>
    /// <param name="right">Second sequence.</param>
    private static bool SignificantEquals(IEnumerable<SyntaxNode> left, IEnumerable<SyntaxNode> right) =>
        left.SelectMany(static n => SignificantText(n, DescendAll))
            .SequenceEqual(right.SelectMany(static n => SignificantText(n, DescendAll)), StringComparer.Ordinal);

    /// <summary>
    /// Token texts of <paramref name="node"/> interleaved with its preprocessor directives and
    /// disabled code. Whitespace and comments are skipped. Yielded lazily so a comparison stops at
    /// the first difference.
    /// </summary>
    /// <param name="node">Node to read.</param>
    /// <param name="descend">Which child nodes to read, given the candidate and <paramref name="node"/>.</param>
    private static IEnumerable<string> SignificantText(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        foreach (var token in node.DescendantTokens(child => descend(child, node)))
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (IsSignificant(trivia))
                {
                    yield return trivia.ToFullString();
                }
            }

            yield return token.Text;

            foreach (var trivia in token.TrailingTrivia)
            {
                if (IsSignificant(trivia))
                {
                    yield return trivia.ToFullString();
                }
            }
        }
    }

    /// <summary>
    /// Directives (<c>#if</c>, <c>#define</c>, <c>#pragma</c>, ...) and code disabled by them change
    /// what gets compiled, unlike whitespace and comments.
    /// </summary>
    /// <param name="trivia">Trivia attached to a token.</param>
    private static bool IsSignificant(SyntaxTrivia trivia) =>
        trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path}: only whitespace or comments changed")]
    private partial void LogCosmetic(string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path}: {Kind} {TypeName} {MemberName}")]
    private partial void LogSymbol(string path, SymbolChangeKind kind, string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Path}: {Count} changed symbol(s), affects whole project: {AffectsWholeProject}")]
    private partial void LogDetected(string path, int count, bool affectsWholeProject);
}
