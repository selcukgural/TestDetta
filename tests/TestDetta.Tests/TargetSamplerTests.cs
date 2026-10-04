using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

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

    [Fact]
    public void Adding_a_member_changes_at_most_one_pick_of_the_sample()
    {
        // A fix to TestDetta's project graph can add or drop a candidate; the outcome cache only pays off
        // when the other mutants of the commit stay the same, kinds included.
        var members = string.Join("\n", Enumerable.Range(0, 40).Select(static i => $"    public static int M{i:00}(int x) => x + {i};"));
        _repo.Write("src/Lib/Many.cs", $"namespace Lib;\npublic static class Many\n{{\n{members}\n}}\n");
        var kinds = new HashSet<MutantKind> { MutantKind.Throw, MutantKind.ReturnDefault, MutantKind.FlipBranch };
        var request = new SampleRequest(10, Seed: 7) { Kinds = kinds };

        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var before = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), request)
            .Select(static t => $"{t.Member.MemberName}:{t.Kind}").ToHashSet(StringComparer.Ordinal);

        _repo.Write("src/Lib/Extra.cs", "namespace Lib;\npublic static class AAExtra { public static int First(int x) => x - 1; }\n");
        graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var after = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), request)
            .Select(static t => $"{t.Member.MemberName}:{t.Kind}").ToHashSet(StringComparer.Ordinal);

        Assert.Equal(10, before.Count);
        Assert.True(before.Intersect(after).Count() >= 9, $"before: {string.Join(", ", before)}; after: {string.Join(", ", after)}");
    }

    public void Dispose() => _repo.Dispose();
}
