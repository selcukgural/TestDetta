using System.Diagnostics;
using System.Text.Json;

namespace Sift.Coverage.Runtime;

/// <summary>
/// Records which instrumented methods run for which test class. Instrumented code calls
/// <see cref="Hit"/> at the start of every method; entry points of test classes call
/// <see cref="EnterTestInstance"/> or <see cref="EnterTestClass"/>, and setup code of test
/// fixtures calls <see cref="EnterFixtureInstance"/>. Everything is static because instrumented
/// assemblies reach it through plain calls.
/// </summary>
/// <remarks>
/// <para>
/// A hit goes to the bucket (test class or fixture) of the current logical flow, carried in an
/// <see cref="AsyncLocal{T}"/> through awaits and <c>Task.Run</c>. Code that runs without that
/// flow, such as an in-memory test server handling a request on its own thread, goes to the
/// bucket that was entered last. That is exact because <c>sift record</c> runs tests one at a time.
/// </para>
/// <para>
/// Fixture setup is kept apart because it runs before the test class it serves is created, and a
/// shared fixture serves several classes; selection credits it to every class that uses the fixture.
/// </para>
/// </remarks>
public static class SiftTracker
{
    /// <summary>Environment variable with the number of instrumented methods, set by <c>sift record</c>.</summary>
    public const string MethodCountVariable = "SIFT_METHOD_COUNT";

    /// <summary>Environment variable with the path of the JSON file written at the end of the session.</summary>
    public const string OutputVariable = "SIFT_COVERAGE_OUTPUT";

    /// <summary>Guards the bucket dictionaries; the hit path never takes it.</summary>
    private static readonly object Gate = new();

    /// <summary>Number of 64-bit words a bit set needs for all method ids.</summary>
    private static readonly int Words = (ReadMethodCount() + 63) / 64;

    /// <summary>Hits per test class, keyed by runtime class name.</summary>
    private static readonly Dictionary<string, Bucket> Classes = new(StringComparer.Ordinal);

    /// <summary>Hits per fixture type, keyed by type name (generic definitions, nested types with '+').</summary>
    private static readonly Dictionary<string, Bucket> Fixtures = new(StringComparer.Ordinal);

    /// <summary>Bucket of the current logical flow.</summary>
    private static readonly AsyncLocal<Bucket?> Current = new();

    /// <summary>Hits before any test class or fixture was entered: discovery, theory data, module initializers.</summary>
    private static readonly Bucket Startup = new("startup", new ulong[Words], isFixture: false);

    /// <summary>Bucket entered last on any thread, for code that runs outside every test flow.</summary>
    private static Bucket? _lastEntered;

    /// <summary><see cref="Stopwatch"/> timestamp of the last switch between buckets.</summary>
    private static long _lastSwitch;

    /// <summary>
    /// Seconds from process start until the first test class or fixture was entered: host start-up,
    /// discovery and module initialization, paid once by every test run whatever it selects.
    /// </summary>
    private static double _startupSeconds;

    /// <summary>
    /// Writes the results when the process ends. Under Microsoft.Testing.Platform the tests run in
    /// their own executable, where no VSTest collector exists to say the session is over.
    /// </summary>
    static SiftTracker() => AppDomain.CurrentDomain.ProcessExit += static (_, _) => WriteResults();

    /// <summary>Records that method <paramref name="id"/> ran. Called by instrumented code; must stay cheap.</summary>
    /// <param name="id">Method id assigned by the instrumenter.</param>
    public static void Hit(int id)
    {
        var bits = BucketForHit().Bits;
        var word = id >> 6;
        if ((uint)word >= (uint)bits.Length)
        {
            return;
        }

        var bit = 1UL << (id & 63);

        // A plain read first: once a method is marked, repeated calls cost no atomic operation.
        if ((Volatile.Read(ref bits[word]) & bit) != 0)
        {
            return;
        }

        Interlocked.Or(ref bits[word], bit);
    }

    /// <summary>
    /// The bucket a hit goes to: the current flow's, or the one entered last for code outside every
    /// flow. A fixture's flow lives on in the background work it starts (a queue processor, a hosted
    /// service); once a test class has been entered, that work serves the class, not the setup.
    /// </summary>
    private static Bucket BucketForHit()
    {
        var lastEntered = Volatile.Read(ref _lastEntered);
        var current = Current.Value;
        if (current is { IsFixture: true } && lastEntered is { IsFixture: false })
        {
            return lastEntered;
        }

        return current ?? lastEntered ?? Startup;
    }

    /// <summary>
    /// Makes the class of <paramref name="instance"/> the current bucket. Called at the start of
    /// constructors, test methods and lifecycle methods of test classes; the runtime type is used so
    /// inherited tests count for the derived class that runs them.
    /// </summary>
    /// <param name="instance">The test class instance (<c>this</c>).</param>
    public static void EnterTestInstance(object instance) => EnterTestClass(RuntimeName(instance.GetType()));

    /// <summary>Makes <paramref name="testClass"/> the current bucket.</summary>
    /// <param name="testClass">Runtime class name, nested classes joined with '+'.</param>
    public static void EnterTestClass(string testClass) => Enter(Classes, testClass, isFixture: false);

    /// <summary>
    /// Makes the fixture <paramref name="instance"/> the current bucket. Called at the start of fixture
    /// constructors and <c>InitializeAsync</c>, including those inherited from a base class, so the
    /// runtime type names the concrete fixture.
    /// </summary>
    /// <param name="instance">The fixture instance (<c>this</c>).</param>
    public static void EnterFixtureInstance(object instance) => Enter(Fixtures, FixtureName(instance.GetType()), isFixture: true);

    /// <summary>
    /// Writes the collected data next to the file named by <see cref="OutputVariable"/>, with this
    /// process's id in the name: <c>coverage-0.json</c> becomes <c>coverage-0.4711.json</c>.
    /// Microsoft.Testing.Platform may run the tests in a child process under a controller process
    /// (with the TRX or dump extensions); both load the tracker, and the controller, which ends last,
    /// would otherwise overwrite the child's results with its own empty ones.
    /// </summary>
    public static void WriteResults()
    {
        var path = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        WriteResults(ProcessResultsPath(path, Environment.ProcessId));
    }

    /// <summary>The results file of one process: the process id goes before the extension.</summary>
    /// <param name="path">Value of <see cref="OutputVariable"/>.</param>
    /// <param name="processId">Process id.</param>
    public static string ProcessResultsPath(string path, int processId) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(path)}.{processId}{Path.GetExtension(path)}");

    /// <summary>Writes the collected data to <paramref name="path"/>, replacing it in one step.</summary>
    /// <param name="path">Results file.</param>
    internal static void WriteResults(string path)
    {
        lock (Gate)
        {
            // Close the time of the bucket still current; a later call (process exit after the
            // collector's session end) then only adds what ran in between.
            var now = Stopwatch.GetTimestamp();
            if (_lastEntered is { } current)
            {
                current.ElapsedTicks += now - _lastSwitch;
                _lastSwitch = now;
            }

            // Written next to the target and moved over it: under VSTest the results are written at
            // session end and again at process exit, and the test host may be killed during the
            // second write. A rename never leaves a half-written file; the earlier one stays.
            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            using (var stream = File.Create(temporary))
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                WriteBuckets(json, "classes", Classes);
                WriteBuckets(json, "fixtures", Fixtures);
                WriteIds(json, "ambient", Startup.Bits);
                WriteSeconds(json, "classSeconds", Classes);
                WriteSeconds(json, "fixtureSeconds", Fixtures);
                json.WriteNumber("startupSeconds", _startupSeconds);
                json.WriteEndObject();
            }

            File.Move(temporary, path, overwrite: true);
        }
    }

    /// <summary>A named bit set of method ids, and the time spent in the bucket.</summary>
    /// <param name="name">Test class or fixture name.</param>
    /// <param name="bits">Hit methods.</param>
    /// <param name="isFixture">True for a fixture bucket.</param>
    private sealed class Bucket(string name, ulong[] bits, bool isFixture)
    {
        /// <summary>Test class or fixture name.</summary>
        public string Name { get; } = name;

        /// <summary>Hit methods.</summary>
        public ulong[] Bits { get; } = bits;

        /// <summary>True for a fixture bucket.</summary>
        public bool IsFixture { get; } = isFixture;

        /// <summary>
        /// <see cref="Stopwatch"/> ticks during which this was the bucket entered last. Because
        /// recording runs one test at a time, that is the time its tests (or its setup) took.
        /// </summary>
        public long ElapsedTicks { get; set; }
    }

    /// <summary>Makes the bucket <paramref name="name"/> of <paramref name="buckets"/> current, for this flow and globally.</summary>
    /// <param name="buckets">Class or fixture buckets.</param>
    /// <param name="name">Bucket name.</param>
    /// <param name="isFixture">True when <paramref name="buckets"/> holds fixtures.</param>
    private static void Enter(Dictionary<string, Bucket> buckets, string name, bool isFixture)
    {
        if (Current.Value is { } current && ReferenceEquals(current, Volatile.Read(ref _lastEntered))
            && string.Equals(current.Name, name, StringComparison.Ordinal))
        {
            return;
        }

        Bucket bucket;
        lock (Gate)
        {
            if (!buckets.TryGetValue(name, out bucket!))
            {
                bucket = new Bucket(name, new ulong[Words], isFixture);
                buckets[name] = bucket;
            }

            // The time since the last switch belongs to the bucket that was current until now.
            var now = Stopwatch.GetTimestamp();
            if (_lastEntered is { } previous && !ReferenceEquals(previous, bucket))
            {
                previous.ElapsedTicks += now - _lastSwitch;
                _lastSwitch = now;
            }
            else if (_lastEntered is null)
            {
                _lastSwitch = now;
                _startupSeconds = SecondsSinceProcessStart();
            }

            Volatile.Write(ref _lastEntered, bucket);
        }

        Current.Value = bucket;
    }

    /// <summary>Name of a type as test platforms print it: nested types with '+', generic definitions without arguments.</summary>
    /// <param name="type">Runtime type.</param>
    private static string RuntimeName(Type type)
    {
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? definition.Name;
    }

    /// <summary>
    /// Name of a fixture type including its generic arguments, <c>App.Tests.ApiHost`1[App.Tests.BlogProfile]</c>:
    /// each constructed fixture sets up different things, and selection matches the arguments too.
    /// </summary>
    /// <param name="type">Runtime type of the fixture.</param>
    private static string FixtureName(Type type)
    {
        if (!type.IsGenericType)
        {
            return RuntimeName(type);
        }

        var arguments = type.GetGenericArguments().Select(FixtureName);
        return $"{RuntimeName(type)}[{string.Join(",", arguments)}]";
    }

    /// <summary>Writes every bucket of <paramref name="buckets"/> as a property with an array of ids.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name of the object that holds the buckets.</param>
    /// <param name="buckets">Buckets to write.</param>
    private static void WriteBuckets(Utf8JsonWriter json, string name, Dictionary<string, Bucket> buckets)
    {
        json.WriteStartObject(name);
        foreach (var bucket in buckets.Values)
        {
            WriteIds(json, bucket.Name, bucket.Bits);
        }

        json.WriteEndObject();
    }

    /// <summary>Seconds since this process started; 0 when the platform does not report a start time.</summary>
    private static double SecondsSinceProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return Math.Max(0, (DateTime.Now - process.StartTime).TotalSeconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <summary>Writes the time spent in each bucket, in seconds.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="buckets">Buckets to write.</param>
    private static void WriteSeconds(Utf8JsonWriter json, string name, Dictionary<string, Bucket> buckets)
    {
        json.WriteStartObject(name);
        foreach (var bucket in buckets.Values)
        {
            // Unrounded: a fast class takes well under a millisecond, and rounding would make it free.
            json.WriteNumber(bucket.Name, (double)bucket.ElapsedTicks / Stopwatch.Frequency);
        }

        json.WriteEndObject();
    }

    /// <summary>Writes the set bits of <paramref name="bits"/> as a JSON array of ids.</summary>
    /// <param name="json">Writer positioned inside an object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="bits">Bit set to write.</param>
    private static void WriteIds(Utf8JsonWriter json, string name, ulong[] bits)
    {
        json.WriteStartArray(name);
        for (var word = 0; word < bits.Length; word++)
        {
            var value = Volatile.Read(ref bits[word]);
            while (value != 0)
            {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(value);
                json.WriteNumberValue((word << 6) + bit);
                value &= value - 1;
            }
        }

        json.WriteEndArray();
    }

    /// <summary>Reads <see cref="MethodCountVariable"/>; zero (tracking off) when it is missing or invalid.</summary>
    private static int ReadMethodCount() =>
        int.TryParse(Environment.GetEnvironmentVariable(MethodCountVariable), out var count) && count > 0 ? count : 0;
}
