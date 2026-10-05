using System.Text.Json;

namespace TestDetta.Eval;

/// <summary>
/// What the tests did against each mutant of one commit, kept across evaluations so a fix to TestDetta
/// does not cost the test runs again. Which classes a mutant fails does not depend on TestDetta, only on
/// the commit, the mutant and how the evaluation injects and runs it, so after a fix only TestDetta's
/// selection is computed anew and compared with the stored failures. A run that crashed, hung or
/// lacked a report stores only that fact (see <see cref="MutantOutcome.FullRunIncomplete"/>).
/// </summary>
internal sealed class OutcomeCache
{
    /// <summary>
    /// Version of everything that decides a stored outcome besides the commit and the mutant:
    /// injection, test runs, baseline and confirmation rules. Raise it with any change there; files
    /// written with another version are ignored. 2: mutants switched on in a schema.
    /// </summary>
    public const int Version = 2;

    /// <summary>Serializer settings for the cache file.</summary>
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The cache file of the commit.</summary>
    private readonly string _path;

    /// <summary>Contents of the cache file.</summary>
    private readonly OutcomeFile _file;

    /// <summary>Guards the file's contents: mutants of one commit may run at once.</summary>
    private readonly Lock _gate = new();

    /// <summary>Reads the cache file of a commit, or starts an empty one.</summary>
    /// <param name="path">Cache file.</param>
    private OutcomeCache(string path)
    {
        _path = path;
        _file = Read(path) is { Version: Version } file ? file : new OutcomeFile { Version = Version };
    }

    /// <summary>The cache named <paramref name="key"/> in <paramref name="directory"/>; null when caching is off.</summary>
    /// <param name="directory">Cache directory of the repository, or null.</param>
    /// <param name="key">The commit the mutants are applied to, with the toolchain when one is pinned.</param>
    public static OutcomeCache? Open(string? directory, string key)
    {
        if (directory is null)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        return new OutcomeCache(Path.Combine(directory, key + ".json"));
    }

    /// <summary>Number of stored mutant outcomes.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _file.Mutants.Count;
            }
        }
    }

    /// <summary>The stored outcome of <paramref name="target"/>, or null.</summary>
    /// <param name="target">Mutant to look up.</param>
    public MutantOutcome? Find(SyntheticTarget target)
    {
        lock (_gate)
        {
            return _file.Mutants.GetValueOrDefault(Key(target));
        }
    }

    /// <summary>Stores the outcome of <paramref name="target"/> and writes the file.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="outcome">What the tests did.</param>
    public void Store(SyntheticTarget target, MutantOutcome outcome)
    {
        lock (_gate)
        {
            _file.Mutants[Key(target)] = outcome;
            Save();
        }
    }

    /// <summary>Whether a class (<c>Project: Class</c>) fails alone without any mutant; null when unknown.</summary>
    /// <param name="testClass">Test class.</param>
    public bool? FailsAlone(string testClass)
    {
        lock (_gate)
        {
            return _file.FailsAlone.TryGetValue(testClass, out var fails) ? fails : null;
        }
    }

    /// <summary>Stores whether a class fails alone without any mutant and writes the file.</summary>
    /// <param name="testClass">Test class (<c>Project: Class</c>).</param>
    /// <param name="fails">True when it failed.</param>
    public void StoreFailsAlone(string testClass, bool fails)
    {
        lock (_gate)
        {
            _file.FailsAlone[testClass] = fails;
            Save();
        }
    }

    /// <summary>The stored run without mutants, or null when none was stored.</summary>
    public StoredBaseline? Baseline
    {
        get
        {
            lock (_gate)
            {
                return _file.Baseline;
            }
        }
    }

    /// <summary>
    /// Stores the run without mutants and writes the file. Only a run in which no project crashed or
    /// hung belongs here: a project that crashed under load once would otherwise stay left out of
    /// every later round.
    /// </summary>
    /// <param name="baseline">The run, with paths relative to the clone.</param>
    public void StoreBaseline(StoredBaseline baseline)
    {
        lock (_gate)
        {
            _file.Baseline = baseline;
            Save();
        }
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

        /// <summary>The run without mutants the outcomes were measured against; null when none was stored.</summary>
        public StoredBaseline? Baseline { get; set; }
    }
}

/// <summary>
/// A run of the full suite without mutants in which no project crashed or hung, kept so a round whose mutants are all stored
/// needs neither a build nor a test run. Paths are relative to the clone, which moves between rounds.
/// </summary>
/// <param name="Candidates">Test projects the evaluation considered, before building; the stored run is used only when today's list is the same.</param>
/// <param name="Reporting">Test projects that built and reported; the ones the outcomes were measured with.</param>
/// <param name="FailedByProject">Classes that failed without any mutant, by project.</param>
/// <param name="Seconds">How long the run took, for the report.</param>
/// <param name="Total">Number of tests that ran.</param>
internal sealed record StoredBaseline(
    List<string> Candidates,
    List<string> Reporting,
    Dictionary<string, List<string>> FailedByProject,
    double Seconds,
    int Total)
{
    /// <summary>Test projects that built but wrote no report without crashing, as the report names them; left out like in the run itself.</summary>
    public List<string> Unreported { get; init; } = [];
}

/// <summary>What the tests did against one mutant, independent of TestDetta's selection.</summary>
/// <param name="MutatedMethods">Number of compiled methods the mutant changed.</param>
/// <param name="Failed">
/// Test classes (<c>Project: Class</c>) that failed in the full run with the mutant and not in the
/// run without, before any confirmation.
/// </param>
/// <param name="FailsAloneWithMutant">
/// Classes run alone with the mutant in place, and whether they failed again; only classes that were
/// missed when the outcome was recorded are here.
/// </param>
/// <param name="Evaluated">
/// Names of the test projects the evaluation ran when the outcome was recorded. A project left out then
/// (its run without mutants crashed) may fail now, so an outcome is reused only when every project
/// evaluated now is in this list. Null in files written before it was stored: such outcomes are run again.
/// </param>
internal sealed record MutantOutcome(int MutatedMethods, List<string> Failed, Dictionary<string, bool> FailsAloneWithMutant, List<string>? Evaluated = null)
{
    /// <summary>
    /// True when the full run with the mutant crashed or hung, so <see cref="Failed"/> is unknown and
    /// empty. Such a mutant is judged by running what TestDetta left out (the only place a miss can
    /// be); later rounds skip the full run, which mostly waits for its timeout.
    /// </summary>
    public bool FullRunIncomplete { get; init; }

    /// <summary>
    /// With <see cref="FullRunIncomplete"/>: the fingerprint of the selection whose left-out classes
    /// were run and missed nothing. The same selection is not run again; any other is. Null when that
    /// run missed a class or did not complete.
    /// </summary>
    public string? NothingMissedWith { get; init; }
}
