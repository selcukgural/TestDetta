using TestDetta.Core;
using static TestDetta.Cli.TestRunArguments;

namespace TestDetta.Cli;

/// <summary>One folder or file MSBuild writes when it builds or tests a project of a working copy.</summary>
/// <param name="Project">The project file in the copy.</param>
/// <param name="Framework">The target framework of the inner build; empty for a multi-targeted project's outer one.</param>
/// <param name="Kind">The property it comes from (<c>TargetPath</c>, <c>OutputPath</c>, <c>IntermediateOutputPath</c> …).</param>
/// <param name="Path">Its full path.</param>
internal sealed record AuditOutputPath(string Project, string Framework, string Kind, string Path);

/// <summary>
/// Asks MSBuild, before anything is built or run in a working copy, where every project of it would write: its build
/// output, intermediate and base folders, the restore's folder, an artifacts folder, a documentation file, for every
/// target framework, with the configuration and the properties the user's <c>dotnet test</c> arguments give. The audit
/// refuses to start when any of them lies outside the copy (an absolute <c>OutputPath</c>, <c>BaseOutputPath</c> or
/// artifacts path, a <c>bin</c> folder that is a link to the team's own): the copy's build would write the team's files.
/// One <c>dotnet msbuild</c> of a driver project evaluates them all, as <see cref="TestDetta.Coverage.MsBuildProjectEvaluator"/> does.
/// </summary>
internal static class AuditOutputPaths
{
    /// <summary>The target injected into every project; an outer build asks each of its target frameworks.</summary>
    private const string Targets = """
        <Project>
          <Target Name="TestDettaAuditPaths" Returns="@(TestDettaAuditPath)">
            <ItemGroup>
              <_TestDettaAuditFramework Include="$(TargetFrameworks)" Condition="'$(TargetFramework)' == ''" />
              <_TestDettaAuditRaw Include="BaseOutputPath" Value="$(BaseOutputPath)" />
              <_TestDettaAuditRaw Include="BaseIntermediateOutputPath" Value="$(BaseIntermediateOutputPath)" />
              <_TestDettaAuditRaw Include="MSBuildProjectExtensionsPath" Value="$(MSBuildProjectExtensionsPath)" />
              <_TestDettaAuditRaw Include="ArtifactsPath" Value="$(ArtifactsPath)" Condition="'$(UseArtifactsOutput)' == 'true'" />
            </ItemGroup>
            <ItemGroup Condition="'@(_TestDettaAuditFramework)' == ''">
              <_TestDettaAuditRaw Include="TargetPath" Value="$(TargetPath)" />
              <_TestDettaAuditRaw Include="OutputPath" Value="$(OutputPath)" />
              <_TestDettaAuditRaw Include="OutDir" Value="$(OutDir)" />
              <_TestDettaAuditRaw Include="IntermediateOutputPath" Value="$(IntermediateOutputPath)" />
              <_TestDettaAuditRaw Include="DocumentationFile" Value="$(DocumentationFile)" />
            </ItemGroup>
            <ItemGroup>
              <TestDettaAuditPath Include="@(_TestDettaAuditRaw->'%(Identity)')" Condition="'%(_TestDettaAuditRaw.Value)' != ''"
                                  Full="$([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', '%(_TestDettaAuditRaw.Value)'))"
                                  Project="$(MSBuildProjectFullPath)" Framework="$(TargetFramework)" />
            </ItemGroup>
            <MSBuild Condition="'@(_TestDettaAuditFramework)' != ''" Projects="$(MSBuildProjectFullPath)" Targets="TestDettaAuditPaths" Properties="TargetFramework=%(_TestDettaAuditFramework.Identity)">
              <Output TaskParameter="TargetOutputs" ItemName="TestDettaAuditPath" />
            </MSBuild>
          </Target>
        </Project>
        """;

    /// <summary>Options of <c>dotnet test</c> whose value is a path it writes to.</summary>
    private static readonly string[] WrittenPathOptions = ["-o", "--output", "--artifacts-path", "--results-directory", "--diag", "-d"];

    /// <summary>Asks MSBuild where each project writes.</summary>
    /// <param name="request">The copy, its projects and the user's arguments.</param>
    /// <param name="scratch">A folder of the work folder for the driver and its output.</param>
    /// <param name="cancel">Stops the query.</param>
    /// <exception cref="InvalidOperationException">MSBuild could not answer.</exception>
    public static List<AuditOutputPath> Query(AuditPathRequest request, string scratch, CancellationToken cancel)
    {
        var targets = Path.Combine(scratch, "testdetta-audit-paths.targets");
        var output = Path.Combine(scratch, "paths.txt");
        var driver = Path.Combine(scratch, "paths.proj");
        File.WriteAllText(targets, Targets);
        File.WriteAllText(driver, Driver(request, targets, output));

        var options = BeforeRunSettings(request.UserArguments);
        var configuration = ValueOf(options, "-c") ?? ValueOf(options, "--configuration") ?? "Debug";
        List<string> command = ["msbuild", driver, "-nologo", "-v:q", "-nodeReuse:false", $"-p:Configuration={configuration}"];
        if ((ValueOf(options, "-r") ?? ValueOf(options, "--runtime")) is { } runtime)
        {
            command.Add($"-p:RuntimeIdentifier={runtime}");
        }

        command.AddRange(options.Where(static o => o.StartsWith("-p:", StringComparison.Ordinal) || o.StartsWith("/p:", StringComparison.Ordinal)
                                                     || o.StartsWith("--property:", StringComparison.Ordinal) || o.StartsWith("-property:", StringComparison.Ordinal)));
        using var log = new StringWriter();
        var exitCode = AuditProcess.Run(command, request.Copy, new Dictionary<string, string>(), log, TimeSpan.FromMinutes(10), cancel);
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(output))
        {
            throw new InvalidOperationException($"MSBuild could not say where the projects of the working copy write (exit code {exitCode}): {log.ToString().Trim()}");
        }

        return [.. Parse(File.ReadLines(output))];
    }

    /// <summary>The driver project: every project once, test projects with the user's <c>-f</c> when given.</summary>
    /// <param name="request">The copy, its projects and the user's arguments.</param>
    /// <param name="targets">The injected targets file.</param>
    /// <param name="output">File the lines are written to.</param>
    internal static string Driver(AuditPathRequest request, string targets, string output)
    {
        var options = BeforeRunSettings(request.UserArguments);
        var framework = ValueOf(options, "-f") ?? ValueOf(options, "--framework");
        var hooks = $"CustomAfterMicrosoftCommonTargets={targets};CustomAfterMicrosoftCommonCrossTargetingTargets={targets}";
        var items = request.Projects.Select(p => new System.Xml.Linq.XElement("TestDettaProject",
            new System.Xml.Linq.XAttribute("Include", Escape(p)),
            new System.Xml.Linq.XElement("Properties", Escape(framework is not null && request.TestProjects.Contains(p) ? $"{hooks};TargetFramework={framework}" : hooks))));
        var document = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Project",
            new System.Xml.Linq.XElement("ItemGroup", items),
            new System.Xml.Linq.XElement("Target", new System.Xml.Linq.XAttribute("Name", "Build"),
                new System.Xml.Linq.XElement("MSBuild",
                    new System.Xml.Linq.XAttribute("Projects", "@(TestDettaProject)"),
                    new System.Xml.Linq.XAttribute("Targets", "TestDettaAuditPaths"),
                    new System.Xml.Linq.XAttribute("BuildInParallel", "true"),
                    new System.Xml.Linq.XAttribute("ContinueOnError", "true"),
                    new System.Xml.Linq.XElement("Output", new System.Xml.Linq.XAttribute("TaskParameter", "TargetOutputs"), new System.Xml.Linq.XAttribute("ItemName", "TestDettaAuditPath"))),
                new System.Xml.Linq.XElement("WriteLinesToFile",
                    new System.Xml.Linq.XAttribute("File", Escape(output)),
                    new System.Xml.Linq.XAttribute("Lines", "@(TestDettaAuditPath->'%(Project)%09%(Framework)%09%(Identity)%09%(Full)')"),
                    new System.Xml.Linq.XAttribute("Overwrite", "true")))));
        return document.ToString();
    }

    /// <summary>Reads the driver's lines: project, framework, kind and full path, tab-separated.</summary>
    /// <param name="lines">Lines of the output file.</param>
    internal static IEnumerable<AuditOutputPath> Parse(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length == 4 && parts[3].Length > 0)
            {
                yield return new AuditOutputPath(Paths.Normalize(parts[0]), parts[1], parts[2], Paths.Normalize(parts[3]));
            }
        }
    }

    /// <summary>
    /// Why the build or the runs would write outside the copy, or null: a project MSBuild gave no answer for (it could
    /// not be checked), or a path that lies outside the copy once every link on the way is resolved.
    /// </summary>
    /// <param name="copy">Root of the copy, as a real path.</param>
    /// <param name="projects">The projects asked for, in the copy.</param>
    /// <param name="paths">What MSBuild answered.</param>
    /// <param name="relative">Makes a copy path readable for the report (relative to the copy's root).</param>
    internal static string? Outside(string copy, IReadOnlyCollection<string> projects, IReadOnlyList<AuditOutputPath> paths, Func<string, string> relative)
    {
        var answered = paths.Select(static p => p.Project).ToHashSet(Paths.Comparer);
        if (projects.FirstOrDefault(p => !answered.Contains(Paths.Normalize(p))) is { } silent)
        {
            return $"MSBuild did not say where {relative(silent)} writes, so the audit cannot check that its build stays inside the working copy";
        }

        foreach (var path in paths)
        {
            var real = Paths.ResolveLinks(path.Path);
            if (!Paths.IsUnder(real, copy))
            {
                var framework = path.Framework.Length > 0 ? $" ({path.Framework})" : string.Empty;
                return $"{relative(path.Project)}{framework} writes outside the working copy: its {path.Kind} is {path.Path}" +
                       (Paths.Comparer.Equals(real, path.Path) ? string.Empty : $" (through a link: {real})") +
                       "; the copy's build would write your own files, so td audit does not start";
            }
        }

        return null;
    }

    /// <summary>
    /// Why an option of the user's arguments would make the runs write outside the copy, or null: <c>-o</c>,
    /// <c>--output</c>, <c>--artifacts-path</c>, <c>--results-directory</c> or <c>--diag</c> with a path that, taken from
    /// the directory a run starts in, leaves the copy (an absolute path, say).
    /// </summary>
    /// <param name="copy">Root of the copy, as a real path.</param>
    /// <param name="runDirectory">A directory the runs start in, inside the copy.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments.</param>
    internal static string? OutsideOption(string copy, string runDirectory, IReadOnlyList<string> userArguments)
    {
        var options = BeforeRunSettings(userArguments);
        foreach (var option in WrittenPathOptions)
        {
            if (ValueOf(options, option) is not { } value)
            {
                continue;
            }

            var full = Paths.ResolveLinks(Paths.Normalize(Path.GetFullPath(value, runDirectory)));
            if (!Paths.IsUnder(full, copy))
            {
                return $"{option} {value} lies outside the working copy, so every run of the audit would write there; give a relative path or leave it out";
            }
        }

        return null;
    }

    /// <summary>Escapes MSBuild's special characters in a literal path or property list.</summary>
    /// <param name="value">Raw text.</param>
    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace("@", "%40", StringComparison.Ordinal)
            .Replace("$", "%24", StringComparison.Ordinal).Replace("'", "%27", StringComparison.Ordinal);
}

/// <summary>What <see cref="AuditOutputPaths.Query"/> asks about.</summary>
/// <param name="Copy">Root of the working copy; the query runs there, so its <c>global.json</c> chooses the SDK.</param>
/// <param name="Projects">Every SDK-style project of the copy.</param>
/// <param name="TestProjects">Of those, the test projects, which get the user's <c>-f</c>.</param>
/// <param name="UserArguments">The user's <c>dotnet test</c> arguments.</param>
internal sealed record AuditPathRequest(string Copy, IReadOnlyList<string> Projects, IReadOnlySet<string> TestProjects, IReadOnlyList<string> UserArguments);
