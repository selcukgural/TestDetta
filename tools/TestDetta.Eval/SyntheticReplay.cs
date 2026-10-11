using System.Text.Json;

namespace TestDetta.Eval;

/// <summary>
/// The changes of an earlier synthetic evaluation (<c>--replay</c>), read from its <c>synthetic.json</c>: every
/// member with the kind it had, and every data file, in the order they were evaluated. After a fix, the same changes
/// run again exactly, whatever seed or candidate list a new sample would have (docs/evaluation.md, "Seeds and replays").
/// </summary>
/// <param name="Source">The file it was read from, for the reports.</param>
/// <param name="Sha">The commit the changes were made at; a replay at another commit still runs, with a warning.</param>
/// <param name="Seed">The earlier run's seed, which picks nothing in a replay; null in a report older than seeds.</param>
/// <param name="Mutants">The changes, in their order.</param>
internal sealed record SyntheticReplay(string Source, string? Sha, int? Seed, IReadOnlyList<ReplayedMutant> Mutants)
{
    /// <summary>
    /// Reads <paramref name="path"/>: a <c>synthetic.json</c>, or the folder of one repository's results that holds it.
    /// </summary>
    /// <param name="path">File or folder.</param>
    /// <exception cref="InvalidDataException">The file is missing, not a synthetic report, or names an unknown kind.</exception>
    public static SyntheticReplay Load(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "synthetic.json") : path;
        if (!File.Exists(file))
        {
            throw new InvalidDataException($"No synthetic report to replay at {file}");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("mutants", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{file} holds no mutants to replay");
            }

            var mutants = new List<ReplayedMutant>(list.GetArrayLength());
            foreach (var mutant in list.EnumerateArray())
            {
                var target = mutant.GetProperty("target").GetString();
                var name = mutant.GetProperty("kind").GetString();
                if (string.IsNullOrEmpty(target) || name is null || !MutantKinds.TryParseOne(name, out var kind))
                {
                    throw new InvalidDataException($"{file}: a mutant without a target or with an unknown kind ({name})");
                }

                mutants.Add(new ReplayedMutant(target, kind));
            }

            var sha = root.TryGetProperty("sha", out var commit) ? commit.GetString() : null;
            int? seed = root.TryGetProperty("seed", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : null;
            return new SyntheticReplay(Path.GetFullPath(file), sha, seed, mutants);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"{file} is not a synthetic report: {ex.Message}", ex);
        }
    }

    /// <summary>The data files among <paramref name="mutants"/>, as repository paths.</summary>
    /// <param name="mutants">Changes of an earlier sample.</param>
    public static IReadOnlyList<string> DataFilesOf(IEnumerable<ReplayedMutant> mutants) =>
        [.. mutants.Where(static m => m.Kind == MutantKind.EmptyFile).Select(static m => m.Target.StartsWith(FilePrefix, StringComparison.Ordinal) ? m.Target[FilePrefix.Length..] : m.Target)];

    /// <summary>Before a data file's path in a report's target (<see cref="DataFile.TypeName"/>).</summary>
    private const string FilePrefix = DataFile.TypeName + ".";
}
