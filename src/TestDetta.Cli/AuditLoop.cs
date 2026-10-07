using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>When the audit stops taking changes from the sample.</summary>
/// <param name="TargetBroken">Changes that broke tests after which it stops.</param>
/// <param name="MaxMutants">Changes that ran tests after which it stops; null for no limit.</param>
/// <param name="Budget">The time budget, started with the command.</param>
internal sealed record AuditStopRules(int TargetBroken, int? MaxMutants, ShadowBudget Budget);

/// <summary>
/// Walks the sample in its fixed order, one change per worker at a time, until a stop condition holds. The conditions
/// look at counts and time only, never at which change missed, so the changes judged are a prefix of the order: at the
/// target, changes already running finish and count. A change cut by the budget or a cancellation is kept as stopped
/// and not counted (docs/design/audit.md, "The sample, and how it stays unbiased").
/// </summary>
internal static class AuditLoop
{
    /// <summary>
    /// Runs the changes and returns them in sample order with the reason the loop stopped, and the failure that stopped
    /// it (a restore that failed, say), after the other workers finished their changes.
    /// </summary>
    /// <param name="sample">The members in sample order.</param>
    /// <param name="workers">Changes at once; worker k (from 0) has its own working copy.</param>
    /// <param name="rules">The stop conditions.</param>
    /// <param name="trial">Tests one change: worker, 1-based index and member.</param>
    /// <param name="progress">Called with each finished change.</param>
    /// <param name="cancel">Stops taking changes (the running ones end themselves).</param>
    public static (List<AuditMutant> Mutants, string StopReason, Exception? Failure) Run(
        IReadOnlyList<SyntheticTarget> sample,
        int workers,
        AuditStopRules rules,
        Func<int, int, SyntheticTarget, AuditMutant> trial,
        Action<AuditMutant> progress,
        CancellationToken cancel)
    {
        var gate = new Lock();
        var results = new AuditMutant?[sample.Count];
        var next = 0;
        var ran = 0;
        var broke = 0;
        var running = 0;
        string? stop = null;
        var failures = new List<Exception>();

        string? Reason()
        {
            if (cancel.IsCancellationRequested)
            {
                return "cancelled";
            }

            if (broke >= rules.TargetBroken)
            {
                return "target";
            }

            // Running changes may still run tests, so they count against the limit before they end.
            if (rules.MaxMutants is { } max && ran + running >= max)
            {
                return "max-mutants";
            }

            if (rules.Budget.RanOut)
            {
                return "budget";
            }

            return next >= sample.Count ? "sample" : null;
        }

        void Work(int worker)
        {
            while (true)
            {
                int index;
                lock (gate)
                {
                    stop ??= Reason();
                    if (stop is not null)
                    {
                        return;
                    }

                    index = next++;
                    running++;
                }

                AuditMutant result;
                try
                {
                    result = trial(worker, index + 1, sample[index]);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    lock (gate)
                    {
                        running--;
                        failures.Add(ex);
                        stop = "error";
                    }

                    return;
                }

                lock (gate)
                {
                    running--;
                    results[index] = result;
                    ran += result.Ran ? 1 : 0;
                    broke += result.Broke ? 1 : 0;
                    if (result.Outcome == AuditOutcome.Stopped)
                    {
                        stop ??= cancel.IsCancellationRequested ? "cancelled" : "budget";
                    }

                    progress(result);
                }
            }
        }

        if (workers <= 1)
        {
            Work(0);
        }
        else
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, Work);
        }

        return ([.. results.OfType<AuditMutant>()], stop ?? "sample", failures.Count == 0 ? null : failures[0]);
    }
}
