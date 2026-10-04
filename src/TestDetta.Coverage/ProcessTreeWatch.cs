using System.Diagnostics;
using System.Globalization;

namespace TestDetta.Coverage;

/// <summary>
/// Tells a process tree that is stuck from one that is busy: a deadlocked test host (a mutant that
/// broke a lock) sits at no CPU until a long timeout kills it, while a slow suite keeps using CPU.
/// The CPU time of the process and all its descendants is read with <c>ps</c>; Windows has no
/// equivalent here, so the watch never reports idleness there.
/// </summary>
internal static class ProcessTreeWatch
{
    /// <summary>True when idleness can be measured on this platform.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>
    /// Total CPU seconds used so far by <paramref name="rootId"/> and every process below it; null when
    /// <c>ps</c> cannot be run or the root is gone.
    /// </summary>
    /// <param name="rootId">Process id of the tree's root.</param>
    public static double? TreeCpuSeconds(int rootId)
    {
        if (!IsSupported)
        {
            return null;
        }

        string output;
        try
        {
            var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in (string[])["-A", "-o", "pid=,ppid=,time="])
            {
                psi.ArgumentList.Add(argument);
            }

            using var ps = Process.Start(psi);
            if (ps is null)
            {
                return null;
            }

            output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        return Sum(output, rootId);
    }

    /// <summary>The CPU seconds of <paramref name="rootId"/> and its descendants in <c>ps -o pid=,ppid=,time=</c> output.</summary>
    /// <param name="psOutput">One process per line: pid, parent pid, CPU time.</param>
    /// <param name="rootId">Root of the tree.</param>
    internal static double? Sum(string psOutput, int rootId)
    {
        var children = new Dictionary<int, List<int>>();
        var cpu = new Dictionary<int, double>();
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parent) || ParseTime(parts[2]) is not { } seconds)
            {
                continue;
            }

            cpu[pid] = seconds;
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(pid);
        }

        if (!cpu.ContainsKey(rootId))
        {
            return null;
        }

        var total = 0d;
        var pending = new Stack<int>([rootId]);
        var seen = new HashSet<int>();
        while (pending.TryPop(out var pid))
        {
            if (!seen.Add(pid))
            {
                continue;
            }

            total += cpu.GetValueOrDefault(pid);
            foreach (var child in children.GetValueOrDefault(pid) ?? [])
            {
                pending.Push(child);
            }
        }

        return total;
    }

    /// <summary>Seconds in a <c>ps</c> CPU time: <c>[[dd-]hh:]mm:ss[.cc]</c>.</summary>
    /// <param name="time">The time column.</param>
    internal static double? ParseTime(string time)
    {
        var days = 0;
        var dash = time.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            if (!int.TryParse(time[..dash], CultureInfo.InvariantCulture, out days))
            {
                return null;
            }

            time = time[(dash + 1)..];
        }

        var seconds = 0d;
        foreach (var part in time.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            seconds = (seconds * 60) + value;
        }

        return (days * 86400) + seconds;
    }
}
