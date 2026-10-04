using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>
/// Compares two versions of a C# file by syntax and reports which types and members changed.
/// Only tokens and preprocessor trivia are compared, so whitespace and comment edits do not count.
/// No compilation is created: this is fast but works on names, not resolved symbols.
/// </summary>
/// <param name="logger">Receives one debug line per changed symbol.</param>
public sealed partial class CodeChangeDetector(ILogger<CodeChangeDetector> logger)
{
    /// <summary>Compares the two versions of <paramref name="path"/>.</summary>
    /// <param name="path">File path relative to the repository root; only used in the result and logs.</param>
    /// <param name="oldText">Content at the base revision, or null when the file was added.</param>
    /// <param name="newText">Content in the working tree, or null when the file was deleted.</param>
    public CodeChange Detect(string path, SourceText? oldText, SourceText? newText) => Detect(path, oldText, newText, []);

    /// <summary>
    /// Compares the two versions of <paramref name="path"/>, also under each symbol set the compiler
    /// really uses for it (its projects' target frameworks), so code under a mix of symbols counts.
    /// </summary>
    /// <param name="path">File path relative to the repository root; only used in the result and logs.</param>
    /// <param name="oldText">Content at the base revision, or null when the file was added.</param>
    /// <param name="newText">Content in the working tree, or null when the file was deleted.</param>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file; empty when unknown.</param>
    public CodeChange Detect(string path, SourceText? oldText, SourceText? newText, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var oldRoot = DeclarationWalker.Parse(oldText);
        var newRoot = DeclarationWalker.Parse(newText);

        if (SignificantEquals(oldRoot, newRoot, DeclarationWalker.DescendAll))
        {
            LogCosmetic(path);
            return new CodeChange(path, IsCosmetic: true, AffectsWholeProject: false, []);
        }

        var (affectsWholeProject, symbols) = Compare(oldRoot, newRoot);

        // Code disabled without symbols, such as a file wrapped in #if NET8_0_OR_GREATER, only shows
        // its declarations when the symbols are defined. Both views are merged.
        var conditionalSymbols = DeclarationWalker.ConditionalSymbols(oldRoot, newRoot);
        if (conditionalSymbols.Count > 0)
        {
            // All named symbols, then each set the compiler uses, which enters code under a mix.
            List<IReadOnlyList<string>> views = [conditionalSymbols, .. DeclarationWalker.RelevantSets(conditionalSymbols, symbolSets)];
            foreach (var defined in views)
            {
                var (definedAffects, definedSymbols) = Compare(
                    DeclarationWalker.Parse(oldText, defined),
                    DeclarationWalker.Parse(newText, defined));
                affectsWholeProject |= definedAffects;
                foreach (var symbol in definedSymbols)
                {
                    var same = symbols.FindIndex(s => s with { MetadataChanged = false } == symbol with { MetadataChanged = false });
                    if (same < 0)
                    {
                        symbols.Add(symbol);
                    }
                    else if (symbol.MetadataChanged)
                    {
                        // Metadata changed under one of the views: the change counts as one.
                        symbols[same] = symbol;
                    }
                }
            }
        }

        // Something that compiles differently changed, but no declaration explains it: code active
        // only under a mix of symbols, for example. The whole project counts as changed.
        if (symbols.Count == 0 && !affectsWholeProject)
        {
            LogUnattributed(path);
            affectsWholeProject = true;
        }

        foreach (var symbol in symbols)
        {
            LogSymbol(path, symbol.Kind, symbol.TypeName, symbol.MemberName ?? "(type)");
        }

        LogDetected(path, symbols.Count, affectsWholeProject);
        return new CodeChange(path, IsCosmetic: false, affectsWholeProject, symbols);
    }

    /// <summary>
    /// Changed symbols between two parses of a file made with the same preprocessor symbols, and
    /// whether project-wide declarations (global usings, assembly attributes) changed.
    /// </summary>
    /// <param name="oldRoot">Old compilation unit.</param>
    /// <param name="newRoot">New compilation unit.</param>
    private static (bool AffectsWholeProject, List<SymbolChange> Symbols) Compare(CompilationUnitSyntax oldRoot, CompilationUnitSyntax newRoot)
    {
        var affectsWholeProject = !SignificantEquals(ProjectWideNodes(oldRoot), ProjectWideNodes(newRoot));
        var symbols = new List<SymbolChange>();
        var oldDeclarations = DeclarationWalker.Collect(oldRoot);
        var newDeclarations = DeclarationWalker.Collect(newRoot);

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
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Modified)
                {
                    MetadataChanged = !SignificantEquals(
                        DeclarationWalker.MetadataNodes(oldDeclaration.Node, oldDeclaration.Descend),
                        DeclarationWalker.MetadataNodes(newDeclaration.Node, newDeclaration.Descend)),
                });
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

        return (affectsWholeProject, symbols);
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

            // A changed using can make an attribute name bind to another type: metadata may have changed.
            symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Modified) { MetadataChanged = true });
        }

        var hasTopLevelStatements =
            oldRoot.Members.Any(static m => m is GlobalStatementSyntax) ||
            newRoot.Members.Any(static m => m is GlobalStatementSyntax);

        if (hasTopLevelStatements && alreadyModified.Add(DeclarationWalker.TopLevelProgramType))
        {
            symbols.Add(new SymbolChange(DeclarationWalker.TopLevelProgramType, null, SymbolChangeKind.Modified));
        }
    }

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
        left.SelectMany(static n => SignificantText(n, DeclarationWalker.DescendAll))
            .SequenceEqual(right.SelectMany(static n => SignificantText(n, DeclarationWalker.DescendAll)), StringComparer.Ordinal);

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
        Message = "{Path}: the change is in no declaration TestDetta can see (conditional code?); the whole project counts as changed")]
    private partial void LogUnattributed(string path);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{Path}: {Count} changed symbol(s), affects whole project: {AffectsWholeProject}")]
    private partial void LogDetected(string path, int count, bool affectsWholeProject);
}
