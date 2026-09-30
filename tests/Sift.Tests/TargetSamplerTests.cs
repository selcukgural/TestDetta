using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;
using Sift.Eval;

namespace Sift.Tests;

public sealed class TargetSamplerTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public TargetSamplerTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Code.cs", """
            namespace Lib;
            public static partial class Log
            {
                // Declaration only: the body comes from a source generator ([LoggerMessage]).
                public static partial void Event(string name);
                public static int Run() { return 1; }
                public static int Twice(int x) => x * 2;
                public static int Auto { get; set; }
            }
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Members_whose_code_cannot_be_edited_in_source_are_not_sampled()
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        var targets = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(10, Seed: 1));

        // Event has no body in source; Auto's accessors compile to code no edit can reach.
        Assert.Equal(["Run", "Twice"], targets.Select(static t => t.Member.MemberName).Order(StringComparer.Ordinal));
        Assert.All(targets, static t => Assert.NotEmpty(t.Edits));
    }

    public void Dispose() => _repo.Dispose();
}
