namespace Sift.Core;

/// <summary>One <c>dotnet</c> invocation that runs the selected tests of a test project.</summary>
/// <param name="Selection">The decision this invocation carries out.</param>
/// <param name="Arguments">
/// Arguments for the <c>dotnet</c> executable, starting with <c>test</c>. The project path is
/// relative to the repository root, so the process must run there.
/// </param>
public sealed record TestInvocation(TestProjectSelection Selection, IReadOnlyList<string> Arguments);

/// <summary>Turns test selections into <c>dotnet test</c> invocations.</summary>
public static class TestInvocationBuilder
{
    /// <summary>One invocation per selection that is not <see cref="TestRunMode.Skipped"/>, in the same order.</summary>
    /// <param name="selections">Decisions from <see cref="TestSelector"/>.</param>
    /// <param name="repoRoot">Repository root; project paths are made relative to it.</param>
    /// <param name="extraArguments">Appended to every invocation as given, for example <c>-c Release --no-build</c>.</param>
    /// <param name="mode">
    /// Runner <c>dotnet test</c> uses: VSTest takes the project as an argument, Microsoft.Testing.Platform
    /// through <c>--project</c>. Both accept the VSTest filter syntax through <c>--filter</c>, except
    /// xUnit v3 under the testing platform, which gets its own class filters.
    /// </param>
    public static IReadOnlyList<TestInvocation> Build(
        IReadOnlyList<TestProjectSelection> selections,
        string repoRoot,
        IReadOnlyList<string> extraArguments,
        DotnetTestMode mode = DotnetTestMode.VSTest)
    {
        var invocations = new List<TestInvocation>(selections.Count);
        foreach (var selection in selections)
        {
            if (selection.Mode == TestRunMode.Skipped)
            {
                continue;
            }

            var project = Path.GetRelativePath(repoRoot, selection.Project.Path).Replace('\\', '/');
            List<string> arguments = mode == DotnetTestMode.TestingPlatform ? ["test", "--project", project] : ["test", project];
            if (selection.Mode == TestRunMode.Filtered)
            {
                var xunitV3 = mode == DotnetTestMode.TestingPlatform && TestFilter.IsXunitV3(selection.Project);
                arguments.AddRange(TestFilter.Arguments(selection.TestClasses, exclude: false, xunitV3));
            }

            arguments.AddRange(extraArguments);
            invocations.Add(new TestInvocation(selection, arguments));
        }

        return invocations;
    }
}
