using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The findings ledger counts classes of problem per corpus batch, and the precision report groups the evidence
/// of explained selections into rules (docs/evaluation.md, "Findings" and "Precision triage").
/// </summary>
public sealed class EvalReportsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-reports-" + Guid.NewGuid().ToString("N"));

    public EvalReportsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Findings_are_counted_once_per_batch_where_they_first_showed()
    {
        var ledger = Path.Combine(_directory, "findings.json");
        File.WriteAllText(ledger, """
            // comments are allowed
            {
              "batches": [
                { "date": "2026-01-01", "repositories": ["a", "b"] },
                { "date": "2026-02-01", "repositories": ["c", "d", "e", "f"], "notEvaluated": ["f"] }
              ],
              "findings": [
                { "id": "x", "date": "2026-01-02", "repository": "a", "kind": "miss", "summary": "s", "fix": "f", "seenAgain": ["c"] },
                { "id": "y", "date": "2026-01-03", "repository": "b", "kind": "evaluation", "summary": "s", "fix": "f" },
                { "id": "z", "date": "2026-02-02", "repository": "d", "kind": "precision", "summary": "s | pipe", "fix": "f" },
                { "id": "w", "date": "2026-02-03", "repository": "", "kind": "narrowing", "summary": "s", "fix": "f" },
                { "id": "v", "date": "2026-02-04", "repository": "e", "kind": "precision", "summary": "s", "fix": "f", "feature": "new rule" }
              ]
            }
            """);

        var text = File.ReadAllText(FindingsReport.Write(_directory, ledger));

        Assert.Contains("| 2026-01-01 | 2 | 2 | 1 | 0.50 | 1 | 0 | 1 |", text, StringComparison.Ordinal);
        // f was not evaluated; v came from a new feature's first measurement.
        Assert.Contains("| 2026-02-01 | 4 | 3 | 1 | 0.33 | 0 | 1 | 0 |", text, StringComparison.Ordinal);
        Assert.Contains("precision (feature: new rule)", text, StringComparison.Ordinal);
        Assert.Contains("Found outside a corpus repository (reading code, a customer repository): 1.", text, StringComparison.Ordinal);
        Assert.Contains("| miss | 1 | 1 |", text, StringComparison.Ordinal);
        Assert.Contains("s \\| pipe", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repositorys_ledger_reads_and_names_only_known_kinds_and_corpus_repositories()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "eval", "findings.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var ledger = FindingsReport.Read(Path.Combine(root, "eval", "findings.json"));
        var corpus = Corpus.Load(Path.Combine(root, "eval", "corpus.json")).Repositories.Select(static r => r.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(ledger.Findings);
        Assert.All(ledger.Findings, f => Assert.Contains(f.Kind, FindingsReport.Kinds));
        Assert.All(ledger.Findings, f => Assert.True(f.Repository.Length == 0 || corpus.Contains(f.Repository), f.Repository));
        Assert.All(ledger.Batches.SelectMany(static b => b.Repositories), r => Assert.Contains(r, corpus));
        Assert.Equal(ledger.Findings.Count, ledger.Findings.Select(static f => f.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Rules_group_evidence_across_names_paths_and_map_commits()
    {
        Assert.Equal(
            PrecisionReport.Rule("Coverage", "ran Calc.Calculator.Tax, which changed (map 9f3c1a2b)"),
            PrecisionReport.Rule("Coverage", "ran App.Orders.Total, which changed (map 0b9d00c9)"));
        Assert.Equal(
            PrecisionReport.Rule("WholeProject", "depends on Lib.csproj: src/Lib/README.md is not C#, Visual Basic or F# code"),
            PrecisionReport.Rule("WholeProject", "docs/x.md is not C#, Visual Basic or F# code"));
        Assert.Equal("WholeProject: <file> is not C#, Visual Basic or F# code", PrecisionReport.Rule("WholeProject", "README.md is not C#, Visual Basic or F# code"));
    }

    [Fact]
    public void The_precision_report_ranks_repositories_and_weighs_rules_by_the_classes_they_select()
    {
        var explain = Path.Combine(_directory, "lib", "explain.json");
        Directory.CreateDirectory(Path.GetDirectoryName(explain)!);
        File.WriteAllText(explain, """
            { "sha": "abc", "testClasses": 10, "changes": [
              { "target": "A.B", "kind": "throw", "covered": true, "selectedShare": 1.0, "projects": [
                { "project": "Lib.Tests", "mode": "All", "reason": "r", "candidates": 10, "selected": [
                  { "class": "T1", "kind": "WholeProject", "evidence": "README.md is not C#, Visual Basic or F# code" },
                  { "class": "T2", "kind": "WholeProject", "evidence": "README.md is not C#, Visual Basic or F# code" } ] } ] },
              { "target": "A.C", "kind": "throw", "covered": true, "selectedShare": 0.1, "projects": [
                { "project": "Lib.Tests", "mode": "Filtered", "reason": "r", "candidates": 10, "selected": [
                  { "class": "T3", "kind": "Coverage", "evidence": "ran A.C, which changed (map abc)" } ] } ] } ] }
            """);
        var repository = PrecisionReport.Read(explain, "lib")!;

        var text = PrecisionReport.Markdown([repository]);

        Assert.Equal(0.55, repository.Mean, 3);
        Assert.Equal(1, repository.WholeProject);
        Assert.Contains("| lib | 2 | 10 | 55% | 100% | 50% |", text.Replace(" %", "%", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("| WholeProject: <file> is not C#, Visual Basic or F# code | 2 | 67% |", text.Replace(" %", "%", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Any_recording_takes_the_newest_full_recording_at_the_commit()
    {
        var cache = Path.Combine(_directory, "maps");
        foreach (var (name, minutes) in (ReadOnlySpan<(string, int)>)[("abc-old", -20), ("abc-new", -10), ("abc-incremental-def", 0), ("abd-other", 0), ("abc-empty", 5)])
        {
            var folder = Path.Combine(cache, name);
            Directory.CreateDirectory(folder);
            if (name != "abc-empty")
            {
                File.WriteAllText(Path.Combine(folder, "T.json"), "{}");
            }

            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddMinutes(minutes));
        }

        Assert.Equal(Path.Combine(cache, "abc-new"), EvalSteps.NewestRecording(cache, "abc"));
        Assert.Null(EvalSteps.NewestRecording(cache, "zzz"));
    }
}
