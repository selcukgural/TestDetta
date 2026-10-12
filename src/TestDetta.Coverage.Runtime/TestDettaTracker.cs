using System.Diagnostics;

namespace TestDetta.Coverage.Runtime;

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
/// bucket that was entered last. That is exact because <c>td record</c> runs tests one at a time.
/// </para>
/// <para>
/// Fixture setup is kept apart because it runs before the test class it serves is created, and a
/// shared fixture serves several classes; selection credits it to every class that uses the fixture.
/// </para>
/// </remarks>
public static class TestDettaTracker
{
    /// <summary>Environment variable with the number of instrumented methods, set by <c>td record</c>.</summary>
    public const string MethodCountVariable = "TESTDETTA_METHOD_COUNT";

    /// <summary>
    /// Environment variable with the path of the startup hook assembly, set by <c>td record</c>. The
    /// tracker adds it to <c>DOTNET_STARTUP_HOOKS</c> of the test host, so every .NET process a test
    /// starts reports itself (see <see cref="BucketVariable"/>).
    /// </summary>
    public const string StartupHookVariable = "TESTDETTA_STARTUP_HOOK";

    /// <summary>
    /// Environment variable that holds the bucket entered last, which processes started from now on
    /// inherit: <c>c|name</c> for a test class, <c>f|name</c> for a fixture, <c>a|</c> outside every test.
    /// The startup hook writes it next to what the process runs.
    /// </summary>
    public const string BucketVariable = "TESTDETTA_BUCKET";

    /// <summary>
    /// Environment variable with the number of tracked fields, set by <c>td record</c>. Field
    /// <c>i</c> has the bit <c>method count + i</c> in every bucket.
    /// </summary>
    public const string FieldCountVariable = "TESTDETTA_FIELD_COUNT";

    /// <summary>
    /// Environment variable that is <c>1</c> when the tests run with the suite's own parallelism (a
    /// suite that crashes when its tests run one at a time). Code outside every test flow then counts
    /// as ambient instead of for the class entered last, which is meaningless with classes running at once.
    /// </summary>
    public const string ParallelFlowsVariable = "TESTDETTA_PARALLEL_FLOWS";

    /// <summary>Environment variable with the path of the JSON file written at the end of the session.</summary>
    public const string OutputVariable = "TESTDETTA_COVERAGE_OUTPUT";

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

    /// <summary>One initialization scope on a thread: a cache fill, a static constructor, a third-party callback.</summary>
    private struct Scope
    {
        /// <summary>Ring position where the scope started: the hits since then ran inside it.</summary>
        public long Start;

        /// <summary>For a static constructor, its method id; -1 for other scopes.</summary>
        public int Owner;

        /// <summary>True once what the scope built was stored into a tracked field, so its readers are known.</summary>
        public bool Linked;

        /// <summary>
        /// What ran in the scope before <see cref="SpilledTo"/>, as a bit set; null while the scope fits in
        /// the ring. A scope longer than the ring (NodaTime parses all of TZDB in one static constructor)
        /// keeps its full contents this way instead of losing its start.
        /// </summary>
        public long[]? Spilled;

        /// <summary>Ring position up to which the scope's hits are in <see cref="Spilled"/>.</summary>
        public long SpilledTo;

        /// <summary>For a static constructor, the tracked fields it stored; null until it stores one.</summary>
        public List<int>? Stores;

        /// <summary>
        /// Files read and directories listed in the scope (see <see cref="NoteFile"/>); null until one is.
        /// They go with the scope's result: to the fields it is stored into, or to initialization.
        /// </summary>
        public HashSet<string>? Files;

        /// <summary>Names of the processes started and assemblies loaded in the scope (see <see cref="OutputUsed"/>); null until one is.</summary>
        public HashSet<string>? Outputs;
    }

    /// <summary>Initialization scopes open on this thread, innermost last.</summary>
    [ThreadStatic]
    private static Scope[]? _scopes;

    /// <summary>Number of open scopes in <see cref="_scopes"/>.</summary>
    [ThreadStatic]
    private static int _scopeCount;

    /// <summary>
    /// Constructions open on this thread, innermost last: calls of constructors that store their own
    /// object into a tracked place, each starting where its arguments started (<see cref="EnterConstruction"/>).
    /// Kept apart from <see cref="_scopes"/>: a construction is not an initialization scope and marks
    /// nothing linked or unlinked.
    /// </summary>
    [ThreadStatic]
    private static Scope[]? _constructions;

    /// <summary>Number of open constructions in <see cref="_constructions"/>.</summary>
    [ThreadStatic]
    private static int _constructionCount;

    /// <summary>Per construction site, the ring position where its arguments started to be built (<see cref="MarkArguments"/>).</summary>
    [ThreadStatic]
    private static Dictionary<int, long>? _argumentStarts;

    /// <summary>Per tracked field, the methods that built a value stored into it in this process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Concurrent.ConcurrentDictionary<int, byte>> Builders = new();

    /// <summary>Guards the bucket dictionaries; the hit path never takes it.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Per tracked field, the test class bucket whose test code stored the value it holds now; null when that value
    /// came from a static constructor, a cache fill, start-up or a fixture, or nothing was stored yet. Sized on first use.
    /// </summary>
    private static Bucket?[]? _storeBuckets;

    /// <summary>Number of instrumented methods; field bits follow the method bits. Declared first: <see cref="Words"/> needs it.</summary>
    private static readonly int MethodCount = ReadCount(MethodCountVariable);

    /// <summary>This process's id, read once (<c>Environment.ProcessId</c> does not exist on .NET Framework).</summary>
    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    /// <summary>True when tests run in parallel; see <see cref="ParallelFlowsVariable"/>.</summary>
    private static readonly bool ParallelFlows = Environment.GetEnvironmentVariable(ParallelFlowsVariable) == "1";

    /// <summary>Number of 64-bit words a bit set needs for all method and field ids.</summary>
    private static readonly int Words = (MethodCount + ReadCount(FieldCountVariable) + 63) / 64;

    /// <summary>Hits per test class, keyed by runtime class name.</summary>
    private static readonly Dictionary<string, Bucket> Classes = new(StringComparer.Ordinal);

    /// <summary>Hits per fixture type, keyed by type name (generic definitions, nested types with '+').</summary>
    private static readonly Dictionary<string, Bucket> Fixtures = new(StringComparer.Ordinal);

    /// <summary>Bucket of the current logical flow.</summary>
    private static readonly AsyncLocal<Bucket?> Current = new();

    /// <summary>Hits before any test class or fixture was entered: discovery, theory data, module initializers.</summary>
    private static readonly Bucket Startup = new("startup", new long[Words], isFixture: false);

    /// <summary>
    /// Methods that ran while a value was being initialized for later reuse: inside a static
    /// constructor, or inside a call that fills a cache (<c>ConcurrentDictionary.GetOrAdd</c>,
    /// <c>Lazy&lt;T&gt;.Value</c>, ...). Such code usually runs once per process, so the map credits
    /// it to whichever test class came first although every user of the value depends on it.
    /// </summary>
    private static readonly long[] InitializationBits = new long[Words];

    /// <summary>
    /// The fixture a test built itself (<c>new FixtureA()</c>), which the rest of that test's flow also counts for, the tasks
    /// it starts included: FastEndpoints keeps one server per fixture type in a static cache, and whichever class first
    /// awaits it boots the host, so with a test that builds the fixture running first the fixture's own setup ran none of
    /// the host's start-up and its users were missed (2026-10-12). Null outside such a test; a new entry clears it.
    /// </summary>
    private static readonly AsyncLocal<Bucket?> TestBuiltFixture = new();

    /// <summary>Fixture name to the classes whose tests built it themselves: users of the fixture as much as those it is injected into.</summary>
    private static readonly Dictionary<string, HashSet<string>> FixtureBuilders = new(StringComparer.Ordinal);

    /// <summary>
    /// Methods that ran inside an initialization scope whose result was stored into a tracked field
    /// (linked). Declared after <see cref="Words"/>, which static initialization order needs.
    /// </summary>
    private static readonly long[] LinkedBits = new long[Words];

    /// <summary>
    /// Files read and directories listed (ending in <c>/</c>) inside an initialization scope: the data
    /// usually serves every later user, as the code run there does (docs/design/runtime-file-reads.md).
    /// </summary>
    private static readonly HashSet<string> InitializationFiles = new(StringComparer.Ordinal);

    /// <summary>Names of processes started and assemblies loaded inside an initialization scope; see <see cref="OutputUsed"/>.</summary>
    private static readonly HashSet<string> InitializationOutputs = new(StringComparer.Ordinal);

    /// <summary>
    /// Every name a wrapped start or load gave (<see cref="OutputUsed"/>), in any bucket, for the whole process: an
    /// assembly the runtime then loads under one of them was loaded by code the recording saw.
    /// </summary>
    private static readonly HashSet<string> WrappedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Assemblies loaded with no wrapped call naming them before: by code the recording does not wrap (a third-party
    /// plugin host, the runtime resolving a dependency, <c>Assembly.Load(bytes)</c>) or before the tracker started. Who
    /// loaded them is unknown, so the selection cannot narrow to the classes that used them (docs/design/loaded-outputs.md).
    /// </summary>
    private static readonly HashSet<string> LoadsUnseen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Before an assembly name in the output uses: the bucket enumerated the loaded assemblies while that one was loaded,
    /// or that assembly's types (<see cref="OutputScanned"/>), rather than loading it by a path or name. Only the rules for outputs found at run time
    /// (plugins) count it (docs/design/loaded-outputs.md).
    /// </summary>
    public const string ScanPrefix = "scan:";

    /// <summary>
    /// Before a full path in the file reads: the bucket only checked whether the file exists (<see cref="FileChecked"/>). A
    /// full path never starts with it: it starts with '/' or a drive letter.
    /// </summary>
    public const string CheckPrefix = "?";

    /// <summary>
    /// Before an assembly name in the output uses: the bucket loaded it by its name (<c>Assembly.Load(name)</c>,
    /// <c>AssemblyLoadContext.LoadFromAssemblyName</c>), as a scanner does with every library of the dependency context
    /// (Carter's module catalog), rather than by a path. A load by name of a project the tests compile against resolves
    /// the assembly every class naming it loads anyway; only the rules for outputs found at run time count it
    /// (docs/design/loaded-outputs.md).
    /// </summary>
    public const string NameLoadPrefix = "name:";

    /// <summary>Guards <see cref="WrappedNames"/> and <see cref="LoadsUnseen"/>; assembly loads come from any thread.</summary>
    private static readonly object LoadGate = new();

    /// <summary>
    /// Per tracked field, the files read inside the initialization scopes whose result was stored into
    /// it: every reader of the field uses them, as it depends on the field's <see cref="Builders"/>.
    /// Guarded by <see cref="Gate"/>.
    /// </summary>
    private static readonly Dictionary<int, HashSet<string>> FieldFiles = [];

    /// <summary>Per tracked field, the outputs used inside the scopes whose result was stored into it; see <see cref="FieldFiles"/>.</summary>
    private static readonly Dictionary<int, HashSet<string>> FieldOutputs = [];

    /// <summary>Extensions dropped from a used name, so a file, an assembly and a project compare by name.</summary>
    private static readonly string[] OutputExtensions = [".dll", ".exe", ".csproj", ".fsproj", ".vbproj"];

    /// <summary>Value of <see cref="BucketVariable"/> last set; null until the hook was installed. Declared before <see cref="HookInstalled"/>.</summary>
    private static string? _bucketVariable;

    /// <summary>True when processes the tests start report themselves; set up once, when the tracker starts.</summary>
    private static readonly bool HookInstalled = InstallStartupHook();

    /// <summary>Methods that ran inside an initialization scope whose result left through a path no probe sees (unlinked).</summary>
    private static readonly long[] UnlinkedBits = new long[Words];

    /// <summary>
    /// Methods that ran between tests since the last class was entered: no test code was running, so
    /// the framework ran them, mostly to build what the next test class gets injected. Added to the
    /// next class entered (docs/design/between-tests-code.md).
    /// </summary>
    private static readonly long[] BetweenBits = new long[Words];

    /// <summary>True when <see cref="BetweenBits"/> may have bits set; spares every test entry a scan of the set.</summary>
    private static bool _hasBetween;

    /// <summary>How many calls of tracked test code (test methods, lifecycle methods) are running, on any thread.</summary>
    private static int _testCodeDepth;

    /// <summary>
    /// True from a test class constructor (or a test-class entry point whose end cannot be seen) until
    /// the next tracked test code exits: the instance is being prepared for its test.
    /// </summary>
    private static bool _instancePending;

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
    /// <summary>
    /// Writes the results when the process ends. In an AppDomain other than the default one (.NET
    /// Framework runners that keep their own, such as NUnit 2's adapter; recording turns them off where the
    /// runner allows it), whose statics the collector in the default domain never sees, they are also
    /// written when the domain unloads and every half second while it lives: such a domain may not be
    /// unloaded before the process exits, and gets no ProcessExit (docs/design/framework-support.md).
    /// </summary>
    static TestDettaTracker()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, loaded) => AssemblyLoaded(loaded.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            AssemblyLoaded(assembly);
        }

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => WriteResults();
        if (!AppDomain.CurrentDomain.IsDefaultAppDomain() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OutputVariable)))
        {
            AppDomain.CurrentDomain.DomainUnload += static (_, _) => WriteResults();
            DomainFlush = new Timer(static _ => WriteResults(), null, 500, 500);
        }
    }

    /// <summary>Writes the results of a non-default AppDomain every half second; null in the default domain.</summary>
    private static readonly Timer? DomainFlush;

    /// <summary>Records that method <paramref name="id"/> ran. Called by instrumented code; must stay cheap.</summary>
    /// <param name="id">Method id assigned by the instrumenter.</param>
    public static void Hit(int id)
    {
        var word = id >> 6;
        var bit = 1L << (id & 63);
        if (_typeInitializerDepth != 0 || _cacheDepth != 0)
        {
            Mark(InitializationBits, word, bit);
        }

        var bucket = BucketForHit();
        Mark(bucket.Bits, word, bit);
        if (TestBuiltFixture.Value is { } built && !ReferenceEquals(built, bucket))
        {
            Mark(built.Bits, word, bit);
        }

        if (IsBetweenTests(bucket))
        {
            MarkBetween(word, bit);
            Mark(InitializationBits, word, bit);
        }

        Remember(id);
    }

    /// <summary>
    /// Records that the current bucket read the file at <paramref name="path"/>, resolved against the
    /// current directory as the call resolves it. Called before a file-reading call of instrumented code
    /// (docs/design/runtime-file-reads.md); a path that cannot be resolved is left to the call to reject.
    /// </summary>
    /// <param name="path">The path the code passes.</param>
    public static void FileRead(string? path) => NoteFile(path, directory: false);

    /// <summary>Records that the current bucket listed (or checked) the directory at <paramref name="path"/>; see <see cref="FileRead"/>.</summary>
    /// <param name="path">The path the code passes.</param>
    public static void DirectoryListed(string? path) => NoteFile(path, directory: true);

    /// <summary>
    /// Records that the current bucket checked whether the file at <paramref name="path"/> exists, without reading it
    /// (<c>File.Exists</c>): the path is kept with <see cref="CheckPrefix"/> before it. A check stands for the tracked file
    /// it names, which a change can add or delete, but is no read of a build output, whose existence a code change does not
    /// alter (docs/design/runtime-file-reads.md).
    /// </summary>
    /// <param name="path">The path the code passes.</param>
    public static void FileChecked(string? path) => NoteFile(path, directory: false, check: true);

    /// <summary>
    /// Records that the current bucket started a process or loaded an assembly by a file or name.
    /// Called before such a call of instrumented code with its first two arguments (the object first,
    /// for an instance method): paths and names (<c>Process.Start(file, arguments)</c>,
    /// <c>Assembly.LoadFrom</c>), a <see cref="ProcessStartInfo"/>, the <see cref="Process"/> to start, an
    /// <see cref="System.Reflection.AssemblyName"/> or the arguments of a process. Every path and name
    /// among them is kept by its last segment without an assembly or project extension, so a change to
    /// the project that builds it can find the classes that used it (docs/design/runtime-file-reads.md).
    /// </summary>
    /// <param name="first">The call's first argument.</param>
    /// <param name="second">Its second argument, or null.</param>
    public static void OutputUsed(object? first, object? second) => RecordOutputs(first, second, prefix: null);

    /// <summary>
    /// Records that code which also runs outside the tests loaded an assembly by its name for the current bucket (see
    /// <see cref="NameLoadPrefix"/>): the names go to the bucket marked as such, and count as wrapped loads for
    /// <see cref="LoadsUnseen"/>, since this call loads them. A test's own load by name goes through <see cref="OutputUsed"/>.
    /// </summary>
    /// <param name="first">The call's first argument.</param>
    /// <param name="second">Its second argument, or null.</param>
    public static void OutputLoadedByName(object? first, object? second) => RecordOutputs(first, second, NameLoadPrefix);

    /// <summary>
    /// Records that the current bucket enumerated loaded assemblies or the types of one: called before such a call of
    /// instrumented code, repository or third-party, with the object it is called on (an <see cref="AppDomain"/>, an
    /// <c>AssemblyLoadContext</c> or an <see cref="System.Reflection.Assembly"/>). Every
    /// assembly it stands for is named with <see cref="ScanPrefix"/>: an assembly loads once per process, so a class that
    /// finds a plugin an earlier class loaded and inspects its types is seen through this call, not through a load
    /// (docs/design/loaded-outputs.md). A scan loads nothing and counts as no wrapped load for <see cref="LoadsUnseen"/>.
    /// </summary>
    /// <param name="receiver">The object the call is made on.</param>
    public static void OutputScanned(object? receiver)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            AddScanned(names, receiver);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.Reflection.TargetInvocationException)
        {
            // A context being unloaded or an odd receiver: the call itself reports the problem.
        }

        if (names.Count > 0)
        {
            RecordScan(names);
        }
    }

    /// <summary>Adds the scanned names the receiver of a scan stands for; see <see cref="OutputScanned"/>.</summary>
    /// <param name="names">Names found so far.</param>
    /// <param name="receiver">The object the scan was called on.</param>
    private static void AddScanned(HashSet<string> names, object? receiver)
    {
        var assemblies = receiver switch
        {
            System.Reflection.Assembly assembly => [assembly],
            AppDomain domain => domain.GetAssemblies(),
            // AssemblyLoadContext is not in netstandard2.0, which this tracker also targets: read it by name.
            not null when IsLoadContext(receiver.GetType()) => (receiver.GetType().GetProperty("Assemblies")?.GetValue(receiver) as IEnumerable<System.Reflection.Assembly>)?.ToArray(),
            _ => null,
        };
        if (assemblies is null)
        {
            return;
        }

        foreach (var assembly in assemblies)
        {
            foreach (var assemblyName in AssemblyNames(assembly))
            {
                names.Add(ScanPrefix + assemblyName);
            }
        }

        static bool IsLoadContext(Type? type)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (type.FullName == "System.Runtime.Loader.AssemblyLoadContext")
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Records the names a start or load of the current bucket gave; see <see cref="OutputUsed"/>.</summary>
    /// <param name="first">The call's first argument.</param>
    /// <param name="second">Its second argument, or null.</param>
    /// <param name="prefix">Put before every name in the bucket (<see cref="NameLoadPrefix"/>), or null.</param>
    private static void RecordOutputs(object? first, object? second, string? prefix)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            AddUsed(names, first);
            AddUsed(names, second);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // A disposed process or an odd argument: the call itself reports the problem.
        }

        if (names.Count == 0)
        {
            return;
        }

        if (prefix is not null)
        {
            lock (LoadGate)
            {
                WrappedNames.UnionWith(names);
            }

            var marked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                marked.Add(prefix + name);
            }

            names = marked;
        }

        RecordNames(names);
    }

    /// <summary>
    /// Adds scanned names to the current bucket, also inside a cache fill: the class that filled the cache scanned the
    /// assemblies (Autofac's reflection cache lists a plugin's types inside <c>GetOrAdd</c>), and put in the initialization
    /// list the scan ran every class of the project for any change to the plugin (Autofac.Specification.Test, 42.3 % to
    /// 55.8 % in the 2026-10-12 A/B; owner's option a). A later class that reads what the fill stored in a field is found
    /// through the map's <c>builtValues</c>. Inside a static constructor, which no class may claim, the scan stays
    /// initialization.
    /// </summary>
    /// <param name="names">Names with <see cref="ScanPrefix"/>.</param>
    private static void RecordScan(HashSet<string> names)
    {
        if (_typeInitializerDepth != 0)
        {
            RecordNames(names);
            return;
        }

        lock (Gate)
        {
            BucketForHit().Outputs.UnionWith(names);
        }
    }

    /// <summary>
    /// Adds output names to the current bucket, or to the scope or initialization a type initializer or cache fill is
    /// running in, and counts the plain ones as wrapped loads for <see cref="LoadsUnseen"/>.
    /// </summary>
    /// <param name="names">Names, plain or with <see cref="ScanPrefix"/> or <see cref="NameLoadPrefix"/>.</param>
    private static void RecordNames(HashSet<string> names)
    {
        lock (LoadGate)
        {
            // A scan loads nothing; a load by name added its plain names above.
            foreach (var name in names)
            {
                if (!name.StartsWith(ScanPrefix, StringComparison.Ordinal) && !name.StartsWith(NameLoadPrefix, StringComparison.Ordinal))
                {
                    WrappedNames.Add(name);
                }
            }
        }

        lock (Gate)
        {
            if (_typeInitializerDepth == 0 && _cacheDepth == 0)
            {
                BucketForHit().Outputs.UnionWith(names);
                TestBuiltFixture.Value?.Outputs.UnionWith(names);
            }
            else if (_scopeCount > 0)
            {
                (_scopes![_scopeCount - 1].Outputs ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(names);
            }
            else
            {
                InitializationOutputs.UnionWith(names);
            }
        }
    }

    /// <summary>Adds the names one argument of a start or load stands for; see <see cref="OutputUsed"/>.</summary>
    /// <param name="names">Names found so far.</param>
    /// <param name="argument">The argument.</param>
    private static void AddUsed(HashSet<string> names, object? argument)
    {
        switch (argument)
        {
            case string text:
                AddNames(names, text);
                break;
            case ProcessStartInfo info:
                AddNames(names, info.FileName);
                AddNames(names, info.Arguments);
                AddNames(names, info.WorkingDirectory);
                // ArgumentList exists from .NET Core 2.1 on; the tracker also runs on .NET Framework.
                if (info.GetType().GetProperty("ArgumentList")?.GetValue(info) is IEnumerable<string> list)
                {
                    foreach (var item in list)
                    {
                        AddNames(names, item);
                    }
                }

                break;
            case Process process:
                AddUsed(names, process.StartInfo);
                break;
            case System.Reflection.AssemblyName name:
                AddNames(names, name.Name);
                break;
            case AppDomain domain:
                // The domain a start runs an assembly in (AppDomain.ExecuteAssembly); scans of it go to OutputScanned
                // since 2026-10-12. Kept as it was: code that runs an assembly in the domain may use any loaded one.
                // The names are marked as a scan: a scan is no load by path, which reads the assembly's declarations
                // (a public API test), and repository code scans for its own serializers and converters while
                // initializing (Newtonsoft.Json), which would make every change run every class.
                foreach (var assembly in domain.GetAssemblies())
                {
                    foreach (var assemblyName in AssemblyNames(assembly))
                    {
                        names.Add(ScanPrefix + assemblyName);
                    }
                }

                break;
            case IEnumerable<string> items:
                foreach (var item in items)
                {
                    AddNames(names, item);
                }

                break;
        }
    }

    /// <summary>
    /// Notes an assembly that was loaded without a wrapped call naming it (<see cref="LoadsUnseen"/>). Called for every
    /// load and, when the tracker starts, for every assembly loaded before it.
    /// </summary>
    /// <param name="assembly">The loaded assembly.</param>
    private static void AssemblyLoaded(System.Reflection.Assembly assembly)
    {
        try
        {
            var names = AssemblyNames(assembly);
            lock (LoadGate)
            {
                if (!names.Exists(static n => WrappedNames.Contains(n)))
                {
                    LoadsUnseen.UnionWith(names);
                }
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Security.SecurityException or IOException)
        {
            // An assembly without a name or a location to read: nothing to compare.
        }
    }

    /// <summary>The names an assembly is known by: its simple name and its file name without the extension.</summary>
    /// <param name="assembly">An assembly.</param>
    private static List<string> AssemblyNames(System.Reflection.Assembly assembly)
    {
        var names = new List<string>(2);
        if (assembly.GetName().Name is { Length: > 0 } simple)
        {
            names.Add(simple);
        }

        if (!assembly.IsDynamic && assembly.Location is { Length: > 0 } location)
        {
            names.Add(Path.GetFileNameWithoutExtension(location));
        }

        return names;
    }

    /// <summary>
    /// Adds the names in <paramref name="text"/>: the whole text and each part separated by spaces or
    /// quotes, each by its last path segment, before a comma (an assembly's display name), without an
    /// assembly or project extension.
    /// </summary>
    /// <param name="names">Names found so far.</param>
    /// <param name="text">A path, an assembly name or a command line.</param>
    internal static void AddNames(HashSet<string> names, string? text)
    {
        // netstandard2.0's IsNullOrWhiteSpace does not tell the compiler text is not null.
        if (text is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AddName(names, text);
        foreach (var part in text.Split([' ', '"', '\'', '='], StringSplitOptions.RemoveEmptyEntries))
        {
            AddName(names, part);
        }

        static void AddName(HashSet<string> names, string value)
        {
            var name = value.Trim().TrimEnd('/', '\\');
            name = name.Substring(name.LastIndexOfAny(['/', '\\']) + 1);
            if (name.IndexOf(',') is var comma and >= 0)
            {
                name = name.Substring(0, comma);
            }

            foreach (var extension in OutputExtensions)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - extension.Length);
                    break;
                }
            }

            if (name.Length > 0)
            {
                names.Add(name);
            }
        }
    }

    /// <summary>
    /// Adds the startup hook named by <see cref="StartupHookVariable"/> to this process's
    /// <c>DOTNET_STARTUP_HOOKS</c>, after any the tests set, so the .NET processes the tests start
    /// report themselves; and starts <see cref="BucketVariable"/> outside every test.
    /// </summary>
    private static bool InstallStartupHook()
    {
        var hook = Environment.GetEnvironmentVariable(StartupHookVariable);
        if (string.IsNullOrEmpty(hook) || !File.Exists(hook))
        {
            return false;
        }

        const string Hooks = "DOTNET_STARTUP_HOOKS";
        var existing = Environment.GetEnvironmentVariable(Hooks);
        Environment.SetEnvironmentVariable(Hooks, string.IsNullOrEmpty(existing) ? hook : existing + Path.PathSeparator + hook);
        _bucketVariable = "a|";
        Environment.SetEnvironmentVariable(BucketVariable, _bucketVariable);
        return true;
    }

    /// <summary>
    /// Sets <see cref="BucketVariable"/> for the processes started from now on. With tests running in
    /// parallel the bucket of a start is unknown, so it stays outside every test.
    /// </summary>
    /// <param name="bucket">The bucket entered; <see cref="Startup"/> for code outside every test.</param>
    private static void SetBucketVariable(Bucket bucket)
    {
        if (!HookInstalled || ParallelFlows)
        {
            return;
        }

        var value = ReferenceEquals(bucket, Startup) ? "a|" : (bucket.IsFixture ? "f|" : "c|") + bucket.Name;
        if (!string.Equals(value, _bucketVariable, StringComparison.Ordinal))
        {
            _bucketVariable = value;
            Environment.SetEnvironmentVariable(BucketVariable, value);
        }
    }

    /// <summary>
    /// Adds a full path, a directory with a trailing <c>/</c> and a file only looked for with <see cref="CheckPrefix"/>
    /// before it, to the bucket a hit would go to.
    /// </summary>
    /// <param name="path">The path the code passes.</param>
    /// <param name="directory">True for a directory.</param>
    /// <param name="check">True for a file whose existence alone was checked.</param>
    private static void NoteFile(string? path, bool directory, bool check = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return;
        }

        full = full.Replace('\\', '/');
        if (directory)
        {
            full = full.TrimEnd('/') + "/";
        }
        else if (check)
        {
            full = CheckPrefix + full;
        }

        lock (Gate)
        {
            if (_typeInitializerDepth == 0 && _cacheDepth == 0)
            {
                BucketForHit().Files.Add(full);
                TestBuiltFixture.Value?.Files.Add(full);
            }
            else if (_scopeCount > 0)
            {
                (_scopes![_scopeCount - 1].Files ??= new HashSet<string>(StringComparer.Ordinal)).Add(full);
            }
            else
            {
                InitializationFiles.Add(full);
            }
        }
    }

    /// <summary>
    /// Records that tracked field <paramref name="field"/> was read: the current bucket depends on the
    /// value in it, and so on whatever built that value (see <see cref="Stored"/>), wherever it ran.
    /// </summary>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    public static void Read(int field)
    {
        var id = MethodCount + field;
        var bucket = BucketForHit();
        if (_storeBuckets is { } stores && (uint)field < (uint)stores.Length && stores[field] is { } storer
            && !ReferenceEquals(storer, bucket) && !bucket.IsFixture && !ReferenceEquals(bucket, Startup))
        {
            NoteLeakedRead(bucket, field);
        }
        Mark(bucket.Bits, id >> 6, 1L << (id & 63));
        if (IsBetweenTests(bucket))
        {
            MarkBetween(id >> 6, 1L << (id & 63));
        }

        // Into the ring as well: a value stored later on this thread was built from this one, so it
        // depends on what built this one (the recorder follows such field builders transitively).
        Remember(id);
    }

    /// <summary>
    /// Remembers which test class's code stored the value <paramref name="field"/> holds now, for
    /// <see cref="NoteLeakedRead"/>. A value stored in a static constructor or a cache fill, at start-up or in a fixture
    /// is not a test's leftover: whoever needs it first builds the same value, which <see cref="Builders"/> covers.
    /// </summary>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    private static void NoteStore(int field)
    {
        var stores = _storeBuckets ??= new Bucket?[Math.Max(1, ReadCount(FieldCountVariable))];
        if ((uint)field >= (uint)stores.Length)
        {
            return;
        }

        var bucket = BucketForHit();
        stores[field] = _typeInitializerDepth != 0 || _cacheDepth != 0 || bucket.IsFixture || ReferenceEquals(bucket, Startup) ? null : bucket;
    }

    /// <summary>
    /// Records that <paramref name="reader"/> read a value another test class's code left in <paramref name="field"/>
    /// (docs/design/order-dependent-state.md): run alone or in another order the reader would find the field unset
    /// and could take a path the map never saw. Measured only; selection does not read it yet.
    /// </summary>
    /// <param name="reader">The reading test class's bucket.</param>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    private static void NoteLeakedRead(Bucket reader, int field)
    {
        var leaked = reader.LeakedFields;
        if (leaked is null)
        {
            lock (Gate)
            {
                leaked = reader.LeakedFields ??= new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            }
        }

        leaked.TryAdd(field, 0);
    }

    /// <summary>
    /// Appends <paramref name="id"/> to the thread's ring. Before the ring starts a new lap, the part of
    /// every open scope it would overwrite goes into that scope's bit set (<see cref="SpillScopes"/>).
    /// </summary>
    /// <param name="id">Method id, or <see cref="MethodCount"/> plus a field index.</param>
    private static void Remember(int id)
    {
        var recent = _recent ??= new int[RecentSize];
        recent[(int)(_recentCount++ & (RecentSize - 1))] = id;
        if ((_recentCount & (RecentSize - 1)) == 0 && (_scopeCount > 0 || _constructionCount > 0))
        {
            SpillScopes(_scopes, _scopeCount);
            SpillScopes(_constructions, _constructionCount);
        }
    }

    /// <summary>
    /// Copies what the ring holds of each open scope, and has not copied yet, into the scope's bit set.
    /// Runs once per lap of the ring, when it holds exactly the last <see cref="RecentSize"/> entries.
    /// </summary>
    /// <param name="scopes">Open initialization scopes or constructions.</param>
    /// <param name="count">How many of them are open.</param>
    private static void SpillScopes(Scope[]? scopes, int count)
    {
        var recent = _recent!;
        var end = _recentCount;
        for (var s = 0; s < count; s++)
        {
            ref var scope = ref scopes![s];
            var from = Math.Max(scope.Start, scope.SpilledTo);
            if (from >= end)
            {
                continue;
            }

            var spilled = scope.Spilled ??= new long[Words];
            for (var i = from; i < end; i++)
            {
                var id = recent[(int)(i & (RecentSize - 1))];
                if ((uint)(id >> 6) < (uint)spilled.Length)
                {
                    spilled[id >> 6] |= 1L << (id & 63);
                }
            }

            scope.SpilledTo = end;
        }
    }

    /// <summary>
    /// True when a hit for <paramref name="bucket"/> happens between tests: tests have started, the
    /// hit would go to a test class, and no test code is running or being prepared. Suites recorded
    /// with their own parallelism have no "between".
    /// </summary>
    /// <param name="bucket">Bucket the hit goes to.</param>
    private static bool IsBetweenTests(Bucket bucket) =>
        !ParallelFlows && _testsStarted && !bucket.IsFixture && !ReferenceEquals(bucket, Startup)
        && Volatile.Read(ref _testCodeDepth) == 0 && !Volatile.Read(ref _instancePending);

    /// <summary>Records a hit between tests for the next class entered.</summary>
    /// <param name="word">Index of the 64-bit word.</param>
    /// <param name="bit">Mask of the bit in the word.</param>
    private static void MarkBetween(int word, long bit)
    {
        Mark(BetweenBits, word, bit);
        if (!Volatile.Read(ref _hasBetween))
        {
            Volatile.Write(ref _hasBetween, true);
        }
    }

    /// <summary>Called at the start of tracked test code, after its bucket marker; <see cref="ExitTestCode"/> ends it.</summary>
    public static void EnterTestCode() => Interlocked.Increment(ref _testCodeDepth);

    /// <summary>
    /// Called when tracked test code ends, also by an exception (from a <c>finally</c>). When no test
    /// code runs any more, the instance it ran on is done too.
    /// </summary>
    public static void ExitTestCode()
    {
        var depth = Interlocked.Decrement(ref _testCodeDepth);
        if (depth > 0)
        {
            return;
        }

        if (depth < 0)
        {
            Interlocked.Exchange(ref _testCodeDepth, 0);
        }

        Volatile.Write(ref _instancePending, false);

        // In an AppDomain the process may end without unloading (NUnit 2's adapter), each finished test
        // writes what the domain recorded so far; the default domain writes at the end as usual.
        if (DomainFlush is not null)
        {
            WriteResults();
        }
    }

    /// <summary>
    /// Called with the task an entry point returns: the test code ends when the task does, not when
    /// the method returns at its first <c>await</c>. Registered before the framework awaits the task,
    /// so the exit is counted before the framework moves on.
    /// </summary>
    /// <param name="task">Returned value; anything but an unfinished task ends the test code at once.</param>
    public static void ExitTestCodeWhenComplete(object? task)
    {
        if (task is Task { IsCompleted: false } running)
        {
            running.ContinueWith(static _ => ExitTestCode(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        ExitTestCode();
    }

    /// <summary>
    /// Called at the start of a test class constructor, and of an entry point whose end cannot be
    /// seen (one returning <c>ValueTask</c>): test code is being prepared or runs until the next
    /// tracked test code exits.
    /// </summary>
    public static void PrepareTestInstance() => Volatile.Write(ref _instancePending, true);

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
        NoteStore(field);
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

        // A static constructor storing its own fields hands its result to their readers, all of it,
        // once it ends (ExitTypeInitializer): the walk above stops where the ring ends, the scope does
        // not, and a field stored early depends on the rest of the constructor too.
        if (_scopeCount > 0 && _scopes![_scopeCount - 1].Owner == method)
        {
            _scopes[_scopeCount - 1].Linked = true;
            (_scopes[_scopeCount - 1].Stores ??= []).Add(field);
        }
    }

    /// <summary>
    /// <see cref="Stored"/> for a constructor storing its own object into a tracked place (NodaTime's
    /// <c>CalendarSystem</c> registers itself in <c>CalendarByOrdinal</c>). The object holds what its
    /// arguments were built from, and the caller built those before the constructor was entered, where
    /// the walk of <see cref="Stored"/> stops. So everything the innermost construction ran since its
    /// arguments started counts as well (docs/design/run-once-cascades.md, constructor arguments).
    /// A constructor called without a construction around it (by reflection, from another assembly)
    /// stores as <see cref="Stored"/> does.
    /// </summary>
    /// <param name="field">Field index assigned by the instrumenter.</param>
    /// <param name="method">Id of the constructor that stored its object.</param>
    public static void StoredSelf(int field, int method)
    {
        Stored(field, method);
        if (_constructionCount > 0)
        {
            AddScopeContents(in _constructions![_constructionCount - 1], Builders.GetOrAdd(field, static _ => new()));
        }
    }

    /// <summary>
    /// Called by instrumented code where the arguments of a construction site start to be built: the
    /// first instruction of the argument expressions, or of the definitions of the locals they read in
    /// the same straight-line code (<c>var calculator = new X(); new CalendarSystem(..., calculator)</c>).
    /// </summary>
    /// <param name="site">Construction site id assigned by the instrumenter.</param>
    public static void MarkArguments(int site) => (_argumentStarts ??= [])[site] = _recentCount;

    /// <summary>
    /// Called by instrumented code right before a constructor that stores its own object into a
    /// tracked place is called (<c>newobj</c>): opens a construction starting where the site's arguments
    /// started. A path that skipped the mark (a jump into the middle of the arguments) starts it here,
    /// as if the arguments had been built by the constructor's caller. Arguments built longer ago than
    /// the ring reaches are cut to the part it still holds, as <see cref="Stored"/> cuts its walk.
    /// </summary>
    /// <param name="site">Construction site id assigned by the instrumenter.</param>
    public static void EnterConstruction(int site)
    {
        var start = _argumentStarts is { } starts && starts.TryGetValue(site, out var marked) && starts.Remove(site) ? marked : _recentCount;
        var constructions = _constructions ??= new Scope[4];
        if (_constructionCount == constructions.Length)
        {
            Array.Resize(ref _constructions, constructions.Length * 2);
            constructions = _constructions;
        }

        constructions[_constructionCount++] = new Scope { Start = Math.Max(start, _recentCount - RecentSize), Owner = -1 };
    }

    /// <summary>
    /// Called by instrumented code after the constructor returned. A constructor that throws never gets
    /// here; its construction stays open until the next test class or fixture is entered on the thread,
    /// which credits later self-stores with more builders, never fewer.
    /// </summary>
    public static void ExitConstruction()
    {
        if (_constructionCount > 0)
        {
            _constructions![--_constructionCount].Spilled = null;
        }
    }

    /// <summary>Sets one bit of a bit set, cheaply when it is already set.</summary>
    /// <param name="bits">Bit set.</param>
    /// <param name="word">Index of the 64-bit word.</param>
    /// <param name="bit">Mask of the bit in the word.</param>
    private static void Mark(long[] bits, int word, long bit)
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

        Or(ref bits[word], bit);
    }

    /// <summary>
    /// Sets <paramref name="bits"/> in <paramref name="location"/> atomically: <c>Interlocked.Or</c>,
    /// which .NET Framework and netstandard2.0 lack, as a compare-exchange loop.
    /// </summary>
    /// <param name="location">The word.</param>
    /// <param name="bits">Bits to set.</param>
    private static void Or(ref long location, long bits)
    {
        var current = Volatile.Read(ref location);
        while ((current & bits) != bits)
        {
            var seen = Interlocked.CompareExchange(ref location, current | bits, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>Index of the lowest set bit of a non-zero word (<c>BitOperations.TrailingZeroCount</c>, which netstandard2.0 lacks).</summary>
    /// <param name="value">A non-zero word.</param>
    private static int LowestBit(long value)
    {
        var bit = 0;
        var rest = (ulong)value;
        if ((rest & 0xFFFFFFFF) == 0) { bit += 32; rest >>= 32; }
        if ((rest & 0xFFFF) == 0) { bit += 16; rest >>= 16; }
        if ((rest & 0xFF) == 0) { bit += 8; rest >>= 8; }
        if ((rest & 0xF) == 0) { bit += 4; rest >>= 4; }
        if ((rest & 0x3) == 0) { bit += 2; rest >>= 2; }
        if ((rest & 0x1) == 0) { bit += 1; }
        return bit;
    }

    /// <summary>
    /// Called by instrumented code right before a call that may fill a cache, such as
    /// <c>ConcurrentDictionary.GetOrAdd</c> with a factory. When the value is cached already the
    /// factory does not run and nothing is marked.
    /// </summary>
    public static void EnterCache()
    {
        _cacheDepth++;
        PushScope(owner: -1);
    }

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

        PopScope(link: false);
    }

    /// <summary>
    /// Called instead of <see cref="ExitCache"/> after a fill whose cache is an object held in a tracked
    /// field (<c>_cache.GetOrAdd(...)</c>, <c>_lazy.Value</c>, <c>Interlocked.CompareExchange(ref _x, ...)</c>):
    /// what the fill ran built a value stored into that field, so every reader of the field depends on
    /// it (docs/design/run-once-cascades.md, C), and the scope is linked.
    /// </summary>
    /// <param name="field">Field index of the cache.</param>
    /// <param name="method">Id of the method that made the call.</param>
    public static void ExitCacheInto(int field, int method)
    {
        if (_cacheDepth > 0)
        {
            _cacheDepth--;
        }

        if (_scopeCount > 0)
        {
            var builders = Builders.GetOrAdd(field, static _ => new());
            AddScopeContents(in _scopes![_scopeCount - 1], builders);
            builders.TryAdd(method, 0);
            GiveUses(in _scopes[_scopeCount - 1], field);
        }

        PopScope(link: true);
    }

    /// <summary>Called by instrumented code at the start of a static constructor.</summary>
    /// <param name="method">Id of the static constructor: its own stores into tracked fields link its scope.</param>
    public static void EnterTypeInitializer(int method)
    {
        _typeInitializerDepth++;
        PushScope(method);
    }

    /// <summary>
    /// <see cref="EnterTypeInitializer"/> for a type with an explicit static constructor (not
    /// <c>beforefieldinit</c>): creating it or using any static member runs the constructor first, and
    /// when the constructor throws the type cannot be used at all. Everything the constructor runs
    /// becomes a builder of <paramref name="field"/>, a field index standing for the type's
    /// initialization, which the type's constructors and static members read. The scope is not linked
    /// by this: what the constructor built may still leave through a path no probe sees.
    /// </summary>
    /// <param name="method">Id of the static constructor.</param>
    /// <param name="field">Field index standing for the type's initialization.</param>
    public static void EnterTypeInitializerOf(int method, int field)
    {
        EnterTypeInitializer(method);
        _scopes![_scopeCount - 1].Stores = [field];
    }

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

        // Every field the static constructor stored depends on all of it: when any part throws, the
        // type cannot be used, and reading even a field stored before that fails. NodaTime's
        // NodaConstants stores UnixEpoch, then builds BclEpoch through the calendar; a broken calendar
        // fails every test reading UnixEpoch.
        if (_scopeCount > 0 && _scopes![_scopeCount - 1] is { Owner: >= 0, Stores: { } stores } scope)
        {
            // Collected once: a static constructor may store hundreds of fields and run a long scope.
            var contents = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            AddScopeContents(in scope, contents);
            foreach (var field in stores)
            {
                GiveUses(in scope, field);
                var builders = Builders.GetOrAdd(field, static _ => new());
                foreach (var id in contents.Keys)
                {
                    builders.TryAdd(id, 0);
                }
            }
        }

        PopScope(link: false);
    }

    /// <summary>Opens an initialization scope at the current ring position.</summary>
    /// <param name="owner">For a static constructor its method id, otherwise -1.</param>
    private static void PushScope(int owner)
    {
        var scopes = _scopes ??= new Scope[8];
        if (_scopeCount == scopes.Length)
        {
            Array.Resize(ref _scopes, scopes.Length * 2);
            scopes = _scopes;
        }

        scopes[_scopeCount++] = new Scope { Start = _recentCount, Owner = owner };
    }

    /// <summary>
    /// Closes the innermost scope and marks what ran in it as linked or unlinked. A scope longer than
    /// the ring cannot say what ran in it in full, so it is unlinked whatever was stored.
    /// </summary>
    /// <param name="link">True when the scope's result was just stored into a tracked field.</param>
    private static void PopScope(bool link)
    {
        if (_scopeCount == 0)
        {
            return;
        }

        ref var scope = ref _scopes![--_scopeCount];
        var linked = scope.Linked || link;
        MarkScope(in scope, linked);
        PassUses(ref scope, linked);
        scope.Spilled = null;
        scope.Stores = null;
    }

    /// <summary>
    /// Adds the files and outputs used in <paramref name="scope"/> so far to those of
    /// <paramref name="field"/>, which its result was stored into: every reader of the field uses them.
    /// </summary>
    /// <param name="scope">An open scope.</param>
    /// <param name="field">Field index its result was stored into.</param>
    private static void GiveUses(in Scope scope, int field)
    {
        if (scope.Files is null && scope.Outputs is null)
        {
            return;
        }

        lock (Gate)
        {
            foreach (var (uses, byField) in (ReadOnlySpan<(HashSet<string>?, Dictionary<int, HashSet<string>>)>)[(scope.Files, FieldFiles), (scope.Outputs, FieldOutputs)])
            {
                if (uses is null)
                {
                    continue;
                }

                if (!byField.TryGetValue(field, out var given))
                {
                    byField[field] = given = new HashSet<string>(StringComparer.Ordinal);
                }

                given.UnionWith(uses);
            }
        }
    }

    /// <summary>
    /// Hands on the files and outputs used in a closing scope. Those of an unlinked scope may serve
    /// anyone, as the code run there does: they become initialization, which runs the whole project.
    /// Those of a linked scope already went to its fields (<see cref="GiveUses"/>); an enclosing scope
    /// built its value from this one's, so they go to it as well.
    /// </summary>
    /// <param name="scope">The scope being closed, already removed from the open ones.</param>
    /// <param name="linked">True when its result was stored into a tracked field.</param>
    private static void PassUses(ref Scope scope, bool linked)
    {
        if (scope.Files is null && scope.Outputs is null)
        {
            return;
        }

        lock (Gate)
        {
            if (!linked)
            {
                InitializationFiles.UnionWith(scope.Files ?? []);
                InitializationOutputs.UnionWith(scope.Outputs ?? []);
            }
            else if (_scopeCount > 0)
            {
                ref var outer = ref _scopes![_scopeCount - 1];
                if (scope.Files is { } files)
                {
                    (outer.Files ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(files);
                }

                if (scope.Outputs is { } outputs)
                {
                    (outer.Outputs ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(outputs);
                }
            }
        }

        scope.Files = null;
        scope.Outputs = null;
    }

    /// <summary>Marks what ran in <paramref name="scope"/> as linked or unlinked.</summary>
    /// <param name="scope">A scope being closed.</param>
    /// <param name="linked">True for a linked scope.</param>
    private static void MarkScope(in Scope scope, bool linked)
    {
        var bits = linked ? LinkedBits : UnlinkedBits;
        if (scope.Spilled is { } spilled)
        {
            for (var word = 0; word < spilled.Length; word++)
            {
                if (spilled[word] != 0)
                {
                    Mark(bits, word, spilled[word]);
                }
            }
        }

        if (_recent is not { } recent)
        {
            return;
        }

        for (var i = Math.Max(scope.Start, scope.SpilledTo); i < _recentCount; i++)
        {
            var id = recent[(int)(i & (RecentSize - 1))];
            Mark(bits, id >> 6, 1L << (id & 63));
        }
    }

    /// <summary>Adds everything that ran in <paramref name="scope"/> so far to <paramref name="builders"/>.</summary>
    /// <param name="scope">An open scope.</param>
    /// <param name="builders">Builders of a tracked field.</param>
    private static void AddScopeContents(in Scope scope, System.Collections.Concurrent.ConcurrentDictionary<int, byte> builders)
    {
        if (scope.Spilled is { } spilled)
        {
            for (var word = 0; word < spilled.Length; word++)
            {
                for (var bits = spilled[word]; bits != 0; bits &= bits - 1)
                {
                    builders.TryAdd((word << 6) + LowestBit(bits), 0);
                }
            }
        }

        if (_recent is not { } recent)
        {
            return;
        }

        for (var i = Math.Max(scope.Start, scope.SpilledTo); i < _recentCount; i++)
        {
            builders.TryAdd(recent[(int)(i & (RecentSize - 1))], 0);
        }
    }

    /// <summary>Closes every open scope as unlinked and every open construction: one threw, and a new test starts clean.</summary>
    private static void AbandonScopes()
    {
        while (_scopeCount > 0)
        {
            ref var scope = ref _scopes![--_scopeCount];
            MarkScope(in scope, linked: false);
            PassUses(ref scope, linked: false);
            scope.Spilled = null;
            scope.Stores = null;
        }

        while (_constructionCount > 0)
        {
            _constructions![--_constructionCount].Spilled = null;
        }

        _argumentStarts?.Clear();
    }

    /// <summary>
    /// The bucket a hit goes to: the current flow's, or the one entered last for code outside every
    /// flow. A fixture's flow lives on in the background work it starts (a queue processor, a hosted
    /// service); once a test class has been entered, that work serves the class, not the setup.
    /// </summary>
    private static Bucket BucketForHit()
    {
        if (ParallelFlows)
        {
            return Current.Value ?? Startup;
        }

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
    public static void EnterTestInstance(object instance)
    {
        var type = instance.GetType();
        EnterClass(RuntimeName(type), data: false, type);
    }

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
    public static void EnterDataSource(string testClass)
    {
        // The framework enumerates a class's data right before its tests; that is the class's code.
        PrepareTestInstance();
        EnterClass(testClass, data: true);
    }

    /// <summary>
    /// Makes <paramref name="testClass"/> the current bucket, unless a test of the current class is
    /// running and calls into it: a test that creates another test class's instance, calls its
    /// attributed helper or its data member. The code that test runs afterwards is still its own, and
    /// crediting it to the other class would hide it from the calling class's map.
    /// </summary>
    /// <param name="testClass">Runtime class name, nested classes joined with '+'.</param>
    /// <param name="data">True when entered for a test data member.</param>
    /// <param name="instanceType">Runtime type of the test instance, when entered through one; tests it inherits run for it.</param>
    private static void EnterClass(string testClass, bool data, Type? instanceType = null)
    {
        if (Current.Value is { IsFixture: false } current && !ReferenceEquals(current, Startup)
            && !string.Equals(current.Name, testClass, StringComparison.Ordinal)
            && CalledFromRunningTest(current.Name, testClass, current.InstanceType))
        {
            return;
        }

        Enter(Classes, testClass, isFixture: false, data);
        if (instanceType is not null && Current.Value is { } entered)
        {
            entered.InstanceType = instanceType;
        }
    }

    /// <summary>
    /// True when the entry point being entered was called by code of the test class
    /// <paramref name="running"/> (its methods, lambdas, iterators, async state machines), not by the
    /// test framework. The stack is read from the entry point down: frames of TestDetta, of the class being
    /// entered and of the class declaring the entry point are skipped (a data member declared on the
    /// running class but used by another is called by the framework for that other class), a frame of
    /// the running class means a call from its test, and a frame
    /// of a test framework means the framework started the entry point, as it does for every test
    /// (also when it runs on inline from the end of the previous class's async test, whose frames are
    /// then further down). Only read when the bucket would change, never on the hit path.
    /// </summary>
    /// <param name="running">Name of the current class bucket.</param>
    /// <param name="entered">Name of the class being entered; null when entering code outside every class.</param>
    /// <param name="runningType">
    /// Runtime type of the running test instance, when known: frames of its base classes (a test
    /// declared on an abstract base, a helper the base declares) are the running class's code too.
    /// </param>
    internal static bool CalledFromRunningTest(string running, string? entered, Type? runningType = null)
    {
        var frames = new StackTrace(1, fNeedFileInfo: false).GetFrames();
        string? entryPoint = null;
        foreach (var frame in frames)
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type is null || type == typeof(TestDettaTracker))
            {
                continue;
            }

            entryPoint ??= RuntimeName(Outermost(type));

            // The running class nested in the entry point's (a test of Outer+Inner calling Outer's
            // helper, an F# test module nested in another): its frames are the running test's.
            if (IsOrIsNestedIn(type, running) && (IsNestedName(running, entryPoint) || entered is not null && IsNestedName(running, entered)))
            {
                return true;
            }

            if (IsOrIsNestedIn(type, entryPoint) || entered is not null && IsOrIsNestedIn(type, entered))
            {
                continue;
            }

            if (IsOrIsNestedIn(type, running) || runningType is not null && IsOrIsNestedInBaseOf(type, runningType))
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

    /// <summary>True when <paramref name="type"/> or a type it is nested in is a base class of <paramref name="derived"/> (generic definitions compared).</summary>
    /// <param name="type">Declaring type of a stack frame.</param>
    /// <param name="derived">Runtime type of the running test instance.</param>
    private static bool IsOrIsNestedInBaseOf(Type type, Type derived)
    {
        for (var frame = type; frame is not null; frame = frame.DeclaringType)
        {
            var definition = frame.IsGenericType ? frame.GetGenericTypeDefinition() : frame;
            for (var baseType = derived.BaseType; baseType is not null && baseType != typeof(object); baseType = baseType.BaseType)
            {
                if ((baseType.IsGenericType ? baseType.GetGenericTypeDefinition() : baseType) == definition)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the runtime name <paramref name="inner"/> is of a type nested in <paramref name="outer"/>.</summary>
    /// <param name="inner">Runtime type name, nested types joined with '+'.</param>
    /// <param name="outer">Runtime type name.</param>
    private static bool IsNestedName(string inner, string outer) =>
        inner.Length > outer.Length + 1 && inner[outer.Length] == '+' && inner.StartsWith(outer, StringComparison.Ordinal);

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
    /// Makes the shared data source <paramref name="name"/> (<c>[data-source]</c> and the type declaring it) the current bucket: a test data
    /// member several classes use, whose rows serve all of them and no other. The recorder names its users, so its code
    /// counts for them rather than as code outside every test (GraphQL's <c>GraphQLSerializersTestData</c>, the
    /// <c>[ClassData]</c> of 13 classes). A test that calls it itself keeps its own bucket.
    /// </summary>
    /// <param name="name">Bucket name, written by the instrumenter.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void EnterSharedDataSource(string name)
    {
        // Only while no test code runs: the framework discovering tests or building a theory's cases. A test that tests
        // the extension itself calls it through the framework's own API (AutoFixture's AutoDataAttributeTest calls
        // BuildFrom), where the stack shows the framework first; switching there credited the rest of that test to the
        // attribute's users, which it is not among.
        if (!NoTestCodeRuns()
            || Current.Value is { IsFixture: false } current && !ReferenceEquals(current, Startup)
            && CalledFromRunningTest(current.Name, entered: null, current.InstanceType))
        {
            return;
        }

        Enter(Fixtures, name, isFixture: true, data: true);
    }

    /// <summary>
    /// True when no test method or other test code is running; recorded with the suite's own parallelism, when the flow is
    /// outside every test. A class instance being prepared does not count: entering a class's own data member marks one
    /// (<see cref="EnterDataSource"/>), and a shared member the framework enumerates right after it must still get its
    /// bucket, or its code would go to that class (DataSourceTests).
    /// </summary>
    private static bool NoTestCodeRuns() =>
        ParallelFlows
            ? BucketForHit() is var bucket && (ReferenceEquals(bucket, Startup) || bucket.IsFixture)
            : Volatile.Read(ref _testCodeDepth) == 0;

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
            && CalledFromRunningTest(current.Name, entered: null, current.InstanceType))
        {
            return;
        }

        _cacheDepth = 0;
        _typeInitializerDepth = 0;
        AbandonScopes();
        Current.Value = Startup;
        SetBucketVariable(Startup);
    }

    /// <summary>
    /// Makes the fixture <paramref name="instance"/> the current bucket. Called at the start of fixture
    /// constructors and <c>InitializeAsync</c>, including those inherited from a base class, so the
    /// runtime type names the concrete fixture. A test that builds a fixture type itself (<c>new StoreFixture()</c>)
    /// keeps its own bucket, as for any entry point it calls: the rest of that test is its own code, and crediting it to
    /// the fixture's users hid it from the test's class (backlog, 2026-10-11).
    /// </summary>
    /// <param name="instance">The fixture instance (<c>this</c>).</param>
    public static void EnterFixtureInstance(object instance)
    {
        if (Current.Value is { IsFixture: false } current && !ReferenceEquals(current, Startup)
            && CalledFromRunningTest(current.Name, entered: null, current.InstanceType))
        {
            // The test is a user of the fixture, and what its flow runs from here on serves the fixture's other users too:
            // a fixture type may share what its first instance built (a cached server) with every later instance.
            var fixture = FixtureBucket(FixtureName(instance.GetType()));
            lock (Gate)
            {
                if (!FixtureBuilders.TryGetValue(fixture.Name, out var classes))
                {
                    classes = new HashSet<string>(StringComparer.Ordinal);
                    FixtureBuilders[fixture.Name] = classes;
                }

                classes.Add(current.Name);
            }

            TestBuiltFixture.Value = fixture;
            return;
        }

        Enter(Fixtures, FixtureName(instance.GetType()), isFixture: true);
    }

    /// <summary>
    /// Makes the fixture <paramref name="instance"/> the current bucket while its <c>Dispose</c> runs outside every test, and
    /// returns what <see cref="ExitFixtureTeardown"/> puts back; null when a test is running, which keeps its own bucket
    /// (a test that builds and disposes a fixture type itself). xUnit disposes a class fixture when its class finishes, with
    /// no test code running: there it counted as code outside every test, and so did every value its teardown read, so the
    /// code that built a shared schema could not be answered by coverage (GraphQL's <c>ComplexityMetaDataFixture</c>). The
    /// teardown serves the fixture's users as its setup does.
    /// </summary>
    /// <param name="instance">The fixture instance (<c>this</c>).</param>
    public static object? EnterFixtureTeardown(object instance)
    {
        var outsideTests = ParallelFlows
            ? ReferenceEquals(BucketForHit(), Startup)
            : _testsStarted && Volatile.Read(ref _testCodeDepth) == 0 && !Volatile.Read(ref _instancePending);
        if (!outsideTests)
        {
            return null;
        }

        var saved = new SavedBuckets(Current.Value, Volatile.Read(ref _lastEntered));
        Enter(Fixtures, FixtureName(instance.GetType()), isFixture: true);
        return saved;
    }

    /// <summary>
    /// Puts back the buckets <see cref="EnterFixtureTeardown"/> replaced, when the teardown returns or throws: what the
    /// framework runs next between tests (the next class's injected services) must not count for the fixture.
    /// </summary>
    /// <param name="saved">What <see cref="EnterFixtureTeardown"/> returned.</param>
    public static void ExitFixtureTeardown(object? saved)
    {
        if (saved is not SavedBuckets buckets)
        {
            return;
        }

        Volatile.Write(ref _lastEntered, buckets.LastEntered);
        Current.Value = buckets.Current;
        SetBucketVariable(buckets.Current ?? buckets.LastEntered ?? Startup);
    }

    /// <summary>The flow's current bucket and the last bucket entered, as a fixture's teardown found them.</summary>
    /// <param name="current">The flow's current bucket.</param>
    /// <param name="lastEntered">The last bucket entered.</param>
    private sealed class SavedBuckets(Bucket? current, Bucket? lastEntered)
    {
        /// <summary>The flow's current bucket.</summary>
        public Bucket? Current { get; } = current;

        /// <summary>The last bucket entered.</summary>
        public Bucket? LastEntered { get; } = lastEntered;
    }

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

        // A test AppDomain of the same process writes a file of its own (its id after the process id).
        WriteResults(ProcessResultsPath(path, CurrentProcessId, AppDomain.CurrentDomain.IsDefaultAppDomain() ? null : AppDomain.CurrentDomain.Id));
    }

    /// <summary>The results file of one process: the process id goes before the extension.</summary>
    /// <param name="path">Value of <see cref="OutputVariable"/>.</param>
    /// <param name="processId">Process id.</param>
    /// <param name="domain">Id of a test AppDomain other than the default one, or null.</param>
    public static string ProcessResultsPath(string path, int processId, int? domain = null) =>
        Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.{processId}{(domain is { } id ? $"-{id}" : string.Empty)}{Path.GetExtension(path)}");

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
            var temporary = $"{path}.{CurrentProcessId}.tmp";
            using (var stream = File.Create(temporary))
            using (var json = new ResultsJsonWriter(stream))
            {
                json.WriteStartObject();
                WriteBuckets(json, "classes", Classes);
                WriteBuckets(json, "fixtures", Fixtures);
                WriteIds(json, "ambient", Startup.Bits);
                json.WriteStartObject("fixtureBuilders");
                foreach (var entry in FixtureBuilders)
                {
                    json.WriteStartArray(entry.Key);
                    foreach (var testClass in entry.Value)
                    {
                        json.WriteStringValue(testClass);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
                WriteIds(json, "initialization", InitializationBits);
                WriteIds(json, "linked", LinkedBits);
                WriteIds(json, "unlinked", UnlinkedBits);
                json.WriteNumber("methodCount", MethodCount);
                json.WriteStartObject("fieldBuilders");
                foreach (var entry in Builders)
                {
                    var (field, builders) = (entry.Key, entry.Value);
                    json.WriteStartArray(field.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var id in builders.Keys)
                    {
                        json.WriteNumberValue(id);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
                WriteFileReads(json);
                WriteOutputUses(json);
                WriteLoadsUnseen(json);
                WriteLeakedReads(json);
                WriteSeconds(json, "classSeconds", Classes);
                WriteSeconds(json, "fixtureSeconds", Fixtures);
                json.WriteNumber("startupSeconds", _startupSeconds);
                json.WriteEndObject();
            }

            // Replace swaps the files in one step where the target exists (File.Move has no overwrite on .NET Framework).
            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }

    /// <summary>A named bit set of method ids, and the time spent in the bucket.</summary>
    /// <param name="name">Test class or fixture name.</param>
    /// <param name="bits">Hit methods.</param>
    /// <param name="isFixture">True for a fixture bucket.</param>
    private sealed class Bucket(string name, long[] bits, bool isFixture)
    {
        /// <summary>Test class or fixture name.</summary>
        public string Name { get; } = name;

        /// <summary>Hit methods.</summary>
        public long[] Bits { get; } = bits;

        /// <summary>True for a fixture bucket.</summary>
        public bool IsFixture { get; } = isFixture;

        /// <summary>Runtime type of the test instance last entered for this class; null until one was.</summary>
        public Type? InstanceType { get; set; }

        /// <summary>Full paths of the files read and directories listed (ending in <c>/</c>); see <see cref="FileRead"/>.</summary>
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Names of the processes started and assemblies loaded; see <see cref="OutputUsed"/>.</summary>
        public HashSet<string> Outputs { get; } = new(StringComparer.Ordinal);

        /// <summary>Tracked fields this class read while they held a value another test class's code stored; see <see cref="NoteLeakedRead"/>.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<int, byte>? LeakedFields { get; set; }

        /// <summary>
        /// <see cref="Stopwatch"/> ticks during which this was the bucket entered last. Because
        /// recording runs one test at a time, that is the time its tests (or its setup) took.
        /// </summary>
        public long ElapsedTicks { get; set; }
    }

    /// <summary>The fixture bucket named <paramref name="name"/>, created when no hit reached it yet, without entering it.</summary>
    /// <param name="name">Fixture name (<see cref="FixtureName"/>).</param>
    private static Bucket FixtureBucket(string name)
    {
        lock (Gate)
        {
            if (!Fixtures.TryGetValue(name, out var bucket))
            {
                bucket = new Bucket(name, new long[Words], isFixture: true);
                Fixtures[name] = bucket;
            }

            return bucket;
        }
    }

    /// <summary>Makes the bucket <paramref name="name"/> of <paramref name="buckets"/> current, for this flow and globally.</summary>
    /// <param name="buckets">Class or fixture buckets.</param>
    /// <param name="name">Bucket name.</param>
    /// <param name="isFixture">True when <paramref name="buckets"/> holds fixtures.</param>
    /// <param name="data">True when entered for a test data member, which before the first test is discovery.</param>
    private static void Enter(Dictionary<string, Bucket> buckets, string name, bool isFixture, bool data = false)
    {
        TestBuiltFixture.Value = null;
        // A cache fill or static constructor that threw left its depth up; a new test starts clean.
        _cacheDepth = 0;
        _typeInitializerDepth = 0;
        AbandonScopes();

        // The first test entry must get through even into the bucket its discovery data entered.
        if (Current.Value is { } current && ReferenceEquals(current, Volatile.Read(ref _lastEntered))
            && string.Equals(current.Name, name, StringComparison.Ordinal) && (_testsStarted || data))
        {
            TakeBetweenTests(current);
            SetBucketVariable(current);
            return;
        }

        Bucket bucket;
        lock (Gate)
        {
            if (!buckets.TryGetValue(name, out bucket!))
            {
                bucket = new Bucket(name, new long[Words], isFixture);
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

        TakeBetweenTests(bucket);
        Current.Value = bucket;
        SetBucketVariable(bucket);
    }

    /// <summary>
    /// Adds what ran between tests since the last class was entered to <paramref name="bucket"/>, when
    /// it is a test class: the framework built it for this class (an injected service, its data).
    /// </summary>
    /// <param name="bucket">Bucket being entered.</param>
    private static void TakeBetweenTests(Bucket bucket)
    {
        if (bucket.IsFixture || ReferenceEquals(bucket, Startup) || !Volatile.Read(ref _hasBetween))
        {
            return;
        }

        lock (Gate)
        {
            Volatile.Write(ref _hasBetween, false);
            for (var word = 0; word < BetweenBits.Length; word++)
            {
                var pending = Interlocked.Exchange(ref BetweenBits[word], 0);
                if (pending != 0)
                {
                    Or(ref bucket.Bits[word], pending);
                }
            }
        }
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
    private static void WriteBuckets(ResultsJsonWriter json, string name, Dictionary<string, Bucket> buckets)
    {
        json.WriteStartObject(name);
        foreach (var bucket in buckets.Values)
        {
            WriteIds(json, bucket.Name, bucket.Bits);
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// Writes <c>fileReads</c>: the files and directories each class and fixture read, those read
    /// outside every test (ambient) and those read inside an initialization scope.
    /// </summary>
    /// <param name="json">Writer positioned inside the results object.</param>
    private static void WriteFileReads(ResultsJsonWriter json)
    {
        json.WriteStartObject("fileReads");
        foreach (var (name, buckets) in (ReadOnlySpan<(string, Dictionary<string, Bucket>)>)[("classes", Classes), ("fixtures", Fixtures)])
        {
            json.WriteStartObject(name);
            foreach (var bucket in buckets.Values.Where(static b => b.Files.Count > 0))
            {
                WritePaths(json, bucket.Name, bucket.Files);
            }

            json.WriteEndObject();
        }

        WritePaths(json, "ambient", Startup.Files);
        WritePaths(json, "initialization", InitializationFiles);
        WriteFieldUses(json, FieldFiles);
        json.WriteEndObject();

        static void WritePaths(ResultsJsonWriter json, string name, HashSet<string> paths)
        {
            json.WriteStartArray(name);
            foreach (var path in paths.OrderBy(static v => v, StringComparer.Ordinal))
            {
                json.WriteStringValue(path);
            }

            json.WriteEndArray();
        }
    }

    /// <summary>Writes <c>outputLoadsUnseen</c>: the names of the assemblies loaded without a wrapped call (<see cref="LoadsUnseen"/>).</summary>
    /// <param name="json">Writer positioned inside the results object.</param>
    private static void WriteLoadsUnseen(ResultsJsonWriter json)
    {
        string[] names;
        lock (LoadGate)
        {
            names = [.. LoadsUnseen.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];
        }

        json.WriteStartArray("outputLoadsUnseen");
        foreach (var name in names)
        {
            json.WriteStringValue(name);
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Writes <c>outputUses</c>, shaped like <c>fileReads</c>: the names of the processes each class and
    /// fixture started and the assemblies it loaded (see <see cref="OutputUsed"/>).
    /// </summary>
    /// <param name="json">Writer positioned inside the results object.</param>
    private static void WriteOutputUses(ResultsJsonWriter json)
    {
        json.WriteStartObject("outputUses");
        foreach (var (name, buckets) in (ReadOnlySpan<(string, Dictionary<string, Bucket>)>)[("classes", Classes), ("fixtures", Fixtures)])
        {
            json.WriteStartObject(name);
            foreach (var bucket in buckets.Values.Where(static b => b.Outputs.Count > 0))
            {
                WriteNames(json, bucket.Name, bucket.Outputs);
            }

            json.WriteEndObject();
        }

        WriteNames(json, "ambient", Startup.Outputs);
        WriteNames(json, "initialization", InitializationOutputs);
        WriteFieldUses(json, FieldOutputs);
        json.WriteEndObject();

        static void WriteNames(ResultsJsonWriter json, string name, HashSet<string> names)
        {
            json.WriteStartArray(name);
            foreach (var value in names.OrderBy(static v => v, StringComparer.Ordinal))
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
        }
    }

    /// <summary>
    /// Writes <c>fields</c>: per tracked field (by index), the files or outputs used while initializing
    /// a value stored into it (<see cref="FieldFiles"/>, <see cref="FieldOutputs"/>), which the recorder
    /// credits to the field's readers.
    /// </summary>
    /// <param name="json">Writer positioned inside <c>fileReads</c> or <c>outputUses</c>.</param>
    /// <param name="byField">Paths or names by field index.</param>
    private static void WriteFieldUses(ResultsJsonWriter json, Dictionary<int, HashSet<string>> byField)
    {
        json.WriteStartObject("fields");
        foreach (var entry in byField.OrderBy(static e => e.Key))
        {
            json.WriteStartArray(entry.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in entry.Value.OrderBy(static v => v, StringComparer.Ordinal))
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// Writes <c>leakedReads</c>: per test class, the tracked fields (by index) it read while they held a value another
    /// test class's code stored (<see cref="NoteLeakedRead"/>).
    /// </summary>
    /// <param name="json">Writer positioned inside the results object.</param>
    private static void WriteLeakedReads(ResultsJsonWriter json)
    {
        json.WriteStartObject("leakedReads");
        foreach (var bucket in Classes.Values)
        {
            if (bucket.LeakedFields is not { Count: > 0 } leaked)
            {
                continue;
            }

            json.WriteStartArray(bucket.Name);
            foreach (var field in leaked.Keys.OrderBy(static f => f))
            {
                json.WriteNumberValue(field);
            }

            json.WriteEndArray();
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
    private static void WriteSeconds(ResultsJsonWriter json, string name, Dictionary<string, Bucket> buckets)
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
    private static void WriteIds(ResultsJsonWriter json, string name, long[] bits)
    {
        json.WriteStartArray(name);
        for (var word = 0; word < bits.Length; word++)
        {
            var value = Volatile.Read(ref bits[word]);
            while (value != 0)
            {
                var bit = LowestBit(value);
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
