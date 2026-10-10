using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestDetta.Eval;

/// <summary>
/// Adds up the <c>explain.json</c> files of a run (<c>--explain</c>: selections and evidence for every sampled
/// change, no build and no test run) into <c>precision.md</c>: per repository, how much of the suite the sampled
/// changes select, how often a whole test project runs, and which rules select the most classes. The rules are
/// the evidence texts with names, paths and numbers taken out, so one rule groups across members and files. It is
/// the quick way to see why a repository selects widely before working on it.
/// </summary>
internal static partial class PrecisionReport
{
    /// <summary>One repository's numbers.</summary>
    /// <param name="Name">Corpus name.</param>
    /// <param name="Changes">Sampled changes.</param>
    /// <param name="Shares">Selected share of the recorded classes per change.</param>
    /// <param name="WholeProject">Changes that ran at least one test project completely.</param>
    /// <param name="Rules">Selected classes per rule, summed over the changes.</param>
    /// <param name="TestClasses">Recorded test classes.</param>
    internal sealed record Repository(string Name, int Changes, List<double> Shares, int WholeProject, Dictionary<string, int> Rules, int TestClasses)
    {
        /// <summary>Selected classes per <see cref="Category"/>.</summary>
        public Dictionary<string, int> Categories => Rules.GroupBy(static r => Category(r.Key)).ToDictionary(static g => g.Key, static g => g.Sum(static r => r.Value), StringComparer.Ordinal);

        /// <summary>Mean selected share.</summary>
        public double Mean => Shares.Count == 0 ? 0 : Shares.Average();

        /// <summary>Median selected share.</summary>
        public double Median => Shares.Count == 0 ? 0 : Shares.Order().ElementAt(Shares.Count / 2);

        /// <summary>True when the explanation was not written to the end; the repository has no numbers.</summary>
        public bool Incomplete { get; init; }
    }

    /// <summary>Writes <c>precision.md</c> when the run has <c>explain.json</c> files; returns its path or null.</summary>
    /// <param name="directory">Run directory.</param>
    /// <param name="corpus">The corpus.</param>
    /// <param name="only">Repositories the run evaluates; empty for all.</param>
    public static string? Write(string directory, Corpus corpus, IReadOnlySet<string> only)
    {
        var repositories = corpus.Repositories
            .Where(r => only.Count == 0 || only.Contains(r.Name))
            .Select(r => Read(Path.Combine(directory, r.Name, "explain.json"), r.Name))
            .OfType<Repository>()
            .ToList();
        if (repositories.Count == 0)
        {
            return null;
        }

        var path = Path.Combine(directory, "precision.md");
        File.WriteAllText(path, Markdown(repositories));
        return path;
    }

    /// <summary>Reads one <c>explain.json</c>; null when there is none.</summary>
    /// <param name="file">The file.</param>
    /// <param name="name">Repository name.</param>
    internal static Repository? Read(string file, string name)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            // An explanation the evaluation stopped writing halfway (it failed on one change): reported as incomplete.
            return new Repository(name, 0, [], 0, [], 0) { Incomplete = true };
        }

        using var _ = document;
        var root = document.RootElement;
        var shares = new List<double>();
        var rules = new Dictionary<string, int>(StringComparer.Ordinal);
        var whole = 0;
        foreach (var change in root.GetProperty("changes").EnumerateArray())
        {
            shares.Add(change.GetProperty("selectedShare").GetDouble());
            var anyWhole = false;
            foreach (var project in change.GetProperty("projects").EnumerateArray())
            {
                anyWhole |= project.GetProperty("mode").GetString() == "All";
                foreach (var selected in project.GetProperty("selected").EnumerateArray())
                {
                    var rule = Rule(selected.GetProperty("kind").GetString()!, selected.GetProperty("evidence").GetString() ?? string.Empty);
                    rules[rule] = rules.GetValueOrDefault(rule) + 1;
                }
            }

            whole += anyWhole ? 1 : 0;
        }

        var testClasses = root.TryGetProperty("testClasses", out var classes) ? classes.GetInt32() : 0;
        return new Repository(name, shares.Count, shares, whole, rules, testClasses);
    }

    /// <summary>
    /// The rule behind one selected class: its decision kind and its evidence without the map commit, the project a
    /// dependent inherited the reason from, names, paths and numbers.
    /// </summary>
    /// <param name="kind">Decision kind.</param>
    /// <param name="evidence">Evidence text.</param>
    internal static string Rule(string kind, string evidence)
    {
        var text = MapCommit().Replace(evidence, string.Empty);
        text = DependsOn().Replace(text, string.Empty);
        text = PathLike().Replace(text, "<file>");
        text = DottedName().Replace(text, "<name>");
        text = Parameters().Replace(text, string.Empty);
        // The name trace that led to a type found through reflection differs per change; the rule does not.
        var reflection = text.IndexOf("through reflection", StringComparison.Ordinal);
        if (reflection >= 0)
        {
            var maps = text.IndexOf("; the maps cannot answer: ", StringComparison.Ordinal);
            text = text[..(reflection + "through reflection".Length)] + (maps >= 0 ? text[maps..] : string.Empty);
        }

        text = Number().Replace(text, "n");
        text = text.Trim();
        return $"{kind}: {(text.Length > 150 ? text[..150] + "…" : text)}";
    }

    /// <summary>Category names of <see cref="Category"/>, in report order.</summary>
    internal static readonly string[] CategoryNames = ["ran the changed code", "ran code that names it", "a rule"];

    /// <summary>
    /// What kind of selection a rule is: the class ran the changed code (the map's exact answer; narrowing it can only
    /// miss), it ran code the name trace reached from the change (coverage of what names it), or a rule selected it
    /// without either (a whole project, a constructor or static constructor rule, a name, a class that failed while
    /// recording): the part a better signal could narrow.
    /// </summary>
    /// <param name="rule">A rule from <see cref="Rule"/>.</param>
    internal static string Category(string rule) =>
        rule.StartsWith("Coverage: ran <name>", StringComparison.Ordinal) && rule.EndsWith("which changed", StringComparison.Ordinal) ? CategoryNames[0]
        : rule.StartsWith("Coverage: ran <name>", StringComparison.Ordinal) && rule.EndsWith("names the changed code", StringComparison.Ordinal) ? CategoryNames[1]
        : CategoryNames[2];

    /// <summary>The report.</summary>
    /// <param name="repositories">Repositories with explanations.</param>
    internal static string Markdown(List<Repository> repositories)
    {
        var text = new StringBuilder();
        text.AppendLine("# Why selections are wide");
        text.AppendLine();
        text.AppendLine("From `--explain` (no build, no test run): every sampled change's selection and evidence. " +
                        "A rule's weight is the classes it selected, summed over the changes; the share is of all classes the changes selected. " +
                        "The last three columns split the selection: classes that ran the changed code (the map's exact answer), classes that ran code naming it, and classes only a rule selected (the part a better signal could narrow).");
        text.AppendLine();
        text.AppendLine("| Repository | Changes | Test classes | Mean share | Median share | Changes running a whole project | Ran the changed code | Ran code that names it | A rule |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var repository in repositories.OrderByDescending(static r => r.Mean))
        {
            if (repository.Incomplete)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {repository.Name} | incomplete explanation (see its log) | | | | |");
                continue;
            }

            var categories = repository.Categories;
            var selected = Math.Max(1, categories.Values.Sum());
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {repository.Name} | {repository.Changes} | {repository.TestClasses} | {repository.Mean:P0} | {repository.Median:P0} | {(double)repository.WholeProject / Math.Max(1, repository.Changes):P0} | {(double)categories.GetValueOrDefault(CategoryNames[0]) / selected:P0} | {(double)categories.GetValueOrDefault(CategoryNames[1]) / selected:P0} | {(double)categories.GetValueOrDefault(CategoryNames[2]) / selected:P0} |");
        }

        text.AppendLine();
        text.AppendLine("## Rules across the repositories");
        text.AppendLine();
        AppendRules(text, Combine(repositories.Select(static r => r.Rules)), 15);

        foreach (var repository in repositories.Where(static r => !r.Incomplete).OrderByDescending(static r => r.Mean))
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"## {repository.Name} (mean {repository.Mean:P0})");
            text.AppendLine();
            AppendRules(text, repository.Rules, 6);
        }

        return text.ToString();
    }

    /// <summary>Adds the heaviest rules as a table.</summary>
    /// <param name="text">The report.</param>
    /// <param name="rules">Selected classes per rule.</param>
    /// <param name="count">Rules to list.</param>
    private static void AppendRules(StringBuilder text, Dictionary<string, int> rules, int count)
    {
        var total = Math.Max(1, rules.Values.Sum());
        text.AppendLine("| Rule | Classes selected | Share of the selection |");
        text.AppendLine("|---|---|---|");
        foreach (var (rule, classes) in rules.OrderByDescending(static r => r.Value).ThenBy(static r => r.Key, StringComparer.Ordinal).Take(count))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {rule.Replace("|", "\\|", StringComparison.Ordinal)} | {classes} | {(double)classes / total:P0} |");
        }
    }

    /// <summary>Sums rule weights over repositories.</summary>
    /// <param name="all">Weights per repository.</param>
    private static Dictionary<string, int> Combine(IEnumerable<Dictionary<string, int>> all)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rules in all)
        {
            foreach (var (rule, classes) in rules)
            {
                result[rule] = result.GetValueOrDefault(rule) + classes;
            }
        }

        return result;
    }

    /// <summary>A map commit in evidence: "(map 9f3c…)".</summary>
    [GeneratedRegex(@"\s*\(map [0-9a-f…]+\)")]
    private static partial Regex MapCommit();

    /// <summary>The project a dependent inherited its reason from: "depends on Lib.csproj: ".</summary>
    [GeneratedRegex(@"depends on [^:]+: ")]
    private static partial Regex DependsOn();

    /// <summary>A path: a token with a slash, or a file name with an extension of a file a change names.</summary>
    [GeneratedRegex(@"[^\s,;()'""]*/[^\s,;()'""]*|[\w.-]+\.(?:csproj|fsproj|vbproj|props|targets|cs|fs|vb|json|md|txt|xml|resx|dll)\b")]
    private static partial Regex PathLike();

    /// <summary>A dotted name: a type or a member.</summary>
    [GeneratedRegex(@"[\w`<>+\[\]]+(?:\.[\w`<>+\[\],]+)+")]
    private static partial Regex DottedName();

    /// <summary>The parameter list after a name: overloads of a member are one rule.</summary>
    [GeneratedRegex(@"(?<=<name>)\([^()]*\)")]
    private static partial Regex Parameters();

    /// <summary>A number.</summary>
    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex Number();
}
