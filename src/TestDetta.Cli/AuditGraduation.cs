namespace TestDetta.Cli;

/// <summary>Whether one audit run is the evidence shadow mode's graduation asks for, and if not, exactly why.</summary>
/// <param name="Met">True when the run meets every condition of <see cref="AuditGraduation.Criterion"/>.</param>
/// <param name="Status"><c>met</c>, <c>not-yet</c> (only too few changes broke tests) or <c>no</c>.</param>
/// <param name="Reasons">Each condition the run does not meet, in plain words; empty when it is met.</param>
/// <param name="Line">The one line the reports show: "yes: …", "not yet: …" or "no: …".</param>
internal sealed record AuditGraduationStatus(bool Met, string Status, IReadOnlyList<string> Reasons, string Line);

/// <summary>
/// The audit's part of shadow mode's graduation criteria (docs/design/shadow-mode.md, "Graduation criteria", owner
/// decision 2026-10-07): one run with at least <see cref="BrokeNeeded"/> changes that broke tests, none missed, and the
/// worst-case 95% upper bound, which counts every inconclusive and stopped change as a miss, at most 3.0% as the report
/// shows it (rounded up to a tenth, so the shown figure is never below the real one). The worst case is the one asked
/// for because a change the runs could not judge might be a miss: a run that leaves many changes unjudged must not
/// pass on the judged ones alone. A run made with <c>--member</c> tests the members asked for, not the sample, and is
/// never the evidence. The target (<c>--target-broken</c>) does not change the criterion.
/// </summary>
internal static class AuditGraduation
{
    /// <summary>Changes that broke tests the criterion asks for.</summary>
    public const int BrokeNeeded = 100;

    /// <summary>The highest worst-case bound allowed, in tenths of a percent as the report shows it (3.0%).</summary>
    public const int WorstCaseTenthsAtMost = 30;

    /// <summary>The criterion in words, for the reports.</summary>
    public const string Criterion =
        "at least 100 changes that broke tests, 0 missed, and the worst-case 95% upper bound (inconclusive and stopped changes counted as misses) at most 3.0% as shown, rounded up";

    /// <summary>Judges a run against the criterion.</summary>
    /// <param name="result">The audit's result.</param>
    public static AuditGraduationStatus Judge(AuditResult result)
    {
        var reasons = new List<string>();
        var final = false;
        if (result.Missed > 0)
        {
            reasons.Add($"{result.Missed} missed");
            final = true;
        }

        if (result.Settings.Members.Count > 0)
        {
            reasons.Add("only the members asked for with --member were audited, not the sample");
            final = true;
        }

        if (result.Broke < BrokeNeeded)
        {
            reasons.Add($"{result.Broke} {(result.Broke == 1 ? "change" : "changes")} broke tests, {BrokeNeeded} needed");
        }

        // Without unjudged changes the worst case is the bound itself, which a miss or too small an n already explains.
        var worstN = result.Broke + result.Unjudged;
        if (result.Unjudged > 0 && worstN > 0 && AuditReport.Tenths(result.WorstCaseBound) > WorstCaseTenthsAtMost)
        {
            reasons.Add($"worst case {AuditReport.Percent(result.WorstCaseBound)} > 3.0% because {result.Count(AuditOutcome.Inconclusive)} inconclusive " +
                        $"and {result.Count(AuditOutcome.Stopped)} stopped are counted as misses");
            final = true;
        }

        if (reasons.Count > 0)
        {
            var status = final ? "no" : "not-yet";
            return new AuditGraduationStatus(false, status, reasons, $"{(final ? "no" : "not yet")}: {string.Join("; ", reasons)}");
        }

        var worst = AuditReport.Percent(result.WorstCaseBound);
        var unaudited = result.Projects.Count(static p => !p.Audited);
        var scope = unaudited == 0 ? string.Empty : $"; it covers the audited test projects only ({unaudited} not audited, listed in the report)";
        return new AuditGraduationStatus(true, "met", [],
            $"yes: {result.Broke} changes broke tests ({BrokeNeeded} needed), 0 missed, worst case {worst} (at most 3.0% needed){scope}");
    }
}
