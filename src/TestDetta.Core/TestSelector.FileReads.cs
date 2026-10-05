using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>The test classes of one test project that read a changed file no project owns.</summary>
/// <param name="Project">The test project.</param>
/// <param name="WholeProject">Why the whole project runs (a read outside the tests, in a fixture or while initializing), or null.</param>
/// <param name="Classes">Reading classes, each with its evidence.</param>
internal sealed record FileReaders(ProjectNode Project, string? WholeProject, SortedDictionary<string, string> Classes);

public sealed partial class TestSelector
{
    /// <summary>
    /// The readers of the changed files that belong to no project (docs/design/runtime-file-reads.md):
    /// per test project whose map recorded file reads, the classes that read such a file or listed its
    /// directory. A read outside the tests, in a fixture's setup or while initializing something shared
    /// runs the whole project. A file nobody read stays ignored, and a map recorded before reads were
    /// knows none.
    /// </summary>
    /// <param name="request">Selection input.</param>
    private Dictionary<string, FileReaders> FindFileReaders(TestSelectionRequest request)
    {
        var readers = new Dictionary<string, FileReaders>(Paths.Comparer);
        var unmapped = request.Impact.UnmappedFiles;
        if (unmapped.Count == 0)
        {
            return readers;
        }

        foreach (var (path, map) in request.CoverageMaps)
        {
            if (map.FileReads is not { } reads || request.Graph.Find(path) is not { } project)
            {
                continue;
            }

            var commit = map.Commit[..Math.Min(12, map.Commit.Length)];
            string? whole = null;
            foreach (var (place, paths) in (ReadOnlySpan<(string, IReadOnlyList<string>)>)[
                ("outside its tests", reads.Ambient),
                ("while initializing what its tests share", reads.Initialization),
                .. reads.Fixtures.Select(static f => ($"in the setup of {f.Key}", f.Value))])
            {
                if (FirstRead(paths, unmapped) is { } file)
                {
                    whole = $"it read {file} {place} (map {commit})";
                    break;
                }
            }

            var classes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (testClass, paths) in reads.Classes)
            {
                if (FirstRead(paths, unmapped) is { } file)
                {
                    classes[testClass] = $"read {file} (map {commit})";
                }
            }

            if (whole is not null || classes.Count > 0)
            {
                LogFileReaders(path, classes.Count, whole ?? "-");
                readers[path] = new FileReaders(project, whole, classes);
            }
        }

        return readers;
    }

    /// <summary>
    /// The files of <paramref name="unmapped"/> that some map recorded a read of, anywhere; null when no
    /// map recorded reads.
    /// </summary>
    /// <param name="unmapped">Changed files outside every project, repository-relative.</param>
    /// <param name="maps">Coverage maps by test project.</param>
    internal static IReadOnlyList<string>? FilesRead(IReadOnlyList<string> unmapped, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var reads = maps.Values.Select(static m => m.FileReads).OfType<FileReads>().ToList();
        if (reads.Count == 0)
        {
            return null;
        }

        var all = reads.SelectMany(static r => r.Classes.Values.Concat(r.Fixtures.Values).Append(r.Ambient).Append(r.Initialization)).ToList();
        return [.. unmapped.Where(f => all.Exists(paths => FirstRead(paths, [f]) is not null))];
    }

    /// <summary>The first of <paramref name="files"/> that one of <paramref name="reads"/> names, itself or through a listed directory above it; null for none.</summary>
    /// <param name="reads">Recorded reads; a directory ends in '/'.</param>
    /// <param name="files">Changed files, repository-relative.</param>
    private static string? FirstRead(IReadOnlyList<string> reads, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            foreach (var read in reads)
            {
                if (string.Equals(read, file, Paths.Comparison) || (read.EndsWith('/') && file.StartsWith(read, Paths.Comparison)))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="decisions"/> with the readers of changed files added: a project that runs
    /// completely stays so; one that runs some classes, or none, runs the readers as well; a project the
    /// changes did not affect otherwise is added with its readers.
    /// </summary>
    /// <param name="decisions">Decisions per affected test project.</param>
    /// <param name="readers">Readers per test project.</param>
    /// <param name="maps">Coverage maps, for the classes a reader project skips.</param>
    private static List<TestProjectSelection> WithFileReaders(List<TestProjectSelection> decisions, Dictionary<string, FileReaders> readers, IReadOnlyDictionary<string, CoverageMap> maps)
    {
        var result = new List<TestProjectSelection>(decisions.Count + readers.Count);
        var seen = new HashSet<string>(Paths.Comparer);
        foreach (var decision in decisions)
        {
            seen.Add(decision.Project.Path);
            result.Add(readers.TryGetValue(decision.Project.Path, out var reading) ? Merge(decision, reading) : decision);
        }

        foreach (var (path, reading) in readers.Where(r => !seen.Contains(r.Key)))
        {
            var unaffected = new TestProjectSelection(reading.Project, TestRunMode.Skipped, [], "nothing it compiles changed")
            {
                Decisions = [.. (maps.TryGetValue(path, out var map) ? map.Classes.Keys : [])
                    .Order(StringComparer.Ordinal)
                    .Select(static c => new ClassDecision(c, Selected: false, ClassDecisionKind.Skipped, "read none of the changed files"))],
            };
            result.Add(Merge(unaffected, reading));
        }

        result.Sort(static (a, b) => string.Compare(a.Project.Path, b.Project.Path, StringComparison.Ordinal));
        return result;
    }

    /// <summary>One decision with a project's readers of changed files added.</summary>
    /// <param name="decision">The decision without them.</param>
    /// <param name="reading">The readers.</param>
    private static TestProjectSelection Merge(TestProjectSelection decision, FileReaders reading)
    {
        if (decision.Mode == TestRunMode.All)
        {
            return decision;
        }

        if (reading.WholeProject is { } why)
        {
            return decision with
            {
                Mode = TestRunMode.All,
                TestClasses = [],
                Reason = why,
                Decisions = [.. decision.Decisions.Select(d => d with { Selected = true, Kind = ClassDecisionKind.WholeProject, Evidence = why })],
            };
        }

        var classes = decision.TestClasses.Union(reading.Classes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var decisions = decision.Decisions.ToDictionary(static d => d.TestClass, StringComparer.Ordinal);
        foreach (var (testClass, evidence) in reading.Classes)
        {
            if (!decisions.TryGetValue(testClass, out var existing) || !existing.Selected)
            {
                decisions[testClass] = new ClassDecision(testClass, Selected: true, ClassDecisionKind.FileRead, evidence);
            }
        }

        var reason = decision.Mode == TestRunMode.Filtered
            ? $"{decision.Reason}; {reading.Classes.Count} test class(es) read a changed file"
            : $"{reading.Classes.Count} test class(es) read a changed file";
        return decision with
        {
            Mode = TestRunMode.Filtered,
            TestClasses = classes,
            Reason = reason,
            Decisions = [.. decisions.Values.OrderBy(static d => d.TestClass, StringComparer.Ordinal)],
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{TestProject}: {Classes} test class(es) read a changed file that no project owns; whole project: {WholeProject}")]
    private partial void LogFileReaders(string testProject, int classes, string wholeProject);
}
