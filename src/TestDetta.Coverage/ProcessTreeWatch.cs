using System.Diagnostics;
using System.Globalization;

namespace TestDetta.Coverage;

/// <summary>
/// Tells a process tree that is stuck from one that is busy: a deadlocked test host (a mutant that
/// broke a lock) sits at no CPU until a long timeout kills it, while a slow suite keeps using CPU.
/// Also measures the tree's memory, so a host that allocates without end (a mutant that breaks a
/// loop's exit) is stopped before the machine runs out. The CPU time and resident memory of the
/// process and all its descendants are read with <c>ps</c>; Windows has no equivalent here, so the
/// watch never reports there.
/// </summary>
internal static class ProcessTreeWatch
{
    /// <summary>True when idleness can be measured on this platform.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>
    /// CPU seconds used so far and resident memory now of <paramref name="rootId"/> and every process
    /// below it; null when <c>ps</c> cannot be run or the root is gone.
    /// </summary>
    /// <param name="rootId">Process id of the tree's root.</param>
    public static TreeUsage? Measure(int rootId)
    {
        if (!IsSupported)
        {
            return null;
        }

        return Run("ps", "-A", "-o", "pid=,ppid=,time=,rss=") is { } output ? Sum(output, rootId) : null;
    }

    /// <summary>
    /// Share of the machine's memory still available, in percent; null when it cannot be read. macOS
    /// reports it as <c>kern.memorystatus_level</c>, Linux as <c>MemAvailable</c> of <c>MemTotal</c>.
    /// </summary>
    public static int? FreeMemoryPercent()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Run("sysctl", "-n", "kern.memorystatus_level") is { } level
                && int.TryParse(level.Trim(), CultureInfo.InvariantCulture, out var percent) ? percent : null;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return FreePercentFromMeminfo(File.ReadAllText("/proc/meminfo"));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Available memory in percent of the total in the text of <c>/proc/meminfo</c>; null when a value is missing.</summary>
    /// <param name="meminfo">Contents of <c>/proc/meminfo</c>.</param>
    internal static int? FreePercentFromMeminfo(string meminfo)
    {
        long? total = null;
        long? available = null;
        foreach (var line in meminfo.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var kilobytes))
            {
                continue;
            }

            if (parts[0] == "MemTotal:")
            {
                total = kilobytes;
            }
            else if (parts[0] == "MemAvailable:")
            {
                available = kilobytes;
            }
        }

        return total is > 0 && available is { } free ? (int)(free * 100 / total.Value) : null;
    }

    /// <summary>Runs a short command and returns its stdout; null when it cannot be started.</summary>
    /// <param name="command">Executable.</param>
    /// <param name="arguments">Its arguments.</param>
    private static string? Run(string command, params ReadOnlySpan<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The CPU seconds and resident memory of <paramref name="rootId"/> and its descendants in
    /// <c>ps -o pid=,ppid=,time=,rss=</c> output; a line without the memory column counts no memory.
    /// </summary>
    /// <param name="psOutput">One process per line: pid, parent pid, CPU time, resident kilobytes.</param>
    /// <param name="rootId">Root of the tree.</param>
    internal static TreeUsage? Sum(string psOutput, int rootId)
    {
        var children = new Dictionary<int, List<int>>();
        var usage = new Dictionary<int, (double Cpu, long Kilobytes)>();
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parent) || ParseTime(parts[2]) is not { } seconds)
            {
                continue;
            }

            var kilobytes = parts.Length > 3 && long.TryParse(parts[3], CultureInfo.InvariantCulture, out var rss) ? rss : 0;
            usage[pid] = (seconds, kilobytes);
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(pid);
        }

        if (!usage.ContainsKey(rootId))
        {
            return null;
        }

        var cpu = 0d;
        var resident = 0L;
        var pending = new Stack<int>([rootId]);
        var seen = new HashSet<int>();
        while (pending.TryPop(out var pid))
        {
            if (!seen.Add(pid))
            {
                continue;
            }

            var (processCpu, processKilobytes) = usage.GetValueOrDefault(pid);
            cpu += processCpu;
            resident += processKilobytes * 1024;
            foreach (var child in children.GetValueOrDefault(pid) ?? [])
            {
                pending.Push(child);
            }
        }

        return new TreeUsage(cpu, resident);
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

/// <summary>What a process tree has used, from one <see cref="ProcessTreeWatch.Measure"/>.</summary>
/// <param name="CpuSeconds">CPU seconds used so far by the processes still in the tree.</param>
/// <param name="ResidentBytes">Memory the tree holds now.</param>
internal sealed record TreeUsage(double CpuSeconds, long ResidentBytes);
