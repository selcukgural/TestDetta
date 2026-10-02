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

    /// <summary>
    /// Environment variable with the number of tracked fields, set by <c>sift record</c>. Field
    /// <c>i</c> has the bit <c>method count + i</c> in every bucket.
    /// </summary>
    public const string FieldCountVariable = "SIFT_FIELD_COUNT";

    /// <summary>Environment variable with the path of the JSON file written at the end of the session.</summary>
    public const string OutputVariable = "SIFT_COVERAGE_OUTPUT";

    /// <summary>Methods remembered per thread for <see cref="Stored"/>; a power of two.</summary>
    private const int RecentSize = 1 << 16;

    /// <summary>
    /// The last <see cref="RecentSize"/> methods entered on this thread, as a ring: what a value
    /// stored into a tracked field was built from is read back from here.
    /// </summary>
    [ThreadStatic]
    private static int[]? _recent;

    /// <summary>Methods entered on this thread so far; the next ring slot.</summary>
    [ThreadStatic]
    private static long _recentCount;

    /// <summary>Ring position of the last store on this thread, and the method that made it.</summary>
    [ThreadStatic]
    private static long _lastStore;

    /// <summary>Method that made the last store on this thread.</summary>
    [ThreadStatic]
    private static int _lastStoreMethod;

    /// <summary>Per tracked field, the methods that built a value stored into it in this process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<int, byte>> Builders = new();

    /// <summary>Guards the bucket dictionaries; the hit path never takes it.</summary>
    private static readonly object Gate = new();

    /// <summary>Number of instrumented methods; field bits follow the method bits. Declared first: <see cref="Words"/> needs it.</summary>
    private static readonly int MethodCount = ReadCount(MethodCountVariable);

    /// <summary>Number of 64-bit words a bit set needs for all method and field ids.</summary>
    private static readonly int Words = (MethodCount + ReadCount(FieldCountVariable) + 63) / 64;

    /// <summary>Hits per test class, keyed by runtime class name.</summary>
    private static readonly Dictionary<string, Bucket> Classes = new(StringComparer.Ordinal);

    /// <summary>Hits per fixture type, keyed by type name (generic definitions, nested types with '+').</summary>
    private static readonly Dictionary<string, Bucket> Fixtures = new(StringComparer.Ordinal);

    /// <summary>Bucket of the current logical flow.</summary>
    private static readonly AsyncLocal<Bucket?> Current = new();

    /// <summary>Hits before any test class or fixture was entered: discovery, theory data, module initializers.</summary>
    private static readonly Bucket Startup = new("startup", new ulong[Words], isFixture: false);

    /// <summary>
    /// Methods that ran while a value was being initialized for later reuse: inside a static
    /// constructor, or inside a call that fills a cache (<c>ConcurrentDictionary.GetOrAdd</c>,
    /// <c>Lazy&lt;T&gt;.Value</c>, ...). Such code usually runs once per process, so the map credits
    /// it to whichever test class came first although every user of the value depends on it.
    /// </summary>
    private static readonly ulong[] InitializationBits = new ulong[Words];

    /// <summary>
    /// How many type initializers are running on this thread: a static constructor may trigger
    /// another. Only type initializers of instrumented assemblies are counted.
    /// </summary>
    [ThreadStatic]
    private static int _typeInitializerDepth;

    /// <summary>How many cache-filling calls are running on this thread.</summary>
    [ThreadStatic]
    private static int _cacheDepth;

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
    /// True once a test class, fixture or setup scope was entered. Test data the framework enumerates
    /// during discovery enters its class too, for the hits, but its time is start-up: every run pays
    /// it, whatever it selects.
    /// </summary>
    private static bool _testsStarted;

    /// <summary>
    /// Writes the results when the process ends. Under Microsoft.Testing.Platform the tests run in
    /// their own executable, where no VSTest collector exists to say the session is over.
    /// </summary>
    static SiftTracker() => AppDomain.CurrentDomain.ProcessExit += static (_, _) => WriteResults();

    /// <summary>Records that method <paramref name="id"/> ran. Called by instrumented code; must stay cheap.</summary>
    /// <param name="id">Method id assigned by the instrumenter.</param>
    public static void Hit(int id)
    {
        var word = id >> 6;
        var bit = 1UL << (id & 63);
        if (_typeInitializerDepth != 0 || _cacheDepth != 0)
        {
            Mark(InitializationBits, word, bit);
        }

        Mark(BucketForHit().Bits, word, bit);
        var recent = _recent ??= new int[RecentSize];
        recent[(int)(_recentCount++ & (RecentSize - 1))] = id;
    }

    /// <summary>
    /// Records that tracked field <paramref name="field"/> was read: the current bucket depends on the
    /// value in it, and so on whatever built that value (see <see cref="Stored"/>), wherever it ran.
    /// </summary>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    public static void Read(int field)
    {
        var id = MethodCount + field;
        Mark(BucketForHit().Bits, id >> 6, 1UL << (id & 63));
    }

    /// <summary>
    /// Records what built the value just stored into tracked field <paramref name="field"/> by
    /// <paramref name="method"/>: the methods entered on this thread since <paramref name="method"/>
    /// was entered last, or since its previous store, whichever is later. A value built once (a
    /// static initializer, <c>_x ??= new X()</c>) runs in the bucket of whichever test needed it
    /// first; with this, every bucket that reads the field depends on that code too. A builder longer
    /// than the ring is cut to its last part; values built on another thread and handed over are not
    /// seen, and the selection's run-once rules still cover those.
    /// </summary>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    /// <param name="method">Id of the method that stored the value.</param>
    public static void Stored(int field, int method)
    {
        if (_recent is not { } recent)
        {
            return;
        }

        var end = _recentCount;
        var stop = Math.Max(0, end - RecentSize);
        if (_lastStoreMethod == method && _lastStore > stop)
        {
            stop = _lastStore;
        }

        var builders = Builders.GetOrAdd(field, static _ => new());
        for (var i = end - 1; i >= stop; i--)
        {
            var id = recent[(int)(i & (RecentSize - 1))];
            builders.TryAdd(id, 0);
            if (id == method)
            {
                break;
            }
        }

        _lastStore = end;
        _lastStoreMethod = method;
    }

    /// <summary>Sets one bit of a bit set, cheaply when it is already set.</summary>
    /// <param name="bits">Bit set.</param>
    /// <param name="word">Index of the 64-bit word.</param>
    /// <param name="bit">Mask of the bit in the word.</param>
    private static void Mark(ulong[] bits, int word, ulong bit)
    {
        if ((uint)word >= (uint)bits.Length)
        {
            return;
        }

        // A plain read first: once a method is marked, repeated calls cost no atomic operation.
        if ((Volatile.Read(ref bits[word]) & bit) != 0)
        {
            return;
        }

        Interlocked.Or(ref bits[word], bit);
    }

    /// <summary>
    /// Called by instrumented code right before a call that may fill a cache, such as
    /// <c>ConcurrentDictionary.GetOrAdd</c> with a factory. When the value is cached already the
    /// factory does not run and nothing is marked.
    /// </summary>
    public static void EnterCache() => _cacheDepth++;

    /// <summary>
    /// Called by instrumented code right after such a call. A call that throws never gets here; the
    /// depth then stays up until the next test class or fixture is entered on the thread.
    /// </summary>
    public static void ExitCache()
    {
        if (_cacheDepth > 0)
        {
            _cacheDepth--;
        }
    }

    /// <summary>Called by instrumented code at the start of a static constructor.</summary>
    public static void EnterTypeInitializer() => _typeInitializerDepth++;

    /// <summary>
    /// Called by instrumented code before a static constructor returns. A static constructor that
    /// throws never gets here, so later code on its thread counts as type initializer code too: more
    /// members treated as run-once, never fewer.
    /// </summary>
    public static void ExitTypeInitializer()
    {
        if (_typeInitializerDepth > 0)
        {
            _typeInitializerDepth--;
        }
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
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void EnterTestInstance(object instance) => EnterClass(RuntimeName(instance.GetType()), data: false);

    /// <summary>Makes <paramref name="testClass"/> the current bucket.</summary>
    /// <param name="testClass">Runtime class name, nested classes joined with '+'.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void EnterTestClass(string testClass) => EnterClass(testClass, data: false);

    /// <summary>
    /// Makes <paramref name="testClass"/> the current bucket for the code of a test data member it
    /// alone uses. Before the first test the framework is discovering tests, and the time spent counts
    /// as start-up rather than as the class's.
    /// </summary>
    /// <param name="testClass">Runtime name of the class using the data.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void EnterDataSource(string testClass) => EnterClass(testClass, data: true);

    /// <summary>
    /// Makes <paramref name="testClass"/> the current bucket, unless a test of the current class is
    /// running and calls into it: a test that creates another test class's instance, calls its
    /// attributed helper or its data member. The code that test runs afterwards is still its own, and
    /// crediting it to the other class would hide it from the calling class's map.
    /// </summary>
    /// <param name="testClass">Runtime class name, nested classes joined with '+'.</param>
    /// <param name="data">True when entered for a test data member.</param>
    private static void EnterClass(string testClass, bool data)
    {
        if (Current.Value is { IsFixture: false } current && !ReferenceEquals(current, Startup)
            && !string.Equals(current.Name, testClass, StringComparison.Ordinal)
            && CalledFromRunningTest(current.Name, testClass))
        {
            return;
        }

        Enter(Classes, testClass, isFixture: false, data);
    }

    /// <summary>
    /// True when the entry point being entered was called by code of the test class
    /// <paramref name="running"/> (its methods, lambdas, iterators, async state machines), not by the
    /// test framework. The stack is read from the entry point down: frames of Sift, of the class being
    /// entered and of the class declaring the entry point are skipped (a data member declared on the
    /// running class but used by another is called by the framework for that other class), a frame of
    /// the running class means a call from its test, and a frame
    /// of a test framework means the framework started the entry point, as it does for every test
    /// (also when it runs on inline from the end of the previous class's async test, whose frames are
    /// then further down). Only read when the bucket would change, never on the hit path.
    /// </summary>
    /// <param name="running">Name of the current class bucket.</param>
    /// <param name="entered">Name of the class being entered; null when entering code outside every class.</param>
    internal static bool CalledFromRunningTest(string running, string? entered)
    {
        var frames = new StackTrace(1, fNeedFileInfo: false).GetFrames();
        string? entryPoint = null;
        foreach (var frame in frames)
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type is null || type == typeof(SiftTracker))
            {
                continue;
            }

            entryPoint ??= RuntimeName(Outermost(type));
            if (IsOrIsNestedIn(type, entryPoint) || entered is not null && IsOrIsNestedIn(type, entered))
            {
                continue;
            }

            if (IsOrIsNestedIn(type, running))
            {
                return true;
            }

            if (IsTestFramework(type.Assembly))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The top-level type <paramref name="type"/> is nested in, or itself.</summary>
    /// <param name="type">A type.</param>
    private static Type Outermost(Type type)
    {
        var outer = type;
        while (outer.DeclaringType is { } declaring)
        {
            outer = declaring;
        }

        return outer;
    }

    /// <summary>True when <paramref name="type"/> or a type it is nested in has the runtime name <paramref name="name"/>.</summary>
    /// <param name="type">Declaring type of a stack frame.</param>
    /// <param name="name">Runtime class name.</param>
    private static bool IsOrIsNestedIn(Type type, string name)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (string.Equals(RuntimeName(t), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Assembly name prefixes of test frameworks and platforms, whose frames start tests.</summary>
    private static readonly string[] TestFrameworkPrefixes =
        ["xunit", "nunit", "MSTest", "Microsoft.VisualStudio.TestPlatform", "Microsoft.VisualStudio.TestTools", "Microsoft.TestPlatform", "Microsoft.Testing", "TUnit", "testhost"];

    /// <summary>True for an assembly of a test framework or test platform.</summary>
    /// <param name="assembly">Assembly of a stack frame.</param>
    private static bool IsTestFramework(System.Reflection.Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;
        foreach (var prefix in TestFrameworkPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Makes code outside every test the current bucket. Called at the start of a test data source
    /// that several test classes use: its rows serve all of them, so the map must not credit them to
    /// one, and code that ran outside every test is answered by the safe fallback rules instead.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void EnterAmbient()
    {
        // A test that calls a shared data member itself keeps its own bucket, as for any entry point.
        if (Current.Value is { IsFixture: false } current && !ReferenceEquals(current, Startup)
            && CalledFromRunningTest(current.Name, entered: null))
        {
            return;
        }

        _cacheDepth = 0;
        _typeInitializerDepth = 0;
        Current.Value = Startup;
    }

    /// <summary>
    /// Makes the fixture <paramref name="instance"/> the current bucket. Called at the start of fixture
    /// constructors and <c>InitializeAsync</c>, including those inherited from a base class, so the
    /// runtime type names the concrete fixture.
    /// </summary>
    /// <param name="instance">The fixture instance (<c>this</c>).</param>
    public static void EnterFixtureInstance(object instance) => Enter(Fixtures, FixtureName(instance.GetType()), isFixture: true);

    /// <summary>
    /// Makes the setup scope <paramref name="name"/> the current bucket. Called at the start of
    /// NUnit <c>[SetUpFixture]</c> setup and MSTest <c>[AssemblyInitialize]</c> methods, which may be
    /// static and run once before every test of their namespace or assembly.
    /// </summary>
    /// <param name="name">Fixture name: the scope prefix and the namespace, written by the instrumenter.</param>
    public static void EnterSetUpScope(string name) => Enter(Fixtures, name, isFixture: true);

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
            if (_testsStarted && _lastEntered is { } current)
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
                WriteIds(json, "initialization", InitializationBits);
                json.WriteNumber("methodCount", MethodCount);
                json.WriteStartObject("fieldBuilders");
                foreach (var (field, builders) in Builders)
                {
                    json.WriteStartArray(field.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var id in builders.Keys)
                    {
                        json.WriteNumberValue(id);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
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
    /// <param name="data">True when entered for a test data member, which before the first test is discovery.</param>
    private static void Enter(Dictionary<string, Bucket> buckets, string name, bool isFixture, bool data = false)
    {
        // A cache fill or static constructor that threw left its depth up; a new test starts clean.
        _cacheDepth = 0;
        _typeInitializerDepth = 0;

        // The first test entry must get through even into the bucket its discovery data entered.
        if (Current.Value is { } current && ReferenceEquals(current, Volatile.Read(ref _lastEntered))
            && string.Equals(current.Name, name, StringComparison.Ordinal) && (_testsStarted || data))
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
            if (!_testsStarted)
            {
                // Discovery data leaves the clock on start-up; the first test entry stops it.
                if (!data)
                {
                    _testsStarted = true;
                    _lastSwitch = now;
                    _startupSeconds = SecondsSinceProcessStart();
                }
            }
            else if (_lastEntered is { } previous && !ReferenceEquals(previous, bucket))
            {
                previous.ElapsedTicks += now - _lastSwitch;
                _lastSwitch = now;
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

    /// <summary>Reads a count such as <see cref="MethodCountVariable"/>; zero (tracking off) when it is missing or invalid.</summary>
    /// <param name="variable">Environment variable.</param>
    private static int ReadCount(string variable) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var count) && count > 0 ? count : 0;
}
