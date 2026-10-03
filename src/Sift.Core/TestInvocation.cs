namespace Sift.Core;

/// <summary>One <c>dotnet</c> invocation that runs the selected tests of a test project.</summary>
/// <param name="Selection">The decision this invocation carries out.</param>
/// <param name="Arguments">
/// Arguments for the <c>dotnet</c> executable, starting with <c>test</c>. The project path is
/// relative to <paramref name="Location"/>'s directory, so the process must run there.
/// </param>
/// <param name="Location">Where the process runs, and the runner it uses there.</param>
public sealed record TestInvocation(TestProjectSelection Selection, IReadOnlyList<string> Arguments, DotnetTestLocation Location);

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
    public static IReadOnlyList<TestInvocation> Build(
        IReadOnlyList<TestProjectSelection> selections,
        string repoRoot,
        IReadOnlyList<string> extraArguments)
    {
        var invocations = new List<TestInvocation>(selections.Count);
        foreach (var selection in selections)
        {
            if (selection.Mode == TestRunMode.Skipped)
            {
                continue;
            }

            var location = DotnetTestLocation.For(repoRoot, selection.Project.Path);
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
}
