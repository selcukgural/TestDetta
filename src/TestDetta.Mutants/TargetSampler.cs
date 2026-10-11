using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TestDetta.Core;
using VB = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace TestDetta.Mutants;

/// <summary>A text insertion into a source file.</summary>
/// <param name="Path">Normalized absolute file path.</param>
/// <param name="Position">Character offset the text is inserted at.</param>
/// <param name="Text">Inserted text.</param>
internal sealed record SourceEdit(string Path, int Position, string Text)
{
    /// <summary>Characters at <see cref="Position"/> that <see cref="Text"/> replaces; 0 inserts.</summary>
    public int Replaces { get; init; }
}

/// <summary>
/// A member picked for a synthetic change: every declaration with its name in its type is edited in
/// source (for TestDetta to analyse) and every compiled method with its name throws (for the tests).
/// </summary>
/// <param name="Member">Type and member name as coverage maps and the mutant injector spell them.</param>
/// <param name="Edits">Source edits that change each declaration without changing what it does.</param>
/// <param name="Covered">True when a coverage map at the base commit says some test ran it.</param>
internal sealed record SyntheticTarget(CoveredMethod Member, IReadOnlyList<SourceEdit> Edits, bool Covered)
{
    /// <summary>How the mutant breaks the member.</summary>
    public MutantKind Kind { get; init; } = MutantKind.Throw;
}

/// <summary>How many targets to pick, and how.</summary>
/// <param name="Count">Targets to pick.</param>
/// <param name="Seed">Seed of the shuffle, so a run can be repeated.</param>
/// <param name="CoveredShare">
/// Share of targets taken from members some test ran. Uncovered members rarely fail a test, so most
/// runs go where a mutant can prove something; the rest keep TestDetta's blind spots (code that coverage
/// never saw, such as code reached only by reflection at start-up) in the sample.
/// </param>
internal sealed record SampleRequest(int Count, int Seed, double CoveredShare = 0.8)
{
    /// <summary>
    /// Mutant kinds to use. Methods get one of the method kinds at random; fields with a constant
    /// initial value join the sample only when <see cref="MutantKind.FieldValue"/> is on.
    /// </summary>
    public IReadOnlySet<MutantKind> Kinds { get; init; } = new HashSet<MutantKind> { MutantKind.Throw };

    /// <summary>Data files to empty after the members (the evaluator's <c>--data-files</c>).</summary>
    public int DataFiles { get; init; }

    /// <summary>
    /// True to take the members in one order over all candidates, with <see cref="CoveredShare"/> ignored:
    /// coverage then decides neither which member is picked nor when. <c>td audit</c> samples this way, since
    /// favouring members a map says some test ran would favour the changes the selection is most likely to catch
    /// (docs/design/audit.md).
    /// </summary>
    public bool Uniform { get; init; }

    /// <summary>
    /// Only members whose type, or type and member (<c>Type.Member(args)</c>), match this pattern join the sample,
    /// <c>*</c> standing for any text (<c>Newtonsoft.Json.Utilities.LinqBridge.*</c>); null for all. A rule that
    /// narrows the selection is checked with changes where it acts, not with the few a random sample of the whole
    /// repository happens to put there (docs/evaluation.md, "Targeted mutants"). Each member keeps the kind and the
    /// place a hash of it and the seed give it, so the stored test outcomes of a target stay usable.
    /// </summary>
    public string? TypePattern { get; init; }

    /// <summary>
    /// The members and kinds of an earlier sample, in its order, to evaluate again exactly (the evaluator's
    /// <c>--replay</c>): after a fix, the changes that failed are tried once more, even where a different candidate
    /// list would move a seed's picks. <see cref="Count"/>, the seed, the kinds and the pattern no longer decide the
    /// sample; a member no longer in the source is left out. Null to sample.
    /// </summary>
    public IReadOnlyList<ReplayedMutant>? Replay { get; init; }

    /// <summary>
    /// The target framework the tests run for (<c>-f</c>), or null for every framework the projects target. With one,
    /// each C# file is read with the preprocessor symbols its project defines for that framework, so the source edit
    /// lands on the declaration the run compiles, the one the mutant changes: Alba declares
    /// <c>WebApplicationFactoryHostBuilderAdapter</c> under <c>#if NET11_0_OR_GREATER</c> and again under <c>#else</c>,
    /// and an edit to the <c>#else</c> one, which a run for <c>net11.0</c> does not compile, was judged against a mutant of
    /// the other (a miss of the evaluation's own making).
    /// </summary>
    public string? Framework { get; init; }
}

/// <summary>One change of an earlier sample, as its report names it.</summary>
/// <param name="Target">The member, as <c>Type.Member</c>; for a data file, its repository path.</param>
/// <param name="Kind">The mutant kind it had.</param>
internal sealed record ReplayedMutant(string Target, MutantKind Kind);

/// <summary>
/// Picks methods, constructors, properties and events of library code that a test project depends
/// on, and computes a behaviour-neutral source edit for each declaration of the member: an empty
/// statement after a block body's opening brace, or parentheses around an expression body. The
/// edited files are only parsed by TestDetta, never compiled; the behaviour change comes from the mutant.
/// </summary>
internal static class TargetSampler
{
    /// <summary>Picks up to <see cref="SampleRequest.Count"/> targets.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="covered">Members that ran in some test, class, fixture or start-up.</param>
    /// <param name="request">How many and how.</param>
    public static List<SyntheticTarget> Sample(ProjectGraph graph, string repoRoot, IReadOnlySet<CoveredMethod> covered, SampleRequest request)
    {
        var candidates = Candidates(graph, repoRoot, request.Kinds.Contains(MutantKind.FieldValue), request.Framework);
        MutantKind[] methodKinds = [.. request.Kinds.Where(static k => k != MutantKind.FieldValue).Order()];
        var pattern = request.TypePattern is { } glob
            ? new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            : null;
        if (request.Replay is { } replay)
        {
            return Replayed(candidates, covered, replay);
        }

        var coveredPool = new List<SyntheticTarget>();
        var otherPool = new List<SyntheticTarget>();
        foreach (var ((member, isField), edits) in candidates.OrderBy(static c => c.Key.Member.TypeName, StringComparer.Ordinal).ThenBy(static c => c.Key.Member.MemberName, StringComparer.Ordinal))
        {
            // No edits: every declaration in source is bodiless (a [LoggerMessage] or other generated
            // partial method). Its code comes from a generator, so the mutant would change code whose
            // source TestDetta was never shown, and the "miss" would be the evaluation's own.
            if (edits is null || edits.Count == 0)
            {
                continue;
            }

            if (!isField && methodKinds.Length == 0)
            {
                continue;
            }

            if (pattern is not null && !pattern.IsMatch(member.TypeName) && !pattern.IsMatch($"{member.TypeName}.{member.MemberName}"))
            {
                continue;
            }

            // A field is covered when the constructor that sets it ran.
            var kind = isField ? MutantKind.FieldValue : methodKinds[(int)(StableKey(member, request.Seed, "kind") % (ulong)methodKinds.Length)];
            var isCovered = covered.Contains(isField ? new CoveredMethod(member.TypeName, ".cctor") : member)
                            || (isField && covered.Contains(new CoveredMethod(member.TypeName, ".ctor")));
            var target = new SyntheticTarget(member, edits, isCovered) { Kind = kind };
            (target.Covered ? coveredPool : otherPool).Add(target);
        }

        // Each member's place and kind come from a hash of the member and the seed, not from a shared
        // random sequence: adding or dropping one candidate (a fix to TestDetta's project graph) then
        // leaves every other pick and kind as it was, and stored test outcomes stay usable.
        if (request.Uniform)
        {
            return
            [
                .. coveredPool.Concat(otherPool)
                    .OrderBy(t => StableKey(t.Member, request.Seed, "order"))
                    .ThenBy(static t => t.Member.TypeName, StringComparer.Ordinal)
                    .ThenBy(static t => t.Member.MemberName, StringComparer.Ordinal)
                    .Take(request.Count),
            ];
        }

        coveredPool.Sort((a, b) => StableKey(a.Member, request.Seed, "order").CompareTo(StableKey(b.Member, request.Seed, "order")));
        otherPool.Sort((a, b) => StableKey(a.Member, request.Seed, "order").CompareTo(StableKey(b.Member, request.Seed, "order")));

        var fromCovered = Math.Min(coveredPool.Count, (int)Math.Round(request.Count * request.CoveredShare));
        var fromOther = Math.Min(otherPool.Count, request.Count - fromCovered);
        fromCovered = Math.Min(coveredPool.Count, request.Count - fromOther);
        return [.. coveredPool.Take(fromCovered), .. otherPool.Take(fromOther)];
    }

    /// <summary>
    /// The candidates <paramref name="replay"/> names, in its order and with its kinds. A field takes only
    /// <see cref="MutantKind.FieldValue"/> and a method none of it, as when it was sampled; data files are the evaluator's.
    /// </summary>
    /// <param name="candidates">Every editable member.</param>
    /// <param name="covered">Members that ran in some test, class, fixture or start-up.</param>
    /// <param name="replay">The earlier sample.</param>
    private static List<SyntheticTarget> Replayed(
        Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> candidates,
        IReadOnlySet<CoveredMethod> covered,
        IReadOnlyList<ReplayedMutant> replay)
    {
        var byName = new Dictionary<string, (CoveredMethod Member, bool IsField, List<SourceEdit> Edits)>(StringComparer.Ordinal);
        foreach (var ((member, isField), edits) in candidates)
        {
            if (edits is { Count: > 0 })
            {
                byName.TryAdd($"{member.TypeName}.{member.MemberName}", (member, isField, edits));
            }
        }

        var targets = new List<SyntheticTarget>(replay.Count);
        foreach (var (name, kind) in replay)
        {
            if (kind == MutantKind.EmptyFile || !byName.TryGetValue(name, out var found) || found.IsField != (kind == MutantKind.FieldValue))
            {
                continue;
            }

            var isCovered = covered.Contains(found.IsField ? new CoveredMethod(found.Member.TypeName, ".cctor") : found.Member)
                            || (found.IsField && covered.Contains(new CoveredMethod(found.Member.TypeName, ".ctor")));
            targets.Add(new SyntheticTarget(found.Member, found.Edits, isCovered) { Kind = kind });
        }

        return targets;
    }

    /// <summary>
    /// The preprocessor symbols a C# file is compiled with for <paramref name="framework"/>: those of the first project
    /// owning it that defines the framework; none without a framework or when no owner targets it.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="file">Normalized file path.</param>
    /// <param name="framework">Target framework of the run, or null.</param>
    private static IReadOnlyCollection<string> SymbolsFor(ProjectGraph graph, string file, string? framework)
    {
        if (framework is null)
        {
            return [];
        }

        foreach (var owner in graph.ResolveFile(file))
        {
            if (owner.Role == FileRole.Source && owner.Project.FrameworkSymbols.TryGetValue(framework, out var symbols))
            {
                return [.. symbols];
            }
        }

        return [];
    }

    /// <summary>A 64-bit FNV-1a hash of the member, the seed and a purpose, the same on every run and machine.</summary>
    /// <param name="member">Sampled member.</param>
    /// <param name="seed">Sample seed.</param>
    /// <param name="purpose">What the key decides, so order and kind are independent.</param>
    private static ulong StableKey(CoveredMethod member, int seed, string purpose)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in $"{purpose}|{seed}|{member.TypeName}|{member.MemberName}")
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Every member of library code, with its edits; null edits when some declaration compiles to
    /// code that cannot be edited (an auto-property accessor, say), since the mutant would reach it.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="withFields">True to add fields with a constant initial value.</param>
    /// <param name="framework">Target framework of the run, whose preprocessor symbols C# files are read with; null for none.</param>
    private static Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> Candidates(ProjectGraph graph, string repoRoot, bool withFields, string? framework)
    {
        var candidates = new Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?>();
        var testProjects = graph.Projects.Where(static p => p.IsTestProject).Select(static p => p.Path).ToHashSet(StringComparer.Ordinal);
        var tested = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in SourceIndexBuilder.EnumerateSourceFiles(Paths.Normalize(repoRoot)))
        {
            if (!graph.ResolveFile(file).Any(o => o.Role == FileRole.Source && IsTestedLibrary(graph, o.Project, testProjects, tested)))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (FSharpSource.IsSource(file))
            {
                AddFSharp(file, text, candidates);
                continue;
            }

            if (SourceLanguage.For(file) is VisualBasicLanguage)
            {
                var owner = graph.ResolveFile(file).First(static o => o.Role == FileRole.Source).Project;
                AddVisualBasic(file, text, owner.RootNamespace, candidates);
                continue;
            }

            var root = DeclarationWalker.Parse(SourceText.From(text), SymbolsFor(graph, file, framework));
            foreach (var declaration in DeclarationWalker.Collect(root).Values)
            {
                if (withFields && FieldEdit(file, declaration) is { } field)
                {
                    candidates[(field.Member, true)] = [field.Edit];
                    continue;
                }

                if (MemberKey(declaration) is not { } member)
                {
                    continue;
                }

                Merge(candidates, member, Edits(file, (Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax)declaration.Node));
            }
        }

        return candidates;
    }

    /// <summary>Adds one declaration's edits to its member's; a member with code that cannot be edited stays null.</summary>
    /// <param name="candidates">Members so far.</param>
    /// <param name="member">The member as compiled code names it.</param>
    /// <param name="edits">The declaration's edits, or null.</param>
    private static void Merge(Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> candidates, CoveredMethod member, List<SourceEdit>? edits)
    {
        var key = (member, false);
        if (candidates.TryGetValue(key, out var existing) && existing is null)
        {
            return;
        }

        candidates[key] = edits is null ? null : [.. existing ?? [], .. edits];
    }

    /// <summary>
    /// The functions, methods and properties with code of an F# file, each edited by piping its body
    /// through <c>id</c> at its end: a token change inside the member that leaves the lines above and
    /// the indentation alone. Inline members are left out (their code is compiled into the callers, so
    /// the mutant could not break them), and so are constructors and auto-properties.
    /// </summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="text">File content.</param>
    /// <param name="candidates">Members so far; updated in place.</param>
    private static void AddFSharp(string file, string text, Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> candidates)
    {
        if (FSharpSource.Parse(text, file, []) is not { } parsed)
        {
            return;
        }

        foreach (var declaration in FSharpSource.Collect(parsed, file, text).Values)
        {
            if (declaration is not { MemberName: { } memberName, IsInline: false, Body: { } body }
                || DeclarationWalker.CompiledNames(memberName) is not [var name]
                || name.StartsWith('.'))
            {
                continue;
            }

            Merge(candidates, new CoveredMethod(declaration.TypeName, name), [new SourceEdit(file, Offset(text, body.EndLine, body.EndColumn), " |> id")]);
        }
    }

    /// <summary>The character offset of a 1-based line and 0-based column.</summary>
    /// <param name="text">File content.</param>
    /// <param name="line">Line, 1-based.</param>
    /// <param name="column">Column, 0-based.</param>
    private static int Offset(string text, int line, int column)
    {
        var start = 0;
        for (var current = 1; current < line; current++)
        {
            start = text.IndexOf('\n', start) + 1;
        }

        return start + column;
    }

    /// <summary>
    /// The methods, constructors, properties and events of a Visual Basic file, each block edited by an
    /// empty <c>If False Then</c> block after its first line; members without code (<c>MustOverride</c>,
    /// interface members) get no edit, auto-properties make the member unusable.
    /// </summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="text">File content.</param>
    /// <param name="rootNamespace">Root namespace of the project compiling the file.</param>
    /// <param name="candidates">Members so far; updated in place.</param>
    private static void AddVisualBasic(string file, string text, string rootNamespace, Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> candidates)
    {
        var language = VisualBasicLanguage.Instance;
        foreach (var declaration in language.Collect(language.Parse(SourceText.From(text), []), rootNamespace).Values)
        {
            if (declaration.MemberName is not { } memberName)
            {
                continue;
            }

            List<SourceEdit>? edits;
            switch (declaration.Node)
            {
                case VB.MethodBlockSyntax or VB.ConstructorBlockSyntax:
                    edits = [VisualBasicEdit(file, (VB.MethodBlockBaseSyntax)declaration.Node)];
                    break;
                case VB.PropertyBlockSyntax property:
                    edits = [.. property.Accessors.Select(a => VisualBasicEdit(file, a))];
                    break;
                case VB.EventBlockSyntax @event:
                    edits = [.. @event.Accessors.Select(a => VisualBasicEdit(file, a))];
                    break;
                case VB.MethodStatementSyntax or VB.PropertyStatementSyntax when declaration.Node.Parent is VB.InterfaceBlockSyntax
                                                                                 || VisualBasicLanguage.Modifiers(declaration.Node).Any(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.MustOverrideKeyword):
                    edits = [];
                    break;
                case VB.PropertyStatementSyntax:
                    // An auto-property still compiles to accessors the mutant would break.
                    edits = null;
                    break;
                default:
                    continue;
            }

            var name = memberName.StartsWith(".ctor", StringComparison.Ordinal) ? ".ctor"
                : DeclarationWalker.CompiledNames(memberName) is [var single] && !single.StartsWith("op_", StringComparison.Ordinal) ? single
                : null;
            if (name is not null)
            {
                Merge(candidates, new CoveredMethod(declaration.TypeName, name), edits);
            }
        }
    }

    /// <summary>An empty <c>If False Then</c> block after a Visual Basic block's first line.</summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="block">A method, constructor or accessor block.</param>
    private static SourceEdit VisualBasicEdit(string file, VB.MethodBlockBaseSyntax block) =>
        new(file, block.BlockStatement.Span.End, "\nIf False Then\nEnd If");

    /// <summary>True for a non-test project (C#, Visual Basic or F#) that some test project depends on.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="project">Owner of a file.</param>
    /// <param name="testProjects">Paths of all test projects.</param>
    /// <param name="cache">Answers so far, by project path.</param>
    private static bool IsTestedLibrary(ProjectGraph graph, ProjectNode project, HashSet<string> testProjects, Dictionary<string, bool> cache)
    {
        if (!cache.TryGetValue(project.Path, out var tested))
        {
            tested = !project.IsTestProject
                     && project.Language is ProjectLanguage.CSharp or ProjectLanguage.VisualBasic or ProjectLanguage.FSharp
                     && graph.WithDependents([project.Path]).Overlaps(testProjects);
            cache[project.Path] = tested;
        }

        return tested;
    }

    /// <summary>
    /// A <c>static readonly</c> or instance field declared alone with a literal initial value
    /// (number, string, <c>true</c>/<c>false</c>), and an edit that wraps the literal in parentheses.
    /// Constants are left out: their value is copied into every use at compile time.
    /// </summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="declaration">A declaration of the file.</param>
    private static (CoveredMethod Member, SourceEdit Edit)? FieldEdit(string file, Declaration declaration)
    {
        if (declaration.Node is not FieldDeclarationSyntax field
            || field.Modifiers.Any(SyntaxKind.ConstKeyword)
            || field.Declaration.Variables is not [{ Initializer.Value: LiteralExpressionSyntax literal } variable]
            || !literal.IsKind(SyntaxKind.NumericLiteralExpression) && !literal.IsKind(SyntaxKind.StringLiteralExpression)
               && !literal.IsKind(SyntaxKind.TrueLiteralExpression) && !literal.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            return null;
        }

        // Both parentheses in one edit that rewrites the literal whole keeps the offsets simple.
        return (new CoveredMethod(declaration.TypeName, variable.Identifier.ValueText),
            new SourceEdit(file, literal.SpanStart, "(" + literal.ToString() + ")") { Replaces = literal.Span.Length });
    }

    /// <summary>
    /// The member as compiled code names it, for methods, constructors, properties and events;
    /// null for fields, operators, indexers and finalizers, whose compiled names differ from source.
    /// </summary>
    /// <param name="declaration">A member declaration.</param>
    private static CoveredMethod? MemberKey(Declaration declaration)
    {
        if (declaration.MemberName is not { } member
            || declaration.Node is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax))
        {
            return null;
        }

        var name = member.StartsWith(".ctor", StringComparison.Ordinal) ? ".ctor"
            : member.StartsWith(".cctor", StringComparison.Ordinal) ? ".cctor"
            : DeclarationWalker.ReferenceNames(member) is [var single] ? single
            : null;
        return name is null ? null : new CoveredMethod(declaration.TypeName, name);
    }

    /// <summary>
    /// Edits for one declaration: empty when it has no code (abstract, extern, a partial definition),
    /// null when it has code that cannot be edited without changing behaviour.
    /// </summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="node">Member declaration.</param>
    private static List<SourceEdit>? Edits(string file, MemberDeclarationSyntax node)
    {
        var edits = new List<SourceEdit>();
        switch (node)
        {
            case BaseMethodDeclarationSyntax method:
                return AddBody(file, method.Body, method.ExpressionBody, edits) ? edits : null;

            case BasePropertyDeclarationSyntax property:
                if (property is PropertyDeclarationSyntax { ExpressionBody: { } arrow })
                {
                    return AddBody(file, null, arrow, edits) ? edits : null;
                }

                if (property.AccessorList is null)
                {
                    return edits;
                }

                var hasCode = !property.Modifiers.Any(SyntaxKind.AbstractKeyword)
                              && !property.Modifiers.Any(SyntaxKind.ExternKeyword)
                              && property.Parent is not InterfaceDeclarationSyntax;
                foreach (var accessor in property.AccessorList.Accessors)
                {
                    if (accessor.Body is null && accessor.ExpressionBody is null)
                    {
                        // An auto-accessor still compiles to a method the mutant would break.
                        if (hasCode)
                        {
                            return null;
                        }

                        continue;
                    }

                    if (!AddBody(file, accessor.Body, accessor.ExpressionBody, edits))
                    {
                        return null;
                    }
                }

                return edits;

            default:
                return null;
        }
    }

    /// <summary>Adds the edit of one body; false when the body cannot be edited neutrally.</summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="block">Block body, or null.</param>
    /// <param name="arrow">Expression body, or null.</param>
    /// <param name="edits">Edits of the declaration; updated in place.</param>
    private static bool AddBody(string file, BlockSyntax? block, ArrowExpressionClauseSyntax? arrow, List<SourceEdit> edits)
    {
        if (block is not null)
        {
            edits.Add(new SourceEdit(file, block.OpenBraceToken.Span.End, ";"));
            return true;
        }

        if (arrow is null)
        {
            return true;
        }

        // (throw x) and (ref x) are not expressions; anything else keeps its meaning in parentheses.
        if (arrow.Expression is ThrowExpressionSyntax or RefExpressionSyntax)
        {
            return false;
        }

        edits.Add(new SourceEdit(file, arrow.Expression.SpanStart, "("));
        edits.Add(new SourceEdit(file, arrow.Expression.Span.End, ")"));
        return true;
    }
}
