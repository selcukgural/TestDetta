using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Coverage;

namespace TestDetta.Tests;

/// <summary>
/// Tests that measure how much CPU a process gets, run alone: next to the suite's own builds on a
/// four-core CI runner, a busy loop got so little CPU for two seconds that it looked idle.
/// </summary>
[CollectionDefinition(nameof(CpuTiming), DisableParallelization = true)]
public sealed class CpuTiming;

/// <summary>A stuck test run (a mutant that deadlocks the host) is told apart from a busy one by the CPU its process tree uses.</summary>
[Collection(nameof(CpuTiming))]
public sealed class ProcessTreeWatchTests
{
    [Theory]
    [InlineData("0:01.50", 1.5)]
    [InlineData("12:03.25", 723.25)]
    [InlineData("1:02:03", 3723)]
    [InlineData("2-01:00:00", 176400)]
    public void Ps_cpu_time_is_read_in_seconds(string time, double seconds) =>
        Assert.Equal(seconds, ProcessTreeWatch.ParseTime(time)!.Value, 3);

    [Fact]
    public void Tree_cpu_adds_up_the_root_and_every_descendant_only()
    {
        const string ps = """
              1     0   9:00.00   1000
            100     1   0:01.00   2048
            101   100   0:02.50   1024
            102   101   0:00.50
            200     1   5:00.00   9999
            """;

        var tree = ProcessTreeWatch.Sum(ps, 100)!;
        Assert.Equal(4.0, tree.CpuSeconds, 3);
        Assert.Equal(3072L * 1024, tree.ResidentBytes);
        Assert.Null(ProcessTreeWatch.Sum(ps, 999));
    }

    [Fact]
    public void Free_memory_is_read_from_meminfo_as_available_of_total()
    {
        const string meminfo = """
            MemTotal:       16000000 kB
            MemFree:          500000 kB
            MemAvailable:    2400000 kB
            """;

        Assert.Equal(15, ProcessTreeWatch.FreePercentFromMeminfo(meminfo));
        Assert.Null(ProcessTreeWatch.FreePercentFromMeminfo("MemTotal: 16000000 kB"));
    }

    [Fact]
    public void A_large_tree_is_killed_when_the_machine_is_nearly_out_and_a_small_one_is_not()
    {
        var limit = new MemoryLimit(TreeBytes: 24L << 30, PressureFreePercent: 10, PressureTreeBytes: 4L << 30);

        Assert.True(limit.Exceeds(25L << 30));
        Assert.False(limit.Exceeds(20L << 30));
        Assert.True(limit.CountsUnderPressure(5L << 30) && limit.UnderPressure(8));
        Assert.False(limit.CountsUnderPressure(1L << 30));
        Assert.False(limit.UnderPressure(40));
        Assert.False(limit.UnderPressure(null));
    }

    [Fact]
    public void A_tree_over_its_memory_limit_is_stopped_and_one_within_it_runs_to_the_end()
    {
        if (!ProcessTreeWatch.IsSupported)
        {
            return;
        }

        var dotnet = new DotnetProcess(NullLogger.Instance);
        var limit = new MemoryLimit(TreeBytes: 100L << 20, PressureFreePercent: 0, PressureTreeBytes: long.MaxValue);

        using (var hungry = Process.Start("perl", ["-e", "$x = 'a' x (300 * 1024 * 1024); sleep 30"]))
        {
            var watch = Stopwatch.StartNew();
            Assert.False(dotnet.WaitWithin(hungry, new RunLimits(TimeSpan.FromSeconds(25), Memory: limit)));
            Assert.InRange(watch.Elapsed.TotalSeconds, 0, 15);
            hungry.Kill();
        }

        using var modest = Process.Start("perl", ["-e", "$x = 'a' x (10 * 1024 * 1024); sleep 1"]);
        Assert.True(dotnet.WaitWithin(modest, new RunLimits(TimeSpan.FromSeconds(25), Memory: limit)));
    }

    [Fact]
    public void A_deadlocked_host_that_still_ticks_counts_as_idle_after_the_window()
    {
        // Newtonsoft.Json's stuck host: 0.07 s of CPU every 30 s (timers, GC, test platform heartbeat).
        var detector = new IdleDetector(TimeSpan.FromMinutes(5));
        var idleAt = Enumerable.Range(0, 21).First(i => detector.Observe(TimeSpan.FromSeconds(30 * i), 8.0 + 0.07 * i));

        Assert.Equal(10, idleAt);
    }

    [Fact]
    public void A_tree_that_works_never_counts_as_idle_and_the_window_starts_after_the_work()
    {
        var detector = new IdleDetector(TimeSpan.FromMinutes(5));
        // Five percent of a core for ten minutes: a slow test, not a stuck one.
        for (var i = 0; i <= 20; i++)
        {
            Assert.False(detector.Observe(TimeSpan.FromSeconds(30 * i), 0.05 * 30 * i));
        }

        // Then it stops: still busy on average a minute later, idle once a whole window is quiet.
        var last = 0.05 * 30 * 20;
        Assert.False(detector.Observe(TimeSpan.FromSeconds(30 * 22), last));
        Assert.True(detector.Observe(TimeSpan.FromSeconds(30 * 30), last));
    }

    [Fact]
    public void A_process_leaving_the_tree_restarts_the_window()
    {
        var detector = new IdleDetector(TimeSpan.FromMinutes(5));
        Assert.False(detector.Observe(TimeSpan.Zero, 100));
        // A busy child exited: the total drops, and what the tree did meanwhile is unknown.
        Assert.False(detector.Observe(TimeSpan.FromMinutes(5), 10));
        Assert.False(detector.Observe(TimeSpan.FromMinutes(9), 10));
        Assert.True(detector.Observe(TimeSpan.FromMinutes(10), 10));
    }

    [Fact]
    public void An_idle_process_is_stopped_at_the_idle_limit_and_a_busy_one_only_at_the_timeout()
    {
        if (!ProcessTreeWatch.IsSupported)
        {
            return;
        }

        var dotnet = new DotnetProcess(NullLogger.Instance);

        using (var idle = Process.Start("sleep", "30"))
        {
            var watch = Stopwatch.StartNew();
            Assert.False(dotnet.WaitWithin(idle, new RunLimits(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(2))));
            Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 10);
            idle.Kill();
        }

        using (var busy = Process.Start("/bin/sh", ["-c", "while :; do :; done"]))
        {
            var watch = Stopwatch.StartNew();
            Assert.False(dotnet.WaitWithin(busy, new RunLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2))));
            Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 15);
            busy.Kill();
        }

        using var quick = Process.Start("/bin/sh", ["-c", "exit 0"]);
        Assert.True(dotnet.WaitWithin(quick, new RunLimits(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(2))));
    }
}
