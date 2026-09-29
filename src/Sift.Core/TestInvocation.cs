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

            List<string> arguments = ["test", Path.GetRelativePath(repoRoot, selection.Project.Path).Replace('\\', '/')];
            if (selection.Mode == TestRunMode.Filtered)
            {
                arguments.Add("--filter");
                arguments.Add(TestFilter.ForClasses(selection.TestClasses));
            }

            arguments.AddRange(extraArguments);
            invocations.Add(new TestInvocation(selection, arguments));
        }

        return invocations;
    }
}
