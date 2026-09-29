using System.Text.Json;
using System.Text.Json.Nodes;
using Sift.Coverage.Runtime;

namespace Sift.Coverage;

/// <summary>
/// Makes <see cref="SiftTracker"/> loadable in a test executable (Microsoft.Testing.Platform), where
/// no VSTest collector resolves it: copies the runtime assembly into the test output and lists it
/// in the test project's <c>deps.json</c>, so the host puts it on the assembly path. Disposing puts
/// the output back as it was.
/// </summary>
internal sealed class RuntimeDeployment : IDisposable
{
    /// <summary>File name of the runtime assembly.</summary>
    private static readonly string RuntimeFileName = Path.GetFileName(typeof(SiftTracker).Assembly.Location);

    private readonly string _copiedRuntime;
    private readonly string _depsFile;
    private readonly string _originalDeps;
    private readonly bool _runtimeExisted;

    /// <summary>Deploys the runtime next to <paramref name="testAssembly"/>.</summary>
    /// <param name="testAssembly">Path of the test project's compiled assembly.</param>
    /// <exception cref="InvalidOperationException">The test output has no <c>deps.json</c> to extend.</exception>
    public RuntimeDeployment(string testAssembly)
    {
        var directory = Path.GetDirectoryName(testAssembly)!;
        _depsFile = Path.ChangeExtension(testAssembly, ".deps.json");
        if (!File.Exists(_depsFile))
        {
            throw new InvalidOperationException($"{_depsFile} does not exist; Sift needs it to load its runtime into the tests.");
        }

        _originalDeps = File.ReadAllText(_depsFile);
        _copiedRuntime = Path.Combine(directory, RuntimeFileName);
        _runtimeExisted = File.Exists(_copiedRuntime);

        File.Copy(typeof(SiftTracker).Assembly.Location, _copiedRuntime, overwrite: true);
        File.WriteAllText(_depsFile, AddRuntime(_originalDeps));
    }

    /// <summary>Restores the original <c>deps.json</c> and removes the copied runtime.</summary>
    public void Dispose()
    {
        File.WriteAllText(_depsFile, _originalDeps);
        if (!_runtimeExisted)
        {
            File.Delete(_copiedRuntime);
        }
    }

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

        var name = typeof(SiftTracker).Assembly.GetName();
        var key = $"{name.Name}/{name.Version?.ToString(3) ?? "1.0.0"}";

        target[key] = new JsonObject
        {
            ["runtime"] = new JsonObject { [RuntimeFileName] = new JsonObject() },
        };
        libraries[key] = new JsonObject
        {
            ["type"] = "project",
            ["serviceable"] = false,
            ["sha512"] = string.Empty,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
