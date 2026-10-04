namespace TestDetta.Coverage;

/// <summary>
/// Decides from samples of a process tree's total CPU time whether it has been idle for a window:
/// its CPU use over the last <c>window</c> stayed below <see cref="IdleCpuShare"/> of one core. A
/// share, not an amount: a deadlocked .NET test host still spends CPU on timers, the garbage
/// collector and the test platform's heartbeat (Newtonsoft.Json's stuck host used 0.14 s a minute,
/// more than a fixed 0.2 s allowance over five minutes), while a test that works uses a large part
/// of a core.
/// </summary>
/// <param name="window">How long the tree must stay below the share to count as idle.</param>
internal sealed class IdleDetector(TimeSpan window)
{
    /// <summary>Fraction of one core the whole tree may use over the window and still count as idle.</summary>
    internal const double IdleCpuShare = 0.01;

    /// <summary>Samples since the oldest one still needed to cover the window, oldest first.</summary>
    private readonly LinkedList<(TimeSpan At, double Cpu)> _samples = [];

    /// <summary>Adds a sample and says whether the tree has been idle for the whole window up to it.</summary>
    /// <param name="at">When the sample was taken, on any clock that only moves forward.</param>
    /// <param name="cpu">Total CPU seconds of the tree at that time.</param>
    public bool Observe(TimeSpan at, double cpu)
    {
        // A process that left the tree takes its CPU time with it; what the tree did before is
        // unknown, so the window starts again.
        if (_samples.Last is { } last && cpu < last.Value.Cpu)
        {
            _samples.Clear();
        }

        _samples.AddLast((at, cpu));

        // Keep the newest sample that is at least a window old as the start; older ones are not needed.
        while (_samples.First!.Next is { } second && at - second.Value.At >= window)
        {
            _samples.RemoveFirst();
        }

        var (startAt, startCpu) = _samples.First.Value;
        var span = at - startAt;
        return span >= window && cpu - startCpu <= IdleCpuShare * span.TotalSeconds;
    }
}
