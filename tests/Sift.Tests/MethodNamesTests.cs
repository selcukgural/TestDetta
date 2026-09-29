using Mono.Cecil;
using Sift.Core;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>Maps real compiled methods of Sift.Core, which contains async code, lambdas, properties and iterators.</summary>
public sealed class MethodNamesTests
{
    private static readonly ModuleDefinition Core = ModuleDefinition.ReadModule(typeof(GitClient).Assembly.Location);

    [Fact]
    public void Property_accessor_maps_to_the_property()
    {
        Assert.Contains(new CoveredMethod("Sift.Core.ImpactResult", "Tests"), MappedMembersOf("Sift.Core.ImpactResult"));
    }

    [Fact]
    public void Lambda_and_iterator_code_maps_to_the_member_that_contains_it()
    {
        // SignificantText is an iterator (a generated state machine), Tests a lambda inside a property.
        var mapped = Core.GetTypes()
            .Where(t => !MethodNames.IsCompilerInfrastructure(t))
            .SelectMany(t => t.Methods).Where(m => m.HasBody).Select(MethodNames.Map).ToHashSet();

        Assert.Contains(new CoveredMethod("Sift.Core.CodeChangeDetector", "SignificantText"), mapped);
        Assert.DoesNotContain(mapped, m => m.TypeName.Contains('<') || m.MemberName.Contains('<'));
    }

    [Fact]
    public void Constructors_keep_their_metadata_name()
    {
        Assert.Contains(new CoveredMethod("Sift.Core.GitClient", ".ctor"), MappedMembersOf("Sift.Core.GitClient"));
    }

    private static HashSet<CoveredMethod> MappedMembersOf(string typeName) =>
        [.. Core.GetType(typeName).Methods.Where(m => m.HasBody).Select(MethodNames.Map)];
}
