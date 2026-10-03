using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Coverage;

namespace Sift.Tests;

/// <summary>A stuck test run (a mutant that deadlocks the host) is told apart from a busy one by the CPU its process tree uses.</summary>
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
              1     0   9:00.00
            100     1   0:01.00
            101   100   0:02.50
            102   101   0:00.50
            200     1   5:00.00
            """;

        Assert.Equal(4.0, ProcessTreeWatch.Sum(ps, 100)!.Value, 3);
        Assert.Null(ProcessTreeWatch.Sum(ps, 999));
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
