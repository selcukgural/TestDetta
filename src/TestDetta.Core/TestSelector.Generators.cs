using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>A project that uses an affected project as a source generator.</summary>
/// <param name="Consumer">The project whose compiler runs the generator.</param>
/// <param name="Generator">The affected generator project.</param>
internal sealed record GeneratorUse(ProjectNode Consumer, ProjectNode Generator);

/// <summary>What the generators write now, compared with the maps (docs/design/generator-output-changes.md).</summary>
/// <param name="Changes">Generated members that differ from the recorded ones, each with the project it is compiled into.</param>
/// <param name="Current">The generated files read from the build, which replace the recorded ones in the index.</param>
internal sealed record GeneratorOutput(List<(SymbolChange Change, ProjectNode Owner)> Changes, List<GeneratedSource> Current);

public sealed partial class TestSelector
{
    /// <summary>
    /// Decides, for every test project that sees an affected source generator through a project it
    /// depends on, whether the generator changes anything there: what the generator writes into a
    /// current build is compared with what the test project's map recorded, and only generated members
    /// that differ count as changes. A test project that runs completely anyway is not built for this.
    /// One that has no map, no reader, several target frameworks (its map holds one framework's
    /// generated code) or whose generated code cannot be read runs completely, as before; a build that
    /// fails ends the analysis, since <c>dotnet test</c> would fail the same way.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="projectLevel">Project-level fallbacks; receives the test projects that cannot be decided.</param>
    /// <param name="uses">Projects that use an affected project as a source generator.</param>
    private GeneratorOutput CompareGeneratorOutput(TestSelectionRequest request, Dictionary<string, string> projectLevel, List<GeneratorUse> uses)
    {
        var output = new GeneratorOutput([], []);
        if (uses.Count == 0)
        {
            return output;
        }

        var complete = RunAll(request.Graph, projectLevel);
        var reads = new Dictionary<string, GeneratedCodeRead>(Paths.Comparer);
        var seen = new HashSet<(string Path, string? Member, string Type)>();
        foreach (var byConsumer in uses.GroupBy(static u => u.Consumer.Path, Paths.Comparer))
        {
            var consumer = byConsumer.First().Consumer;
            var generators = string.Join(", ", byConsumer.Select(static u => Path.GetFileName(u.Generator.Path)).Distinct(StringComparer.Ordinal));
            var reason = $"it uses {generators}, which is affected, as a source generator";
            foreach (var test in request.Graph.WithDependents([consumer.Path]).Select(p => request.Graph[p]).Where(static p => p.IsTestProject))
            {
                if (complete.ContainsKey(test.Path))
                {
                    LogGeneratorNotCompared(test.Path, generators, "it runs completely anyway");
                    continue;
                }

                if (request.GeneratedCode is not { } reader || detector is null)
                {
                    AddFallback(projectLevel, test.Path, $"{reason}, and nothing builds it here to compare what the generator writes");
                    continue;
                }

                if (!request.CoverageMaps.TryGetValue(test.Path, out var map))
                {
                    AddFallback(projectLevel, test.Path, $"{reason}, and it has no coverage map with the generated code to compare with");
                    continue;
                }

                // dotnet test runs every framework, and a generator may write other code for another one
                // (a polyfill a framework lacks); the map holds one framework's generated code.
                if (map.Framework is not null || test.SymbolSets.Count > 1)
                {
                    AddFallback(projectLevel, test.Path, $"{reason}, and it targets several frameworks while its map holds the generated code of {map.Framework ?? "one"}");
                    continue;
                }

                if (!reads.TryGetValue(test.Path, out var read))
                {
                    LogReadingGeneratedCode(test.Path, generators, consumer.Path, map.Framework ?? "its only framework");
                    read = reader.Read(request.Graph, request.RepoRoot, test, map.Framework);
                    reads[test.Path] = read;
                }

                if (read.BuildErrors is { } errors)
                {
                    throw new InvalidOperationException(
                        $"Building {Path.GetFileName(test.Path)} to see what {generators} generates failed; dotnet test would fail the same way:{Environment.NewLine}{errors}");
                }

                if (read.Sources is not { } sources)
                {
                    AddFallback(projectLevel, test.Path, $"{reason}, and {read.Unavailable}");
                    continue;
                }

                var project = Path.GetRelativePath(request.RepoRoot, consumer.Path).Replace('\\', '/');
                var now = sources.Where(s => string.Equals(s.Project, project, StringComparison.Ordinal)).ToList();
                var before = map.Generated.Where(s => string.Equals(s.Project, project, StringComparison.Ordinal)).ToList();
                var changes = Compare(consumer, project, before, now);
                foreach (var change in changes.Where(c => seen.Add((c.Path, c.Symbol.MemberName, c.Symbol.TypeName))))
                {
                    output.Changes.Add((change.Symbol, consumer));
                }

                if (changes.Count == 0)
                {
                    LogGeneratedCodeSame(test.Path, generators, consumer.Path, now.Count);
                }
                else if (logger.IsEnabled(LogLevel.Information))
                {
                    var members = string.Join(", ", changes.Select(static c => $"{c.Symbol.TypeName}.{c.Symbol.MemberName ?? "(type)"} ({c.Symbol.Kind})").Distinct(StringComparer.Ordinal).Take(10));
                    LogGeneratedCodeChanged(test.Path, generators, consumer.Path, changes.Count, members);
                }

                output.Current.AddRange(now);
            }
        }

        return output;
    }

    /// <summary>
    /// The generated members of <paramref name="consumer"/> that differ between the recorded files and
    /// the current ones, matched by <see cref="GeneratedSource.Key"/>: modified, added and removed
    /// members, or a type, as a source file's change would give them.
    /// </summary>
    /// <param name="consumer">Project the generated code is compiled into.</param>
    /// <param name="project">Its path relative to the repository, as the generated files name it.</param>
    /// <param name="before">Generated files the map recorded.</param>
    /// <param name="now">Generated files of the current build.</param>
    private List<(string Path, SymbolChange Symbol)> Compare(ProjectNode consumer, string project, List<GeneratedSource> before, List<GeneratedSource> now)
    {
        var old = before.GroupBy(static s => s.Key, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First().Text, StringComparer.Ordinal);
        var current = now.GroupBy(static s => s.Key, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First().Text, StringComparer.Ordinal);
        var changes = new List<(string, SymbolChange)>();
        foreach (var key in old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldText = old.GetValueOrDefault(key);
            var newText = current.GetValueOrDefault(key);
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                continue;
            }

            var path = $"{SourceIndexBuilder.GeneratedPathPrefix}{project}/{key}";
            var change = detector!.Detect(
                path,
                oldText is null ? null : SourceText.From(oldText),
                newText is null ? null : SourceText.From(newText),
                consumer.SymbolSets);
            changes.AddRange(change.Symbols.Select(symbol => (path, symbol)));
        }

        return changes;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{TestProject}: building it to see what {Generators} generates into {Consumer} now, for {Framework}; the generator is affected by the change")]
    private partial void LogReadingGeneratedCode(string testProject, string generators, string consumer, string framework);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{TestProject}: what {Generators} generates into {Consumer} is the same as when its map was recorded ({Files} file(s)); the generator changes nothing there")]
    private partial void LogGeneratedCodeSame(string testProject, string generators, string consumer, int files);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{TestProject}: {Count} generated member(s) of {Consumer} differ from the map, through {Generators}: {Members}")]
    private partial void LogGeneratedCodeChanged(string testProject, string generators, string consumer, int count, string members);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TestProject}: not built to compare what {Generators} generates: {Reason}")]
    private partial void LogGeneratorNotCompared(string testProject, string generators, string reason);
}
