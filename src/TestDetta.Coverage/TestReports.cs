using System.Xml.Linq;
using TestDetta.Core;

namespace TestDetta.Coverage;

/// <summary>
/// Makes a <c>dotnet test</c> run write a TRX report and finds it afterwards, under both runners,
/// so failures are known per test class.
/// </summary>
internal static class TestReports
{
    /// <summary>
    /// Arguments that make the run write a TRX report: VSTest's logger, or the testing platform's
    /// report option, which xUnit v3 names differently. Empty when the run cannot write one: under
    /// the testing platform the TRX extension must be in the test output, and an unknown option
    /// would fail the run.
    /// </summary>
    /// <param name="mode">Runner <c>dotnet test</c> uses.</param>
    /// <param name="outputDirectory">Output directory of the test project.</param>
    /// <param name="results">Results directory (VSTest only).</param>
    /// <param name="reportName">Unique report file name (testing platform only).</param>
    /// <remarks>
    /// Under the testing platform no results directory is passed: repositories may already set one
    /// in their build, and a second <c>--results-directory</c> makes the command line invalid. The
    /// report gets a unique name instead and <see cref="Collect"/> finds it.
    /// </remarks>
    public static List<string> Arguments(DotnetTestMode mode, string outputDirectory, string results, string reportName)
    {
        if (mode == DotnetTestMode.VSTest)
        {
            return ["--logger", "trx", "--results-directory", results];
        }

        if (File.Exists(Path.Combine(outputDirectory, "xunit.v3.core.dll")))
        {
            return ["--report-xunit-trx", "--report-xunit-trx-filename", reportName];
        }

        if (File.Exists(Path.Combine(outputDirectory, "Microsoft.Testing.Extensions.TrxReport.dll")))
        {
            return ["--report-trx", "--report-trx-filename", reportName];
        }

        if (!UsesNUnitXml(mode, outputDirectory))
        {
            return [];
        }

        var settings = new XElement("RunSettings");
        AddNUnitXml(settings, results);
        Directory.CreateDirectory(results);
        var path = Path.Combine(results, "testdetta-nunit.runsettings");
        new XDocument(settings).Save(path);
        return ["--settings", path];
    }

    /// <summary>
    /// True for NUnit under the testing platform without the TRX extension (NSubstitute): no TRX
    /// report can be asked for, but NUnit's adapter writes its own result file when the run settings
    /// name a folder (<see cref="AddNUnitXml"/>), which <see cref="TrxReport.Read"/> reads as well.
    /// </summary>
    /// <param name="mode">Runner <c>dotnet test</c> uses.</param>
    /// <param name="outputDirectory">Output directory of the test project.</param>
    public static bool UsesNUnitXml(DotnetTestMode mode, string outputDirectory) =>
        mode == DotnetTestMode.TestingPlatform
        && !File.Exists(Path.Combine(outputDirectory, "xunit.v3.core.dll"))
        && !File.Exists(Path.Combine(outputDirectory, "Microsoft.Testing.Extensions.TrxReport.dll"))
        && File.Exists(Path.Combine(outputDirectory, "NUnit3.TestAdapter.dll"));

    /// <summary>Makes NUnit's adapter write its result XML into <paramref name="results"/>.</summary>
    /// <param name="runSettings">The <c>RunSettings</c> element; its <c>NUnit</c> section is created or extended.</param>
    /// <param name="results">Folder for the result file, named after the test assembly.</param>
    public static void AddNUnitXml(XElement runSettings, string results)
    {
        if (runSettings.Element("NUnit") is not { } nunit)
        {
            nunit = new XElement("NUnit");
            runSettings.Add(nunit);
        }

        nunit.SetElementValue("TestOutputXml", results);
        nunit.SetElementValue("OutputXmlFolderMode", "AsSpecified");
    }

    /// <summary>
    /// Moves a testing-platform report named <paramref name="reportName"/> from wherever the run wrote
    /// it (the repository's own results directory, <c>TestResults</c> next to the output) into
    /// <paramref name="results"/>.
    /// </summary>
    /// <param name="mode">Runner <c>dotnet test</c> used.</param>
    /// <param name="repoRoot">Repository root, searched for the report.</param>
    /// <param name="reportName">Unique report file name.</param>
    /// <param name="results">Directory the report is read from.</param>
    public static void Collect(DotnetTestMode mode, string repoRoot, string reportName, string results)
    {
        if (mode == DotnetTestMode.VSTest)
        {
            return;
        }

        var found = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(repoRoot, reportName, found))
        {
            Directory.CreateDirectory(results);
            File.Move(file, Path.Combine(results, Path.GetFileName(file)), overwrite: true);
        }
    }
}
