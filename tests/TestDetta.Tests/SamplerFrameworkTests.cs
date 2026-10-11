using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// With a run framework (<c>-f</c>), the sampler reads C# with that framework's preprocessor symbols, so the source edit
/// lands on the declaration the run compiles, the one the mutant changes (Alba's adapter declared under
/// <c>#if NET11_0_OR_GREATER</c> and again under <c>#else</c>).
/// </summary>
public sealed class SamplerFrameworkTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public SamplerFrameworkTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Adapter.cs", """
            namespace Lib;
            #if NET11_0_OR_GREATER
            public sealed class Adapter { public int Configure(int x) { return x + 11; } }
            #else
            public sealed class Adapter { public int Configure(int x) { return x + 10; } }
            #endif
            """);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("net11.0", "x + 11")]
    [InlineData("net10.0", "x + 10")]
    [InlineData(null, "x + 10")]
    public void The_edit_lands_on_the_declaration_the_run_framework_compiles(string? framework, string expected)
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance, new Symbols(_repo.Root)).Load(_repo.Root);

        var target = Assert.Single(TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), new SampleRequest(10, Seed: 1) { Framework = framework }));

        var text = File.ReadAllText(target.Edits[0].Path);
        var line = text[..target.Edits[0].Position].Split('\n')[^1] + text[target.Edits[0].Position..].Split('\n')[0];
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    /// <summary>An evaluation that gives Lib the symbols of net10.0 and net11.0.</summary>
    /// <param name="root">Repository root.</param>
    private sealed class Symbols(string root) : IProjectEvaluator
    {
        public IReadOnlyDictionary<string, EvaluatedProject>? Evaluate(string repoRoot, IReadOnlyCollection<string> projects)
        {
            string Full(string relative) => Paths.Normalize(Path.Combine(root, relative));
            var frameworks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["net10.0"] = ["NET10_0", "NET10_0_OR_GREATER"],
                ["net11.0"] = ["NET11_0", "NET10_0_OR_GREATER", "NET11_0_OR_GREATER"],
            };
            EvaluatedProject Project(bool test, string[] references, string[] items) =>
                new(test, null, [.. references.Select(Full)], items.Select(Full).ToHashSet(Paths.Comparer), new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                {
                    FrameworkSymbols = frameworks,
                    SymbolSets = [.. frameworks.Values],
                };

            return new Dictionary<string, EvaluatedProject>(Paths.Comparer)
            {
                [Full("src/Lib/Lib.csproj")] = Project(false, [], ["src/Lib/Adapter.cs"]),
                [Full("tests/Lib.Tests/Lib.Tests.csproj")] = Project(true, ["src/Lib/Lib.csproj"], []),
            };
        }
    }
}
