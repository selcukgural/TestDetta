using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Sift.Core;

namespace Sift.Eval;

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
/// source (for Sift to analyse) and every compiled method with its name throws (for the tests).
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
/// runs go where a mutant can prove something; the rest keep Sift's blind spots (code that coverage
/// never saw, such as code reached only by reflection at start-up) in the sample.
/// </param>
internal sealed record SampleRequest(int Count, int Seed, double CoveredShare = 0.8)
{
    /// <summary>
    /// Mutant kinds to use. Methods get one of the method kinds at random; fields with a constant
    /// initial value join the sample only when <see cref="MutantKind.FieldValue"/> is on.
    /// </summary>
    public IReadOnlySet<MutantKind> Kinds { get; init; } = new HashSet<MutantKind> { MutantKind.Throw };
}

/// <summary>
/// Picks methods, constructors, properties and events of library code that a test project depends
/// on, and computes a behaviour-neutral source edit for each declaration of the member: an empty
/// statement after a block body's opening brace, or parentheses around an expression body. The
/// edited files are only parsed by Sift, never compiled; the behaviour change comes from the mutant.
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
        var candidates = Candidates(graph, repoRoot, request.Kinds.Contains(MutantKind.FieldValue));
        MutantKind[] methodKinds = [.. request.Kinds.Where(static k => k != MutantKind.FieldValue).Order()];
        var coveredPool = new List<SyntheticTarget>();
        var otherPool = new List<SyntheticTarget>();
        foreach (var ((member, isField), edits) in candidates.OrderBy(static c => c.Key.Member.TypeName, StringComparer.Ordinal).ThenBy(static c => c.Key.Member.MemberName, StringComparer.Ordinal))
        {
            // No edits: every declaration in source is bodiless (a [LoggerMessage] or other generated
            // partial method). Its code comes from a generator, so the mutant would change code whose
            // source Sift was never shown, and the "miss" would be the evaluation's own.
            if (edits is null || edits.Count == 0)
            {
                continue;
            }

            if (!isField && methodKinds.Length == 0)
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
        // random sequence: adding or dropping one candidate (a fix to Sift's project graph) then
        // leaves every other pick and kind as it was, and stored test outcomes stay usable.
        coveredPool.Sort((a, b) => StableKey(a.Member, request.Seed, "order").CompareTo(StableKey(b.Member, request.Seed, "order")));
        otherPool.Sort((a, b) => StableKey(a.Member, request.Seed, "order").CompareTo(StableKey(b.Member, request.Seed, "order")));

        var fromCovered = Math.Min(coveredPool.Count, (int)Math.Round(request.Count * request.CoveredShare));
        var fromOther = Math.Min(otherPool.Count, request.Count - fromCovered);
        fromCovered = Math.Min(coveredPool.Count, request.Count - fromOther);
        return [.. coveredPool.Take(fromCovered), .. otherPool.Take(fromOther)];
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
    private static Dictionary<(CoveredMethod Member, bool IsField), List<SourceEdit>?> Candidates(ProjectGraph graph, string repoRoot, bool withFields)
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

            var root = DeclarationWalker.Parse(SourceText.From(File.ReadAllText(file)));
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

                var key = (member, false);
                var edits = Edits(file, declaration.Node);
                candidates.TryGetValue(key, out var existing);
                if (candidates.ContainsKey(key) && existing is null)
                {
                    continue;
                }

                candidates[key] = edits is null ? null : [.. existing ?? [], .. edits];
            }
        }

        return candidates;
    }

    /// <summary>True for a non-test C# project that some test project depends on.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="project">Owner of a file.</param>
    /// <param name="testProjects">Paths of all test projects.</param>
    /// <param name="cache">Answers so far, by project path.</param>
    private static bool IsTestedLibrary(ProjectGraph graph, ProjectNode project, HashSet<string> testProjects, Dictionary<string, bool> cache)
    {
        if (!cache.TryGetValue(project.Path, out var tested))
        {
            tested = !project.IsTestProject
                     && project.Language == ProjectLanguage.CSharp
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
