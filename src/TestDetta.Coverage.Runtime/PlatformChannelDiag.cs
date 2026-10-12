using System.Diagnostics;
using System.Text;

namespace TestDetta.Coverage.Runtime;

/// <summary>
/// DIAGNOSTIC ONLY (branch diag/platform-channels, never merged): counts the hits the test platform's own code causes
/// in the repository's assemblies (Newtonsoft.Json's VSTest host serializing its messages with the instrumented
/// Newtonsoft.Json), by the channel through which they reach a bucket. Off unless <see cref="PathVariable"/> names
/// a results file; then <see cref="TestDettaTracker.Hit"/> calls <see cref="Note"/>, and nothing the tracker records
/// changes. Each hit is counted; the stack is walked only for the first <see cref="WalksPerKey"/> hits of each method
/// and channel, which bounds the cost (a serializer runs millions of hits) and still shows who calls the method there.
/// </summary>
internal static class PlatformChannelDiag
{
    /// <summary>Results file; the process id goes before the extension.</summary>
    public const string PathVariable = "TESTDETTA_DIAG_PLATFORM";

    /// <summary>True when the diagnostic is on for this process.</summary>
    public static readonly bool Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PathVariable));

    /// <summary>Stack walks per method and channel.</summary>
    private const int WalksPerKey = 3;

    /// <summary>Channel names, by <see cref="Channel"/> value.</summary>
    private static readonly string[] ChannelNames = ["inTest", "startup", "hostThread", "between"];

    /// <summary>Counts by method id and channel.</summary>
    private static readonly Dictionary<long, Entry> Entries = [];

    /// <summary>Guards <see cref="Entries"/>.</summary>
    private static readonly object Gate = new();

    /// <summary>True once the process-exit writer is registered.</summary>
    private static bool _registered;

    /// <summary>How a hit reaches its bucket.</summary>
    public enum Channel
    {
        /// <summary>On a test's or fixture's flow while its code may run.</summary>
        InTest,

        /// <summary>No flow and no class entered yet: the start-up bucket.</summary>
        Startup,

        /// <summary>No flow, after a class was entered: the hit goes to the class entered last.</summary>
        HostThread,

        /// <summary>On a class's flow with no test code running: between tests.</summary>
        Between,
    }

    /// <summary>What a stack walk found below the hit method.</summary>
    public enum Origin
    {
        /// <summary>The first caller outside the hit's assembly and the BCL is the test platform, and nothing below is other code.</summary>
        PlatformOnly,

        /// <summary>The first such caller is the test platform, but test code or a test framework is further down.</summary>
        PlatformMixed,

        /// <summary>The first such caller is other code (tests, frameworks, the repository).</summary>
        Other,
    }

    /// <summary>Counts of one method in one channel.</summary>
    private sealed class Entry
    {
        public string? Method;
        public long Hits;
        public int Walks;
        public int PlatformOnly;
        public int PlatformMixed;
        public int Pool;
        public readonly HashSet<string> FirstCallers = [];
    }

    /// <summary>Counts a hit of method <paramref name="id"/> through <paramref name="channel"/>.</summary>
    /// <param name="id">Method id.</param>
    /// <param name="channel">How the hit reaches its bucket.</param>
    public static void Note(int id, Channel channel)
    {
        Entry entry;
        bool walk;
        lock (Gate)
        {
            if (!_registered)
            {
                _registered = true;
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Write();
            }

            var key = ((long)id << 2) | (long)channel;
            if (!Entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                Entries[key] = entry;
            }

            entry.Hits++;
            walk = entry.Walks < WalksPerKey;
            if (walk)
            {
                entry.Walks++;
            }
        }

        if (!walk)
        {
            return;
        }

        (string? Method, Origin Origin, string? FirstCaller) walked;
        try
        {
            walked = Walk();
        }
        catch (Exception)
        {
            return;
        }

        var (method, origin, firstCaller) = walked;
        var pool = Thread.CurrentThread.IsThreadPoolThread;
        lock (Gate)
        {
            entry.Method ??= method;
            if (origin == Origin.PlatformOnly)
            {
                entry.PlatformOnly++;
            }
            else if (origin == Origin.PlatformMixed)
            {
                entry.PlatformMixed++;
            }

            if (pool)
            {
                entry.Pool++;
            }

            if (firstCaller is not null && entry.FirstCallers.Count < 4)
            {
                entry.FirstCallers.Add(firstCaller);
            }
        }
    }

    /// <summary>
    /// Walks the current stack: the first frame outside the tracker is the hit method; its callers are classified by
    /// <see cref="Classify"/>.
    /// </summary>
    internal static (string? Method, Origin Origin, string? FirstCaller) Walk()
    {
        var frames = new StackTrace(1, fNeedFileInfo: false).GetFrames() ?? [];
        string? method = null;
        string? hitAssembly = null;
        var callers = new List<string>();
        foreach (var frame in frames)
        {
            var declaring = frame.GetMethod()?.DeclaringType;
            if (declaring is null || declaring == typeof(PlatformChannelDiag) || declaring == typeof(TestDettaTracker))
            {
                continue;
            }

            var assembly = declaring.Assembly.GetName().Name ?? string.Empty;
            if (method is null)
            {
                method = $"{declaring.FullName}.{frame.GetMethod()!.Name}";
                hitAssembly = assembly;
                continue;
            }

            callers.Add(assembly);
        }

        var (origin, first) = Classify(hitAssembly ?? string.Empty, callers);
        return (method, origin, first);
    }

    /// <summary>
    /// Classifies the callers of a hit, innermost first: frames of the hit's own assembly and of the BCL are skipped;
    /// the first other assembly decides. A test platform assembly with nothing but the BCL, the platform and the
    /// hit's assembly below it is <see cref="Origin.PlatformOnly"/>.
    /// </summary>
    /// <param name="hitAssembly">Assembly of the hit method.</param>
    /// <param name="callers">Assembly names of the callers, innermost first.</param>
    internal static (Origin Origin, string? FirstCaller) Classify(string hitAssembly, IReadOnlyList<string> callers)
    {
        string? first = null;
        var index = 0;
        for (; index < callers.Count; index++)
        {
            var name = callers[index];
            if (name == hitAssembly || IsBcl(name))
            {
                continue;
            }

            first = name;
            break;
        }

        if (first is null || !IsPlatform(first))
        {
            return (Origin.Other, first);
        }

        for (var rest = index + 1; rest < callers.Count; rest++)
        {
            var name = callers[rest];
            if (name != hitAssembly && !IsBcl(name) && !IsPlatform(name))
            {
                return (Origin.PlatformMixed, first);
            }
        }

        return (Origin.PlatformOnly, first);
    }

    /// <summary>True for the test platform's assemblies (not the test frameworks).</summary>
    /// <param name="name">Assembly name.</param>
    private static bool IsPlatform(string name) =>
        name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the base class library.</summary>
    /// <param name="name">Assembly name.</param>
    private static bool IsBcl(string name) =>
        name.StartsWith("System", StringComparison.Ordinal) || name is "mscorlib" or "netstandard" or "Microsoft.Win32.Primitives";

    /// <summary>Writes the counts as JSON next to <see cref="PathVariable"/>'s file, with the process id.</summary>
    internal static void Write()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var json = new StringBuilder("{\"methods\":[");
        lock (Gate)
        {
            var separator = "";
            foreach (var pair in Entries)
            {
                var e = pair.Value;
                json.Append(separator).Append("{\"id\":").Append(pair.Key >> 2)
                    .Append(",\"channel\":\"").Append(ChannelNames[pair.Key & 3])
                    .Append("\",\"method\":\"").Append(Escape(e.Method ?? "?"))
                    .Append("\",\"hits\":").Append(e.Hits).Append(",\"walks\":").Append(e.Walks)
                    .Append(",\"platformOnly\":").Append(e.PlatformOnly).Append(",\"platformMixed\":").Append(e.PlatformMixed)
                    .Append(",\"pool\":").Append(e.Pool).Append(",\"firstCallers\":[");
                var inner = "";
                foreach (var caller in e.FirstCallers)
                {
                    json.Append(inner).Append('"').Append(Escape(caller)).Append('"');
                    inner = ",";
                }

                json.Append("]}");
                separator = ",";
            }
        }

        json.Append("]}");
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(path)}.{Process.GetCurrentProcess().Id}{Path.GetExtension(path)}");
            File.WriteAllText(target, json.ToString());
        }
        catch (Exception)
        {
            // A diagnostic never fails the test run.
        }
    }

    /// <summary>Escapes a string for JSON.</summary>
    /// <param name="text">Text.</param>
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
