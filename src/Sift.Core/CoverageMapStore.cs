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
        json.WriteString("recordedAt"u8, map.Header.RecordedAt);

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

        WriteBuckets(json, "classes", map.Classes);
        WriteBuckets(json, "fixtures", map.Fixtures);
        WriteIds(json, "ambient", map.Ambient);

        if (map.Timings is { } timings)
        {
            json.WriteStartObject("timings"u8);
            WriteSeconds(json, "classes", timings.ClassSeconds);
            WriteSeconds(json, "fixtures", timings.FixtureSeconds);
            json.WriteNumber("startup"u8, Math.Round(timings.StartupSeconds, 4));
            json.WriteEndObject();
        }

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

            var header = new CoverageMapHeader(
                root.GetProperty("testProject").GetString()!,
                root.GetProperty("commit").GetString()!,
                root.GetProperty("recordedAt").GetDateTimeOffset());

            // Maps written before fixtures were recorded have no "fixtures" object.
            var fixtures = root.TryGetProperty("fixtures", out var fixtureElement)
                ? ReadBuckets(fixtureElement)
                : new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

            CoverageTimings? timings = null;
            if (root.TryGetProperty("timings", out var timingElement))
            {
                timings = new CoverageTimings(
                    ReadSeconds(timingElement.GetProperty("classes")),
                    ReadSeconds(timingElement.GetProperty("fixtures")),
                    timingElement.TryGetProperty("startup", out var startup) ? startup.GetDouble() : 0);
            }

            return new CoverageMap(
                header,
                methods,
                new CoverageHits(ReadBuckets(root.GetProperty("classes")), fixtures, ReadIds(root.GetProperty("ambient"))),
                timings);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{path} is not a valid coverage map: {ex.Message}", ex);
        }
    }

    /// <summary>Writes an object of named id arrays, sorted by name so the file is stable.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="buckets">Name to method ids.</param>
    private static void WriteBuckets(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, IReadOnlyList<int>> buckets)
    {
        json.WriteStartObject(name);
        foreach (var (key, ids) in buckets.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            WriteIds(json, key, ids);
        }

        json.WriteEndObject();
    }

    /// <summary>Reads an object of named id arrays.</summary>
    /// <param name="element">JSON object.</param>
    private static Dictionary<string, IReadOnlyList<int>> ReadBuckets(JsonElement element)
    {
        var buckets = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            buckets[property.Name] = ReadIds(property.Value);
        }

        return buckets;
    }

    /// <summary>Writes an object of name to seconds, sorted by name.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="seconds">Name to seconds.</param>
    private static void WriteSeconds(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, double> seconds)
    {
        json.WriteStartObject(name);
        foreach (var (key, value) in seconds.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            json.WriteNumber(key, Math.Round(value, 4));
        }

        json.WriteEndObject();
    }

    /// <summary>Reads an object of name to seconds.</summary>
    /// <param name="element">JSON object.</param>
    private static Dictionary<string, double> ReadSeconds(JsonElement element)
    {
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            seconds[property.Name] = property.Value.GetDouble();
        }

        return seconds;
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
