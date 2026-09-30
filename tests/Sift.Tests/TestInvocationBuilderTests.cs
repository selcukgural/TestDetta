using Sift.Cli;
using Sift.Core;

namespace Sift.Tests;

public sealed class TestInvocationBuilderTests
{
    private static readonly ProjectNode Tests = new("/repo/tests/A.Tests/A.Tests.csproj", "/repo/tests/A.Tests", true, []);
    private static readonly ProjectNode Other = new("/repo/tests/B.Tests/B.Tests.csproj", "/repo/tests/B.Tests", true, []);
    private static readonly ProjectNode Skipped = new("/repo/tests/C.Tests/C.Tests.csproj", "/repo/tests/C.Tests", true, []);

    [Fact]
    public void Builds_filtered_and_full_runs_and_leaves_out_skipped_projects()
    {
        TestProjectSelection[] selections =
        [
            new(Tests, TestRunMode.Filtered, ["A.Tests.OrderTests"], "1 of 3"),
            new(Other, TestRunMode.All, [], "config changed"),
            new(Skipped, TestRunMode.Skipped, [], "nothing reaches it"),
        ];

        var invocations = TestInvocationBuilder.Build(selections, "/repo", ["-c", "Release"]);

        Assert.Equal(2, invocations.Count);
        Assert.Equal(["test", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.Tests.OrderTests.", "-c", "Release"], invocations[0].Arguments);
        Assert.Equal(["test", "tests/B.Tests/B.Tests.csproj", "-c", "Release"], invocations[1].Arguments);
    }

    [Fact]
    public void Xunit_v3_on_the_testing_platform_gets_its_own_class_filters()
    {
        // It rejects the VSTest --filter with "Unknown option" (Scrutor, xunit.v3.mtp-v2).
        var xunit = Tests with { PackageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xunit.v3.mtp-v2" } };
        var mstest = Other with { PackageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MSTest" } };
        TestProjectSelection[] selections =
        [
            new(xunit, TestRunMode.Filtered, ["A.Tests.OrderTests", "A.Tests.Outer+Inner"], "2 of 3"),
            new(mstest, TestRunMode.Filtered, ["B.Tests.CartTests"], "1 of 3"),
        ];

        var invocations = TestInvocationBuilder.Build(selections, "/repo", [], DotnetTestMode.TestingPlatform);

        Assert.Equal(
            ["test", "--project", "tests/A.Tests/A.Tests.csproj", "--filter-class", "A.Tests.OrderTests", "--filter-class", "A.Tests.Outer+Inner"],
            invocations[0].Arguments);
        Assert.Equal(["test", "--project", "tests/B.Tests/B.Tests.csproj", "--filter", "FullyQualifiedName~B.Tests.CartTests."], invocations[1].Arguments);
    }

    [Fact]
    public void Command_line_quotes_only_arguments_the_shell_would_change()
    {
        var invocation = new TestInvocation(
            new TestProjectSelection(Tests, TestRunMode.Filtered, ["A.Outer+Inner"], ""),
            ["test", "tests/A.Tests/A.Tests.csproj", "--filter", "FullyQualifiedName~A.X.|FullyQualifiedName~A.Y's."]);

        Assert.Equal(
            @"dotnet test tests/A.Tests/A.Tests.csproj --filter 'FullyQualifiedName~A.X.|FullyQualifiedName~A.Y'\''s.'",
            ResultWriter.CommandLine(invocation));
    }
}
