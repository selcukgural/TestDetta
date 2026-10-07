using System.Globalization;
using System.Text.Json;
using TestDetta.Mutants;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>
/// Writes the result of <c>td audit</c>: the verdict and the misses on the console, <c>testdetta-audit.json</c> (schema 2,
/// docs/design/audit.md) and the Markdown job summary. Decisions are shown only for classes that ran and failed.
/// </summary>
internal static class AuditReport
{
    /// <summary>Version of the JSON report.</summary>
    public const int Schema = 2;

    /// <summary>What the report says about the license, for the owner and for reviewers.</summary>
    public const string License =
        "No license is needed and none was read: the audit runs every test that can see a change and skips nothing. TestDetta made no network call.";

    /// <summary>The name of an outcome in the reports.</summary>
    /// <param name="outcome">The outcome.</param>
    public static string Name(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Caught => "caught",
        AuditOutcome.Missed => "missed",
        AuditOutcome.DidNotBreak => "did-not-break",
        AuditOutcome.NotReached => "not-reached",
        AuditOutcome.Inconclusive => "inconclusive",
        _ => "stopped",
    };

    /// <summary>
    /// The verdict line: "On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); 2
    /// inconclusive, 0 stopped (worst case, counting them as misses: 95% upper bound 5.8%, n = 105)", then why the audit
    /// stopped, and whether the target was reached. Bounds are rounded up, so the line never states less than the bound.
    /// </summary>
    /// <param name="result">The result.</param>
    public static string Verdict(AuditResult result)
    {
        if (result.Error is { } error && result.Changes == 0 && result.Unjudged == 0)
        {
            return $"No audit result: {error}";
        }

        var missed = result.Missed == 0 ? "0 missed" : $"{result.Missed} MISSED";
        var bound = result.Broke == 0 ? "no upper bound without a change that broke tests" : $"95% upper bound {Percent(result.UpperBound)}, n = {result.Broke}";
        var worstN = result.Broke + result.Unjudged;
        var worst = worstN == 0 ? "no worst-case bound" : $"worst case, counting them as misses: 95% upper bound {Percent(result.WorstCaseBound)}, n = {worstN}";
        var line = $"On your code: {result.Changes} {(result.Changes == 1 ? "change" : "changes")}, {result.Broke} broke tests, {missed} ({bound}); " +
                   $"{result.Count(AuditOutcome.Inconclusive)} inconclusive, {result.Count(AuditOutcome.Stopped)} stopped ({worst})";
        var target = result.Settings.TargetBroken;
        var why = result.StopReason switch
        {
            "target" => $"target of {target} reached",
            "max-mutants" => $"stopped at --max-mutants {result.Settings.MaxMutants}" + (result.TargetReached ? string.Empty : $", before the target of {target}"),
            "budget" => $"stopped by the budget ({Durations.DescribeLimit(result.Settings.Budget)})" + (result.TargetReached ? string.Empty : $" before the target of {target}"),
            "cancelled" => "cancelled",
            "error" => $"stopped by an error: {result.Error}",
            _ => result.Settings.Members.Count > 0 ? "the members asked for" : result.TargetReached ? "the whole sample" : $"the whole sample, before the target of {target}",
        };
        return $"{line}; {why}.";
    }

    /// <summary>The options that decide the result, as the command line takes them; with a change, those that repeat it alone.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="mutant">The change to repeat alone, or null for the whole audit.</param>
    public static List<string> Options(CliOptions options, AuditMutant? mutant)
    {
        var audit = options.Audit;
        var drift = options.Analysis.DriftFallback == TestDetta.Core.DriftFallback.RunAll ? "all" : "name";
        List<string> result = mutant is { } one
            ? [$"--member {Quoted(one.Member)}", $"--kinds {MutantKinds.Name(one.Target.Kind)}"]
            : [$"--seed {audit.Seed.ToString(CultureInfo.InvariantCulture)}", $"--kinds {Quoted(audit.KindsText)}", .. audit.Members.Select(static m => $"--member {Quoted(m)}")];
        result.Add($"--max-map-age {options.Analysis.MaxMapAge.ToString(CultureInfo.InvariantCulture)}");
        result.Add($"--drift-fallback {drift}");
        if (options.Analysis.MapsDir is { } mapsDir)
        {
            result.Add($"--maps-dir {Quoted(mapsDir)}");
        }

        if (options.Test.DotnetTestArguments.Count > 0)
        {
            result.Add("-- " + CommandLine(options.Test.DotnetTestArguments));
        }

        return result;
    }

    /// <summary>Writes the verdict, the misses and the projects left out to the console.</summary>
    /// <param name="result">The result.</param>
    /// <param name="options">Parsed command line.</param>
    /// <param name="stdout">The console.</param>
    public static void WriteConsole(AuditResult result, CliOptions options, TextWriter stdout)
    {
        stdout.WriteLine();
        stdout.WriteLine("td audit: " + Verdict(result));
        stdout.WriteLine($"  caught {result.Count(AuditOutcome.Caught)}, missed {result.Missed}, did not break {result.Count(AuditOutcome.DidNotBreak)}, " +
                         $"inconclusive {result.Count(AuditOutcome.Inconclusive)}, not reached {result.Count(AuditOutcome.NotReached)}, stopped {result.Count(AuditOutcome.Stopped)}");
        foreach (var mutant in result.Mutants.Where(static m => m.Outcome == AuditOutcome.Missed))
        {
            stdout.WriteLine($"  MISS #{mutant.Index} {mutant.Member} ({MutantKinds.Name(mutant.Target.Kind)}, {mutant.File}):");
            foreach (var testClass in mutant.Broken.Where(static c => !c.Selected))
            {
                stdout.WriteLine($"    {testClass.Project}: {testClass.Name}: {testClass.Note}");
                stdout.WriteLine($"      TestDetta saw: {string.Join("; ", mutant.Seen)}");
                stdout.WriteLine($"      project {testClass.ProjectMode}: {testClass.ProjectReason}");
                stdout.WriteLine($"      class {testClass.Decision}: {testClass.Evidence} (map {testClass.MapCommit ?? "none"})");
            }

            stdout.WriteLine($"    reproduce: td audit {string.Join(' ', Options(options, mutant))}");
        }

        foreach (var project in result.Projects.Where(static p => !p.Audited))
        {
            stdout.WriteLine($"  not audited: {project.Path}: {project.Reason}");
        }

        stdout.WriteLine($"  {License}");
        if (result.LeftBehind is { } left)
        {
            stdout.WriteLine($"  the work folder {left} could not be deleted; the next td audit removes it");
        }

        stdout.Flush();
    }

    /// <summary>Writes <c>testdetta-audit.json</c>.</summary>
    /// <param name="result">The result.</param>
    /// <param name="options">Parsed command line.</param>
    /// <param name="path">The file.</param>
    public static void WriteJson(AuditResult result, CliOptions options, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", Schema);
        json.WriteString("verdict", Verdict(result));
        json.WriteString("stopReason", result.StopReason);
        if (result.Error is { } error)
        {
            json.WriteString("error", error);
        }

        json.WriteString("license", License);
        json.WriteStartObject("totals");
        json.WriteNumber("changes", result.Changes);
        json.WriteNumber("broke", result.Broke);
        json.WriteNumber("caught", result.Count(AuditOutcome.Caught));
        json.WriteNumber("missed", result.Missed);
        json.WriteNumber("didNotBreak", result.Count(AuditOutcome.DidNotBreak));
        json.WriteNumber("inconclusive", result.Count(AuditOutcome.Inconclusive));
        json.WriteNumber("notReached", result.Count(AuditOutcome.NotReached));
        json.WriteNumber("stopped", result.Count(AuditOutcome.Stopped));
        json.WriteNumber("brokeUncovered", result.Mutants.Count(static m => m.Broke && !m.Target.Covered));
        if (result.Broke > 0)
        {
            json.WriteNumber("upperBound95", Math.Round(result.UpperBound, 6));
        }
        else
        {
            json.WriteNull("upperBound95");
        }

        // Every inconclusive and stopped change counted as one that broke tests and was missed.
        json.WriteNumber("unjudged", result.Unjudged);
        json.WriteNumber("worstCaseN", result.Broke + result.Unjudged);
        if (result.Broke + result.Unjudged > 0)
        {
            json.WriteNumber("worstCaseUpperBound95", Math.Round(result.WorstCaseBound, 6));
        }
        else
        {
            json.WriteNull("worstCaseUpperBound95");
        }

        json.WriteNumber("targetBroken", result.Settings.TargetBroken);
        json.WriteBoolean("targetReached", result.TargetReached);
        json.WriteEndObject();

        json.WriteStartObject("run");
        json.WriteNumber("wallSeconds", Math.Round(result.Seconds, 1));
        json.WriteNumber("baselineSeconds", Math.Round(result.BaselineSeconds, 1));
        json.WriteNumber("parallelism", result.Parallelism);
        WriteLimit(json, "budgetSeconds", result.Settings.Budget);
        if (result.Settings.MaxMutants is { } max)
        {
            json.WriteNumber("maxMutants", max);
        }
        else
        {
            json.WriteNull("maxMutants");
        }

        json.WriteString("workFolder", result.WorkFolder);
        json.WriteBoolean("workFolderRemoved", result.WorkFolderRemoved);
        json.WriteStartArray("dotnetTestArguments");
        foreach (var argument in result.TestArguments)
        {
            json.WriteStringValue(argument);
        }

        json.WriteEndArray();
        json.WriteEndObject();

        if (result.Sample is { } sample)
        {
            json.WriteStartObject("sample");
            json.WriteNumber("seed", sample.Seed);
            json.WriteString("kinds", sample.Kinds);
            json.WriteString("order", "uniform over every candidate by a stable hash of the member and the seed; coverage not used");
            json.WriteNumber("candidates", sample.Candidates);
            json.WriteNumber("coveredCandidates", sample.CoveredCandidates);
            json.WriteNumber("generatorMembers", sample.GeneratorMembers);
            json.WriteStartArray("members");
            foreach (var member in sample.Members)
            {
                json.WriteStringValue(member);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteStartArray("projects");
        foreach (var project in result.Projects)
        {
            json.WriteStartObject();
            json.WriteString("path", project.Path);
            json.WriteBoolean("audited", project.Audited);
            if (project.Reason is { } reason)
            {
                json.WriteString("reason", reason);
            }

            json.WriteNumber("classes", project.Classes);
            json.WriteNumber("baselineSeconds", Math.Round(project.BaselineSeconds, 1));
            json.WriteStartArray("baselineFailed");
            foreach (var failed in project.BaselineFailed)
            {
                json.WriteStringValue(failed);
            }

            json.WriteEndArray();
            json.WriteString("mapCommit", project.MapCommit);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("mutants");
        foreach (var mutant in result.Mutants)
        {
            WriteMutant(json, mutant, options, result.Root);
        }

        json.WriteEndArray();
        WriteReproduction(json, result.Reproduction);
        json.WriteEndObject();
    }

    /// <summary>Writes one change.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="mutant">The change.</param>
    /// <param name="options">Parsed command line, for its reproduction.</param>
    /// <param name="root">Repository root, for the edits' files and lines.</param>
    private static void WriteMutant(Utf8JsonWriter json, AuditMutant mutant, CliOptions options, string root)
    {
        json.WriteStartObject();
        json.WriteNumber("index", mutant.Index);
        json.WriteString("member", mutant.Member);
        json.WriteString("kind", MutantKinds.Name(mutant.Target.Kind));
        json.WriteString("file", mutant.File.Length > 0 ? mutant.File : Path.GetRelativePath(root, mutant.Target.Edits[0].Path).Replace('\\', '/'));
        json.WriteBoolean("covered", mutant.Target.Covered);
        json.WriteString("outcome", Name(mutant.Outcome));
        if (mutant.Note is { } note)
        {
            json.WriteString("note", note);
        }

        json.WriteNumber("mutatedMethods", mutant.MutatedMethods);
        json.WriteNumber("seconds", Math.Round(mutant.Seconds, 1));
        if (mutant.Reached.Count > 0)
        {
            json.WriteNumber("selectedShare", Math.Round(mutant.SelectedShare, 4));
            json.WriteBoolean("caughtByCrash", mutant.CaughtByCrash);
            WriteStrings(json, "reached", mutant.Reached);
            WriteStrings(json, "seen", mutant.Seen);
        }

        json.WriteStartArray("broken");
        foreach (var testClass in mutant.Broken)
        {
            WriteClass(json, testClass);
        }

        json.WriteEndArray();
        if (mutant.NotCounted.Count > 0)
        {
            json.WriteStartArray("notCounted");
            foreach (var testClass in mutant.NotCounted)
            {
                WriteClass(json, testClass);
            }

            json.WriteEndArray();
        }

        if (mutant.Outcome == AuditOutcome.Missed)
        {
            // The edit to make by hand, to see the same decision with td test or td affected (which needs a license).
            json.WriteStartArray("edits");
            for (var i = 0; i < mutant.Target.Edits.Count; i++)
            {
                var edit = mutant.Target.Edits[i];
                var (line, column) = i < mutant.EditPositions.Count ? mutant.EditPositions[i] : (0, 0);
                json.WriteStartObject();
                json.WriteString("file", Path.GetRelativePath(root, edit.Path).Replace('\\', '/'));
                json.WriteNumber("line", line);
                json.WriteNumber("column", column);
                json.WriteNumber("replaces", edit.Replaces);
                json.WriteString("insert", edit.Text);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteString("reproduce", "td audit " + string.Join(' ', Options(options, mutant)));
        }

        json.WriteEndObject();
    }

    /// <summary>Writes one failed class with its decision chain.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="testClass">The class.</param>
    private static void WriteClass(Utf8JsonWriter json, AuditClass testClass)
    {
        json.WriteStartObject();
        json.WriteString("project", testClass.Project);
        if (testClass.Crash && testClass.Name is AuditClass.SelectedPart or AuditClass.LeftOutPart)
        {
            // A part of the project whose run did not complete with the change: no class can be named.
            json.WriteNull("class");
            json.WriteString("part", testClass.Selected ? "selected" : "left-out");
        }
        else
        {
            json.WriteString("class", testClass.Name);
        }

        if (testClass.Crash)
        {
            json.WriteBoolean("crash", true);
        }

        json.WriteBoolean("selected", testClass.Selected);
        json.WriteStartObject("chain");
        json.WriteString("projectMode", testClass.ProjectMode);
        json.WriteString("projectReason", testClass.ProjectReason);
        json.WriteString("decision", testClass.Decision);
        json.WriteString("evidence", testClass.Evidence);
        json.WriteString("mapCommit", testClass.MapCommit);
        json.WriteEndObject();
        if (testClass.Note is { } note)
        {
            json.WriteString("note", note);
        }

        json.WriteEndObject();
    }

    /// <summary>Writes the reproduction block.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="reproduction">What repeats the audit.</param>
    private static void WriteReproduction(Utf8JsonWriter json, AuditReproduction reproduction)
    {
        json.WriteStartObject("reproduction");
        json.WriteString("testDettaVersion", reproduction.Version);
        json.WriteString("testDettaSha256", reproduction.BinarySha256);
        json.WriteString("head", reproduction.Head);
        json.WriteStartArray("maps");
        foreach (var (project, commit, sha) in reproduction.Maps)
        {
            json.WriteStartObject();
            json.WriteString("testProject", project);
            json.WriteString("commit", commit);
            json.WriteString("sha256", sha);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        WriteStrings(json, "options", reproduction.Options);
        json.WriteString("command", reproduction.Command);
        json.WriteEndObject();
    }

    /// <summary>Appends the Markdown job summary.</summary>
    /// <param name="result">The result.</param>
    /// <param name="options">Parsed command line.</param>
    /// <param name="writer">The summary file.</param>
    public static void WriteMarkdown(AuditResult result, CliOptions options, TextWriter writer)
    {
        writer.WriteLine("### TestDetta audit");
        writer.WriteLine();
        writer.WriteLine(MarkdownSummary.Code(Verdict(result)));
        writer.WriteLine();
        writer.WriteLine("| Caught | Missed | Did not break | Inconclusive | Not reached | Stopped | 95% upper bound | Worst case |");
        writer.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        var bound = result.Broke == 0 ? "none" : $"{Percent(result.UpperBound)} (n = {result.Broke})";
        var worstN = result.Broke + result.Unjudged;
        var worst = worstN == 0 ? "none" : $"{Percent(result.WorstCaseBound)} (n = {worstN})";
        writer.WriteLine($"| {result.Count(AuditOutcome.Caught)} | {result.Missed} | {result.Count(AuditOutcome.DidNotBreak)} | {result.Count(AuditOutcome.Inconclusive)} | {result.Count(AuditOutcome.NotReached)} | {result.Count(AuditOutcome.Stopped)} | {bound} | {worst} |");
        writer.WriteLine();
        writer.WriteLine("The worst case counts every inconclusive and stopped change as one that broke tests and was missed.");
        writer.WriteLine();
        var misses = result.Mutants.Where(static m => m.Outcome == AuditOutcome.Missed).ToList();
        if (misses.Count > 0)
        {
            writer.WriteLine($"**Misses** ({misses.Count})");
            writer.WriteLine();
            foreach (var mutant in misses)
            {
                writer.WriteLine($"- {MarkdownSummary.Code(mutant.Member)} ({MutantKinds.Name(mutant.Target.Kind)}, {MarkdownSummary.Code(mutant.File)}); TestDetta saw {MarkdownSummary.Code(string.Join("; ", mutant.Seen))}");
                foreach (var testClass in mutant.Broken.Where(static c => !c.Selected))
                {
                    writer.WriteLine($"  - {MarkdownSummary.Code(testClass.Name)} in {MarkdownSummary.Code(testClass.Project)}: project {MarkdownSummary.Code(testClass.ProjectMode)} ({MarkdownSummary.Code(testClass.ProjectReason)}), " +
                                     $"class {MarkdownSummary.Code(testClass.Decision)} ({MarkdownSummary.Code(testClass.Evidence)}), map {MarkdownSummary.Code(testClass.MapCommit ?? "none")}");
                }

                writer.WriteLine($"  - reproduce: {MarkdownSummary.Code("td audit " + string.Join(' ', Options(options, mutant)))}");
            }

            writer.WriteLine();
        }

        var left = result.Projects.Where(static p => !p.Audited).ToList();
        if (left.Count > 0)
        {
            writer.WriteLine($"Not audited ({left.Count}): " + string.Join(", ", left.Select(static p => $"{MarkdownSummary.Code(p.Path)} ({MarkdownSummary.Code(p.Reason ?? string.Empty)})")));
            writer.WriteLine();
        }

        writer.WriteLine($"Reproduce: {MarkdownSummary.Code(result.Reproduction.Command)} at {MarkdownSummary.Code(result.Reproduction.Head ?? "HEAD")}. {License}");
        writer.WriteLine();
    }

    /// <summary>
    /// An upper bound as a percentage with one decimal, rounded up: <c>2.9%</c> for 0.02922, <c>3.0%</c> for 0.02951. A
    /// bound shown rounded to the nearest tenth could read lower than it is.
    /// </summary>
    /// <param name="share">From 0 to 1.</param>
    internal static string Percent(double share) =>
        (Math.Max(0, Math.Ceiling((share * 1000) - 1e-9)) / 10).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Writes a time limit in seconds, or null for none.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="name">Property name.</param>
    /// <param name="limit">The limit.</param>
    private static void WriteLimit(Utf8JsonWriter json, string name, TimeSpan? limit)
    {
        if (limit is { } time)
        {
            json.WriteNumber(name, time.TotalSeconds);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    /// <summary>Writes a string array.</summary>
    /// <param name="json">The writer.</param>
    /// <param name="name">Property name.</param>
    /// <param name="values">The strings.</param>
    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }
}
