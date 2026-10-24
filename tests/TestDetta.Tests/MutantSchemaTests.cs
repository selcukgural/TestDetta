using System.Reflection;
using System.Runtime.Loader;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>Tests that set process-wide environment variables, run one at a time.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// A schema writes every mutant in behind a switch. With the switch on for one mutant, the code must
/// behave exactly like that mutant written in alone (the way the evaluation always worked); with no
/// switch, exactly like the original. Checked for every mutant kind and the awkward shapes: a method
/// that starts with a try block, a branch on a reference, an equality branch, a field constant.
/// Slow: builds a library.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
[Trait("Category", "Slow")]
public sealed class MutantSchemaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "testdetta-schema-" + Guid.NewGuid().ToString("N"));

    /// <summary>Each case: id, member, kind, and how to observe it (method and argument, or a field).</summary>
    private static readonly (int Id, string Member, MutantKind Kind, string Probe, object?[] Arguments)[] Cases =
    [
        (1, "Twice", MutantKind.Throw, "Twice", [3]),
        (2, "Guarded", MutantKind.Throw, "Guarded", [3]),
        (3, "Sign", MutantKind.ReturnDefault, "Sign", [5]),
        (4, "IsSmall", MutantKind.FlipBranch, "IsSmall", [1]),
        (5, "Describe", MutantKind.FlipBranch, "Describe", [null]),
        (6, "Same", MutantKind.FlipBranch, "Same", [2, 2]),
        (7, "Max", MutantKind.FieldValue, "Max", []),
        (8, "Label", MutantKind.FieldValue, "Label", []),
        (9, "Log", MutantKind.ReturnDefault, "Log", ["x"]),
    ];

    [Fact]
    public void Switched_on_mutant_behaves_like_the_mutant_written_in_alone_and_off_like_the_original()
    {
        var original = Build();

        var schemaCopy = Copy(original, "schema");
        using var schema = MutationInjector.InjectSchema(
            [schemaCopy],
            [.. Cases.Select(static c => new SchemaTarget(c.Id, new CoveredMethod("Lib.Target", c.Member), c.Kind))],
            Path.Combine(_directory, "schema-backup"));
        Assert.Equal(Cases.Length, schema.Entries.Count);

        foreach (var (id, member, kind, probe, arguments) in Cases)
        {
            var aloneCopy = Copy(original, $"alone-{id}");
            using var alone = MutationInjector.Inject([aloneCopy], new CoveredMethod("Lib.Target", member), Path.Combine(_directory, $"alone-backup-{id}"), kind);
            Assert.Equal(alone.MutatedMethods, schema.Entries[id].MutatedMethods);

            var expected = Observe(aloneCopy, probe, arguments, mutant: null);
            Assert.Equal(expected, Observe(schemaCopy, probe, arguments, mutant: id));
            Assert.Equal(Observe(original, probe, arguments, mutant: null), Observe(schemaCopy, probe, arguments, mutant: null));
            // Another mutant switched on leaves this member alone.
            Assert.Equal(Observe(original, probe, arguments, mutant: null), Observe(schemaCopy, probe, arguments, mutant: id % Cases.Length + 1));
            Assert.NotEqual(Observe(original, probe, arguments, mutant: null), expected);
        }
    }

    /// <summary>Compiles the library every case is taken from and returns its path.</summary>
    private string Build()
    {
        var project = Path.Combine(_directory, "Lib");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(_directory, "global.json"), """{ "sdk": { "version": "10.0.105", "rollForward": "latestFeature" } }""");
        File.WriteAllText(Path.Combine(project, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Target.cs"), """
            namespace Lib;
            public static class Target
            {
                public static readonly int Max = 3;
                public static readonly string Label = "a";
                public static int Twice(int x) => x * 2;

                // Starts with a try block: the mutant's exception must not be caught by its own handler.
                public static int Guarded(int x)
                {
                    try { return x + 1; }
                    catch (System.InvalidOperationException) { return -1; }
                }

                public static int Sign(int x) => x > 0 ? 1 : -1;
                public static bool IsSmall(int x) { if (x < 3) { return true; } return false; }
                public static string Describe(object? o) { if (o == null) { return "null"; } return "set"; }
                public static bool Same(int a, int b) { if (a == b) { return true; } return false; }
                public static string Log(string text) { System.Console.Write(""); return "logged " + text; }
            }
            """);
        var output = Path.Combine(_directory, "out");
        using var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["build", project, "-o", output, "-nologo", "-v", "q", "-nodeReuse:false"]) { RedirectStandardOutput = true })!;
        build.StandardOutput.ReadToEnd();
        build.WaitForExit();
        Assert.Equal(0, build.ExitCode);
        return Path.Combine(output, "Lib.dll");
    }

    /// <summary>Copies the built assembly and its .pdb to a folder of its own.</summary>
    private string Copy(string assembly, string name)
    {
        var folder = Path.Combine(_directory, name);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(assembly));
        File.Copy(assembly, target);
        File.Copy(Path.ChangeExtension(assembly, ".pdb"), Path.ChangeExtension(target, ".pdb"));
        return target;
    }

    /// <summary>
    /// Loads the assembly in a load context of its own, with <c>TESTDETTA_MUTANT</c> set to
    /// <paramref name="mutant"/> (or unset) before its switch is first read, and reports what the probe
    /// returned or threw.
    /// </summary>
    private static string Observe(string assembly, string probe, object?[] arguments, int? mutant)
    {
        var previous = Environment.GetEnvironmentVariable(Schema.Variable);
        Environment.SetEnvironmentVariable(Schema.Variable, mutant?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var context = new AssemblyLoadContext("schema-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(assembly));
            var type = context.LoadFromStream(stream).GetType("Lib.Target", throwOnError: true)!;
            if (type.GetField(probe, BindingFlags.Public | BindingFlags.Static) is { } field)
            {
                return $"value {field.GetValue(null)}";
            }

            try
            {
                return $"value {type.GetMethod(probe)!.Invoke(null, arguments)}";
            }
            catch (TargetInvocationException ex)
            {
                return $"throws {ex.InnerException!.GetType().Name}: {ex.InnerException.Message}";
            }
        }
        finally
        {
            context.Unload();
            Environment.SetEnvironmentVariable(Schema.Variable, previous);
        }
    }

    public void Dispose() => Paths.DeleteTree(_directory);
}
