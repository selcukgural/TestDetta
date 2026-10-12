using System.Text.Json;

namespace TestDetta.Core;

/// <summary>Reads and writes coverage maps as JSON under <c>.testdetta/coverage</c> in the repository.</summary>
public static class CoverageMapStore
{
    /// <summary>Directory, relative to the repository root, that holds one map per test project.</summary>
    public const string Directory = ".testdetta/coverage";

    /// <summary>
    /// Format of the maps written. 2 (2026-10-05): generated code is stored as its source instead of
    /// the names each generated type mentions. 3 (2026-10-12): the same fields, recorded by a recorder that
    /// keeps a test's own code in its class when the test builds a fixture type itself; a map of format 2 could
    /// credit what followed to the fixture's users and miss that test, and nothing in the map tells the two apart.
    /// A map of another format is not read, and its project is selected without coverage until it is recorded again.
    /// </summary>
    public const int Version = 3;

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
        json.WriteNumber("version"u8, Version);
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

        WriteIds(json, "initialization", map.Initialization);
        if (map.LinkedInitialization is { } linked)
        {
            WriteIds(json, "linkedInitialization", linked);
        }

        if (map.BuiltValues is { } built)
        {
            json.WriteStartObject("builtValues"u8);
            WriteBuckets(json, "classes", built.Classes);
            WriteBuckets(json, "fixtures", built.Fixtures);
            WriteIds(json, "ambient", built.Ambient);
            json.WriteEndObject();
        }

        if (map.ExternalCallbacks is { } callbacks)
        {
            WriteIds(json, "externalCallbacks", callbacks);
        }

        if (map.NestedMethods is { } nested)
        {
            WriteIds(json, "nestedMethods", nested);
        }

        if (map.Unobserved is { } unobserved)
        {
            WriteIds(json, "unobserved", unobserved);
        }

        if (map.Lineage is { } lineage)
        {
            json.WriteStartObject("lineage"u8);
            json.WriteString("fullCommit"u8, lineage.FullCommit);
            json.WriteString("fullRecordedAt"u8, lineage.FullRecordedAt);
            json.WriteNumber("steps"u8, lineage.Steps);
            json.WriteEndObject();
        }

        // Generated files can be large (a polyfill package writes hundreds of members); compressed,
        // they cost a fraction of the map.
        json.WriteStartArray("generated"u8);
        foreach (var source in map.Generated)
        {
            json.WriteStartArray();
            json.WriteStringValue(source.Project);
            json.WriteStringValue(source.Name);
            json.WriteBase64StringValue(Compress(source.Text));
            if (source.Framework is { } sourceFramework)
            {
                json.WriteStringValue(sourceFramework);
            }

            json.WriteEndArray();
        }

        json.WriteEndArray();

        if (map.FileReads is { } reads)
        {
            json.WriteStartObject("fileReads"u8);
            WritePaths(json, "classes", reads.Classes);
            WritePaths(json, "fixtures", reads.Fixtures);
            WriteStrings(json, "ambient", reads.Ambient);
            WriteStrings(json, "initialization", reads.Initialization);
            json.WriteEndObject();
        }

        if (map.OutputUses is { } uses)
        {
            json.WriteStartObject("outputUses"u8);
            WritePaths(json, "classes", uses.Classes);
            WritePaths(json, "fixtures", uses.Fixtures);
            WriteStrings(json, "ambient", uses.Ambient);
            WriteStrings(json, "initialization", uses.Initialization);
            json.WriteEndObject();
        }

        if (map.FailedClasses is { } failed)
        {
            json.WriteStartArray("failedClasses"u8);
            foreach (var name in failed.Order(StringComparer.Ordinal))
            {
                json.WriteStringValue(name);
            }

            json.WriteEndArray();
        }
        else
        {
            json.WriteNull("failedClasses"u8);
        }

        if (map.SkippedClasses is { } skipped)
        {
            json.WriteStartArray("skippedClasses"u8);
            foreach (var name in skipped.Order(StringComparer.Ordinal))
            {
                json.WriteStringValue(name);
            }

            json.WriteEndArray();
        }

        if (map.Framework is { } framework)
        {
            json.WriteString("framework"u8, framework);
        }

        if (map.BuildInputs is { } inputs)
        {
            json.WriteStartObject("buildInputs"u8);
            foreach (var (project, values) in inputs.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(project);
                foreach (var (key, value) in values.OrderBy(static v => v.Key, StringComparer.Ordinal))
                {
                    json.WriteString(key, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        if (map.FrameworkBuildInputs is { } frameworkInputs)
        {
            json.WriteStartObject("frameworkBuildInputs"u8);
            foreach (var (inputsFramework, projects) in frameworkInputs.OrderBy(static f => f.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(inputsFramework);
                foreach (var (project, values) in projects.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    json.WriteStartObject(project);
                    foreach (var (key, value) in values.OrderBy(static v => v.Key, StringComparer.Ordinal))
                    {
                        json.WriteString(key, value);
                    }

                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        if (map.GeneratedFrameworks is { } generatedFrameworks)
        {
            json.WriteStartArray("generatedFrameworks"u8);
            foreach (var generatedFramework in generatedFrameworks)
            {
                json.WriteStringValue(generatedFramework);
            }

            json.WriteEndArray();
        }

        if (map.Platform is { } platform)
        {
            json.WriteString("platform"u8, platform);
        }

        if (map.FileReadsUnseen is { } unseen)
        {
            json.WriteStartArray("fileReadsUnseen"u8);
            foreach (var assembly in unseen)
            {
                json.WriteStringValue(assembly);
            }

            json.WriteEndArray();
        }

        if (map.TracksShapeReads)
        {
            json.WriteBoolean("tracksShapeReads"u8, true);
        }

        if (map.OutputLoadsUnseen is { } loadsUnseen)
        {
            json.WriteStartArray("outputLoadsUnseen"u8);
            foreach (var assembly in loadsUnseen)
            {
                json.WriteStringValue(assembly);
            }

            json.WriteEndArray();
        }

        if (map.InstrumentedProjects is { } instrumented)
        {
            json.WriteStartArray("instrumentedProjects"u8);
            foreach (var project in instrumented)
            {
                json.WriteStringValue(project);
            }

            json.WriteEndArray();
        }

        if (map.DataSourceUsers is { } dataUsers)
        {
            json.WriteStartObject("dataSourceUsers"u8);
            foreach (var (bucket, classes) in dataUsers.OrderBy(static d => d.Key, StringComparer.Ordinal))
            {
                json.WriteStartArray(bucket);
                foreach (var testClass in classes)
                {
                    json.WriteStringValue(testClass);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        if (map.LeakedReads is { } leaked)
        {
            json.WriteStartObject("leakedReads"u8);
            foreach (var (testClass, fields) in leaked.OrderBy(static l => l.Key, StringComparer.Ordinal))
            {
                json.WriteStartArray(testClass);
                foreach (var field in fields)
                {
                    json.WriteStringValue(field);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        if (map.ClassLocations is { } locations)
        {
            WriteLocations(json, locations);
        }

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

    /// <summary>
    /// The format of the map at <paramref name="path"/>, read from its first property without parsing the rest (maps of
    /// large suites run to megabytes); null when the file is not a map that starts with its format.
    /// </summary>
    /// <param name="path">Map file.</param>
    public static int? FormatOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> start = stackalloc byte[64];
            var reader = new Utf8JsonReader(start[..stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false)], isFinalBlock: false, default);
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject
                   && reader.Read() && reader.ValueTextEquals("version"u8)
                   && reader.Read() && reader.TryGetInt32(out var version)
                ? version
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
            if (root.GetProperty("version").GetInt32() is var version && version != Version)
            {
                throw new InvalidDataException($"{path} is a coverage map of format {version}, this TestDetta reads format {Version}; record the maps again.");
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

            // Maps written before failures were recorded have no "failedClasses": nothing is known to have failed.
            var failed = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("failedClasses", out var failedElement))
            {
                if (failedElement.ValueKind == JsonValueKind.Null)
                {
                    failed = null;
                }
                else
                {
                    foreach (var name in failedElement.EnumerateArray())
                    {
                        failed.Add(name.GetString()!);
                    }
                }
            }

            return new CoverageMap(
                header,
                methods,
                new CoverageHits(ReadBuckets(root.GetProperty("classes")), fixtures, ReadIds(root.GetProperty("ambient"))),
                timings)
            {
                FailedClasses = failed,
                Initialization = root.TryGetProperty("initialization", out var initialization) ? ReadIds(initialization) : [],
                LinkedInitialization = root.TryGetProperty("linkedInitialization", out var linked) ? ReadIds(linked) : null,
                ExternalCallbacks = root.TryGetProperty("externalCallbacks", out var callbacks) ? ReadIds(callbacks) : null,
                NestedMethods = root.TryGetProperty("nestedMethods", out var nested) ? ReadIds(nested) : null,
                Unobserved = root.TryGetProperty("unobserved", out var unobserved) ? ReadIds(unobserved) : null,
                Lineage = root.TryGetProperty("lineage", out var lineage)
                    ? new MapLineage(
                        lineage.GetProperty("fullCommit").GetString()!,
                        lineage.GetProperty("fullRecordedAt").GetDateTimeOffset(),
                        lineage.GetProperty("steps").GetInt32())
                    : null,
                BuiltValues = root.TryGetProperty("builtValues", out var built)
                    ? new CoverageHits(ReadBuckets(built.GetProperty("classes")), ReadBuckets(built.GetProperty("fixtures")), ReadIds(built.GetProperty("ambient")))
                    : null,
                Generated = root.TryGetProperty("generated", out var generated) ? ReadGenerated(generated) : [],
                SkippedClasses = root.TryGetProperty("skippedClasses", out var skipped)
                    ? skipped.EnumerateArray().Select(static n => n.GetString()!).ToHashSet(StringComparer.Ordinal)
                    : null,
                Platform = root.TryGetProperty("platform", out var platform) ? platform.GetString() : null,
                FileReadsUnseen = root.TryGetProperty("fileReadsUnseen", out var unseen)
                    ? [.. unseen.EnumerateArray().Select(static a => a.GetString()!)]
                    : null,
                TracksShapeReads = root.TryGetProperty("tracksShapeReads", out var shapes) && shapes.GetBoolean(),
                OutputLoadsUnseen = root.TryGetProperty("outputLoadsUnseen", out var loadsUnseen)
                    ? [.. loadsUnseen.EnumerateArray().Select(static a => a.GetString()!)]
                    : null,
                InstrumentedProjects = root.TryGetProperty("instrumentedProjects", out var instrumented)
                    ? [.. instrumented.EnumerateArray().Select(static p => p.GetString()!)]
                    : null,
                DataSourceUsers = root.TryGetProperty("dataSourceUsers", out var dataUsers)
                    ? dataUsers.EnumerateObject().ToDictionary(
                        static d => d.Name,
                        static IReadOnlyList<string> (d) => [.. d.Value.EnumerateArray().Select(static c => c.GetString()!)],
                        StringComparer.Ordinal)
                    : null,
                LeakedReads = root.TryGetProperty("leakedReads", out var leaked)
                    ? leaked.EnumerateObject().ToDictionary(
                        static c => c.Name,
                        static IReadOnlyList<string> (c) => [.. c.Value.EnumerateArray().Select(static f => f.GetString()!)],
                        StringComparer.Ordinal)
                    : null,
                Framework = root.TryGetProperty("framework", out var framework) ? framework.GetString() : null,
                GeneratedFrameworks = root.TryGetProperty("generatedFrameworks", out var generatedFrameworks)
                    ? [.. generatedFrameworks.EnumerateArray().Select(static f => f.GetString()!)]
                    : null,
                ClassLocations = root.TryGetProperty("classLocations", out var locations) ? ReadLocations(locations) : null,
                FileReads = root.TryGetProperty("fileReads", out var reads)
                    ? new FileReads(ReadPaths(reads.GetProperty("classes")), ReadPaths(reads.GetProperty("fixtures")), ReadStrings(reads.GetProperty("ambient")), ReadStrings(reads.GetProperty("initialization")))
                    : null,
                BuildInputs = root.TryGetProperty("buildInputs", out var inputs) ? ReadBuildInputs(inputs) : null,
                FrameworkBuildInputs = root.TryGetProperty("frameworkBuildInputs", out var frameworkInputs)
                    ? frameworkInputs.EnumerateObject().ToDictionary(static f => f.Name, static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> (f) => ReadBuildInputs(f.Value), StringComparer.Ordinal)
                    : null,
                OutputUses = root.TryGetProperty("outputUses", out var uses)
                    ? new FileReads(ReadPaths(uses.GetProperty("classes")), ReadPaths(uses.GetProperty("fixtures")), ReadStrings(uses.GetProperty("ambient")), ReadStrings(uses.GetProperty("initialization")))
                    : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"{path} is not a valid coverage map: {ex.Message}", ex);
        }
    }

    /// <summary>Writes <c>{"name": ["path", ...]}</c> sorted by name, for the file reads of classes or fixtures.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="paths">Paths by class or fixture.</param>
    private static void WritePaths(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, IReadOnlyList<string>> paths)
    {
        json.WriteStartObject(name);
        foreach (var (key, values) in paths.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            WriteStrings(json, key, values);
        }

        json.WriteEndObject();
    }

    /// <summary>Writes an array of strings.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="values">The strings.</param>
    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    /// <summary>Reads what <see cref="WritePaths"/> wrote.</summary>
    /// <param name="element">The object.</param>
    private static Dictionary<string, IReadOnlyList<string>> ReadPaths(JsonElement element) =>
        element.EnumerateObject().ToDictionary(static p => p.Name, static p => ReadStrings(p.Value), StringComparer.Ordinal);

    /// <summary>Reads an array of strings.</summary>
    /// <param name="element">The array.</param>
    private static IReadOnlyList<string> ReadStrings(JsonElement element) => [.. element.EnumerateArray().Select(static v => v.GetString()!)];

    /// <summary>Reads <c>[project, name, compressed text, framework?]</c> entries.</summary>
    /// <param name="element">The "generated" array.</param>
    private static List<GeneratedSource> ReadGenerated(JsonElement element)
    {
        var sources = new List<GeneratedSource>();
        foreach (var entry in element.EnumerateArray())
        {
            sources.Add(new GeneratedSource(entry[0].GetString()!, entry[1].GetString()!, Decompress(entry[2].GetBytesFromBase64()))
            {
                Framework = entry.GetArrayLength() > 3 ? entry[3].GetString() : null,
            });
        }

        return sources;
    }

    /// <summary>A text as gzip-compressed UTF-8.</summary>
    /// <param name="text">The text.</param>
    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
        {
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    /// <summary>The text of <see cref="Compress"/>'s output.</summary>
    /// <param name="data">Gzip-compressed UTF-8.</param>
    private static string Decompress(byte[] data)
    {
        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Writes class locations as <c>{"class": [[file, line], ...]}</c>, sorted by class so the file is stable.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="locations">Class to its locations.</param>
    private static void WriteLocations(Utf8JsonWriter json, IReadOnlyDictionary<string, IReadOnlyList<ClassLocation>> locations)
    {
        json.WriteStartObject("classLocations"u8);
        foreach (var (testClass, places) in locations.OrderBy(static l => l.Key, StringComparer.Ordinal))
        {
            json.WriteStartArray(testClass);
            foreach (var place in places)
            {
                json.WriteStartArray();
                json.WriteStringValue(place.File);
                json.WriteNumberValue(place.Line);
                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>Reads one framework's build inputs: by project, the inputs' names and checksums.</summary>
    /// <param name="inputs">The JSON object.</param>
    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadBuildInputs(JsonElement inputs) =>
        inputs.EnumerateObject().ToDictionary(
            static p => p.Name,
            static p => (IReadOnlyDictionary<string, string>)p.Value.EnumerateObject().ToDictionary(static v => v.Name, static v => v.Value.GetString()!, StringComparer.Ordinal),
            StringComparer.Ordinal);

    /// <summary>Reads the <c>classLocations</c> object written by <see cref="WriteLocations"/>.</summary>
    /// <param name="element">JSON object.</param>
    private static Dictionary<string, IReadOnlyList<ClassLocation>> ReadLocations(JsonElement element)
    {
        var locations = new Dictionary<string, IReadOnlyList<ClassLocation>>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var places = new List<ClassLocation>(property.Value.GetArrayLength());
            foreach (var place in property.Value.EnumerateArray())
            {
                places.Add(new ClassLocation(place[0].GetString()!, place[1].GetInt32()));
            }

            locations[property.Name] = places;
        }

        return locations;
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
