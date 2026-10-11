using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

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

    [Fact]
    public void A_uniform_sample_orders_every_member_the_same_whatever_the_coverage()
    {
        // td audit must not favour members a map says some test ran: they are the changes most likely to be caught.
        var members = string.Join("\n", Enumerable.Range(0, 30).Select(static i => $"    public static int M{i:00}(int x) => x + {i};"));
        _repo.Write("src/Lib/Many.cs", $"namespace Lib;\npublic static class Many\n{{\n{members}\n}}\n");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var request = new SampleRequest(int.MaxValue, Seed: 3) { Uniform = true };
        var half = Enumerable.Range(0, 15).Select(static i => new CoveredMethod("Lib.Many", $"M{i:00}")).ToHashSet();

        var withoutCoverage = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), request).Select(static t => t.Member).ToList();
        var withCoverage = TargetSampler.Sample(graph, _repo.Root, half, request).ToList();

        Assert.Equal(32, withoutCoverage.Count);
        Assert.Equal(withoutCoverage, withCoverage.Select(static t => t.Member));
        Assert.Equal(15, withCoverage.Count(static t => t.Covered));

        // Covered and uncovered members are mixed in the order, not covered ones first as in the evaluator's default.
        var firstTen = withCoverage.Take(10).Count(static t => t.Covered);
        Assert.InRange(firstTen, 1, 9);
        Assert.NotEqual(
            withoutCoverage.Take(10),
            TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), request with { Seed = 4 }).Select(static t => t.Member).Take(10));
    }

    [Fact]
    public void Fsharp_and_visual_basic_members_are_sampled_with_edits_the_analysis_attributes_to_them()
    {
        _repo.Write("src/FsLib/FsLib.fsproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="Calc.fs" /></ItemGroup></Project>""");
        _repo.Write("src/FsLib/Calc.fs", """
            namespace FsLib

            type Calc() =
                member _.Add(a: int, b: int) =
                    let s = a + b
                    s // sum

                member val Auto = 1 with get, set

            module Helpers =
                let twice x = x * 2

                let inline half x = x / 2

                let limit = 3
            """);
        _repo.Write("src/VbLib/VbLib.vbproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Shop</RootNamespace></PropertyGroup></Project>""");
        _repo.Write("src/VbLib/Calc.vb", """
            Public Class VbCalc
                Public Function Add(a As Integer, b As Integer) As Integer
                    Return a + b
                End Function

                Public Property Auto As Integer
            End Class
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
                <ProjectReference Include="../../src/FsLib/FsLib.fsproj" />
                <ProjectReference Include="../../src/VbLib/VbLib.vbproj" />
              </ItemGroup>
            </Project>
            """);
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        var targets = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(20, Seed: 1))
            .Where(static t => t.Edits[0].Path.EndsWith(".fs", StringComparison.Ordinal) || t.Edits[0].Path.EndsWith(".vb", StringComparison.Ordinal))
            .ToList();

        // Auto-properties compile to accessors no edit reaches, an inline function into its callers, a value into the initializer.
        Assert.Equal(
            ["FsLib.Calc.Add", "FsLib.Helpers.twice", "Shop.VbCalc.Add"],
            targets.Select(static t => $"{t.Member.TypeName}.{t.Member.MemberName}").Order(StringComparer.Ordinal));
        foreach (var target in targets)
        {
            var path = target.Edits[0].Path;
            var before = File.ReadAllText(path);
            var after = target.Edits.OrderByDescending(static e => e.Position)
                .Aggregate(before, static (text, edit) => text.Remove(edit.Position, edit.Replaces).Insert(edit.Position, edit.Text));
            var change = new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
                .Detect(path, SourceText.From(before), SourceText.From(after), [], rootNamespace: "Shop");

            var symbol = Assert.Single(change.Symbols);
            Assert.Equal(target.Member.TypeName, symbol.TypeName);
            Assert.Equal([target.Member.MemberName], DeclarationWalker.CompiledNames(symbol.MemberName!));
        }
    }

    [Fact]
    public void A_targeted_sample_takes_only_the_matching_members_and_keeps_their_kinds()
    {
        // A rule that narrows the selection is checked with changes where it acts (docs/evaluation.md, "Targeted mutants").
        var bridge = string.Join("\n", Enumerable.Range(0, 30).Select(static i => $"    public static int B{i:00}(int x) => x + {i};"));
        _repo.Write("src/Lib/Bridge.cs", $"namespace Lib.Bridge;\npublic static class Linq\n{{\n{bridge}\n}}\n");
        var others = string.Join("\n", Enumerable.Range(0, 30).Select(static i => $"    public static int O{i:00}(int x) => x - {i};"));
        _repo.Write("src/Lib/Other.cs", $"namespace Lib;\npublic static class Other\n{{\n{others}\n}}\n");
        var kinds = new HashSet<MutantKind> { MutantKind.Throw, MutantKind.ReturnDefault, MutantKind.FlipBranch };
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);

        var targeted = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(25, Seed: 5) { Kinds = kinds, TypePattern = "Lib.Bridge.*" });
        var byMember = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(5, Seed: 5) { Kinds = kinds, TypePattern = "Lib.Other.O0*" });
        var all = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(int.MaxValue, Seed: 5) { Kinds = kinds })
            .ToDictionary(static t => t.Member, static t => t.Kind);

        Assert.Equal(25, targeted.Count);
        Assert.All(targeted, static t => Assert.Equal("Lib.Bridge.Linq", t.Member.TypeName));
        Assert.All(byMember, static t => Assert.StartsWith("O0", t.Member.MemberName, StringComparison.Ordinal));
        Assert.Equal(5, byMember.Count);
        Assert.All(targeted, t => Assert.Equal(all[t.Member], t.Kind));
    }

    [Fact]
    public void A_replay_takes_the_named_members_in_their_order_with_their_kinds()
    {
        // After a fix, the changes that failed run again exactly, whatever a new sample would pick (docs/evaluation.md, "Seeds and replays").
        var members = string.Join("\n", Enumerable.Range(0, 20).Select(static i => $"    public static int M{i:00}(int x) => x + {i};"));
        _repo.Write("src/Lib/Many.cs", $"namespace Lib;\npublic static class Many\n{{\n{members}\n}}\n");
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        var request = new SampleRequest(50, Seed: 9)
        {
            Replay =
            [
                new ReplayedMutant("Lib.Many.M17", MutantKind.FlipBranch),
                new ReplayedMutant("Lib.Log.Run", MutantKind.ReturnDefault),
                new ReplayedMutant("Lib.Many.Gone", MutantKind.Throw),
                new ReplayedMutant("Lib.Many.M03", MutantKind.FieldValue),
                new ReplayedMutant("(file).data/rates.json", MutantKind.EmptyFile),
            ],
        };

        var targets = TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod> { new("Lib.Log", "Run") }, request);

        // A removed member, a method given a field's kind and a data file (the evaluator's) are left out.
        Assert.Equal(["Lib.Many.M17:FlipBranch", "Lib.Log.Run:ReturnDefault"], targets.Select(static t => $"{t.Member.TypeName}.{t.Member.MemberName}:{t.Kind}"));
        Assert.Equal([false, true], targets.Select(static t => t.Covered));
        Assert.All(targets, static t => Assert.NotEmpty(t.Edits));
    }

    public void Dispose() => _repo.Dispose();
}
