namespace TestDetta.Core;

/// <summary>One <c>dotnet</c> invocation that runs the selected tests of a test project.</summary>
/// <param name="Selection">The decision this invocation carries out.</param>
/// <param name="Arguments">
/// Arguments for the <c>dotnet</c> executable, starting with <c>test</c>. The project path is
/// relative to <paramref name="Location"/>'s directory, so the process must run there.
/// </param>
/// <param name="Location">Where the process runs, and the runner it uses there.</param>
public sealed record TestInvocation(TestProjectSelection Selection, IReadOnlyList<string> Arguments, DotnetTestLocation Location)
{
    /// <summary>
    /// For a legacy (non-SDK) test project, or one that references a legacy project: the configuration
    /// to build it in with the full MSBuild before its tests run from the compiled assembly, which takes
    /// the place of the project path in <see cref="Arguments"/> (docs/design/legacy-projects.md). Null
    /// for a project <c>dotnet test</c> builds and runs itself.
    /// </summary>
    public string? LegacyConfiguration { get; init; }

    /// <summary>True when <see cref="Arguments"/> include <c>--no-build</c>: a legacy project is not built either.</summary>
    public bool NoBuild { get; init; }
}

/// <summary>Turns test selections into <c>dotnet test</c> invocations.</summary>
public static class TestInvocationBuilder
{
    /// <summary>One invocation per selection that is not <see cref="TestRunMode.Skipped"/>, in the same order.</summary>
    /// <param name="selections">Decisions from <see cref="TestSelector"/>.</param>
    /// <param name="repoRoot">Repository root; project paths are made relative to it.</param>
    /// <param name="extraArguments">Appended to every invocation as given, for example <c>-c Release --no-build</c>.</param>
    /// <remarks>
    /// Each project runs where its <c>global.json</c> is (<see cref="DotnetTestLocation"/>), with the
    /// runner it selects: VSTest takes the project as an argument, Microsoft.Testing.Platform through
    /// <c>--project</c>. Both accept the VSTest filter syntax through <c>--filter</c>, except xUnit v3
    /// and TUnit under the testing platform, which get their own class filters.
    /// </remarks>
    /// <param name="graph">
    /// Projects of the repository, to find legacy projects (<see cref="ProjectGraph.NeedsFullMsBuild"/>);
    /// null treats every project as one <c>dotnet test</c> builds.
    /// </param>
    public static IReadOnlyList<TestInvocation> Build(
        IReadOnlyList<TestProjectSelection> selections,
        string repoRoot,
        IReadOnlyList<string> extraArguments,
        ProjectGraph? graph = null)
    {
        var invocations = new List<TestInvocation>(selections.Count);
        foreach (var selection in selections)
        {
            if (selection.Mode == TestRunMode.Skipped)
            {
                continue;
            }

            var location = DotnetTestLocation.For(repoRoot, selection.Project.Path);
            if (graph?.NeedsFullMsBuild(selection.Project) == true)
            {
                // The compiled assembly replaces the project path once the full MSBuild built it; VSTest runs
                // it, and the build-only arguments stay with the build.
                location = location with { Mode = DotnetTestMode.VSTest };
                List<string> legacy = ["test", Path.GetRelativePath(location.FullDirectory(repoRoot), selection.Project.Path)];
                if (selection.Mode == TestRunMode.Filtered)
                {
                    legacy.AddRange(TestFilter.Arguments(selection.TestClasses, exclude: false, TestFilterSyntax.VSTest));
                }

                legacy.AddRange(WithoutBuildArguments(extraArguments));
                invocations.Add(new TestInvocation(selection, legacy, location)
                {
                    LegacyConfiguration = Configuration(extraArguments),
                    NoBuild = extraArguments.Contains("--no-build"),
                });
                continue;
            }

            var arguments = location.TestArguments(repoRoot, selection.Project.Path);
            if (selection.Mode == TestRunMode.Filtered)
            {
                arguments.AddRange(TestFilter.Arguments(selection.TestClasses, exclude: false, TestFilter.SyntaxFor(selection.Project, location.Mode)));
            }

            arguments.AddRange(extraArguments);
            invocations.Add(new TestInvocation(selection, arguments, location));
        }

        return invocations;
    }

    /// <summary>The configuration named by <c>-c</c> or <c>--configuration</c>, or Debug.</summary>
    /// <param name="arguments">The <c>dotnet test</c> arguments.</param>
    private static string Configuration(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] is "-c" or "--configuration")
            {
                return arguments[i + 1];
            }
        }

        return "Debug";
    }

    /// <summary>
    /// The arguments without those that only a project build takes (<c>-c</c>, <c>-f</c>, <c>-r</c> with
    /// their values, <c>--no-build</c>, <c>--no-restore</c>): <c>dotnet test</c> on an assembly rejects them.
    /// </summary>
    /// <param name="arguments">The <c>dotnet test</c> arguments.</param>
    internal static List<string> WithoutBuildArguments(IReadOnlyList<string> arguments)
    {
        var kept = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "-c" or "--configuration" or "-f" or "--framework" or "-r" or "--runtime" or "-a" or "--arch":
                    i++;
                    break;
                case "--no-build" or "--no-restore":
                    break;
                default:
                    kept.Add(arguments[i]);
                    break;
            }
        }

        return kept;
    }
}
