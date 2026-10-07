using System.Text.Json;

namespace TestDetta.Eval;

/// <summary>
/// What the tests did against each mutant of one commit, kept across evaluations so a fix to TestDetta
/// does not cost the test runs again. A run's result does not depend on TestDetta, only on the commit,
/// the mutant, the run (its project, which classes it runs, the mutant on or off) and how the evaluation
/// injects and runs it, so every run is stored raw under what it ran (<see cref="RunAsk.Key"/>), and a
/// later evaluation judges again from the stored runs: a changed selection asks for other runs, and only
/// those are made (docs/design/eval-result-cache.md). Runs without any mutant are the same for every
/// mutant of the commit and are stored once for all of them, when they completed.
/// </summary>
internal sealed class OutcomeCache
{
    /// <summary>
    /// Version of everything that decides a stored run besides the commit and the mutant: injection, test
    /// runs, completeness, baseline. Raise it with any change there; files written with another version are
    /// ignored. 2: mutants switched on in a schema. 3: the strict judging (<see cref="MutantJudge.Rules"/>):
    /// raw runs per project and part with their completeness (report outcome, exit code, class count),
    /// in English; nothing of a version 2 file can be judged by it (docs/evaluation.md, "Judging").
    /// </summary>
    public const int Version = 3;

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
            return _file.Mutants.GetValueOrDefault(Key(target)) is { } outcome
                ? outcome with { Runs = new Dictionary<string, StoredRun>(outcome.Runs, StringComparer.Ordinal) }
                : null;
        }
    }

    /// <summary>Stores the outcome of <paramref name="target"/> and writes the file.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="outcome">What the tests did.</param>
    public void Store(SyntheticTarget target, MutantOutcome outcome)
    {
        lock (_gate)
        {
            // A copy: the caller may go on reading its runs while another worker writes the file.
            _file.Mutants[Key(target)] = outcome with { Runs = new Dictionary<string, StoredRun>(outcome.Runs, StringComparer.Ordinal) };
            Save();
        }
    }

    /// <summary>Adds one run with the mutant to the stored outcome of <paramref name="target"/> (stored before) and writes the file.</summary>
    /// <param name="target">The mutant.</param>
    /// <param name="key">What the run ran (<see cref="RunAsk.Key"/>).</param>
    /// <param name="run">Its result.</param>
    public void StoreRun(SyntheticTarget target, string key, StoredRun run)
    {
        lock (_gate)
        {
            if (_file.Mutants.TryGetValue(Key(target), out var outcome))
            {
                outcome.Runs[key] = run;
                Save();
            }
        }
    }

    /// <summary>A stored run without any mutant (<see cref="RunAsk.Key"/>), or null.</summary>
    /// <param name="key">What it ran.</param>
    public StoredRun? Control(string key)
    {
        lock (_gate)
        {
            return _file.Controls.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Stores a run without any mutant and writes the file. Only a complete run belongs here: one that crashed
    /// or hung may have done so under load, and is asked again next time.
    /// </summary>
    /// <param name="key">What it ran.</param>
    /// <param name="run">Its result.</param>
    public void StoreControl(string key, StoredRun run)
    {
        lock (_gate)
        {
            _file.Controls[key] = run;
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

        /// <summary>Complete runs without any mutant, by what they ran (<see cref="RunAsk.Key"/>); the same for every mutant.</summary>
        public Dictionary<string, StoredRun> Controls { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Test classes in each reporting project's report; a whole run with a mutant that reports fewer did not complete.
    /// Null in files written before it was stored: such a baseline is run again.
    /// </summary>
    public Dictionary<string, int>? ClassesByProject { get; init; }
}

/// <summary>What the tests did against one mutant, independent of TestDetta's selection: its runs, raw.</summary>
/// <param name="MutatedMethods">Number of compiled methods the mutant changed; 0 when no test output holds it, and nothing ran.</param>
/// <param name="Evaluated">
/// Names of the test projects the evaluation ran when the outcome was recorded. A project left out then
/// (its run without mutants crashed) may fail now, so an outcome is reused only when every project
/// evaluated now is in this list.
/// </param>
internal sealed record MutantOutcome(int MutatedMethods, List<string>? Evaluated = null)
{
    /// <summary>The test projects that can load the mutant (relative to the clone), which the judging runs.</summary>
    public List<string> Reached { get; init; } = [];

    /// <summary>Test projects (relative to the clone) that did not build with a generator mutant.</summary>
    public List<string> NotBuilt { get; init; } = [];

    /// <summary>Runs with the mutant on, by what they ran (<see cref="RunAsk.Key"/>).</summary>
    public Dictionary<string, StoredRun> Runs { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// True when the mutant is in a source generator and the test projects that see what it writes were
    /// built with it (docs/design/generator-mutants.md); an outcome without it does not hold for such a mutant.
    /// </summary>
    public bool ConsumersCompiled { get; init; }
}

/// <summary>One project's run, as stored (<see cref="ProjectRun"/>).</summary>
/// <param name="Complete">See <see cref="ProjectRun.Complete"/>.</param>
/// <param name="Problem">See <see cref="ProjectRun.Problem"/>.</param>
/// <param name="Failed">Failed classes.</param>
/// <param name="Executed">Classes with a test that ran; kept only for runs of some classes, where the judging asks for it.</param>
/// <param name="Classes">Test classes in its report.</param>
internal sealed record StoredRun(bool Complete, string? Problem, List<string> Failed, List<string> Executed, int Classes)
{
    /// <summary>The run as the judging reads it.</summary>
    public ProjectRun ToRun() => new(
        Complete,
        Problem,
        Failed.ToHashSet(StringComparer.Ordinal),
        Executed.ToHashSet(StringComparer.Ordinal),
        Classes);

    /// <summary>A run to store; the executed classes of a whole run or of all but some classes are not needed and not kept.</summary>
    /// <param name="run">The run.</param>
    /// <param name="part">What it ran.</param>
    public static StoredRun From(ProjectRun run, RunPart part) => new(
        run.Complete,
        run.Problem,
        [.. run.Failed.Order(StringComparer.Ordinal)],
        part == RunPart.Only ? [.. run.Executed.Order(StringComparer.Ordinal)] : [],
        run.Classes);
}
