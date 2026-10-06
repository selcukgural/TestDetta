using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TestDetta.Core;

/// <summary>
/// What the change detector and the source index need from a language's syntax: parsing with
/// preprocessor symbols, the declarations of a file with stable identities, and which parts of a file
/// apply to the whole project, to the whole file, or are metadata. C# and Visual Basic implement it
/// (docs/design/vb-fsharp-support.md); everything after it works on names and is language-independent.
/// </summary>
internal abstract class SourceLanguage
{
    /// <summary>The language of a source file, by its extension: <c>.vb</c> is Visual Basic, anything else C#.</summary>
    /// <param name="path">File path.</param>
    public static SourceLanguage For(string path) =>
        path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? VisualBasicLanguage.Instance : CSharpLanguage.Instance;

    /// <summary>True for a source file TestDetta reads: <c>.cs</c>, <c>.vb</c> or <c>.fs</c> (F# goes through <see cref="FSharpSource"/>).</summary>
    /// <param name="path">File path.</param>
    public static bool IsSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) || FSharpSource.IsSource(path);

    /// <summary>True when identifiers differ only by case are the same name (Visual Basic).</summary>
    public abstract bool IgnoresCase { get; }

    /// <summary>Parses <paramref name="text"/> with the given preprocessor symbols defined; null parses as an empty file.</summary>
    /// <param name="text">File content, or null.</param>
    /// <param name="symbols">Symbols to define.</param>
    public abstract SyntaxNode Parse(SourceText? text, IReadOnlyCollection<string> symbols);

    /// <summary>Every symbol named in a conditional directive of the given trees, sorted.</summary>
    /// <param name="roots">Parsed trees.</param>
    public abstract List<string> ConditionalSymbols(params ReadOnlySpan<SyntaxNode> roots);

    /// <summary>The declarations of a file, keyed by identity (see <see cref="DeclarationWalker.Collect"/>).</summary>
    /// <param name="root">Parsed file.</param>
    /// <param name="rootNamespace">The project's root namespace, which Visual Basic puts before every namespace; C# ignores it.</param>
    public abstract Dictionary<string, Declaration> Collect(SyntaxNode root, string rootNamespace);

    /// <summary>Declarations that apply to the whole project: global usings, assembly and module attributes.</summary>
    /// <param name="root">Parsed file.</param>
    public abstract IEnumerable<SyntaxNode> ProjectWideNodes(SyntaxNode root);

    /// <summary>True for a node outside every type: imports, namespace lines, options, top-level statements.</summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="root">The file being compared.</param>
    public abstract bool DescendFileShell(SyntaxNode node, SyntaxNode root);

    /// <summary>The parts of a declaration stored as metadata: attributes and parameter default values.</summary>
    /// <param name="node">Declaration node.</param>
    /// <param name="descend">Which child nodes belong to the declaration.</param>
    public abstract IEnumerable<SyntaxNode> MetadataNodes(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend);

    /// <summary>True for trivia that changes what compiles: directives and code they disable.</summary>
    /// <param name="trivia">Trivia attached to a token.</param>
    public abstract bool IsSignificant(SyntaxTrivia trivia);

    /// <summary>True when the file has top-level statements (C# only).</summary>
    /// <param name="root">Parsed file.</param>
    public abstract bool HasTopLevelStatements(SyntaxNode root);

    /// <summary>
    /// The constructor the field or property initializers of <paramref name="member"/> compile into
    /// (<c>.cctor</c> for static ones, <c>.ctor</c> for instance ones) and the initializer nodes; null when
    /// the member has none (a constant has none: its value is compiled into its users).
    /// </summary>
    /// <param name="member">Member declaration node.</param>
    public virtual (string Constructor, IReadOnlyList<SyntaxNode> Initializers)? Initializer(SyntaxNode member) => null;

    /// <summary>
    /// The file parsed with no symbols, with all the symbols its conditions name, and with each symbol set
    /// the compiler uses reduced to those (see <see cref="DeclarationWalker.ParseVariants(SourceText?, IReadOnlyCollection{IReadOnlyList{string}})"/>).
    /// </summary>
    /// <param name="text">File content, or null.</param>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file; empty when unknown.</param>
    public List<SyntaxNode> ParseVariants(SourceText? text, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var root = Parse(text, []);
        var named = ConditionalSymbols(root);
        if (named.Count == 0)
        {
            return [root];
        }

        var roots = new List<SyntaxNode> { root, Parse(text, named) };
        foreach (var relevant in DeclarationWalker.RelevantSets(named, symbolSets))
        {
            roots.Add(Parse(text, relevant));
        }

        return roots;
    }
}

/// <summary>C#: the rules <see cref="DeclarationWalker"/> and the change detector always had.</summary>
internal sealed class CSharpLanguage : SourceLanguage
{
    /// <summary>The one instance.</summary>
    public static readonly CSharpLanguage Instance = new();

    /// <inheritdoc/>
    public override bool IgnoresCase => false;

    /// <inheritdoc/>
    public override SyntaxNode Parse(SourceText? text, IReadOnlyCollection<string> symbols) => DeclarationWalker.Parse(text, symbols);

    /// <inheritdoc/>
    public override List<string> ConditionalSymbols(params ReadOnlySpan<SyntaxNode> roots)
    {
        var units = new CompilationUnitSyntax[roots.Length];
        for (var i = 0; i < roots.Length; i++)
        {
            units[i] = (CompilationUnitSyntax)roots[i];
        }

        return DeclarationWalker.ConditionalSymbols(units);
    }

    /// <inheritdoc/>
    public override Dictionary<string, Declaration> Collect(SyntaxNode root, string rootNamespace) =>
        DeclarationWalker.Collect((CompilationUnitSyntax)root);

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> ProjectWideNodes(SyntaxNode root)
    {
        var unit = (CompilationUnitSyntax)root;
        foreach (var directive in unit.Usings)
        {
            if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            {
                yield return directive;
            }
        }

        foreach (var attributes in unit.AttributeLists)
        {
            yield return attributes;
        }
    }

    /// <inheritdoc/>
    public override bool DescendFileShell(SyntaxNode node, SyntaxNode root) =>
        node is not MemberDeclarationSyntax or BaseNamespaceDeclarationSyntax or GlobalStatementSyntax;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> MetadataNodes(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend) =>
        DeclarationWalker.MetadataNodes(node, descend);

    /// <inheritdoc/>
    public override bool IsSignificant(SyntaxTrivia trivia) =>
        trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia);

    /// <inheritdoc/>
    public override bool HasTopLevelStatements(SyntaxNode root) =>
        ((CompilationUnitSyntax)root).Members.Any(static m => m is GlobalStatementSyntax);

    /// <inheritdoc/>
    public override (string Constructor, IReadOnlyList<SyntaxNode> Initializers)? Initializer(SyntaxNode member) => member switch
    {
        BaseFieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword) && field.Declaration.Variables.Any(static v => v.Initializer is not null) =>
            (field.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor", [.. field.Declaration.Variables.Select(static v => v.Initializer).OfType<SyntaxNode>()]),
        PropertyDeclarationSyntax { Initializer: { } initializer } property => (property.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor", [initializer]),
        _ => null,
    };
}
