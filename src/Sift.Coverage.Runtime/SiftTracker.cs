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

    /// <summary>
    /// Environment variable that is <c>1</c> when the tests run with the suite's own parallelism (a
    /// suite that crashes when its tests run one at a time). Code outside every test flow then counts
    /// as ambient instead of for the class entered last, which is meaningless with classes running at once.
    /// </summary>
    public const string ParallelFlowsVariable = "SIFT_PARALLEL_FLOWS";

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
        public ulong[]? Spilled;

        /// <summary>Ring position up to which the scope's hits are in <see cref="Spilled"/>.</summary>
        public long SpilledTo;

        /// <summary>For a static constructor, the tracked fields it stored; null until it stores one.</summary>
        public List<int>? Stores;
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

    /// <summary>Number of instrumented methods; field bits follow the method bits. Declared first: <see cref="Words"/> needs it.</summary>
    private static readonly int MethodCount = ReadCount(MethodCountVariable);

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
    private static readonly Bucket Startup = new("startup", new ulong[Words], isFixture: false);

    /// <summary>
    /// Methods that ran while a value was being initialized for later reuse: inside a static
    /// constructor, or inside a call that fills a cache (<c>ConcurrentDictionary.GetOrAdd</c>,
    /// <c>Lazy&lt;T&gt;.Value</c>, ...). Such code usually runs once per process, so the map credits
    /// it to whichever test class came first although every user of the value depends on it.
    /// </summary>
    private static readonly ulong[] InitializationBits = new ulong[Words];

    /// <summary>
    /// Methods that ran inside an initialization scope whose result was stored into a tracked field
    /// (linked). Declared after <see cref="Words"/>, which static initialization order needs.
    /// </summary>
    private static readonly ulong[] LinkedBits = new ulong[Words];

    /// <summary>Methods that ran inside an initialization scope whose result left through a path no probe sees (unlinked).</summary>
    private static readonly ulong[] UnlinkedBits = new ulong[Words];

    /// <summary>
    /// Methods that ran between tests since the last class was entered: no test code was running, so
    /// the framework ran them, mostly to build what the next test class gets injected. Added to the
    /// next class entered (docs/design/between-tests-code.md).
    /// </summary>
    private static readonly ulong[] BetweenBits = new ulong[Words];

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

        var bucket = BucketForHit();
        Mark(bucket.Bits, word, bit);
        if (IsBetweenTests(bucket))
        {
            MarkBetween(word, bit);
            Mark(InitializationBits, word, bit);
        }

        Remember(id);
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
        Mark(bucket.Bits, id >> 6, 1UL << (id & 63));
        if (IsBetweenTests(bucket))
        {
            MarkBetween(id >> 6, 1UL << (id & 63));
        }

        // Into the ring as well: a value stored later on this thread was built from this one, so it
        // depends on what built this one (the recorder follows such field builders transitively).
        Remember(id);
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

            var spilled = scope.Spilled ??= new ulong[Words];
            for (var i = from; i < end; i++)
            {
                var id = recent[(int)(i & (RecentSize - 1))];
                if ((uint)(id >> 6) < (uint)spilled.Length)
                {
                    spilled[id >> 6] |= 1UL << (id & 63);
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
    private static void MarkBetween(int word, ulong bit)
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
        var start = _argumentStarts is { } starts && starts.Remove(site, out var marked) ? marked : _recentCount;
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
        MarkScope(in scope, scope.Linked || link);
        scope.Spilled = null;
        scope.Stores = null;
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
            Mark(bits, id >> 6, 1UL << (id & 63));
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
                    builders.TryAdd((word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits), 0);
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
            if (type is null || type == typeof(SiftTracker))
            {
                continue;
            }

            entryPoint ??= RuntimeName(Outermost(type));
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
            && CalledFromRunningTest(current.Name, entered: null, current.InstanceType))
        {
            return;
        }

        _cacheDepth = 0;
        _typeInitializerDepth = 0;
        AbandonScopes();
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
                WriteIds(json, "linked", LinkedBits);
                WriteIds(json, "unlinked", UnlinkedBits);
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

        /// <summary>Runtime type of the test instance last entered for this class; null until one was.</summary>
        public Type? InstanceType { get; set; }

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
        AbandonScopes();

        // The first test entry must get through even into the bucket its discovery data entered.
        if (Current.Value is { } current && ReferenceEquals(current, Volatile.Read(ref _lastEntered))
            && string.Equals(current.Name, name, StringComparison.Ordinal) && (_testsStarted || data))
        {
            TakeBetweenTests(current);
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

        TakeBetweenTests(bucket);
        Current.Value = bucket;
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
                    Interlocked.Or(ref bucket.Bits[word], pending);
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
