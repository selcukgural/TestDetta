using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>
/// Compares two versions of a C#, Visual Basic (<see cref="SourceLanguage"/>) or F# (<see cref="FSharpChangeDetector"/>) file by syntax and reports which types and members changed.
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
    /// <param name="rootNamespace">Root namespace of the project compiling the file, which Visual Basic puts before its namespaces.</param>
    public CodeChange Detect(string path, SourceText? oldText, SourceText? newText, IReadOnlyCollection<IReadOnlyList<string>> symbolSets, string rootNamespace = "")
    {
        if (FSharpSource.IsSource(path))
        {
            return DetectFSharp(path, oldText, newText, symbolSets);
        }

        var language = SourceLanguage.For(path);
        var oldRoot = language.Parse(oldText, []);
        var newRoot = language.Parse(newText, []);

        if (SignificantEquals(language, oldRoot, newRoot, DeclarationWalker.DescendAll))
        {
            LogCosmetic(path);
            return new CodeChange(path, IsCosmetic: true, AffectsWholeProject: false, []);
        }

        var (affectsWholeProject, symbols) = Compare(language, oldRoot, newRoot, rootNamespace);

        // Code disabled without symbols, such as a file wrapped in #if NET8_0_OR_GREATER, only shows
        // its declarations when the symbols are defined. Both views are merged.
        var conditionalSymbols = language.ConditionalSymbols(oldRoot, newRoot);
        if (conditionalSymbols.Count > 0)
        {
            // All named symbols, then each set the compiler uses, which enters code under a mix.
            List<IReadOnlyList<string>> views = [conditionalSymbols, .. DeclarationWalker.RelevantSets(conditionalSymbols, symbolSets)];
            foreach (var defined in views)
            {
                var (definedAffects, definedSymbols) = Compare(
                    language,
                    language.Parse(oldText, defined),
                    language.Parse(newText, defined),
                    rootNamespace);
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

    /// <summary>Compares two versions of an F# file (<see cref="FSharpChangeDetector"/>).</summary>
    /// <param name="path">File path relative to the repository root.</param>
    /// <param name="oldText">Content at the base revision, or null when the file was added.</param>
    /// <param name="newText">Content in the working tree, or null when the file was deleted.</param>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file.</param>
    private CodeChange DetectFSharp(string path, SourceText? oldText, SourceText? newText, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var (cosmetic, affectsWholeProject, symbols) = FSharpChangeDetector.Detect(path, oldText?.ToString(), newText?.ToString(), symbolSets);
        if (cosmetic)
        {
            LogCosmetic(path);
            return new CodeChange(path, IsCosmetic: true, AffectsWholeProject: false, []);
        }

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
    /// <param name="language">The file's language.</param>
    /// <param name="oldRoot">Old compilation unit.</param>
    /// <param name="newRoot">New compilation unit.</param>
    /// <param name="rootNamespace">Root namespace of the project (Visual Basic).</param>
    private static (bool AffectsWholeProject, List<SymbolChange> Symbols) Compare(SourceLanguage language, SyntaxNode oldRoot, SyntaxNode newRoot, string rootNamespace)
    {
        var affectsWholeProject = !SignificantEquals(language, language.ProjectWideNodes(oldRoot), language.ProjectWideNodes(newRoot));
        var symbols = new List<SymbolChange>();
        var oldDeclarations = language.Collect(oldRoot, rootNamespace);
        var newDeclarations = language.Collect(newRoot, rootNamespace);

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

        var initializers = new HashSet<(string TypeName, string Constructor)>();
        void Initializer(Declaration declaration)
        {
            if (declaration.MemberName is not null && language.Initializer(declaration.Node) is var (constructor, _))
            {
                initializers.Add((declaration.TypeName, constructor));
            }
        }

        // A modified member changes its constructor when its initializers did, not for an attribute.
        void InitializerChange(Declaration oldDeclaration, Declaration newDeclaration)
        {
            var before = language.Initializer(oldDeclaration.Node);
            var after = language.Initializer(newDeclaration.Node);
            if (before?.Constructor != after?.Constructor || before is { } b && after is { } a && !SignificantEquals(language, b.Initializers, a.Initializers))
            {
                Initializer(oldDeclaration);
                Initializer(newDeclaration);
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
                Initializer(oldDeclaration);
                continue;
            }

            if (!SignificantEquals(language, oldDeclaration.Node, newDeclaration.Node, oldDeclaration.Descend))
            {
                InitializerChange(oldDeclaration, newDeclaration);
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Modified)
                {
                    MetadataChanged = !SignificantEquals(
                        language,
                        language.MetadataNodes(oldDeclaration.Node, oldDeclaration.Descend),
                        language.MetadataNodes(newDeclaration.Node, newDeclaration.Descend)),
                });
            }
        }

        foreach (var (key, newDeclaration) in newDeclarations)
        {
            if (!wholeTypeChanges.Contains(newDeclaration.TypeName) && !oldDeclarations.ContainsKey(key))
            {
                symbols.Add(new SymbolChange(newDeclaration.TypeName, newDeclaration.MemberName, SymbolChangeKind.Added));
                Initializer(newDeclaration);
            }
        }

        // A field or property initializer compiles into the static constructor or every instance
        // constructor, so changing, adding or removing one changes that constructor: a map credits the
        // classes that ran the type (a static field read through reflection runs no member of its own).
        foreach (var (typeName, constructor) in initializers)
        {
            if (!symbols.Any(c => c.TypeName == typeName && c.MemberName == constructor))
            {
                symbols.Add(new SymbolChange(typeName, constructor, SymbolChangeKind.Modified));
            }
        }

        // Usings, namespace lines and top-level statements belong to no declaration. When they change,
        // every type in the file may bind differently, so all of them count as modified.
        if (!SignificantEquals(language, oldRoot, newRoot, language.DescendFileShell))
        {
            AddFileLevelChanges(language, oldRoot, newRoot, newDeclarations, wholeTypeChanges, symbols);
        }

        return (affectsWholeProject, symbols);
    }

    /// <summary>
    /// Adds a type-level <see cref="SymbolChangeKind.Modified"/> entry for every type declared in the
    /// file that is not already reported as a whole, plus the generated program type when the file
    /// has top-level statements.
    /// </summary>
    /// <param name="language">The file's language.</param>
    /// <param name="oldRoot">Old compilation unit.</param>
    /// <param name="newRoot">New compilation unit.</param>
    /// <param name="newDeclarations">Declarations of the new version.</param>
    /// <param name="wholeTypeChanges">Types already reported as added or removed.</param>
    /// <param name="symbols">Result list; updated in place.</param>
    private static void AddFileLevelChanges(
        SourceLanguage language,
        SyntaxNode oldRoot,
        SyntaxNode newRoot,
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

        var hasTopLevelStatements = language.HasTopLevelStatements(oldRoot) || language.HasTopLevelStatements(newRoot);

        if (hasTopLevelStatements && alreadyModified.Add(DeclarationWalker.TopLevelProgramType))
        {
            symbols.Add(new SymbolChange(DeclarationWalker.TopLevelProgramType, null, SymbolChangeKind.Modified));
        }
    }

    /// <summary>True when both nodes have the same tokens and preprocessor trivia in the same order.</summary>
    /// <param name="language">The file's language.</param>
    /// <param name="left">First node.</param>
    /// <param name="right">Second node.</param>
    /// <param name="descend">Which child nodes to compare.</param>
    private static bool SignificantEquals(SourceLanguage language, SyntaxNode left, SyntaxNode right, Func<SyntaxNode, SyntaxNode, bool> descend) =>
        SignificantText(language, left, descend).SequenceEqual(SignificantText(language, right, descend), Comparer(language));

    /// <summary>True when both node sequences have the same tokens and preprocessor trivia in the same order.</summary>
    /// <param name="language">The file's language.</param>
    /// <param name="left">First sequence.</param>
    /// <param name="right">Second sequence.</param>
    private static bool SignificantEquals(SourceLanguage language, IEnumerable<SyntaxNode> left, IEnumerable<SyntaxNode> right) =>
        left.SelectMany(n => SignificantText(language, n, DeclarationWalker.DescendAll))
            .SequenceEqual(right.SelectMany(n => SignificantText(language, n, DeclarationWalker.DescendAll)), Comparer(language));

    /// <summary>How token texts compare: Visual Basic ignores case (<c>dim</c> is <c>Dim</c>), C# does not.</summary>
    /// <param name="language">The file's language.</param>
    private static StringComparer Comparer(SourceLanguage language) =>
        language.IgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Token texts of <paramref name="node"/> interleaved with its preprocessor directives and
    /// disabled code. Whitespace and comments are skipped. Yielded lazily so a comparison stops at
    /// the first difference.
    /// </summary>
    /// <param name="language">The file's language.</param>
    /// <param name="node">Node to read.</param>
    /// <param name="descend">Which child nodes to read, given the candidate and <paramref name="node"/>.</param>
    private static IEnumerable<string> SignificantText(SourceLanguage language, SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        foreach (var token in node.DescendantTokens(child => descend(child, node)))
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (language.IsSignificant(trivia))
                {
                    yield return trivia.ToFullString();
                }
            }

            yield return token.Text;

            foreach (var trivia in token.TrailingTrivia)
            {
                if (language.IsSignificant(trivia))
                {
                    yield return trivia.ToFullString();
                }
            }
        }
    }

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
