using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// The user's own test filter (<c>td test -- --filter ...</c>) and TestDetta's class filter together:
/// <c>dotnet test</c> rejects a second <c>--filter</c>, so the two are joined, or the project runs whole
/// under the user's filter when the runner cannot join them.
/// </summary>
public sealed class UserFilterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-userfilter-" + Guid.NewGuid().ToString("N"));

    public UserFilterTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void A_vstest_filter_of_the_user_is_joined_with_the_class_filter()
    {
        var invocation = Build(Project(), ["--filter", "Category!=Integration", "--no-build"]);

        Assert.Equal(
            ["test", "tests/A.Tests/A.Tests.csproj", "--filter", "(Category!=Integration)&(FullyQualifiedName~A.Tests.OrderTests.)", "--no-build"],
            invocation.Arguments);
        Assert.Equal(TestRunMode.Filtered, invocation.Selection.Mode);

        // Should the class filter match nothing, the project runs under the user's filter, never without it.
        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "Category!=Integration", "--no-build"], invocation.UnfilteredArguments);
    }

    [Fact]
    public void The_equals_form_and_several_user_filters_are_joined_too()
    {
        var invocation = Build(Project(), ["--filter=Category!=Slow", "--filter", "Priority=1"]);

        Assert.Equal(
            ["test", "tests/A.Tests/A.Tests.csproj", "--filter", "(Category!=Slow)&(Priority=1)&(FullyQualifiedName~A.Tests.OrderTests.)"],
            invocation.Arguments);
    }

    [Fact]
    public void Run_settings_after_a_double_dash_are_left_alone()
    {
        var invocation = Build(Project(), ["--", "RunConfiguration.TestSessionTimeout=60000"]);

        Assert.Equal(
            ["test", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests.", "--", "RunConfiguration.TestSessionTimeout=60000"],
            invocation.Arguments);
    }

    [Fact]
    public void An_xunit_v3_trait_filter_narrows_the_class_filter_as_given()
    {
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        var invocation = Build(Project("xunit.v3"), ["--filter-not-trait", "Category=Integration"]);

        Assert.Equal(
            ["test", "--project", "tests/A.Tests/A.Tests.csproj", "--filter-class", "A.Tests.OrderTests", "--filter-not-trait", "Category=Integration"],
            invocation.Arguments);
        Assert.Equal(TestRunMode.Filtered, invocation.Selection.Mode);
    }

    [Theory]
    [InlineData("xunit.v3", "--filter-class", "A.Tests.Fast*")]
    [InlineData("xunit.v3", "--filter-query", "/*/*/*/*[Category=Fast]")]
    [InlineData("TUnit", "--treenode-filter", "/*/*/*/*[Category=Fast]")]
    public void A_user_filter_the_runner_cannot_join_runs_the_whole_project_under_it(string framework, string option, string value)
    {
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        var invocation = Build(Project(framework), [option, value]);

        // xUnit v3 joins two class filters with "or" and throws when a query meets a class filter;
        // TUnit takes one tree-node filter.
        Assert.Equal(["test", "--project", "tests/A.Tests/A.Tests.csproj", option, value], invocation.Arguments);
        Assert.Equal(TestRunMode.All, invocation.Selection.Mode);
        Assert.Contains("own filter", invocation.Selection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_that_runs_whole_keeps_the_user_filter_as_given()
    {
        var project = Project();
        TestProjectSelection[] selections = [new(project, TestRunMode.All, [], "")];

        var invocation = TestInvocationBuilder.Build(selections, _directory, ["--filter", "Category!=Integration"]).Single();

        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "Category!=Integration"], invocation.Arguments);
        Assert.Null(invocation.UnfilteredArguments);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private TestInvocation Build(ProjectNode project, IReadOnlyList<string> userArguments)
    {
        TestProjectSelection[] selections = [new(project, TestRunMode.Filtered, ["A.Tests.OrderTests"], "OrderTests reaches the change")];
        return TestInvocationBuilder.Build(selections, _directory, userArguments).Single();
    }

    private ProjectNode Project(params string[] packages) =>
        new(Path.Combine(_directory, "tests/A.Tests/A.Tests.csproj"), Path.Combine(_directory, "tests/A.Tests"), true, [])
        {
            PackageReferences = new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase),
        };
}
