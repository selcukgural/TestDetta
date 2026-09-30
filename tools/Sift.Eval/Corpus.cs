using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sift.Eval;

/// <summary>
/// Settings of one corpus repository. Every property but <see cref="Name"/> and <see cref="Url"/>
/// may be left out; <see cref="Corpus.Defaults"/> fills it in.
/// </summary>
internal sealed record CorpusEntry
{
    /// <summary>Directory name of the repository's results, unique in the corpus.</summary>
    public required string Name { get; init; }

    /// <summary>Clone URL or local path.</summary>
    public required string Url { get; init; }

    /// <summary>Branch to evaluate.</summary>
    public string? Branch { get; init; }

    /// <summary>Target framework to build, record and test.</summary>
    public string? Framework { get; init; }

    /// <summary>Test processes per project when recording.</summary>
    public int? Parallel { get; init; }

    /// <summary>Latest commits to evaluate from history.</summary>
    public int? Merges { get; init; }

    /// <summary>Mutants per history commit.</summary>
    public int? Mutants { get; init; }

    /// <summary>Synthetic changes at the branch head.</summary>
    public int? Synthetic { get; init; }

    /// <summary>Seed of the synthetic sample.</summary>
    public int? Seed { get; init; }

    /// <summary>Shell command after every checkout, such as <c>dotnet tool restore</c>.</summary>
    public string? Setup { get; init; }

    /// <summary>Shell command after every test run.</summary>
    public string? Cleanup { get; init; }

    /// <summary>Test projects to use (paths in the repository); empty for all.</summary>
    public IReadOnlyList<string>? Projects { get; init; }

    /// <summary>
    /// What the repository exercises, such as <c>reflection</c>, <c>pinvoke</c>, <c>source-generator</c>
    /// or <c>large</c>; the summary reports results per tag.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Why the repository is in the corpus, for the reader of the corpus file.</summary>
    public string? Notes { get; init; }
}

/// <summary>A list of repositories to evaluate, from a JSON file.</summary>
internal sealed record Corpus
{
    /// <summary>Values for properties an entry leaves out.</summary>
    public CorpusEntry? Defaults { get; init; }

    /// <summary>Repositories, evaluated in this order.</summary>
    public required IReadOnlyList<CorpusEntry> Repositories { get; init; }

    /// <summary>Reads a corpus file; comments and trailing commas are allowed.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads and validates a corpus file.</summary>
    /// <param name="path">Corpus JSON file.</param>
    /// <exception cref="InvalidDataException">The file is not a valid corpus.</exception>
    public static Corpus Load(string path)
    {
        Corpus? corpus;
        try
        {
            // The defaults object has no name or URL, so it is read without the required checks.
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var defaults = document.RootElement.TryGetProperty("defaults", out var element)
                ? JsonSerializer.Deserialize<CorpusDefaults>(element.GetRawText(), Options)
                : null;
            var repositories = document.RootElement.TryGetProperty("repositories", out var list)
                ? JsonSerializer.Deserialize<List<CorpusEntry>>(list.GetRawText(), Options)
                : null;
            corpus = repositories is null ? null : new Corpus { Defaults = defaults?.ToEntry(), Repositories = repositories };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not a valid corpus: {ex.Message}", ex);
        }

        if (corpus is null || corpus.Repositories.Count == 0)
        {
            throw new InvalidDataException($"{path} lists no repositories.");
        }

        var duplicate = corpus.Repositories.GroupBy(static r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"{path} lists {duplicate.Key} more than once.");
        }

        return corpus;
    }

    /// <summary>The entry with <see cref="Defaults"/> filled in where it leaves a property out.</summary>
    /// <param name="entry">A repository of the corpus.</param>
    public CorpusEntry Resolve(CorpusEntry entry)
    {
        if (Defaults is not { } d)
        {
            return entry;
        }

        return entry with
        {
            Branch = entry.Branch ?? d.Branch,
            Framework = entry.Framework ?? d.Framework,
            Parallel = entry.Parallel ?? d.Parallel,
            Merges = entry.Merges ?? d.Merges,
            Mutants = entry.Mutants ?? d.Mutants,
            Synthetic = entry.Synthetic ?? d.Synthetic,
            Seed = entry.Seed ?? d.Seed,
            Setup = entry.Setup ?? d.Setup,
            Cleanup = entry.Cleanup ?? d.Cleanup,
        };
    }

    /// <summary>The <c>defaults</c> object: an entry without name and URL.</summary>
    private sealed record CorpusDefaults
    {
        /// <summary>See <see cref="CorpusEntry.Branch"/>.</summary>
        public string? Branch { get; init; }

        /// <summary>See <see cref="CorpusEntry.Framework"/>.</summary>
        public string? Framework { get; init; }

        /// <summary>See <see cref="CorpusEntry.Parallel"/>.</summary>
        public int? Parallel { get; init; }

        /// <summary>See <see cref="CorpusEntry.Merges"/>.</summary>
        public int? Merges { get; init; }

        /// <summary>See <see cref="CorpusEntry.Mutants"/>.</summary>
        public int? Mutants { get; init; }

        /// <summary>See <see cref="CorpusEntry.Synthetic"/>.</summary>
        public int? Synthetic { get; init; }

        /// <summary>See <see cref="CorpusEntry.Seed"/>.</summary>
        public int? Seed { get; init; }

        /// <summary>See <see cref="CorpusEntry.Setup"/>.</summary>
        public string? Setup { get; init; }

        /// <summary>See <see cref="CorpusEntry.Cleanup"/>.</summary>
        public string? Cleanup { get; init; }

        /// <summary>As an entry, so <see cref="Resolve"/> can copy from it.</summary>
        public CorpusEntry ToEntry() => new()
        {
            Name = string.Empty,
            Url = string.Empty,
            Branch = Branch,
            Framework = Framework,
            Parallel = Parallel,
            Merges = Merges,
            Mutants = Mutants,
            Synthetic = Synthetic,
            Seed = Seed,
            Setup = Setup,
            Cleanup = Cleanup,
        };
    }
}
