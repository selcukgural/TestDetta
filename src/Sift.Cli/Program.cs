using System.Text.Json;
using Sift.Core;

const string usage = """
    Usage: sift affected [--base <ref>] [--repo <path>] [--tests-only] [--format text|json|paths]

      --base        Git ref to compare against (default: origin/main)
      --repo        Any path inside the repository (default: current directory)
      --tests-only  Only list affected test projects
      --format      text (default, human readable), json, or paths (one .csproj per line)
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] != "affected")
{
    Console.Error.WriteLine(usage);
    return args.Length > 0 && args[0] is "-h" or "--help" ? 0 : 2;
}

var baseRef = "origin/main";
var repo = Directory.GetCurrentDirectory();
var testsOnly = false;
var format = "text";

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" when i + 1 < args.Length: baseRef = args[++i]; break;
        case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
        case "--format" when i + 1 < args.Length: format = args[++i]; break;
        case "--tests-only": testsOnly = true; break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}\n\n{usage}");
            return 2;
    }
}

if (format is not ("text" or "json" or "paths"))
{
    Console.Error.WriteLine($"Unknown format: {format}");
    return 2;
}

try
{
    var root = Git.TopLevel(repo);
    var changed = Git.ChangedFiles(root, baseRef);
    var graph = ProjectGraph.Load(root);
    var result = ImpactAnalyzer.Analyze(graph, root, changed);
    var selected = (testsOnly ? result.Tests : result.Affected).ToList();

    string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    switch (format)
    {
        case "paths":
            foreach (var a in selected) Console.WriteLine(Rel(a.Project.Path));
            break;

        case "json":
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                @base = baseRef,
                changedFiles = changed.Count,
                projects = selected.Select(a => new
                {
                    path = Rel(a.Project.Path),
                    isTest = a.Project.IsTestProject,
                    reason = a.Reason.ToString(),
                }),
                unmappedFiles = result.UnmappedFiles,
            }, new JsonSerializerOptions { WriteIndented = true }));
            break;

        default:
            Console.WriteLine($"{changed.Count} changed file(s) vs {baseRef}, {graph.Projects.Count} project(s) in repo.");
            Console.WriteLine($"{selected.Count} affected{(testsOnly ? " test" : "")} project(s):");
            foreach (var a in selected)
                Console.WriteLine($"  {(a.Project.IsTestProject ? "[test] " : "       ")}{Rel(a.Project.Path)}  ({a.Reason})");
            if (result.UnmappedFiles.Count > 0)
                Console.WriteLine($"{result.UnmappedFiles.Count} changed file(s) outside any project (ignored).");
            break;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"sift: {ex.Message}");
    return 1;
}
