using System.Text.Json;

namespace Sift.Coverage.Runtime;

/// <summary>
/// Records which instrumented methods run during which test class. Instrumented code calls
/// <see cref="Hit"/> at the start of every method; methods of test classes also call
/// <see cref="EnterTestInstance"/> or <see cref="EnterTestClass"/> first. Everything is static
/// because instrumented assemblies reach it through plain calls.
/// </summary>
/// <remarks>
/// <para>
/// Hits are attributed in two ways. Code that runs in the logical flow of a test (including
/// awaits and <c>Task.Run</c>) carries the test class in an <see cref="AsyncLocal{T}"/> and is
/// credited exactly. Code outside that flow, such as a test server's request threads, falls back
/// to time windows between test boundaries reported by <see cref="SiftInProcCollector"/>.
/// </para>
/// <para>
/// Test platforms report boundaries asynchronously, so a window can lag behind the code it
/// describes. Window hits are therefore also credited to the neighbouring classes: more selected
/// tests, never fewer.
/// </para>
/// </remarks>
public static class SiftTracker
{
    /// <summary>Environment variable with the number of instrumented methods, set by <c>sift record</c>.</summary>
    public const string MethodCountVariable = "SIFT_METHOD_COUNT";

    /// <summary>Environment variable with the path of the JSON file written at the end of the session.</summary>
    public const string OutputVariable = "SIFT_COVERAGE_OUTPUT";

    /// <summary>Guards everything except the hit fast path.</summary>
    private static readonly object Gate = new();

    /// <summary>Number of 64-bit words a bit set needs for all method ids.</summary>
    private static readonly int Words = (ReadMethodCount() + 63) / 64;

    /// <summary>Accumulated hits per test class, keyed by runtime class name.</summary>
    private static readonly Dictionary<string, ulong[]> ByClass = new(StringComparer.Ordinal);

    /// <summary>Test class of the current logical flow, set when a test class method starts.</summary>
    private static readonly AsyncLocal<ClassHits?> Current = new();

    /// <summary>Test classes with a running test according to the platform, with their running test count.</summary>
    private static readonly Dictionary<string, int> Running = new(StringComparer.Ordinal);

    /// <summary>Window hits made before the first test started: discovery, theory data, module initializers.</summary>
    private static readonly ulong[] Startup = new ulong[Words];

    /// <summary>Window hits made since the last test ended; the next class to start receives them.</summary>
    private static readonly ulong[] Gap = new ulong[Words];

    /// <summary>Hits outside any test flow since the last boundary; replaced at every boundary.</summary>
    private static ulong[] _window = new ulong[Words];

    /// <summary>
    /// The window before the current one. A thread that read <see cref="_window"/> just before a
    /// boundary may still write into it, so it is merged a second time at the next boundary.
    /// </summary>
    private static ulong[] _previousWindow = new ulong[Words];

    /// <summary>Merged hits of the last closed window, also credited to the next class that starts.</summary>
    private static ulong[] _lastWindowHits = new ulong[Words];

    /// <summary>Test class that ended last; null before the first test.</summary>
    private static string? _lastEndedClass;

    /// <summary>Records that method <paramref name="id"/> ran. Called by instrumented code; must stay cheap.</summary>
    /// <param name="id">Method id assigned by the instrumenter.</param>
    public static void Hit(int id)
    {
        var bits = Current.Value?.Bits ?? Volatile.Read(ref _window);
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
    /// Makes the class of <paramref name="instance"/> the test class of the current logical flow.
    /// Called at the start of test methods, constructors and lifecycle methods of test classes; the
    /// runtime type is used so inherited tests count for the derived class that runs them.
    /// </summary>
    /// <param name="instance">The test class instance (<c>this</c>).</param>
    public static void EnterTestInstance(object instance) => EnterTestClass(RuntimeName(instance.GetType()));

    /// <summary>Makes <paramref name="testClass"/> the test class of the current logical flow.</summary>
    /// <param name="testClass">Runtime class name, nested classes joined with '+'.</param>
    public static void EnterTestClass(string testClass)
    {
        if (Current.Value is { } current && string.Equals(current.Name, testClass, StringComparison.Ordinal))
        {
            return;
        }

        ulong[] bits;
        lock (Gate)
        {
            bits = Accumulator(testClass);
        }

        Current.Value = new ClassHits(testClass, bits);
    }

    /// <summary>Marks the start of a test as reported by the test platform.</summary>
    /// <param name="fullyQualifiedName">Test name as reported by the test platform.</param>
    public static void BeginTest(string fullyQualifiedName)
    {
        var testClass = ClassOf(fullyQualifiedName);
        lock (Gate)
        {
            MergeWindow(CloseWindow());

            var accumulator = Accumulator(testClass);

            // Fixture setup runs right before a class's first test, while nothing is running; and a
            // late boundary means the end of the last window may already belong to this class.
            if (Running.Count == 0)
            {
                Or(accumulator, Gap);
                Array.Clear(Gap);
            }

            Or(accumulator, _lastWindowHits);
            Running[testClass] = Running.GetValueOrDefault(testClass) + 1;
        }
    }

    /// <summary>Marks the end of a test as reported by the test platform.</summary>
    /// <param name="fullyQualifiedName">Test name as reported by the test platform.</param>
    public static void EndTest(string fullyQualifiedName)
    {
        var testClass = ClassOf(fullyQualifiedName);
        lock (Gate)
        {
            MergeWindow(CloseWindow());

            if (Running.TryGetValue(testClass, out var count))
            {
                if (count <= 1)
                {
                    Running.Remove(testClass);
                }
                else
                {
                    Running[testClass] = count - 1;
                }
            }

            _lastEndedClass = testClass;
        }
    }

    /// <summary>Writes the collected data to the file named by <see cref="OutputVariable"/>.</summary>
    public static void WriteResults()
    {
        var path = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            MergeWindow(CloseWindow());
            MergeWindow(CloseWindow());

            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteStartObject("classes");
            foreach (var (testClass, bits) in ByClass)
            {
                WriteIds(json, testClass, bits);
            }

            json.WriteEndObject();
            WriteIds(json, "ambient", Startup);
            json.WriteEndObject();
        }
    }

    /// <summary>The test class of a logical flow and the bit set its hits go to.</summary>
    /// <param name="Name">Runtime class name.</param>
    /// <param name="Bits">Accumulated hits of the class.</param>
    private sealed record ClassHits(string Name, ulong[] Bits);

    /// <summary>
    /// Replaces the current window and returns the hits to merge: the closed window plus the one
    /// before it, which may have received late writes.
    /// </summary>
    private static ulong[] CloseWindow()
    {
        var closed = Interlocked.Exchange(ref _window, new ulong[Words]);
        var late = _previousWindow;
        _previousWindow = closed;

        var merged = (ulong[])closed.Clone();
        Or(merged, late);
        _lastWindowHits = merged;
        return merged;
    }

    /// <summary>
    /// Credits window hits to every running class and to the class that ended last; between tests
    /// also to the gap the next class receives; before the first test to the startup set.
    /// </summary>
    /// <param name="hits">Hits of a closed window.</param>
    private static void MergeWindow(ulong[] hits)
    {
        if (_lastEndedClass is null && Running.Count == 0)
        {
            Or(Startup, hits);
            return;
        }

        foreach (var testClass in Running.Keys)
        {
            Or(Accumulator(testClass), hits);
        }

        if (_lastEndedClass is not null)
        {
            Or(Accumulator(_lastEndedClass), hits);
        }

        if (Running.Count == 0)
        {
            Or(Gap, hits);
        }
    }

    /// <summary>Returns the bit set of <paramref name="testClass"/>, creating it on first use. Call under <see cref="Gate"/>.</summary>
    /// <param name="testClass">Runtime class name.</param>
    private static ulong[] Accumulator(string testClass)
    {
        if (!ByClass.TryGetValue(testClass, out var bits))
        {
            bits = new ulong[Words];
            ByClass[testClass] = bits;
        }

        return bits;
    }

    /// <summary>ORs <paramref name="source"/> into <paramref name="target"/>.</summary>
    /// <param name="target">Bit set that receives the bits.</param>
    /// <param name="source">Bits to add.</param>
    private static void Or(ulong[] target, ulong[] source)
    {
        for (var i = 0; i < target.Length; i++)
        {
            target[i] |= Volatile.Read(ref source[i]);
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
    /// The class part of a test name: <c>App.Tests.OrderTests.Places(1, 2)</c> gives
    /// <c>App.Tests.OrderTests</c>. Parameters are cut first because they may contain dots.
    /// </summary>
    /// <param name="fullyQualifiedName">Test name as reported by the test platform.</param>
    private static string ClassOf(string fullyQualifiedName)
    {
        var name = fullyQualifiedName.AsSpan();
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name[..paren];
        }

        var dot = name.LastIndexOf('.');
        return dot < 0 ? name.ToString() : name[..dot].ToString();
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
