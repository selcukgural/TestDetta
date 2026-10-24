using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mono.Cecil;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Shape mutants (docs/evaluation.md, "Shape mutants") change a declaration's metadata, not its code: an attribute is
/// removed, or a sealed class unsealed, in source and in the compiled assembly. Only a test that reads the shape through
/// reflection can fail on one, the kind of test the code mutants never show a miss of.
/// </summary>
public sealed class ShapeMutantTests : IDisposable
{
    private const string Code = """
        using System;
        using System.ComponentModel;
        namespace Lib;

        [Obsolete("old")]
        public sealed class Widget
        {
            [Description("size")] public int Size { get; set; }
            public int Run() { return 1; }
            [Category("a")] public void Over(int x) { }
            [Category("b")] public void Over(string s) { }
            [System.Diagnostics.DebuggerHidden, Browsable(false)] public void Hidden() { }
        }

        [Serializable]
        public sealed partial class Data { }

        public static class Helpers { public static int One() => 1; }
        """;

    private readonly GitRepoFixture _repo = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-shape-" + Guid.NewGuid().ToString("N"));

    public ShapeMutantTests()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/Code.cs", Code);
        _repo.Write("tests/Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _repo.Dispose();
        Paths.DeleteTree(_directory);
    }

    [Fact]
    public void Shape_kinds_pick_attributes_and_sealed_classes_but_not_overloads_or_compiler_attributes()
    {
        var targets = Sample(new SampleRequest(100, Seed: 1) { Kinds = new HashSet<MutantKind> { MutantKind.AttributeRemoved, MutantKind.Unsealed } });

        // Serializable is a flag in metadata, DebuggerHidden only the debugger reads; Over is overloaded; Helpers is static.
        Assert.Equal(
            ["Lib.Data.<sealed>:Unsealed", "Lib.Widget.<sealed>:Unsealed", "Lib.Widget.Hidden[Browsable]:AttributeRemoved", "Lib.Widget.Size[Description]:AttributeRemoved", "Lib.Widget.[Obsolete]:AttributeRemoved"],
            targets.Select(static t => $"{t.Member.TypeName}.{t.Member.MemberName}:{t.Kind}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_default_and_all_kinds_take_no_shape_mutant()
    {
        Assert.True(MutantKinds.TryParse("all", out var all, out _));
        Assert.DoesNotContain(all, MutantKinds.IsShape);
        Assert.True(MutantKinds.TryParse("shape", out var shape, out _));
        Assert.Equal([MutantKind.AttributeRemoved, MutantKind.Unsealed], shape.Order());

        Assert.DoesNotContain(Sample(new SampleRequest(100, Seed: 1) { Kinds = all }), static t => MutantKinds.IsShape(t.Kind));
    }

    [Fact]
    public void TestDetta_sees_each_edit_as_the_metadata_change_it_is()
    {
        var targets = Sample(new SampleRequest(100, Seed: 1) { Kinds = new HashSet<MutantKind> { MutantKind.AttributeRemoved, MutantKind.Unsealed } });
        var detector = new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance);

        var unsealed = Changes(detector, targets.Single(static t => t.Member.MemberName == "<sealed>" && t.Member.TypeName == "Lib.Widget"));
        Assert.Contains(unsealed, static c => c is { TypeName: "Lib.Widget", MemberName: null, Kind: SymbolChangeKind.Modified });

        var property = Changes(detector, targets.Single(static t => t.Member.MemberName == "Size[Description]"));
        Assert.Contains(property, static c => c is { TypeName: "Lib.Widget", MemberName: "Size", Kind: SymbolChangeKind.Modified, MetadataChanged: true });

        var type = Changes(detector, targets.Single(static t => t.Member.MemberName == "[Obsolete]"));
        Assert.Contains(type, static c => c is { TypeName: "Lib.Widget", MemberName: null, Kind: SymbolChangeKind.Modified });
    }

    [Fact]
    public void A_replay_takes_shape_mutants_back_by_their_names()
    {
        var targets = Sample(new SampleRequest(0, Seed: 1)
        {
            Replay = [new ReplayedMutant("Lib.Widget.<sealed>", MutantKind.Unsealed), new ReplayedMutant("Lib.Widget.Size[Description]", MutantKind.AttributeRemoved), new ReplayedMutant("Lib.Widget.Gone[X]", MutantKind.AttributeRemoved)],
        });

        Assert.Equal(["Lib.Widget.<sealed>", "Lib.Widget.Size[Description]"], targets.Select(static t => $"{t.Member.TypeName}.{t.Member.MemberName}"));
    }

    [Fact]
    public void The_injector_unseals_and_removes_the_attribute_and_a_schema_leaves_shape_mutants_out()
    {
        var assembly = Compile();
        var original = File.ReadAllBytes(assembly);

        using (var unsealed = MutationInjector.Inject([assembly], new CoveredMethod("Lib.Widget", "<sealed>"), Path.Combine(_directory, "backup"), MutantKind.Unsealed))
        {
            Assert.Equal(1, unsealed.MutatedMethods);
            using var module = ModuleDefinition.ReadModule(assembly, new ReaderParameters { InMemory = true });
            Assert.False(module.GetType("Lib.Widget").IsSealed);
            Assert.True(module.GetType("Lib.Data").IsSealed);
        }

        Assert.Equal(original, File.ReadAllBytes(assembly));
        using (var removed = MutationInjector.Inject([assembly], new CoveredMethod("Lib.Widget", "Size[Description]"), Path.Combine(_directory, "backup"), MutantKind.AttributeRemoved))
        {
            Assert.Equal(1, removed.MutatedMethods);
            using var module = ModuleDefinition.ReadModule(assembly, new ReaderParameters { InMemory = true });
            var widget = module.GetType("Lib.Widget");
            Assert.Empty(widget.Properties.Single(static p => p.Name == "Size").CustomAttributes);
            Assert.Contains(widget.CustomAttributes, static a => a.AttributeType.Name == "ObsoleteAttribute");
        }

        using (var typeAttribute = MutationInjector.Inject([assembly], new CoveredMethod("Lib.Widget", "[Obsolete]"), Path.Combine(_directory, "backup"), MutantKind.AttributeRemoved))
        {
            Assert.Equal(1, typeAttribute.MutatedMethods);
            using var module = ModuleDefinition.ReadModule(assembly, new ReaderParameters { InMemory = true });
            Assert.DoesNotContain(module.GetType("Lib.Widget").CustomAttributes, static a => a.AttributeType.Name == "ObsoleteAttribute");
        }

        using var schema = MutationInjector.InjectSchema([assembly], [new SchemaTarget(1, new CoveredMethod("Lib.Widget", "<sealed>"), MutantKind.Unsealed)], Path.Combine(_directory, "schema"));
        Assert.Empty(schema.Entries);
        Assert.Equal(original, File.ReadAllBytes(assembly));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void A_test_that_reads_the_shape_through_reflection_fails_on_each_shape_mutant()
    {
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        Directory.CreateDirectory(Path.Combine(_directory, "Lib"));
        Directory.CreateDirectory(Path.Combine(_directory, "Lib.Tests"));
        File.WriteAllText(Path.Combine(_directory, "Lib", "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(_directory, "Lib", "Widget.cs"), """
            namespace Lib;
            [System.ComponentModel.Description("widget")]
            public sealed class Widget { public int Run() => 1; }
            """);
        File.WriteAllText(Path.Combine(_directory, "Lib.Tests", "Lib.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory, "Lib.Tests", "ShapeTests.cs"), """
            using System.Reflection;
            using Xunit;
            namespace Lib.Tests;
            public class ShapeTests
            {
                [Fact] public void Widget_is_sealed() => Assert.True(typeof(Widget).IsSealed);
                [Fact] public void Widget_is_described() => Assert.NotNull(typeof(Widget).GetCustomAttribute<System.ComponentModel.DescriptionAttribute>());
            }
            """);
        var tests = Path.Combine(_directory, "Lib.Tests");
        Assert.Equal(0, Dotnet(tests, "build", "-nologo", "-v", "q"));
        Assert.Equal(0, Dotnet(tests, "test", "--no-build", "-nologo"));
        string[] library = [Path.Combine(tests, "bin", "Debug", "net10.0", "Lib.dll")];

        using (MutationInjector.Inject(library, new CoveredMethod("Lib.Widget", "<sealed>"), Path.Combine(_directory, "backup"), MutantKind.Unsealed))
        {
            Assert.NotEqual(0, Dotnet(tests, "test", "--no-build", "-nologo"));
        }

        using (MutationInjector.Inject(library, new CoveredMethod("Lib.Widget", "[Description]"), Path.Combine(_directory, "backup"), MutantKind.AttributeRemoved))
        {
            Assert.NotEqual(0, Dotnet(tests, "test", "--no-build", "-nologo"));
        }

        Assert.Equal(0, Dotnet(tests, "test", "--no-build", "-nologo"));
    }

    /// <summary>Runs <c>dotnet</c> in <paramref name="directory"/> and returns its exit code.</summary>
    /// <param name="directory">Working directory.</param>
    /// <param name="arguments">Arguments.</param>
    private static int Dotnet(string directory, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", arguments) { WorkingDirectory = directory, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Samples the fixture repository.</summary>
    /// <param name="request">How many and how.</param>
    private List<SyntheticTarget> Sample(SampleRequest request)
    {
        var graph = new ProjectGraphLoader(NullLogger<ProjectGraphLoader>.Instance).Load(_repo.Root);
        return TargetSampler.Sample(graph, _repo.Root, new HashSet<CoveredMethod>(), request);
    }

    /// <summary>What TestDetta's change detection sees in the file once the target's edits are made.</summary>
    /// <param name="detector">The detector.</param>
    /// <param name="target">A sampled target.</param>
    private static IReadOnlyList<SymbolChange> Changes(CodeChangeDetector detector, SyntheticTarget target)
    {
        var before = File.ReadAllText(target.Edits[0].Path);
        var edited = new System.Text.StringBuilder(before);
        foreach (var edit in target.Edits.OrderByDescending(static e => e.Position))
        {
            edited.Remove(edit.Position, edit.Replaces).Insert(edit.Position, edit.Text);
        }

        var change = detector.Detect("src/Lib/Code.cs", SourceText.From(before), SourceText.From(edited.ToString()));
        Assert.False(change.IsCosmetic);
        return change.Symbols;
    }

    /// <summary>Compiles <see cref="Code"/> against the reference pack into the test's directory.</summary>
    private string Compile()
    {
        var reference = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(
            "Lib",
            [CSharpSyntaxTree.ParseText(Code)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(reference, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(reference, "System.ComponentModel.Primitives.dll")),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(_directory, "Lib.dll");
        using (var pe = File.Create(path))
        {
            var emitted = compilation.Emit(pe);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        }

        return path;
    }
}
