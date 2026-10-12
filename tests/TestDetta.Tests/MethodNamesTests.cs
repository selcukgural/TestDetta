using Mono.Cecil;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>Maps real compiled methods of TestDetta.Core, which contains async code, lambdas, properties and iterators.</summary>
public sealed class MethodNamesTests
{
    private static readonly ModuleDefinition Core = ModuleDefinition.ReadModule(typeof(GitClient).Assembly.Location);

    [Fact]
    public void Property_accessor_maps_to_the_property()
    {
        Assert.Contains(new CoveredMethod("TestDetta.Core.ImpactResult", "Tests"), MappedMembersOf("TestDetta.Core.ImpactResult"));
    }

    [Fact]
    public void Lambda_and_iterator_code_maps_to_the_member_that_contains_it()
    {
        // SignificantText is an iterator (a generated state machine), Tests a lambda inside a property.
        var mapped = Core.GetTypes()
            .Where(t => !MethodNames.IsCompilerInfrastructure(t))
            .SelectMany(t => t.Methods).Where(m => m.HasBody).Select(MethodNames.Map).ToHashSet();

        Assert.Contains(new CoveredMethod("TestDetta.Core.CodeChangeDetector", "SignificantText"), mapped);
        Assert.DoesNotContain(mapped, m => m.TypeName.Contains('<') || m.MemberName.Contains('<'));
    }

    [Fact]
    public void Constructors_keep_their_metadata_name()
    {
        Assert.Contains(new CoveredMethod("TestDetta.Core.GitClient", ".ctor"), MappedMembersOf("TestDetta.Core.GitClient"));
    }

    [Fact]
    public void Top_level_statements_keep_the_name_the_source_index_gives_them()
    {
        // The evaluation tool's Program.cs is top-level statements: <Main>$, as SourceIndexBuilder names the member.
        var eval = ModuleDefinition.ReadModule(typeof(TestDetta.Eval.EvalApp).Assembly.Location);
        var program = eval.GetType("Program");
        Assert.Contains(new CoveredMethod("Program", "<Main>$"), program.Methods.Where(static m => m.HasBody).Select(MethodNames.Map));

        // Async top-level statements add an entry point named <Main> that awaits <Main>$; their lambdas map there too.
        var type = new TypeDefinition("", "Program", TypeAttributes.Class, eval.TypeSystem.Object);
        foreach (var name in (string[])["<Main>", "<<Main>$>b__0_0"])
        {
            type.Methods.Add(new MethodDefinition(name, MethodAttributes.Static, eval.TypeSystem.Void));
        }

        Assert.All(type.Methods.Select(MethodNames.Map), static m => Assert.Equal(new CoveredMethod("Program", "<Main>$"), m));
    }

    private static HashSet<CoveredMethod> MappedMembersOf(string typeName) =>
        [.. Core.GetType(typeName).Methods.Where(m => m.HasBody).Select(MethodNames.Map)];
}
