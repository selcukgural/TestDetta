using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Cli;
using TestDetta.Core;

namespace TestDetta.Lab;

/// <summary>
/// Answers "what does this change select, and why?" for a working tree, with the maps the repository holds: the
/// analysis <c>td affected</c> runs, without the license check (which made a local experiment select everything until
/// an environment variable said the repository was public) and with every class's decision and evidence, which the
/// CLI shows only in its debug log.
/// </summary>
internal static class WhatIf
{
    /// <summary>Runs the analysis of the working tree's change against <paramref name="baseRef"/>.</summary>
    /// <param name="repository">Repository root.</param>
    /// <param name="baseRef">Base commit or branch.</param>
    public static IReadOnlyList<TestProjectSelection> Run(string repository, string baseRef) =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(Path.GetFullPath(repository), baseRef).Tests;

    /// <summary>Writes each test project's mode and reason, then every class with its decision and evidence.</summary>
    /// <param name="selections">The selections.</param>
    /// <param name="writer">Where the report goes.</param>
    /// <param name="style">Console styling.</param>
    public static void Write(IReadOnlyList<TestProjectSelection> selections, TextWriter writer, ConsoleStyle style)
    {
        if (selections.Count == 0)
        {
            writer.WriteLine(style.Paint(Role.Muted, "The change affects no test project."));
            return;
        }

        foreach (var selection in selections)
        {
            writer.WriteLine($"{style.Paint(Role.Heading, Path.GetFileNameWithoutExtension(selection.Project.Path))}: {style.Paint(Role.Number, selection.Mode.ToString())}, {selection.Reason}");
            if (selection.Decisions.Count == 0)
            {
                continue;
            }

            var table = new TextTable(style, new TextColumn("Class"), new TextColumn("Runs"), new TextColumn("Decided by"), new TextColumn("Evidence", Wrap: true));
            foreach (var decision in selection.Decisions)
            {
                table.Add(
                    new Cell(decision.TestClass, Role.Test),
                    decision.Selected ? new Cell("yes", Role.Good) : new Cell("no", Role.Muted),
                    new Cell(decision.Kind.ToString()),
                    new Cell(decision.Evidence));
            }

            table.Write(writer, "  ");
        }
    }
}
