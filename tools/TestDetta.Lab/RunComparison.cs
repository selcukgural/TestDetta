using System.Globalization;
using System.Text.Json;
using TestDetta.Cli;

namespace TestDetta.Lab;

/// <summary>One mutant of an evaluation run, as <c>synthetic.json</c> reports it.</summary>
/// <param name="Target">The changed member (<c>Type.Member</c>) or data file.</param>
/// <param name="Kind">The mutant kind.</param>
/// <param name="Share">Share of the test classes the selection ran, 0 to 1; null when the run gave none.</param>
/// <param name="Killed">True when some test failed on the mutant.</param>
/// <param name="Missed">True when a test class that failed was not selected.</param>
internal sealed record RunMutant(string Target, string Kind, double? Share, bool Killed, bool Missed);

/// <summary>A mutant both runs judged whose share differs.</summary>
/// <param name="Target">The changed member.</param>
/// <param name="Kind">The mutant kind.</param>
/// <param name="First">Share in the first run.</param>
/// <param name="Second">Share in the second run.</param>
internal sealed record ShareChange(string Target, string Kind, double First, double Second);

/// <summary>How two runs of one repository compare.</summary>
/// <param name="Repository">The repository.</param>
/// <param name="Common">Mutants both runs judged.</param>
/// <param name="OnlyFirst">Mutants only the first run has; a comparison on the same sample has none.</param>
/// <param name="OnlySecond">Mutants only the second run has.</param>
/// <param name="FirstMean">Mean share over the common mutants with a share, first run.</param>
/// <param name="SecondMean">The same, second run.</param>
/// <param name="Changes">Common mutants whose share differs, the largest difference first.</param>
/// <param name="FirstMissed">Failing mutants the first run missed, of the common ones.</param>
/// <param name="SecondMissed">The same, second run.</param>
/// <param name="Failing">Common mutants some test failed on in either run.</param>
internal sealed record RepositoryComparison(
    string Repository,
    int Common,
    int OnlyFirst,
    int OnlySecond,
    double FirstMean,
    double SecondMean,
    IReadOnlyList<ShareChange> Changes,
    IReadOnlyList<string> FirstMissed,
    IReadOnlyList<string> SecondMissed,
    int Failing);

/// <summary>
/// Compares two evaluation runs mutant by mutant, as every A/B of two builds needs: which changes the second build
/// selects narrower or wider, whether either missed a failing change, and whether both judged the same sample (a
/// different map can move a seed's pick; the second side should replay the first, docs/evaluation.md).
/// </summary>
internal static class RunComparison
{
    /// <summary>Compares every repository both runs hold, in name order.</summary>
    /// <param name="first">The first run's folder (<c>&lt;repository&gt;/synthetic.json</c> in it).</param>
    /// <param name="second">The second run's folder.</param>
    /// <exception cref="IOException">The runs share no repository.</exception>
    public static List<RepositoryComparison> Compare(string first, string second)
    {
        var comparisons = new List<RepositoryComparison>();
        foreach (var directory in Directory.GetDirectories(first).Order(StringComparer.Ordinal))
        {
            var repository = Path.GetFileName(directory);
            var a = Path.Combine(directory, "synthetic.json");
            var b = Path.Combine(second, repository, "synthetic.json");
            if (File.Exists(a) && File.Exists(b))
            {
                comparisons.Add(Compare(repository, Read(a), Read(b)));
            }
        }

        return comparisons.Count > 0 ? comparisons : throw new IOException($"{first} and {second} share no repository with a synthetic.json");
    }

    /// <summary>Compares the mutants of one repository.</summary>
    /// <param name="repository">The repository.</param>
    /// <param name="first">The first run's mutants.</param>
    /// <param name="second">The second run's mutants.</param>
    public static RepositoryComparison Compare(string repository, IReadOnlyList<RunMutant> first, IReadOnlyList<RunMutant> second)
    {
        var byKey = new Dictionary<(string, string), RunMutant>();
        foreach (var mutant in second)
        {
            byKey.TryAdd((mutant.Target, mutant.Kind), mutant);
        }

        var common = 0;
        var failing = 0;
        double firstSum = 0, secondSum = 0;
        var shared = 0;
        var changes = new List<ShareChange>();
        List<string> firstMissed = [], secondMissed = [];
        var matched = new HashSet<(string, string)>();
        foreach (var a in first)
        {
            if (!byKey.TryGetValue((a.Target, a.Kind), out var b) || !matched.Add((a.Target, a.Kind)))
            {
                continue;
            }

            common++;
            failing += a.Killed || b.Killed ? 1 : 0;
            if (a.Missed)
            {
                firstMissed.Add(a.Target);
            }

            if (b.Missed)
            {
                secondMissed.Add(b.Target);
            }

            if (a.Share is not { } x || b.Share is not { } y)
            {
                continue;
            }

            shared++;
            firstSum += x;
            secondSum += y;
            if (Math.Abs(x - y) > 1e-9)
            {
                changes.Add(new ShareChange(a.Target, a.Kind, x, y));
            }
        }

        changes.Sort(static (l, r) => Math.Abs(r.Second - r.First).CompareTo(Math.Abs(l.Second - l.First)));
        return new RepositoryComparison(
            repository,
            common,
            first.Count - common,
            second.Count - common,
            shared == 0 ? 0 : firstSum / shared,
            shared == 0 ? 0 : secondSum / shared,
            changes,
            firstMissed,
            secondMissed,
            failing);
    }

    /// <summary>Writes a row per repository, then the largest share changes of each.</summary>
    /// <param name="comparisons">The comparisons.</param>
    /// <param name="writer">Where the report goes.</param>
    /// <param name="style">Console styling.</param>
    public static void Write(IReadOnlyList<RepositoryComparison> comparisons, TextWriter writer, ConsoleStyle style)
    {
        var table = new TextTable(
            style,
            new TextColumn("Repository"),
            new TextColumn("Common", AlignRight: true),
            new TextColumn("Only A/B", AlignRight: true),
            new TextColumn("Share A", AlignRight: true),
            new TextColumn("Share B", AlignRight: true),
            new TextColumn("Narrower", AlignRight: true),
            new TextColumn("Wider", AlignRight: true),
            new TextColumn("Missed A", AlignRight: true),
            new TextColumn("Missed B", AlignRight: true));
        foreach (var c in comparisons)
        {
            var narrower = c.Changes.Count(static s => s.Second < s.First);
            table.Add(
                new Cell(c.Repository),
                new Cell(Number(c.Common), Role.Number),
                new Cell($"{c.OnlyFirst}/{c.OnlySecond}", c.OnlyFirst + c.OnlySecond > 0 ? Role.Notice : Role.Muted),
                new Cell(Percent(c.FirstMean)),
                new Cell(Percent(c.SecondMean), c.SecondMean > c.FirstMean + 1e-9 ? Role.Notice : c.SecondMean < c.FirstMean - 1e-9 ? Role.Good : Role.Plain),
                new Cell(Number(narrower)),
                new Cell(Number(c.Changes.Count - narrower)),
                new Cell($"{c.FirstMissed.Count}/{c.Failing}", c.FirstMissed.Count > 0 ? Role.Bad : Role.Good),
                new Cell($"{c.SecondMissed.Count}/{c.Failing}", c.SecondMissed.Count > 0 ? Role.Bad : Role.Good));
        }

        table.Write(writer);
        foreach (var c in comparisons)
        {
            foreach (var (missed, side) in (ReadOnlySpan<(IReadOnlyList<string>, string)>)[(c.FirstMissed, "A"), (c.SecondMissed, "B")])
            {
                foreach (var target in missed)
                {
                    writer.WriteLine(style.Paint(Role.Bad, $"{c.Repository}: {side} missed {target}"));
                }
            }

            foreach (var change in c.Changes.Take(5))
            {
                writer.WriteLine($"{c.Repository}: {change.Target} ({change.Kind}) {Percent(change.First)} -> {Percent(change.Second)}");
            }
        }
    }

    /// <summary>Reads the mutants of a <c>synthetic.json</c>.</summary>
    /// <param name="path">The report.</param>
    private static List<RunMutant> Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var mutants = new List<RunMutant>();
        foreach (var mutant in document.RootElement.GetProperty("mutants").EnumerateArray())
        {
            mutants.Add(new RunMutant(
                mutant.GetProperty("target").GetString() ?? "",
                mutant.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "",
                mutant.TryGetProperty("selectedShare", out var share) && share.ValueKind == JsonValueKind.Number ? share.GetDouble() : null,
                NonEmpty(mutant, "killed"),
                NonEmpty(mutant, "missed")));
        }

        return mutants;

        static bool NonEmpty(JsonElement mutant, string name) =>
            mutant.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;
    }

    /// <summary>A share as a percentage with one decimal.</summary>
    /// <param name="share">0 to 1.</param>
    private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    /// <summary>An integer in the invariant culture.</summary>
    /// <param name="value">The number.</param>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
