using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>One call of <see cref="FakeDotnet"/>: what ran, where, and which classes.</summary>
public sealed record FakeCall(IReadOnlyList<string> Arguments, string Directory, string Project, bool OnBase, IReadOnlyList<string> Classes, int Index)
{
    /// <summary>True for a run that names classes in a filter (a rerun of some classes).</summary>
    public bool Filtered { get; init; }
}

/// <summary>
/// Stands in for <c>dotnet test</c> under VSTest in shadow mode's tests (<see cref="Cli.ShadowHooks"/>): it finds the
/// project or the solution's projects in the arguments, runs the configured classes the filter lets through, decides
/// each test's outcome with <see cref="Fails"/>, and writes a TRX report where VSTest would (the results directory,
/// or <c>TestResults</c> beside the project) when a TRX logger is asked for.
/// </summary>
public sealed partial class FakeDotnet(string repoRoot)
{
    private readonly string _root = Paths.ResolveLinks(repoRoot);

    /// <summary>Every call, in order.</summary>
    public List<FakeCall> Calls { get; } = [];

    /// <summary>Test classes and their tests per project file name ("Shop.Tests.csproj").</summary>
    public Dictionary<string, Dictionary<string, string[]>> Classes { get; } = [];

    /// <summary>Projects (file names) of each solution file name; "" for a command without a project argument.</summary>
    public Dictionary<string, string[]> Solutions { get; } = [];

    /// <summary>Assembly names that differ from the project file name.</summary>
    public Dictionary<string, string> AssemblyNames { get; } = [];

    /// <summary>Decides whether a test fails: the call, the class, the test.</summary>
    public Func<FakeCall, string, string, bool> Fails { get; set; } = static (_, _, _) => false;

    /// <summary>False for a call that writes no report (a build error, a crash).</summary>
    public Func<FakeCall, bool> WritesReport { get; set; } = static _ => true;

    /// <summary>Runs before each call; may throw or cancel.</summary>
    public Action<FakeCall>? Before { get; set; }

    /// <summary>Runs after a report was written, with its folder: to add files a real run would leave there.</summary>
    public Action<FakeCall, string>? After { get; set; }

    /// <summary>The calls of the full run and the reruns that ran in the base worktree.</summary>
    public IEnumerable<FakeCall> OnBase => Calls.Where(static c => c.OnBase);

    /// <summary>Runs the "command".</summary>
    public int Run(IReadOnlyList<string> arguments, string directory)
    {
        var projects = ProjectsOf(arguments, directory);
        var allowed = ClassesInFilter(arguments);
        var exitCode = 0;
        if (projects.Count == 0)
        {
            // A command whose projects this fake does not know: recorded, and it passes.
            var call = new FakeCall(arguments, directory, string.Empty, !Paths.IsUnder(Paths.ResolveLinks(directory), _root), [], Calls.Count);
            Calls.Add(call);
            Before?.Invoke(call);
            return 0;
        }

        foreach (var project in projects)
        {
            var name = Path.GetFileName(project);
            var classes = Classes.GetValueOrDefault(name) ?? [];
            var ran = classes.Keys.Where(c => allowed is null || allowed.Contains(c)).Order(StringComparer.Ordinal).ToList();
            var call = new FakeCall(arguments, directory, project, !Paths.IsUnder(Paths.ResolveLinks(directory), _root), ran, Calls.Count) { Filtered = allowed is not null };
            Calls.Add(call);
            Before?.Invoke(call);
            var outcomes = ran.SelectMany(c => classes[c].Select(t => (Class: c, Test: t, Failed: Fails(call, c, t)))).ToList();
            if (outcomes.Any(static o => o.Failed))
            {
                exitCode = 1;
            }

            if (!WritesReport(call))
            {
                exitCode = 1;
                continue;
            }

            if (Logger(arguments) is not { } logger)
            {
                continue;
            }

            var results = Option(arguments, "--results-directory") is { } given ? Path.GetFullPath(given, directory) : Path.Combine(Path.GetDirectoryName(project)!, "TestResults");
            Directory.CreateDirectory(results);
            var file = Regex.Match(logger, "LogFileName=([^;]+)") is { Success: true } named ? named.Groups[1].Value : $"fake_{Guid.NewGuid():N}.trx";
            var assembly = AssemblyNames.GetValueOrDefault(name) ?? Path.GetFileNameWithoutExtension(name);
            WriteTrx(Path.Combine(results, file), Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", assembly + ".dll"), outcomes);
            After?.Invoke(call, results);
        }

        return exitCode;
    }

    /// <summary>Writes a TRX report as VSTest does, with one result per test.</summary>
    public static void WriteTrx(string path, string assembly, IEnumerable<(string Class, string Test, bool Failed)> outcomes)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var list = outcomes.ToList();
        var definitions = new XElement(ns + "TestDefinitions");
        var results = new XElement(ns + "Results");
        foreach (var (testClass, test, failed) in list)
        {
            var id = Guid.NewGuid().ToString();
            definitions.Add(new XElement(ns + "UnitTest", new XAttribute("name", test), new XAttribute("storage", assembly.ToLowerInvariant()), new XAttribute("id", id),
                new XElement(ns + "TestMethod", new XAttribute("codeBase", assembly), new XAttribute("className", testClass), new XAttribute("name", test))));
            results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", id), new XAttribute("testName", $"{testClass}.{test}"),
                new XAttribute("outcome", failed ? "Failed" : "Passed"), new XAttribute("duration", "00:00:00.1000000")));
        }

        new XDocument(new XElement(ns + "TestRun", new XAttribute("id", Guid.NewGuid().ToString()), new XAttribute("name", "fake"),
            new XElement(ns + "Times", new XAttribute("start", "2026-10-07T10:00:00.0+00:00"), new XAttribute("finish", "2026-10-07T10:00:02.0+00:00")),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", list.Any(static o => o.Failed) ? "Failed" : "Completed")),
            definitions,
            results)).Save(path);
    }

    /// <summary>The project files the arguments run, in the tree <paramref name="directory"/> belongs to.</summary>
    private List<string> ProjectsOf(IReadOnlyList<string> arguments, string directory)
    {
        var target = arguments.Count > 1 && arguments[1] == "--project" ? arguments[2]
            : arguments.Count > 1 && !arguments[1].StartsWith('-') ? arguments[1]
            : string.Empty;
        if (target.EndsWith("proj", StringComparison.Ordinal))
        {
            return [Path.GetFullPath(target, directory)];
        }

        var tree = TreeRoot(directory);
        return [.. (Solutions.GetValueOrDefault(Path.GetFileName(target)) ?? []).Select(p => FindProject(tree, p)).OfType<string>()];
    }

    /// <summary>The root of the tree a directory is in: the repository, or the base worktree above it.</summary>
    private string TreeRoot(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".git")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
        }

        return _root;
    }

    /// <summary>A project file by name anywhere under the tree.</summary>
    private static string? FindProject(string tree, string name) =>
        Directory.EnumerateFiles(tree, name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault();

    /// <summary>The classes a VSTest class filter names, or null without one.</summary>
    private static HashSet<string>? ClassesInFilter(IReadOnlyList<string> arguments)
    {
        if (Option(arguments, "--filter") is not { } filter)
        {
            return null;
        }

        var classes = ClassFilter().Matches(filter).Select(static m => m.Groups[1].Value.Replace("\\", string.Empty, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return classes.Count > 0 ? classes : null;
    }

    /// <summary>The TRX logger the arguments ask for, in any spelling, or null.</summary>
    private static string? Logger(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count && arguments[i] != "--"; i++)
        {
            var value = arguments[i] is "--logger" or "-l" && i + 1 < arguments.Count ? arguments[i + 1]
                : arguments[i].StartsWith("--logger:", StringComparison.Ordinal) || arguments[i].StartsWith("--logger=", StringComparison.Ordinal) ? arguments[i][9..]
                : null;
            if (value?.StartsWith("trx", StringComparison.OrdinalIgnoreCase) == true)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The value of an option, or null.</summary>
    private static string? Option(IReadOnlyList<string> arguments, string option)
    {
        for (var i = 0; i < arguments.Count - 1 && arguments[i] != "--"; i++)
        {
            if (arguments[i] == option)
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    [GeneratedRegex(@"FullyQualifiedName~((?:\\.|[^|&()\\])+)\.")]
    private static partial Regex ClassFilter();
}
