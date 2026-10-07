using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestDetta.Core;

namespace TestDetta.Cli;

/// <summary>What the team's own <c>dotnet test</c> arguments run.</summary>
internal enum ShadowTargetKind
{
    /// <summary>No project argument: <c>dotnet test</c> runs the one solution or project in its working directory.</summary>
    None,

    /// <summary>A solution (<c>.sln</c>, <c>.slnx</c>) or a solution filter (<c>.slnf</c>).</summary>
    Solution,

    /// <summary>A directory, in which <c>dotnet test</c> runs the one solution or project it finds.</summary>
    Directory,

    /// <summary>A project file.</summary>
    Project,

    /// <summary>Something shadow mode does not run as given: test assemblies or test modules.</summary>
    Other,
}

/// <summary>What the team's <c>dotnet test</c> arguments name, and where in them.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Path">Full path of what the arguments name; the working directory when they name nothing.</param>
/// <param name="Index">Position of the naming argument in the arguments, or -1 when there is none.</param>
/// <param name="Length">Arguments it takes: 1 for a positional path or <c>--project=&lt;path&gt;</c>, 2 for <c>--project &lt;path&gt;</c> and its kind.</param>
internal sealed record ShadowTarget(ShadowTargetKind Kind, string Path, int Index, int Length)
{
    /// <summary>The arguments without the target, for a run per test project, which names each project itself.</summary>
    /// <param name="arguments">The arguments the target was read from.</param>
    public IReadOnlyList<string> Without(IReadOnlyList<string> arguments) =>
        Index < 0 ? arguments : [.. arguments.Take(Index), .. arguments.Skip(Index + Length)];

    /// <summary>How the reports name it: "the solution x.slnx", "no project argument" and so on.</summary>
    public string Describe() => Kind switch
    {
        ShadowTargetKind.None => "no project argument",
        ShadowTargetKind.Solution => $"the solution {System.IO.Path.GetFileName(Path)}",
        ShadowTargetKind.Directory => $"the directory {Path}",
        ShadowTargetKind.Project => $"the project {System.IO.Path.GetFileName(Path)}",
        _ => System.IO.Path.GetFileName(Path),
    };
}

/// <summary>
/// Reads the team's own <c>dotnet test</c> command line for shadow mode (docs/design/shadow-mode.md, "Nothing
/// changes for the team"): what it runs, and which test projects that covers. Shadow mode runs that command as
/// it is when it can tell the reports of its test projects apart afterwards.
/// </summary>
internal static partial class ShadowCommand
{
    /// <summary>
    /// <c>dotnet test</c> options that take a value, in both runners' spelling, so the value is not read as the
    /// project argument. An option this list does not know is harmless: a positional argument only counts as the
    /// target when it is a solution, project, assembly or directory that exists.
    /// </summary>
    private static readonly FrozenSet<string> ValueOptions = FrozenSet.Create(
        StringComparer.Ordinal,
        "-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-a", "--arch", "--os", "-o", "--output",
        "-s", "--settings", "-l", "--logger", "--filter", "-d", "--diag", "--results-directory", "--collect",
        "-v", "--verbosity", "-e", "--environment", "--test-adapter-path", "--artifacts-path", "--blame-crash-dump-type",
        "--blame-hang-dump-type", "--blame-hang-timeout", "--max-parallel-test-modules", "--filter-class",
        "--filter-not-class", "--filter-method", "--filter-not-method", "--filter-namespace", "--filter-not-namespace",
        "--filter-trait", "--filter-not-trait", "--filter-query", "--treenode-filter", "--report-trx-filename",
        "--report-xunit-trx-filename", "--timeout", "--minimum-expected-tests", "--config-file");

    /// <summary>Solution file extensions, the filter included.</summary>
    private static readonly FrozenSet<string> SolutionExtensions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".sln", ".slnx", ".slnf");

    /// <summary>
    /// What <paramref name="arguments"/> run: the value of <c>--project</c>, <c>--solution</c>, <c>--directory</c>
    /// or <c>--test-modules</c> (the testing platform's spelling), or else the first positional argument that is a
    /// solution, project, assembly or existing directory (VSTest's). The options are read in each spelling the
    /// <c>dotnet</c> command line takes: <c>--project x</c>, <c>--project=x</c> and <c>--project:x</c>. Only the
    /// arguments before a <c>--</c> are read; those after it are run settings.
    /// </summary>
    /// <param name="arguments">The user's <c>dotnet test</c> arguments.</param>
    /// <param name="directory">Directory the command runs in; relative paths resolve against it.</param>
    public static ShadowTarget Target(IReadOnlyList<string> arguments, string directory)
    {
        for (var i = 0; i < arguments.Count && arguments[i] != "--"; i++)
        {
            var argument = arguments[i];
            if (Named(argument, i + 1 < arguments.Count ? arguments[i + 1] : null) is var (option, given, length))
            {
                var value = System.IO.Path.GetFullPath(given, directory);
                var kind = option switch
                {
                    "--solution" => ShadowTargetKind.Solution,
                    "--directory" => ShadowTargetKind.Directory,
                    "--test-modules" => ShadowTargetKind.Other,
                    _ => KindOf(value) ?? ShadowTargetKind.Project,
                };
                return new ShadowTarget(kind, value, i, length);
            }

            if (argument.StartsWith('-'))
            {
                if (ValueOptions.Contains(argument))
                {
                    i++;
                }

                continue;
            }

            var path = System.IO.Path.GetFullPath(argument, directory);
            if (KindOf(path) is { } positional)
            {
                return new ShadowTarget(positional, path, i, 1);
            }
        }

        return new ShadowTarget(ShadowTargetKind.None, directory, -1, 0);
    }

    /// <summary>The options that name what <c>dotnet test</c> runs, in the testing platform's spelling.</summary>
    private static readonly string[] TargetOptions = ["--project", "--solution", "--directory", "--test-modules"];

    /// <summary>
    /// The target option <paramref name="argument"/> gives, with its value and how many arguments it takes: two for
    /// <c>--project x</c>, one for <c>--project=x</c> or <c>--project:x</c>; null for any other argument.
    /// </summary>
    /// <param name="argument">The argument.</param>
    /// <param name="next">The argument after it, or null at the end.</param>
    private static (string Option, string Value, int Length)? Named(string argument, string? next)
    {
        foreach (var option in TargetOptions)
        {
            if (argument == option)
            {
                return next is null ? null : (option, next, 2);
            }

            if (argument.Length > option.Length + 1 && argument.StartsWith(option, StringComparison.Ordinal) && argument[option.Length] is '=' or ':')
            {
                return (option, argument[(option.Length + 1)..], 1);
            }
        }

        return null;
    }

    /// <summary>The kind of a path by its extension (<c>.csproj</c>, <c>.fsproj</c>, <c>.proj</c>: any <c>*proj</c>), or a directory that exists; null for anything else.</summary>
    /// <param name="path">Full path.</param>
    private static ShadowTargetKind? KindOf(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return SolutionExtensions.Contains(extension) ? ShadowTargetKind.Solution
            : extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? ShadowTargetKind.Project
            : extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ShadowTargetKind.Other
            : System.IO.Directory.Exists(path) ? ShadowTargetKind.Directory
            : null;
    }

    /// <summary>
    /// The project files the command runs, as <c>dotnet test</c> finds them: those of the solution (or filter) it
    /// names, the project it names, or, without either, the one solution or project in the directory. Null when
    /// that cannot be told (several candidates in the directory, a file that cannot be read or has an unexpected
    /// shape, such as a solution filter without its <c>solution</c> object): the caller then assumes every test
    /// project under the directory. Nothing a solution file holds can make this throw.
    /// </summary>
    /// <param name="target">What the command names.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Shadow mode is fail-open: a solution file of any shape only makes the scope unknown.")]
    public static IReadOnlyList<string>? Projects(ShadowTarget target)
    {
        try
        {
            return target.Kind switch
            {
                ShadowTargetKind.Solution => SolutionProjects(target.Path),
                ShadowTargetKind.Project => [Paths.Normalize(target.Path)],
                ShadowTargetKind.None or ShadowTargetKind.Directory => InDirectory(target.Path),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <c>dotnet test</c> without a project argument has something to run in <paramref name="directory"/>:
    /// exactly one solution or project file there. With none, or more than one, it stops with an MSBuild error.
    /// </summary>
    /// <param name="directory">The directory.</param>
    public static bool FindsOne(string directory) => System.IO.Directory.Exists(directory) && Candidates(directory).Count == 1;

    /// <summary>The solution and project files <c>dotnet test</c> without a project argument chooses from in a directory.</summary>
    /// <param name="directory">The directory.</param>
    private static List<string> Candidates(string directory) =>
    [
        .. System.IO.Directory.GetFiles(directory, "*.sln"),
        .. System.IO.Directory.GetFiles(directory, "*.slnx"),
        .. System.IO.Directory.GetFiles(directory, "*.*proj").Where(static p => KindOf(p) == ShadowTargetKind.Project),
    ];

    /// <summary>What <c>dotnet test</c> runs in a directory: its one solution, or its one project.</summary>
    /// <param name="directory">The directory.</param>
    private static List<string>? InDirectory(string directory) => Candidates(directory) switch
    {
        [var solution] when KindOf(solution) == ShadowTargetKind.Solution => SolutionProjects(solution),
        [var project] => [Paths.Normalize(project)],
        _ => null,
    };

    /// <summary>The project files of a <c>.sln</c>, <c>.slnx</c> or <c>.slnf</c>, normalized; a filter's paths are relative to its solution.</summary>
    /// <param name="solution">Solution file.</param>
    private static List<string> SolutionProjects(string solution)
    {
        var directory = System.IO.Path.GetDirectoryName(solution)!;
        var extension = System.IO.Path.GetExtension(solution);
        if (extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            using var filter = JsonDocument.Parse(File.ReadAllText(solution), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var inner = filter.RootElement.GetProperty("solution");
            var solutionDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(inner.GetProperty("path").GetString()!.Replace('\\', '/'), directory))!;
            return [.. inner.GetProperty("projects").EnumerateArray().Select(p => Full(solutionDirectory, p.GetString()!))];
        }

        if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return [.. XDocument.Load(solution).Descendants().Where(static e => e.Name.LocalName == "Project")
                .Select(static e => e.Attribute("Path")?.Value).OfType<string>().Select(p => Full(directory, p))];
        }

        return [.. SlnProject().Matches(File.ReadAllText(solution)).Select(static m => m.Groups[1].Value)
            .Where(static p => KindOf(p) == ShadowTargetKind.Project).Select(p => Full(directory, p))];
    }

    /// <summary>A path from a solution, with either separator, made absolute and normalized.</summary>
    /// <param name="directory">Directory it is relative to.</param>
    /// <param name="path">The path as the solution writes it.</param>
    private static string Full(string directory, string path) => Paths.Normalize(System.IO.Path.GetFullPath(path.Replace('\\', '/'), directory));

    /// <summary>A <c>Project(...) = "Name", "path", "{guid}"</c> line of a <c>.sln</c>; group 1 is the path.</summary>
    [GeneratedRegex("""^Project\("\{[^}]*\}"\)\s*=\s*"[^"]*",\s*"([^"]*)",""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();
}
