using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

/// <summary>
/// Test classes are found by following base lists by simple name. Many types sharing one simple
/// name (Swashbuckle's snapshot folders of generated Kiota clients) made the search try every
/// candidate at every level, which never finished.
/// </summary>
public sealed class BaseTypeSearchTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    [Fact]
    public async Task Many_types_with_one_simple_name_are_searched_in_linear_time()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Rule.cs", "namespace Lib; public static class Rule { public static int Check() => 1; }");
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="xunit.v3" /><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);

        // N0.Node : N1.Node : ... : N39.Node, none with tests: every Node is a candidate at every level.
        var nodes = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            nodes.AppendLine(i < 39 ? $"namespace N{i} {{ public class Node : N{i + 1}.Node {{ }} }}" : $"namespace N{i} {{ public class Node {{ }} }}");
        }

        _repo.Write("tests/Lib.Tests/Nodes.cs", nodes.ToString());
        _repo.Write("tests/Lib.Tests/RuleTests.cs", "namespace Lib.Tests; public class RuleTests { [Xunit.Fact] public void Checks() => Xunit.Assert.Equal(1, Lib.Rule.Check()); }");
        _repo.Commit("base");
        _repo.Replace("src/Lib/Rule.cs", "=> 1;", "=> 0 + 1;");

        var analysis = Task.Run(() => new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main"));
        // The exponential search took hours; a minute leaves room for a loaded machine.
        var result = await analysis.WaitAsync(TimeSpan.FromSeconds(60));

        var tests = Assert.Single(result.Tests);
        Assert.True(tests.Mode == TestRunMode.All || tests.TestClasses.Contains("Lib.Tests.RuleTests"), tests.Reason);
    }

    public void Dispose() => _repo.Dispose();
}
