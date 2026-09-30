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
internal sealed record SourceEdit(string Path, int Position, string Text);

/// <summary>
/// A member picked for a synthetic change: every declaration with its name in its type is edited in
/// source (for Sift to analyse) and every compiled method with its name throws (for the tests).
/// </summary>
/// <param name="Member">Type and member name as coverage maps and the mutant injector spell them.</param>
/// <param name="Edits">Source edits that change each declaration without changing what it does.</param>
/// <param name="Covered">True when a coverage map at the base commit says some test ran it.</param>
internal sealed record SyntheticTarget(CoveredMethod Member, IReadOnlyList<SourceEdit> Edits, bool Covered);

/// <summary>How many targets to pick, and how.</summary>
/// <param name="Count">Targets to pick.</param>
/// <param name="Seed">Seed of the shuffle, so a run can be repeated.</param>
/// <param name="CoveredShare">
/// Share of targets taken from members some test ran. Uncovered members rarely fail a test, so most
/// runs go where a mutant can prove something; the rest keep Sift's blind spots (code that coverage
/// never saw, such as code reached only by reflection at start-up) in the sample.
/// </param>
internal sealed record SampleRequest(int Count, int Seed, double CoveredShare = 0.8);

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
        var candidates = Candidates(graph, repoRoot);
        var random = new Random(request.Seed);
        var coveredPool = new List<SyntheticTarget>();
        var otherPool = new List<SyntheticTarget>();
        foreach (var (member, edits) in candidates.OrderBy(static c => c.Key.TypeName, StringComparer.Ordinal).ThenBy(static c => c.Key.MemberName, StringComparer.Ordinal))
        {
            // No edits: every declaration in source is bodiless (a [LoggerMessage] or other generated
            // partial method). Its code comes from a generator, so the mutant would change code whose
            // source Sift was never shown, and the "miss" would be the evaluation's own.
            if (edits is null || edits.Count == 0)
            {
                continue;
            }

            var target = new SyntheticTarget(member, edits, covered.Contains(member));
            (target.Covered ? coveredPool : otherPool).Add(target);
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coveredPool));
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(otherPool));

        var fromCovered = Math.Min(coveredPool.Count, (int)Math.Round(request.Count * request.CoveredShare));
        var fromOther = Math.Min(otherPool.Count, request.Count - fromCovered);
        fromCovered = Math.Min(coveredPool.Count, request.Count - fromOther);
        return [.. coveredPool.Take(fromCovered), .. otherPool.Take(fromOther)];
    }

    /// <summary>
    /// Every member of library code, with its edits; null edits when some declaration compiles to
    /// code that cannot be edited (an auto-property accessor, say), since the mutant would reach it.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root.</param>
    private static Dictionary<CoveredMethod, List<SourceEdit>?> Candidates(ProjectGraph graph, string repoRoot)
    {
        var candidates = new Dictionary<CoveredMethod, List<SourceEdit>?>();
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
                if (MemberKey(declaration) is not { } key)
                {
                    continue;
                }

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
