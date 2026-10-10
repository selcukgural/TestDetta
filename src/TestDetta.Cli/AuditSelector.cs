using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TestDetta.Core;
using TestDetta.Mutants;

namespace TestDetta.Cli;

/// <summary>The selection for one change, and where its edits lie in the audited commit's text of their files.</summary>
/// <param name="Analysis">What <c>td test --base HEAD</c> would decide with the change.</param>
/// <param name="Positions">The 1-based line and column of each edit.</param>
internal sealed record AuditSelection(AnalysisResult Analysis, IReadOnlyList<(int Line, int Column)> Positions);

/// <summary>
/// The team's repository moved under a running audit: another commit, or changes in the working tree. Every later
/// change would be compared with something else than the commit audited, so the audit stops with an error.
/// </summary>
/// <param name="message">What moved.</param>
internal sealed class AuditTreeChangedException(string message) : Exception(message);

/// <summary>
/// TestDetta's selection for one audited change, made as <c>td test --base HEAD</c> would make it with the change in
/// the working tree: the same pipeline, maps and options, with the evaluator's behaviour-neutral source edit given in
/// memory (<see cref="AnalysisOptions.WorkingTreeOverlay"/>), so the team's sources are never written. The audited
/// commit is fixed when the audit starts: every analysis compares with it, the edited text is that commit's, and
/// before and after each analysis the repository must still be at it with a clean working tree. One analysis at a
/// time: the MSBuild evaluation it shares is not made for several at once.
/// </summary>
/// <param name="root">The team's repository root.</param>
/// <param name="head">The commit audited, as recorded when the audit started.</param>
/// <param name="options">The analysis options of the command line (map age, drift fallback).</param>
/// <param name="loggerFactory">Creates the pipeline's loggers.</param>
/// <param name="cancel">Stops an analysis between its steps.</param>
internal sealed class AuditSelector(string root, string head, AnalysisSettings options, ILoggerFactory loggerFactory, CancellationToken cancel)
{
    /// <summary>Longest one <c>git cat-file</c> of a file's text may take.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);

    /// <summary>One analysis at a time.</summary>
    private readonly Lock _gate = new();

    /// <summary>The pipeline, with one MSBuild evaluator for every change.</summary>
    private readonly AnalysisPipeline _pipeline = new(loggerFactory, new TestDetta.Coverage.MsBuildProjectEvaluator(loggerFactory));

    /// <summary>Reads the repository's state between analyses.</summary>
    private readonly GitClient _git = new(loggerFactory.CreateLogger<GitClient>());

    /// <summary>The commit's text of each file read so far, by full path.</summary>
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>The selection for a change to <paramref name="target"/>.</summary>
    /// <param name="target">The sampled member, with its edits.</param>
    /// <exception cref="OperationCanceledException">The audit was cancelled.</exception>
    /// <exception cref="AuditTreeChangedException">The repository is no longer at the audited commit, or its working tree has changes.</exception>
    public AuditSelection Select(SyntheticTarget target)
    {
        lock (_gate)
        {
            Check();
            var (overlay, positions) = Edited(root, target, Text);
            var analysis = _pipeline.Run(root, head, new AnalysisOptions
            {
                MaxMapAge = options.MaxMapAge,
                DriftFallback = options.DriftFallback,
                NonCodeFiles = options.NonCodeFiles,
                WorkingTreeOverlay = overlay,
                Cancellation = cancel,
            });

            // A change made while the analysis read the tree would have joined its diff.
            Check();
            return new AuditSelection(analysis, positions);
        }
    }

    /// <summary>
    /// The edited text of each file the target's edits touch, by path relative to <paramref name="root"/>, and the
    /// position of each edit in the unedited text.
    /// </summary>
    /// <param name="root">The team's repository root.</param>
    /// <param name="target">The sampled member.</param>
    /// <param name="text">The audited commit's text of a file, by full path.</param>
    internal static (Dictionary<string, string> Overlay, List<(int Line, int Column)> Positions) Edited(string root, SyntheticTarget target, Func<string, string> text)
    {
        var overlay = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in target.Edits.GroupBy(static e => e.Path, StringComparer.Ordinal))
        {
            // From the end, so earlier offsets stay valid; the offsets were taken from this same text.
            var edited = new System.Text.StringBuilder(text(group.Key));
            foreach (var edit in group.OrderByDescending(static e => e.Position))
            {
                edited.Remove(edit.Position, edit.Replaces).Insert(edit.Position, edit.Text);
            }

            overlay[Path.GetRelativePath(root, group.Key).Replace('\\', '/')] = edited.ToString();
        }

        return (overlay, [.. target.Edits.Select(e => Position(text(e.Path), e.Position))]);
    }

    /// <summary>The 1-based line and column of a character offset in a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="offset">Character offset, as the sampler took it from the file's text.</param>
    internal static (int Line, int Column) Position(string text, int offset)
    {
        var line = 1;
        var start = 0;
        for (var i = 0; i < Math.Min(offset, text.Length); i++)
        {
            if (text[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }

        return (line, offset - start + 1);
    }

    /// <summary>
    /// Stops the audit when the repository is no longer what it audits: HEAD moved, or the working tree has changes
    /// (tracked or untracked files git does not ignore, <c>.testdetta/</c> aside) that would join every change's diff.
    /// </summary>
    /// <exception cref="AuditTreeChangedException">It moved.</exception>
    private void Check()
    {
        var now = _git.Head(root);
        if (!string.Equals(now, head, StringComparison.Ordinal))
        {
            throw new AuditTreeChangedException($"the repository moved from {head[..Math.Min(12, head.Length)]} to {now[..Math.Min(12, now.Length)]} during the audit; every change is compared with the commit the audit started on, so it stopped. Run it where nothing commits or checks out meanwhile");
        }

        var dirty = _git.ChangedFiles(root, head).Where(static f => !f.StartsWith(".testdetta/", StringComparison.Ordinal)).ToList();
        if (dirty.Count > 0)
        {
            throw new AuditTreeChangedException($"the working tree changed during the audit ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", …" : string.Empty)}); every change would join those edits, so it stopped");
        }
    }

    /// <summary>
    /// The audited commit's text of a file, as a checkout writes it (<c>git cat-file --filters</c>: line endings and
    /// other filters applied, so the sampler's offsets, taken from the clean working tree, fit it).
    /// </summary>
    /// <param name="path">Full path of a file in the repository.</param>
    /// <exception cref="InvalidOperationException">git could not read it.</exception>
    private string Text(string path)
    {
        if (_texts.TryGetValue(path, out var known))
        {
            return known;
        }

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in (string[])["cat-file", "--filters", $"{head}:{relative}"])
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var error = process.StandardError.ReadToEndAsync(cancel);
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        if (!process.WaitForExit(GitTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"git cat-file of {relative} did not finish within a minute.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git cat-file of {relative} at {head} failed: {error.GetAwaiter().GetResult().Trim()}");
        }

        // Decoded as File.ReadAllText decodes, which the sampler used: a byte order mark decides, else UTF-8.
        bytes.Position = 0;
        using var reader = new StreamReader(bytes, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return _texts[path] = reader.ReadToEnd();
    }
}
