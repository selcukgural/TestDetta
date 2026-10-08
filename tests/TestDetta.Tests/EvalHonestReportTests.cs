using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// The evaluation's reports say how the numbers were judged and what went wrong (docs/evaluation.md, "Judging" and
/// "Incidents"): the judging rules, the inconclusive mutants and the worst-case bound next to the normal one, a project
/// TestDetta failed to record, a map that recorded nothing or only failures, and fewer recorded classes than the
/// previous round. The outcome cache keeps nothing judged by the old rules.
/// </summary>
public sealed class EvalHonestReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-honest-" + Guid.NewGuid().ToString("N"));

    public EvalHonestReportTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_repository_report_names_the_judging_the_inconclusive_mutants_the_worst_case_and_the_incidents()
    {
        var caught = new SyntheticMutant(Target("Lib.A", "Run"), true, 1, ["T: X"], [], 0.5, Inconclusive: false);
        var open = new SyntheticMutant(Target("Lib.B", "Run"), true, 1, [], [], 0.5, Inconclusive: true) { Note = "T.Tests: hung with the mutant, and timed out without it" };
        var run = new SyntheticRun("abcdef1", 10, 1, [caught, open], null) { Incidents = ["tests/T.Tests/T.Tests.csproj: the map recorded 0 test classes"] };

        var text = SyntheticReport.Markdown("repo", run);

        Assert.Contains($"- Judging: {MutantJudge.Rules}", text, StringComparison.Ordinal);
        Assert.Contains("inconclusive: 1", text, StringComparison.Ordinal);
        Assert.Contains("worst case, every inconclusive mutant a miss: 1 of 2, upper bound", text, StringComparison.Ordinal);
        Assert.Contains("## Incidents", text, StringComparison.Ordinal);
        Assert.Contains("the map recorded 0 test classes", text, StringComparison.Ordinal);
        Assert.Contains("`Lib.B.Run`: T.Tests: hung with the mutant", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_round_summary_adds_up_inconclusive_mutants_and_incidents_and_states_the_judging()
    {
        WriteReport("a", """
            { "sha": "abcdef1234", "error": null, "judging": "strict (2026-10-07)", "testClasses": 12, "seconds": 1,
              "maps": [ { "project": "A.Tests", "classes": 12, "failed": 0 } ],
              "incidents": [ "tests/A.Tests/A.Tests.csproj: not recorded although its tests passed: the tracker did not load" ],
              "mutants": [
                { "target": "A.Hit", "inconclusive": false, "selectedShare": 0.5, "killed": ["T: X"], "missed": [] },
                { "target": "A.Open", "inconclusive": true, "selectedShare": 0.5, "killed": [], "missed": [] } ] }
            """);

        var text = BatchSummary.Write(_directory, Corpus.Load(WriteCorpus("a")));
        var read = Assert.Single(BatchSummary.ReadSummary(_directory)!);

        Assert.Contains("- Judging: strict (2026-10-07)", text, StringComparison.Ordinal);
        Assert.Contains("- Inconclusive: 1 mutant(s); worst case, every one of them a miss: 1 of 2", text, StringComparison.Ordinal);
        Assert.Contains("**Incidents: 1**", text, StringComparison.Ordinal);
        Assert.Contains("- a: tests/A.Tests/A.Tests.csproj: not recorded although its tests passed", text, StringComparison.Ordinal);
        Assert.Equal((1, 1, 12, "strict (2026-10-07)"), (read.Killing, read.Inconclusive, read.TestClasses, read.Judging));
        Assert.Equal(12, Assert.Single(read.Maps).Classes);
        Assert.Single(read.Incidents);
    }

    [Fact]
    public void Fewer_recorded_classes_than_the_previous_round_is_an_incident_also_against_a_summary_without_class_counts()
    {
        // The previous round (runs-full-1006 and before) kept the class counts only in each repository's own report.
        var previous = Path.Combine(_directory, "previous");
        Directory.CreateDirectory(Path.Combine(previous, "a"));
        File.WriteAllText(Path.Combine(previous, "summary.json"), """
            { "repositories": [ { "name": "a", "status": "done", "killing": 3, "missed": 0, "selectedShare": 0.5, "syntheticKilling": 3,
              "commits": 0, "missedMutants": [], "sha": "abcdef1234", "testdettaCommit": "c0ffee", "toolchain": null, "seconds": 9, "reused": 0 } ] }
            """);
        File.WriteAllText(Path.Combine(previous, "a", "synthetic.json"), """
            { "sha": "abcdef1234", "error": null, "testClasses": 933, "mutants": [] }
            """);
        WriteReport("a", """
            { "sha": "abcdef1234", "error": null, "judging": "strict (2026-10-07)", "testClasses": 612, "seconds": 1,
              "maps": [ { "project": "A.Tests", "classes": 612, "failed": 0 } ], "incidents": [],
              "mutants": [ { "target": "A.Hit", "inconclusive": false, "selectedShare": 0.5, "killed": ["T: X"], "missed": [] } ] }
            """);

        var text = BatchSummary.Write(_directory, Corpus.Load(WriteCorpus("a")), previous: previous);

        Assert.Contains("- a: the maps recorded 612 test classes, 933 in the previous round at the same commit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_without_judging_rules_was_judged_leniently_and_the_comparison_says_the_numbers_are_not_comparable()
    {
        WriteReport("a", """
            { "sha": "abcdef1234", "error": null, "mutants": [ { "target": "A.Hit", "inconclusive": false, "selectedShare": 0.5, "killed": ["T: X"], "missed": [] } ] }
            """);
        BatchSummary.Write(_directory, Corpus.Load(WriteCorpus("a")));
        var lenient = Assert.Single(BatchSummary.ReadSummary(_directory)!);
        var strict = lenient with { Judging = MutantJudge.Rules };

        Assert.Equal(BatchSummary.LenientRules, lenient.Judging);
        Assert.Contains($"judged {MutantJudge.Rules}, the previous run lenient (up to runs-full-1006): misses, catches and inconclusive mutants are not comparable",
            BatchComparison.Markdown([lenient], [strict]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_TestDetta_failed_to_record_and_maps_that_say_nothing_are_incidents()
    {
        var root = Path.Combine(_directory, "repo");
        var maps = Path.Combine(_directory, "maps");
        Directory.CreateDirectory(Path.Combine(maps, "abc"));
        File.WriteAllLines(Path.Combine(maps, "abc", "not-recorded.tsv"), [
            "tests/A.Tests/A.Tests.csproj\tpassed\tthe tests of A.Tests.csproj ran and passed (3), but TestDetta's tracker wrote no coverage results",
            "tests/B.Tests/B.Tests.csproj\t-\tBuilding B.Tests.csproj failed with exit code 1",
        ]);
        var settings = new RunSettings(Parallelism: 1, Mutants: 1, Projects: [], new RepoCommands(Setup: null, Cleanup: null), Framework: null);
        var executor = new TestExecutor(new TestDetta.Eval.ExecutionContext(root, settings, maps), NullLogger.Instance);
        var steps = new EvalSteps(new EvalWorkspace(new EvalGit(root), root, maps, executor, NullLoggerFactory.Instance), settings);

        var incidents = steps.Incidents("abc", new Dictionary<string, CoverageMap>
        {
            ["c"] = Map("tests/C.Tests/C.Tests.csproj", classes: [], failed: ["C.Tests.X"]),
            ["d"] = Map("tests/D.Tests/D.Tests.csproj", classes: ["D.Tests.X"], failed: ["D.Tests.X"]),
            ["e"] = Map("tests/E.Tests/E.Tests.csproj", classes: ["E.Tests.X"], failed: null),
            ["f"] = Map("tests/F.Tests/F.Tests.csproj", classes: ["F.Tests.X", "F.Tests.Y"], failed: ["F.Tests.X"]),
        });

        Assert.Equal(
            [
                "tests/A.Tests/A.Tests.csproj: not recorded although its tests passed: the tests of A.Tests.csproj ran and passed (3), but TestDetta's tracker wrote no coverage results",
                "tests/C.Tests/C.Tests.csproj: the map recorded 0 test classes (1 failed or unrecorded)",
                "tests/D.Tests/D.Tests.csproj: every one of the map's 1 test classes failed while recording",
                "tests/E.Tests/E.Tests.csproj: tests failed while recording and no report says which; the map is not used",
            ],
            incidents);
    }

    [Fact]
    public void The_outcome_cache_ignores_outcomes_judged_by_the_old_rules_and_keeps_raw_runs()
    {
        var target = Target("Lib.A", "Run");
        File.WriteAllText(Path.Combine(_directory, "abc.json"), """
            { "Version": 2, "Mutants": { "Lib.A|Run|Throw": { "MutatedMethods": 1, "Failed": ["T: X"], "FailsAloneWithMutant": {} } }, "FailsAlone": { "T: X": false } }
            """);

        var old = OutcomeCache.Open(_directory, "abc")!;
        Assert.Equal(0, old.Count);
        Assert.Null(old.Find(target));

        old.Store(target, new MutantOutcome(1, ["T"]) { Reached = ["tests/T/T.csproj"] });
        old.StoreRun(target, "tests/T/T.csproj|Whole|with|1|-", new StoredRun(false, "timed out", [], [], 0));
        old.StoreControl("tests/T/T.csproj|Only|without|1|abc", new StoredRun(true, null, ["T.X"], ["T.X", "T.Y"], 2));

        var reopened = OutcomeCache.Open(_directory, "abc")!;
        var stored = reopened.Find(target)!;
        Assert.Equal(["tests/T/T.csproj"], stored.Reached);
        Assert.Equal("timed out", stored.Runs["tests/T/T.csproj|Whole|with|1|-"].Problem);
        var control = reopened.Control("tests/T/T.csproj|Only|without|1|abc")!.ToRun();
        Assert.True(control.Complete);
        Assert.Contains("T.Y", control.Executed);
        Assert.Contains("T.X", control.Failed);
    }

    private static SyntheticTarget Target(string type, string member) => new(new CoveredMethod(type, member), [new SourceEdit("/x.cs", 0, "x")], Covered: true);

    private static CoverageMap Map(string project, string[] classes, string[]? failed) =>
        new(new CoverageMapHeader(project, "abc", DateTimeOffset.UtcNow), [], new CoverageHits(classes.ToDictionary(static c => c, static IReadOnlyList<int> (_) => []), new Dictionary<string, IReadOnlyList<int>>(), []))
        {
            FailedClasses = failed?.ToHashSet(StringComparer.Ordinal),
        };

    private void WriteReport(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_directory, name));
        File.WriteAllText(Path.Combine(_directory, name, "stdout.md"), "done");
        File.WriteAllText(Path.Combine(_directory, name, "synthetic.json"), json);
    }

    private string WriteCorpus(string name)
    {
        var path = Path.Combine(_directory, "corpus.json");
        File.WriteAllText(path, $$"""{ "repositories": [ { "name": "{{name}}", "url": "x" } ] }""");
        return path;
    }
}
