using System.Text.Json;

namespace Sift.Core;

/// <summary>Reads and writes coverage maps as JSON under <c>.sift/coverage</c> in the repository.</summary>
public static class CoverageMapStore
{
    /// <summary>Directory, relative to the repository root, that holds one map per test project.</summary>
    public const string Directory = ".sift/coverage";

    /// <summary>Path of the map for <paramref name="testProjectPath"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="testProjectPath">Path of the test project's .csproj file.</param>
    public static string PathFor(string repoRoot, string testProjectPath) =>
        Path.Combine(repoRoot, Directory, Path.GetFileNameWithoutExtension(testProjectPath) + ".json");

    /// <summary>Writes <paramref name="map"/> to <paramref name="path"/>, creating the directory.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="map">Map to write.</param>
    public static void Write(string path, CoverageMap map)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });

        json.WriteStartObject();
        json.WriteNumber("version"u8, 1);
        json.WriteString("testProject"u8, map.TestProject);
        json.WriteString("commit"u8, map.Commit);
        json.WriteString("recordedAt"u8, map.RecordedAt);

        // Methods as [type, member] pairs keep the file compact; the index is the id used below.
        json.WriteStartArray("methods"u8);
        foreach (var method in map.Methods)
        {
            json.WriteStartArray();
            json.WriteStringValue(method.TypeName);
            json.WriteStringValue(method.MemberName);
            json.WriteEndArray();
        }
        json.WriteEndArray();

        json.WriteStartObject("classes"u8);
        foreach (var (testClass, ids) in map.Classes.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            WriteIds(json, testClass, ids);
        }
        json.WriteEndObject();

        WriteIds(json, "ambient", map.Ambient);
        json.WriteEndObject();
    }

    /// <summary>Reads the map at <paramref name="path"/>; null when the file does not exist.</summary>
    /// <param name="path">Map file.</param>
    /// <exception cref="InvalidDataException">The file is not a coverage map this version understands.</exception>
    public static CoverageMap? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1)
            {
                throw new InvalidDataException($"{path} has an unsupported coverage map version.");
            }

            var methods = new List<CoveredMethod>();
            foreach (var pair in root.GetProperty("methods").EnumerateArray())
            {
                methods.Add(new CoveredMethod(pair[0].GetString()!, pair[1].GetString()!));
            }

            var classes = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
            foreach (var property in root.GetProperty("classes").EnumerateObject())
            {
                classes[property.Name] = ReadIds(property.Value);
            }

            return new CoverageMap(
                root.GetProperty("testProject").GetString()!,
                root.GetProperty("commit").GetString()!,
                root.GetProperty("recordedAt").GetDateTimeOffset(),
                methods,
                classes,
                ReadIds(root.GetProperty("ambient")));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{path} is not a valid coverage map: {ex.Message}", ex);
        }
    }

    /// <summary>Writes an array of method ids under <paramref name="name"/>.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="ids">Method ids.</param>
    private static void WriteIds(Utf8JsonWriter json, string name, IReadOnlyList<int> ids)
    {
        json.WriteStartArray(name);
        foreach (var id in ids)
        {
            json.WriteNumberValue(id);
        }

        json.WriteEndArray();
    }

    /// <summary>Reads an array of method ids.</summary>
    /// <param name="element">JSON array.</param>
    private static int[] ReadIds(JsonElement element)
    {
        var ids = new int[element.GetArrayLength()];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            ids[i++] = item.GetInt32();
        }

        return ids;
    }
}
