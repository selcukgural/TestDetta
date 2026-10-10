using TestDetta.Core;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>Which synthetic changes are in a source generator, and what is built for them (docs/design/generator-mutants.md).</summary>
[Trait("Category", "Eval")]
public sealed class EvalGeneratorMutantsTests
{
    private static readonly string Root = Paths.Normalize(Path.Combine(Path.GetTempPath(), "td-generator-mutants"));

    // Gen is used as a generator by Lib; Mid depends on Lib, App.Tests on Mid; Other.Tests on Other only.
    private static readonly ProjectGraph Graph = new(new Dictionary<string, ProjectNode>(Paths.Comparer)
    {
        [Project("src/Gen")] = Node("src/Gen"),
        [Project("src/Lib")] = Node("src/Lib", references: ["src/Gen"], generators: ["src/Gen"]),
        [Project("src/Mid")] = Node("src/Mid", references: ["src/Lib"]),
        [Project("src/Other")] = Node("src/Other"),
        [Project("tests/App.Tests")] = Node("tests/App.Tests", test: true, references: ["src/Mid", "src/Other"]),
        [Project("tests/Other.Tests")] = Node("tests/Other.Tests", test: true, references: ["src/Other"]),
        [Project("tests/Gen.Tests")] = Node("tests/Gen.Tests", test: true, references: ["src/Gen"]),
    });

    [Fact]
    public void A_change_in_a_generator_reaches_the_test_projects_downstream_of_its_consumers()
    {
        var mutants = new GeneratorMutants(Graph);
        var tests = Graph.Projects.Where(static p => p.IsTestProject).ToList();

        Assert.Equal([Project("src/Gen")], mutants.Generators([File("src/Gen/Emitter.cs")]).Select(static p => p.Path));
        Assert.Equal([Project("tests/App.Tests")], mutants.TestProjectsReached([File("src/Gen/Emitter.cs")], tests).Select(static p => p.Path));

        // A project that references the generator as a library is not reached through a compile.
        Assert.Empty(mutants.TestProjectsReached([File("src/Other/Thing.cs")], tests));
        Assert.Empty(mutants.Generators([File("src/Lib/Calc.cs")]));
    }

    [Fact]
    public void The_build_order_is_the_consumer_chain_alone_dependencies_first()
    {
        var order = new GeneratorMutants(Graph).BuildOrder([File("src/Gen/Emitter.cs")], Graph[Project("tests/App.Tests")]);

        // Neither the generator nor Other, which do not see what it writes, build again.
        Assert.Equal([Project("src/Lib"), Project("src/Mid"), Project("tests/App.Tests")], order.Select(static p => p.Path));
    }

    private static string Project(string directory) => Paths.Normalize(Path.Combine(Root, directory, Path.GetFileName(directory) + ".csproj"));

    private static string File(string path) => Paths.Normalize(Path.Combine(Root, path));

    private static ProjectNode Node(string directory, bool test = false, string[]? references = null, string[]? generators = null) =>
        new(Project(directory), Paths.Normalize(Path.Combine(Root, directory)), test, [.. (references ?? []).Select(Project)])
        {
            OpaqueReferences = new HashSet<string>((generators ?? []).Select(Project), Paths.Comparer),
            GeneratorReferences = new HashSet<string>((generators ?? []).Select(Project), Paths.Comparer),
        };
}
