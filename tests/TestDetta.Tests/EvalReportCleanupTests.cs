using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>Test reports an evaluation deletes when it ends.</summary>
public sealed class EvalReportCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "td-trx-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Reports_of_missed_and_crashed_mutants_are_kept_and_the_rest_deleted()
    {
        string[] runs = ["001-baseline", "002-synthetic-1", "003-synthetic-2", "004-synthetic-2-confirm", "005-synthetic-3", "006-synthetic-4", "007-synthetic-12"];
        foreach (var run in runs)
        {
            Directory.CreateDirectory(Path.Combine(_directory, run));
            File.WriteAllText(Path.Combine(_directory, run, "r.trx"), "<TestRun />");
        }

        SyntheticMutant?[] results =
        [
            Mutant(missed: []),
            Mutant(missed: ["Tests: CalcTests"]),
            Mutant(missed: []) with { Inconclusive = true },
            Mutant(missed: []) with { CaughtByCrash = true },
        ];

        SyntheticEvaluator.RemoveReports(_directory, results);

        // A run whose mutant is not in the results (from another evaluation in the same directory) goes too.
        Assert.Equal(["003-synthetic-2", "004-synthetic-2-confirm", "005-synthetic-3", "006-synthetic-4"],
            Directory.EnumerateDirectories(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    private static SyntheticMutant Mutant(IReadOnlyList<string> missed) =>
        new(new SyntheticTarget(new CoveredMethod("Calc.Calculator", "Add"), [], Covered: true), Detected: true, MutatedMethods: 1, KilledClasses: missed, MissedClasses: missed, SelectedShare: 0.1, Inconclusive: false);
}
