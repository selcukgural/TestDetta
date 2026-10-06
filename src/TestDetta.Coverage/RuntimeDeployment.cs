using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestDetta.Coverage.Runtime;

namespace TestDetta.Coverage;

/// <summary>
/// Makes <see cref="TestDettaTracker"/> loadable in a test executable (Microsoft.Testing.Platform), where
/// no VSTest collector resolves it: copies the runtime assembly into the test output and lists it
/// in the test project's <c>deps.json</c>, so the host puts it on the assembly path. A .NET Framework
/// output has no <c>deps.json</c>: the runtime probes the application's folder, so the copy is enough.
/// Disposing puts the output back as it was.
/// </summary>
internal sealed class RuntimeDeployment : IDisposable
{
    /// <summary>File name of the runtime assembly.</summary>
    private static readonly string RuntimeFileName = Path.GetFileName(typeof(TestDettaTracker).Assembly.Location);

    private readonly string _copiedRuntime;
    private readonly string? _depsFile;
    private readonly string? _originalDeps;
    private readonly bool _runtimeExisted;

    /// <summary>Deploys the runtime next to <paramref name="testAssembly"/>.</summary>
    /// <param name="testAssembly">Path of the test project's compiled assembly.</param>
    /// <exception cref="InvalidOperationException">The test output has no <c>deps.json</c> to extend.</exception>
    public RuntimeDeployment(string testAssembly)
    {
        var directory = Path.GetDirectoryName(testAssembly)!;
        var depsFile = Path.ChangeExtension(testAssembly, ".deps.json");
        if (File.Exists(depsFile))
        {
            _depsFile = depsFile;
            _originalDeps = File.ReadAllText(depsFile);
        }
        else if (!IsNetFramework(testAssembly))
        {
            throw new InvalidOperationException($"{depsFile} does not exist; TestDetta needs it to load its runtime into the tests.");
        }

        _copiedRuntime = Path.Combine(directory, RuntimeFileName);
        _runtimeExisted = File.Exists(_copiedRuntime);

        File.Copy(TrackerFor(testAssembly), _copiedRuntime, overwrite: true);
        if (_depsFile is not null)
        {
            File.WriteAllText(_depsFile, AddRuntime(_originalDeps!));
        }
    }

    /// <summary>
    /// The build of the tracker assembly a test host of <paramref name="testAssembly"/> loads: the net462
    /// build, shipped under <c>runtime/net462</c>, for a .NET Framework test assembly, and the netstandard2.0
    /// build TestDetta itself references for any other (docs/design/framework-support.md).
    /// </summary>
    /// <param name="testAssembly">The compiled test assembly.</param>
    internal static string TrackerFor(string testAssembly)
    {
        var standard = typeof(TestDettaTracker).Assembly.Location;
        if (!IsNetFramework(testAssembly))
        {
            return standard;
        }

        var framework = Path.Combine(Path.GetDirectoryName(typeof(RuntimeDeployment).Assembly.Location)!, "runtime", "net462", RuntimeFileName);
        return File.Exists(framework)
            ? framework
            : throw new InvalidOperationException($"{framework} is missing; TestDetta needs it to record .NET Framework tests.");
    }

    /// <summary>True when <paramref name="assembly"/> targets .NET Framework (its <c>TargetFrameworkAttribute</c>, or none at all).</summary>
    /// <param name="assembly">A compiled test assembly.</param>
    internal static bool IsNetFramework(string assembly)
    {
        try
        {
            using var module = Mono.Cecil.ModuleDefinition.ReadModule(assembly);
            return module.Assembly?.CustomAttributes
                    .FirstOrDefault(static a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")?
                    .ConstructorArguments.FirstOrDefault().Value is not string framework
                || framework.StartsWith(".NETFramework", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return false;
        }
    }

    /// <summary>Restores the original <c>deps.json</c> and removes the copied runtime.</summary>
    public void Dispose()
    {
        if (_depsFile is not null)
        {
            File.WriteAllText(_depsFile, _originalDeps!);
        }

        if (!_runtimeExisted)
        {
            File.Delete(_copiedRuntime);
        }
    }

    /// <summary>Writes the patched file as the SDK does: indented, and without escaping characters such as <c>+</c>.</summary>
    /// <remarks>
    /// The default encoder writes the <c>+</c> of package hashes as <c>\u002B</c>. The host reads that,
    /// but the JSON reader bundled with xUnit 2.3's VSTest adapter does not: it then sees no reference
    /// to xUnit and finds no tests (Newtonsoft.Json).
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Adds the runtime as a project library of the runtime target. The host puts the runtime assets
    /// of every library of the target on the assembly path, so nothing needs to depend on it.
    /// </summary>
    /// <param name="depsJson">Original <c>deps.json</c> content.</param>
    /// <exception cref="InvalidOperationException">The file does not have the expected shape.</exception>
    internal static string AddRuntime(string depsJson)
    {
        var root = JsonNode.Parse(depsJson)?.AsObject()
                   ?? throw new InvalidOperationException("deps.json is empty.");
        var targetName = root["runtimeTarget"]?["name"]?.GetValue<string>()
                         ?? throw new InvalidOperationException("deps.json has no runtime target.");
        var target = root["targets"]?[targetName]?.AsObject()
                     ?? throw new InvalidOperationException($"deps.json has no target {targetName}.");
        var libraries = root["libraries"]?.AsObject()
                        ?? throw new InvalidOperationException("deps.json has no libraries.");

        var name = typeof(TestDettaTracker).Assembly.GetName();
        var key = $"{name.Name}/{name.Version?.ToString(3) ?? "1.0.0"}";

        // A project that already references the runtime has it listed, with its own dependencies;
        // that entry is kept as the SDK wrote it.
        target.TryAdd(key, new JsonObject
        {
            ["runtime"] = new JsonObject { [RuntimeFileName] = new JsonObject() },
        });
        libraries.TryAdd(key, new JsonObject
        {
            ["type"] = "project",
            ["serviceable"] = false,
            ["sha512"] = string.Empty,
        });

        return root.ToJsonString(WriteOptions);
    }
}
