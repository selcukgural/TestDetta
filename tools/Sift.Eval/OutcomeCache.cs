using System.Text.Json;

namespace Sift.Eval;

/// <summary>
/// What the tests did against each mutant of one commit, kept across evaluations so a fix to Sift
/// does not cost the test runs again. Which classes a mutant fails does not depend on Sift, only on
/// the commit, the mutant and how the evaluation injects and runs it, so after a fix only Sift's
/// selection is computed anew and compared with the stored failures. Runs that crashed, hung or
/// lacked a report are never stored; they are run again.
/// </summary>
internal sealed class OutcomeCache
{
    /// <summary>
    /// Version of everything that decides a stored outcome besides the commit and the mutant:
    /// injection, test runs, baseline and confirmation rules. Raise it with any change there; files
    /// written with another version are ignored.
    /// </summary>
    public const int Version = 1;

    /// <summary>Serializer settings for the cache file.</summary>
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The cache file of the commit.</summary>
    private readonly string _path;

    /// <summary>Contents of the cache file.</summary>
    private readonly OutcomeFile _file;

    /// <summary>Reads the cache file of a commit, or starts an empty one.</summary>
    /// <param name="path">Cache file.</param>
    private OutcomeCache(string path)
    {
        _path = path;
        _file = Read(path) is { Version: Version } file ? file : new OutcomeFile { Version = Version };
    }

    /// <summary>The cache of <paramref name="sha"/> in <paramref name="directory"/>; null when caching is off.</summary>
    /// <param name="directory">Cache directory of the repository, or null.</param>
    /// <param name="sha">Commit the mutants are applied to.</param>
    public static OutcomeCache? Open(string? directory, string sha)
    {
        if (directory is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        return new OutcomeCache(Path.Combine(directory, sha + ".json"));
    }

    /// <summary>Number of stored mutant outcomes.</summary>
    public int Count => _file.Mutants.Count;

    /// <summary>The stored outcome of <paramref name="target"/>, or null.</summary>
    /// <param name="target">Mutant to look up.</param>
    public MutantOutcome? Find(SyntheticTarget target) =>
        _file.Mutants.GetValueOrDefault(Key(target));

    /// <summary>Stores the outcome of <paramref name="target"/> and writes the file.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="outcome">What the tests did.</param>
    public void Store(SyntheticTarget target, MutantOutcome outcome)
    {
        _file.Mutants[Key(target)] = outcome;
        Save();
    }

    /// <summary>Whether a class (<c>Project: Class</c>) fails alone without any mutant; null when unknown.</summary>
    /// <param name="testClass">Test class.</param>
    public bool? FailsAlone(string testClass) =>
        _file.FailsAlone.TryGetValue(testClass, out var fails) ? fails : null;

    /// <summary>Stores whether a class fails alone without any mutant and writes the file.</summary>
    /// <param name="testClass">Test class (<c>Project: Class</c>).</param>
    /// <param name="fails">True when it failed.</param>
    public void StoreFailsAlone(string testClass, bool fails)
    {
        _file.FailsAlone[testClass] = fails;
        Save();
    }

    /// <summary>Key of a mutant: member and kind.</summary>
    /// <param name="target">The mutant.</param>
    private static string Key(SyntheticTarget target) =>
        $"{target.Member.TypeName}|{target.Member.MemberName}|{target.Kind}";

    /// <summary>Writes the file atomically, so an evaluation killed while writing leaves the old one.</summary>
    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_file, Options));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>Reads a cache file; null when it is missing or unreadable.</summary>
    /// <param name="path">Cache file.</param>
    private static OutcomeFile? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<OutcomeFile>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Contents of one commit's cache file.</summary>
    private sealed class OutcomeFile
    {
        /// <summary>See <see cref="OutcomeCache.Version"/>.</summary>
        public int Version { get; set; }

        /// <summary>Outcomes by mutant key (<c>Type|Member|Kind</c>).</summary>
        public Dictionary<string, MutantOutcome> Mutants { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Whether a class (<c>Project: Class</c>) fails alone without any mutant.</summary>
        public Dictionary<string, bool> FailsAlone { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>What the tests did against one mutant, independent of Sift's selection.</summary>
/// <param name="MutatedMethods">Number of compiled methods the mutant changed.</param>
/// <param name="Failed">
/// Test classes (<c>Project: Class</c>) that failed in the full run with the mutant and not in the
/// run without, before any confirmation.
/// </param>
/// <param name="FailsAloneWithMutant">
/// Classes run alone with the mutant in place, and whether they failed again; only classes that were
/// missed when the outcome was recorded are here.
/// </param>
internal sealed record MutantOutcome(int MutatedMethods, List<string> Failed, Dictionary<string, bool> FailsAloneWithMutant);
