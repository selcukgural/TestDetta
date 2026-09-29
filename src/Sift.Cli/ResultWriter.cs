using System.Text.Encodings.Web;
using System.Text.Json;
using Sift.Core;

namespace Sift.Cli;

/// <summary>Everything <see cref="ResultWriter"/> needs to print one analysis.</summary>
/// <param name="Options">Command line options, for the format and the tests-only filter.</param>
/// <param name="RepoRoot">Repository root; printed paths are relative to it.</param>
/// <param name="ChangedFileCount">Number of changed files git reported.</param>
/// <param name="ProjectCount">Number of projects in the repository.</param>
/// <param name="Result">The analysis result.</param>
internal sealed record ResultReport(
    CliOptions Options,
    string RepoRoot,
    int ChangedFileCount,
    int ProjectCount,
    ImpactResult Result);

/// <summary>Prints an analysis result on stdout in the format chosen on the command line.</summary>
internal static class ResultWriter
{
    /// <summary>Writes <paramref name="report"/> to <paramref name="stdout"/>.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="stdout">Destination, normally <see cref="Console.Out"/>.</param>
    public static void Write(ResultReport report, TextWriter stdout)
    {
        var selected = report.Options.TestsOnly
            ? [.. report.Result.Tests]
            : report.Result.Affected;

        switch (report.Options.Format)
        {
            case OutputFormat.Paths:
                WritePaths(report, selected, stdout);
                break;

            case OutputFormat.Json:
                WriteJson(report, selected, stdout);
                break;

            default:
                WriteText(report, selected, stdout);
                break;
        }
    }

    /// <summary>One relative .csproj path per line.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="selected">Projects to list.</param>
    /// <param name="stdout">Destination.</param>
    private static void WritePaths(ResultReport report, IReadOnlyList<AffectedProject> selected, TextWriter stdout)
    {
        foreach (var affected in selected)
        {
            stdout.WriteLine(Relative(report.RepoRoot, affected.Project.Path));
        }
    }

    /// <summary>Human readable summary with one line per project and its reason.</summary>
    /// <param name="report">What to print.</param>
    /// <param name="selected">Projects to list.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteText(ResultReport report, IReadOnlyList<AffectedProject> selected, TextWriter stdout)
    {
        var options = report.Options;
        stdout.WriteLine($"{report.ChangedFileCount} changed file(s) vs {options.BaseRef}, {report.ProjectCount} project(s) in repo.");
        stdout.WriteLine($"{selected.Count} affected{(options.TestsOnly ? " test" : "")} project(s):");

        foreach (var affected in selected)
        {
            var marker = affected.Project.IsTestProject ? "[test] " : "       ";
            stdout.WriteLine($"  {marker}{Relative(report.RepoRoot, affected.Project.Path)}  ({affected.Reason})");
        }

        if (report.Result.UnmappedFiles.Count > 0)
        {
            stdout.WriteLine($"{report.Result.UnmappedFiles.Count} changed file(s) outside any project (ignored).");
        }
    }

    /// <summary>
    /// JSON written with <see cref="Utf8JsonWriter"/> directly: no reflection-based serializer and
    /// no intermediate objects for the shape.
    /// </summary>
    /// <param name="report">What to print.</param>
    /// <param name="selected">Projects to list.</param>
    /// <param name="stdout">Destination.</param>
    private static void WriteJson(ResultReport report, IReadOnlyList<AffectedProject> selected, TextWriter stdout)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = true,
                   // Paths contain characters such as '+' that the default encoder escapes needlessly.
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            json.WriteStartObject();
            json.WriteString("base"u8, report.Options.BaseRef);
            json.WriteNumber("changedFiles"u8, report.ChangedFileCount);

            json.WriteStartArray("projects"u8);
            foreach (var affected in selected)
            {
                json.WriteStartObject();
                json.WriteString("path"u8, Relative(report.RepoRoot, affected.Project.Path));
                json.WriteBoolean("isTest"u8, affected.Project.IsTestProject);
                json.WriteString("reason"u8, affected.Reason.ToString());
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("unmappedFiles"u8);
            foreach (var file in report.Result.UnmappedFiles)
            {
                json.WriteStringValue(file);
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        stdout.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    /// <summary>Path of <paramref name="path"/> relative to <paramref name="root"/>, with '/' separators.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="path">Absolute path inside the repository.</param>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
