using System.Collections.Frozen;

namespace TestDetta.Core;

/// <summary>
/// The F# side of <see cref="CodeChangeDetector"/>: compares two versions of a file declaration by
/// declaration over the compiler's tokens (<see cref="FSharpSource"/>), by the same rules as C#: types
/// added or removed as a whole, members modified, attribute changes as metadata, a changed header or
/// <c>open</c> marking every type of the file, and <c>[&lt;assembly: ...&gt;]</c> the whole project.
/// </summary>
internal static class FSharpChangeDetector
{
    /// <summary>Compares the two versions of an F# file under every preprocessor view the file needs.</summary>
    /// <param name="path">File path; its name names an implicit module.</param>
    /// <param name="oldText">Old content, or null when the file was added.</param>
    /// <param name="newText">New content, or null when the file was deleted.</param>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file; empty when unknown.</param>
    /// <returns>Whether only whitespace or comments changed, whether the whole project counts as changed, and the changed symbols.</returns>
    public static (bool Cosmetic, bool AffectsWholeProject, List<SymbolChange> Symbols) Detect(string path, string? oldText, string? newText, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var views = Views(symbolSets, oldText, newText);
        var cosmetic = views.All(defines => Texts(FSharpSource.Tokens(oldText ?? string.Empty, defines)).SequenceEqual(Texts(FSharpSource.Tokens(newText ?? string.Empty, defines)), StringComparer.Ordinal));
        if (cosmetic)
        {
            return (true, false, []);
        }

        var affectsWholeProject = false;
        var symbols = new List<SymbolChange>();
        foreach (var defines in views)
        {
            var (affects, changes) = Compare(path, oldText ?? string.Empty, newText ?? string.Empty, defines);
            affectsWholeProject |= affects;
            foreach (var symbol in changes)
            {
                var same = symbols.FindIndex(s => s with { MetadataChanged = false } == symbol with { MetadataChanged = false });
                if (same < 0)
                {
                    symbols.Add(symbol);
                }
                else if (symbol.MetadataChanged)
                {
                    symbols[same] = symbol;
                }
            }
        }

        return (false, affectsWholeProject, symbols);
    }

    /// <summary>
    /// The preprocessor views of a file: no symbols, every symbol its conditions name, and each symbol
    /// set the compiler uses reduced to those (as <see cref="SourceLanguage.ParseVariants"/> for C#).
    /// </summary>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file.</param>
    /// <param name="texts">The file's versions.</param>
    public static List<IReadOnlyList<string>> Views(IReadOnlyCollection<IReadOnlyList<string>> symbolSets, params string?[] texts)
    {
        var named = FSharpSource.ConditionalSymbols(texts);
        List<IReadOnlyList<string>> views = [(string[])[]];
        if (named.Count > 0)
        {
            views.Add(named);
            views.AddRange(DeclarationWalker.RelevantSets(named, symbolSets));
        }

        return views;
    }

    /// <summary>Changed symbols between two versions parsed with the same symbols, and whether project-wide attributes changed.</summary>
    /// <param name="path">File path.</param>
    /// <param name="oldText">Old content.</param>
    /// <param name="newText">New content.</param>
    /// <param name="defines">Preprocessor symbols.</param>
    private static (bool AffectsWholeProject, List<SymbolChange> Symbols) Compare(string path, string oldText, string newText, IReadOnlyList<string> defines)
    {
        var old = Parsed.Of(path, oldText, defines);
        var @new = Parsed.Of(path, newText, defines);
        var symbols = new List<SymbolChange>();
        if (old.File is null || @new.File is null)
        {
            // A version the parser cannot read as an implementation file: nothing to compare by declaration.
            return (true, symbols);
        }

        var affectsWholeProject = !Same(In(old.Tokens, FSharpSource.AssemblyAttributes(old.File)), In(@new.Tokens, FSharpSource.AssemblyAttributes(@new.File)));

        var wholeTypeChanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, declaration) in old.Declarations)
        {
            if (declaration.MemberName is null && !@new.Declarations.ContainsKey(key))
            {
                wholeTypeChanges.Add(declaration.TypeName);
                symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Removed));
            }
        }

        foreach (var (key, declaration) in @new.Declarations)
        {
            if (declaration.MemberName is null && !old.Declarations.ContainsKey(key))
            {
                wholeTypeChanges.Add(declaration.TypeName);
                symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Added));
            }
        }

        foreach (var (key, oldDeclaration) in old.Declarations)
        {
            if (wholeTypeChanges.Contains(oldDeclaration.TypeName))
            {
                continue;
            }

            if (!@new.Declarations.TryGetValue(key, out var newDeclaration))
            {
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Removed) { Inlined = oldDeclaration.IsInline });
                continue;
            }

            if (!Same(FSharpSource.Within(old.Tokens, oldDeclaration.Span, oldDeclaration.Excluded), FSharpSource.Within(@new.Tokens, newDeclaration.Span, newDeclaration.Excluded)))
            {
                symbols.Add(new SymbolChange(oldDeclaration.TypeName, oldDeclaration.MemberName, SymbolChangeKind.Modified)
                {
                    MetadataChanged = !Same(In(old.Tokens, oldDeclaration.AttributeSpans), In(@new.Tokens, newDeclaration.AttributeSpans)),
                    Inlined = oldDeclaration.IsInline || newDeclaration.IsInline,
                });
            }
        }

        foreach (var (key, newDeclaration) in @new.Declarations)
        {
            if (!wholeTypeChanges.Contains(newDeclaration.TypeName) && !old.Declarations.ContainsKey(key))
            {
                symbols.Add(new SymbolChange(newDeclaration.TypeName, newDeclaration.MemberName, SymbolChangeKind.Added) { Inlined = newDeclaration.IsInline });
            }
        }

        // Namespace and module lines and opens belong to no member: when they change, every type in
        // the file may bind differently (an attribute name to another type: metadata too).
        if (!Same(old.Shell(), @new.Shell()))
        {
            var reported = symbols.Where(static s => s is { MemberName: null, Kind: SymbolChangeKind.Modified }).Select(static s => s.TypeName).ToHashSet(StringComparer.Ordinal);
            foreach (var declaration in @new.Declarations.Values)
            {
                if (declaration.MemberName is null && !wholeTypeChanges.Contains(declaration.TypeName) && reported.Add(declaration.TypeName))
                {
                    symbols.Add(new SymbolChange(declaration.TypeName, null, SymbolChangeKind.Modified) { MetadataChanged = true });
                }
            }
        }

        return (affectsWholeProject, symbols);
    }

    /// <summary>The tokens inside any of <paramref name="spans"/>.</summary>
    /// <param name="tokens">A file's tokens.</param>
    /// <param name="spans">Spans.</param>
    private static IEnumerable<FsToken> In(List<FsToken> tokens, IReadOnlyList<FsSpan> spans) =>
        tokens.Where(t => spans.Any(s => s.Contains(t.Line, t.Column)));

    /// <summary>True when both token sequences have the same texts.</summary>
    /// <param name="left">First tokens.</param>
    /// <param name="right">Second tokens.</param>
    private static bool Same(IEnumerable<FsToken> left, IEnumerable<FsToken> right) =>
        Texts(left).SequenceEqual(Texts(right), StringComparer.Ordinal);

    /// <summary>The texts of tokens.</summary>
    /// <param name="tokens">Tokens.</param>
    private static IEnumerable<string> Texts(IEnumerable<FsToken> tokens) => tokens.Select(static t => t.Text);

    /// <summary>One version of a file, tokenized and parsed with one set of symbols.</summary>
    /// <param name="Tokens">Significant tokens.</param>
    /// <param name="File">The parse, or null when it is not an implementation file.</param>
    /// <param name="Declarations">Its declarations.</param>
    internal sealed record Parsed(List<FsToken> Tokens, FSharp.Compiler.Syntax.ParsedImplFileInput? File, Dictionary<string, FsDeclaration> Declarations)
    {
        /// <summary>Tokenizes, parses and collects a version of a file.</summary>
        /// <param name="path">File path.</param>
        /// <param name="text">Content.</param>
        /// <param name="defines">Preprocessor symbols.</param>
        public static Parsed Of(string path, string text, IReadOnlyCollection<string> defines)
        {
            var file = FSharpSource.Parse(text, path, defines);
            return new Parsed(FSharpSource.Tokens(text, defines), file, file is null ? [] : FSharpSource.Collect(file, path, text));
        }

        /// <summary>Tokens outside every member and type that is not a module: namespace and module lines, opens, and what no declaration explains.</summary>
        public IEnumerable<FsToken> Shell()
        {
            var spans = Declarations.Values.Where(static d => !d.IsModule).Select(static d => d.Span).ToList();
            return Tokens.Where(t => !spans.Any(s => s.Contains(t.Line, t.Column)));
        }
    }
}

/// <summary>
/// The F# side of <see cref="SourceIndexBuilder"/>: types (modules and classes) and members of a file
/// with the identifiers they mention, by the same rules as C#. F# names are case-sensitive.
/// </summary>
internal static class FSharpIndexer
{
    /// <summary>Indexes an F# file under every preprocessor view it needs.</summary>
    /// <param name="path">Normalized file path.</param>
    /// <param name="text">File content.</param>
    /// <param name="owner">Project that compiles the file.</param>
    /// <param name="members">Members of the file; updated in place.</param>
    /// <param name="types">Types of the file; updated in place.</param>
    public static void Index(string path, string text, ProjectNode owner, List<IndexedMember> members, List<IndexedType> types)
    {
        foreach (var defines in FSharpChangeDetector.Views(owner.SymbolSets, text))
        {
            var parsed = FSharpChangeDetector.Parsed.Of(path, text, defines);
            if (parsed.File is null)
            {
                continue;
            }

            // An open of a module brings its functions into scope without its name: every member may reach them.
            var opened = FSharpSource.Opened(parsed.File);
            foreach (var declaration in parsed.Declarations.Values)
            {
                if (declaration.MemberName is null)
                {
                    types.Add(new IndexedType(
                        declaration.TypeName,
                        declaration.RuntimeTypeName,
                        owner.Path,
                        owner.IsTestProject,
                        declaration.IsRunnableClass,
                        declaration.HasTestMethods,
                        declaration.BaseTypeNames,
                        CanContainTests: false,
                        declaration.IsModule ? declaration.HasDiscoverableAttributes : declaration.AttributeSpans.Count > 0 || declaration.BaseTypeNames.Count > 0)
                    {
                        Spans = owner.IsTestProject ? [new DeclarationSpan(path, declaration.Span.StartLine, declaration.Span.EndLine)] : [],
                    });
                }

                members.Add(new IndexedMember(
                    owner.Path,
                    owner.IsTestProject,
                    declaration.TypeName,
                    declaration.MemberName,
                    declaration.MemberName is null ? [] : DeclaredNames(declaration.MemberName),
                    declaration.MemberName is null || declaration.MayBeCalledImplicitly || Implicit(declaration.MemberName),
                    declaration.MemberName is not null && declaration.IsPrivate,
                    declaration.MemberName is not null && declaration.IsStatic,
                    References(FSharpSource.Within(parsed.Tokens, declaration.Span, declaration.Excluded), opened))
                {
                    MetadataReferences = References(parsed.Tokens.Where(t => declaration.AttributeSpans.Any(s => s.Contains(t.Line, t.Column))), []),
                });
            }
        }
    }

    /// <summary>
    /// The names code uses for a member: an active pattern's cases (<c>(|Even|Odd|)</c> is used as
    /// <c>Even</c> and <c>Odd</c>), otherwise as for C#.
    /// </summary>
    /// <param name="memberName">Member identity.</param>
    private static IReadOnlyList<string> DeclaredNames(string memberName) =>
        memberName.StartsWith('|')
            ? [.. memberName.Split('|', StringSplitOptions.RemoveEmptyEntries).Where(static n => n != "_")]
            : DeclarationWalker.ReferenceNames(memberName);

    /// <summary>
    /// True for a member code may run without its name: one the runtime or frameworks call by
    /// convention, or an operator (<c>op_PlusDot</c>, used as <c>+.</c>).
    /// </summary>
    /// <param name="memberName">Member identity.</param>
    private static bool Implicit(string memberName) =>
        DeclaredNames(memberName) is var names
        && (names.Count == 0 || names.Any(static n => n.StartsWith("op_", StringComparison.Ordinal)) || names.Any(SourceIndexBuilder.ImplicitlyCalledNames.Contains));

    /// <summary>The identifiers among <paramref name="tokens"/> (an attribute's also with <c>Attribute</c> after it), plus <paramref name="extra"/>.</summary>
    /// <param name="tokens">Tokens of a declaration.</param>
    /// <param name="extra">Names to start with.</param>
    private static FrozenSet<string> References(IEnumerable<FsToken> tokens, List<string> extra)
    {
        var references = new HashSet<string>(extra, StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (token.IsIdentifier)
            {
                references.Add(token.Name);
                references.Add(token.Name + "Attribute");
            }
        }

        return references.ToFrozenSet(StringComparer.Ordinal);
    }
}
