using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>
/// TestDetta's selection for one audited change, made as <c>td test --base HEAD</c> would make it with the change in
/// the working tree: the same pipeline, maps and options, with the evaluator's behaviour-neutral source edit given in
/// memory (<see cref="AnalysisOptions.WorkingTreeOverlay"/>), so the team's sources are never written. One analysis
/// at a time: the MSBuild evaluation it shares is not made for several at once.
/// </summary>
/// <param name="root">The team's repository root.</param>
/// <param name="options">The analysis options of the command line (map age, drift fallback).</param>
/// <param name="loggerFactory">Creates the pipeline's loggers.</param>
/// <param name="cancel">Stops an analysis between its steps.</param>
internal sealed class AuditSelector(string root, AnalysisSettings options, ILoggerFactory loggerFactory, CancellationToken cancel)
{
    /// <summary>One analysis at a time.</summary>
    private readonly Lock _gate = new();

    /// <summary>The pipeline, with one MSBuild evaluator for every change.</summary>
    private readonly AnalysisPipeline _pipeline = new(loggerFactory, new TestDetta.Coverage.MsBuildProjectEvaluator(loggerFactory));

    /// <summary>The selection for a change to <paramref name="target"/>.</summary>
    /// <param name="target">The sampled member, with its edits.</param>
    /// <exception cref="OperationCanceledException">The audit was cancelled.</exception>
    public AnalysisResult Select(SyntheticTarget target)
    {
        var overlay = Edited(root, target);
        lock (_gate)
        {
            return _pipeline.Run(root, "HEAD", new AnalysisOptions
            {
                MaxMapAge = options.MaxMapAge,
                DriftFallback = options.DriftFallback,
                WorkingTreeOverlay = overlay,
                Cancellation = cancel,
            });
        }
    }

    /// <summary>The edited text of each file the target's edits touch, by path relative to <paramref name="root"/>.</summary>
    /// <param name="root">The team's repository root.</param>
    /// <param name="target">The sampled member.</param>
    internal static Dictionary<string, string> Edited(string root, SyntheticTarget target)
    {
        var overlay = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in target.Edits.GroupBy(static e => e.Path, StringComparer.Ordinal))
        {
            // From the end, so earlier offsets stay valid; the offsets were taken from this same text.
            var edited = new System.Text.StringBuilder(File.ReadAllText(group.Key));
            foreach (var edit in group.OrderByDescending(static e => e.Position))
            {
                edited.Remove(edit.Position, edit.Replaces).Insert(edit.Position, edit.Text);
            }

            overlay[Path.GetRelativePath(root, group.Key).Replace('\\', '/')] = edited.ToString();
        }

        return overlay;
    }
}
