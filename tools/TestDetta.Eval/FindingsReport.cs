using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TestDetta.Eval;

/// <summary>
/// Reads the findings ledger (<c>eval/findings.json</c>, docs/evaluation.md, "Findings") and writes
/// <c>findings.md</c>: per batch of repositories that joined the corpus, how many classes of problem those
/// repositories revealed for the first time, and the classes themselves. A class is a rule TestDetta got
/// wrong or lacked (a miss, a narrowing that could miss, a recording failure, a selection far wider than
/// needed), not a repository: a class found again in another repository counts once. When new repositories
/// keep revealing new classes, repository-by-repository work is not converging; when the count per new
/// repository falls, it is.
/// </summary>
internal static class FindingsReport
{
    /// <summary>Kinds a finding may have, in the order the report lists them.</summary>
    internal static readonly string[] Kinds = ["miss", "narrowing", "recording", "analysis", "precision", "evaluation"];

    /// <summary>One class of problem.</summary>
    /// <param name="Id">Short stable name.</param>
    /// <param name="Date">When it was found (yyyy-MM-dd).</param>
    /// <param name="Repository">Corpus repository it was first found in; empty when it was found by reading code.</param>
    /// <param name="Kind">One of <see cref="Kinds"/>.</param>
    /// <param name="Summary">What went wrong.</param>
    /// <param name="Fix">The general rule that closed it, with the commit.</param>
    internal sealed record Finding(string Id, string Date, string Repository, string Kind, string Summary, string Fix)
    {
        /// <summary>Other repositories where the same class showed later.</summary>
        public IReadOnlyList<string> SeenAgain { get; init; } = [];

        /// <summary>
        /// The new TestDetta feature whose first measurement exposed the class (it could not show before the feature
        /// existed), or null when the repository exposed it under rules that were already there.
        /// </summary>
        public string? Feature { get; init; }
    }

    /// <summary>Repositories that joined the corpus on one date.</summary>
    /// <param name="Date">yyyy-MM-dd.</param>
    /// <param name="Repositories">Corpus names.</param>
    internal sealed record Batch(string Date, IReadOnlyList<string> Repositories)
    {
        /// <summary>Repositories of the batch that could not be evaluated yet; they reveal nothing and do not count.</summary>
        public IReadOnlyList<string> NotEvaluated { get; init; } = [];

        /// <summary>Repositories of the batch that were evaluated.</summary>
        public int Evaluated => Repositories.Count - NotEvaluated.Count;
    }

    /// <summary>The ledger.</summary>
    /// <param name="Batches">Batches in date order.</param>
    /// <param name="Findings">Findings in date order.</param>
    internal sealed record Ledger(IReadOnlyList<Batch> Batches, IReadOnlyList<Finding> Findings);

    /// <summary>The ledger next to the corpus file (<c>findings.json</c> in its folder); null when there is none.</summary>
    /// <param name="corpusPath">Path of the corpus file.</param>
    public static string? LedgerFor(string corpusPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(corpusPath))!, "findings.json") is var path && File.Exists(path) ? path : null;

    /// <summary>Writes <c>findings.md</c> into <paramref name="directory"/> and returns its path.</summary>
    /// <param name="directory">Run directory.</param>
    /// <param name="ledgerPath">The ledger file.</param>
    public static string Write(string directory, string ledgerPath)
    {
        var path = Path.Combine(directory, "findings.md");
        File.WriteAllText(path, Markdown(Read(ledgerPath)));
        return path;
    }

    /// <summary>Reads a ledger file (JSON with comments).</summary>
    /// <param name="path">The file.</param>
    internal static Ledger Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var batches = root.GetProperty("batches").EnumerateArray()
            .Select(static b => new Batch(b.GetProperty("date").GetString()!, [.. b.GetProperty("repositories").EnumerateArray().Select(static r => r.GetString()!)])
            {
                NotEvaluated = b.TryGetProperty("notEvaluated", out var not) ? [.. not.EnumerateArray().Select(static r => r.GetString()!)] : [],
            })
            .OrderBy(static b => b.Date, StringComparer.Ordinal)
            .ToList();
        var findings = root.GetProperty("findings").EnumerateArray()
            .Select(static f => new Finding(
                f.GetProperty("id").GetString()!,
                f.GetProperty("date").GetString()!,
                f.TryGetProperty("repository", out var repository) ? repository.GetString() ?? string.Empty : string.Empty,
                f.GetProperty("kind").GetString()!,
                f.GetProperty("summary").GetString()!,
                f.GetProperty("fix").GetString()!)
            {
                SeenAgain = f.TryGetProperty("seenAgain", out var again) ? [.. again.EnumerateArray().Select(static r => r.GetString()!)] : [],
                Feature = f.TryGetProperty("feature", out var feature) ? feature.GetString() : null,
            })
            .OrderBy(static f => f.Date, StringComparer.Ordinal)
            .ToList();
        return new Ledger(batches, findings);
    }

    /// <summary>The report.</summary>
    /// <param name="ledger">The ledger.</param>
    internal static string Markdown(Ledger ledger)
    {
        var batchOf = new Dictionary<string, Batch>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ledger.Batches)
        {
            foreach (var repository in batch.Repositories)
            {
                batchOf.TryAdd(repository, batch);
            }
        }

        var text = new StringBuilder();
        text.AppendLine("# Findings per corpus batch");
        text.AppendLine();
        text.AppendLine("A finding is a class of problem, counted once where it first showed (docs/evaluation.md, \"Findings\"). " +
                        "Product classes per evaluated new repository falling from batch to batch means repository-by-repository work converges. " +
                        "Classes a new TestDetta feature exposed in its first measurement are counted apart: they say how well the feature was designed, not how far the corpus is from covering .NET.");
        text.AppendLine();
        text.AppendLine("| Batch | New repositories | Evaluated | Product classes they revealed | Per evaluated repository | Misses among them | From new features | Evaluation classes |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var batch in ledger.Batches)
        {
            var found = ledger.Findings.Where(f => batchOf.TryGetValue(f.Repository, out var b) && ReferenceEquals(b, batch)).ToList();
            var product = found.Count(static f => f.Kind != "evaluation" && f.Feature is null);
            var misses = found.Count(static f => f.Kind == "miss" && f.Feature is null);
            var features = found.Count(static f => f.Kind != "evaluation" && f.Feature is not null);
            var evaluation = found.Count(static f => f.Kind == "evaluation");
            var perRepository = batch.Evaluated == 0 ? "–" : ((double)product / batch.Evaluated).ToString("0.00", CultureInfo.InvariantCulture);
            text.AppendLine(CultureInfo.InvariantCulture, $"| {batch.Date} | {batch.Repositories.Count} | {batch.Evaluated} | {product} | {perRepository} | {misses} | {features} | {evaluation} |");
        }

        var unattributed = ledger.Findings.Where(f => !batchOf.ContainsKey(f.Repository)).ToList();
        if (unattributed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Found outside a corpus repository (reading code, a customer repository): {unattributed.Count}.");
        }

        text.AppendLine();
        text.AppendLine("## By kind");
        text.AppendLine();
        text.AppendLine("| Kind | Classes | Seen again elsewhere |");
        text.AppendLine("|---|---|---|");
        foreach (var kind in Kinds)
        {
            var ofKind = ledger.Findings.Where(f => f.Kind == kind).ToList();
            text.AppendLine(CultureInfo.InvariantCulture, $"| {kind} | {ofKind.Count} | {ofKind.Count(static f => f.SeenAgain.Count > 0)} |");
        }

        text.AppendLine();
        text.AppendLine("## Classes");
        text.AppendLine();
        text.AppendLine("| Date | Repository | Kind | Class | Fix |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var finding in ledger.Findings)
        {
            var where = finding.Repository.Length == 0 ? "(code reading)" : finding.Repository;
            var again = finding.SeenAgain.Count == 0 ? string.Empty : $" (again: {string.Join(", ", finding.SeenAgain)})";
            var feature = finding.Feature is null ? string.Empty : $" (feature: {finding.Feature})";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {finding.Date} | {where}{again} | {finding.Kind}{feature} | {Cell(finding.Summary)} | {Cell(finding.Fix)} |");
        }

        return text.ToString();
    }

    /// <summary>Text fit for a Markdown table cell.</summary>
    /// <param name="text">Raw text.</param>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
