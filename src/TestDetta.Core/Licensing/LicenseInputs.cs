using System.Text.Json;

namespace TestDetta.Core.Licensing;

/// <summary>
/// Gathers what a license check needs from the environment, the files and git, the same way on any
/// CI (docs/design/licensing.md, "Verification in TestDetta"). Nothing here uses the network.
/// </summary>
/// <param name="git">Runs git; its output for remotes and authors is never logged.</param>
/// <param name="environment">Reads an environment variable; null when it is not set.</param>
/// <param name="home">The user's home directory, where <c>~/.testdetta/license</c> lives.</param>
public sealed class LicenseInputs(GitClient git, Func<string, string?> environment, string home)
{
    /// <summary>
    /// The environment variables that carry a license or name its file. TestDetta reads them itself and keeps them
    /// out of every process it starts (builds, <c>dotnet test</c>, audit runs): the code there, the team's tests and
    /// the packages' build steps, has no use for the license and should not be handed it.
    /// </summary>
    private static readonly string[] Secrets = ["TESTDETTA_LICENSE", "TESTDETTA_LICENSE_FILE"];

    /// <summary>Removes the license variables from the environment of a process about to start.</summary>
    /// <param name="processEnvironment">The process's environment (<c>ProcessStartInfo.Environment</c>).</param>
    public static void KeepOutOf(IDictionary<string, string?> processEnvironment)
    {
        foreach (var name in Secrets)
        {
            processEnvironment.Remove(name);
        }
    }

    /// <summary>Gathers the inputs for the repository at <paramref name="repoRoot"/> at <paramref name="clock"/>.</summary>
    /// <param name="repoRoot">Repository root.</param>
    /// <param name="clock">The system clock's current time.</param>
    public LicenseCheckInput Gather(string repoRoot, DateTimeOffset clock)
    {
        // Moving the clock back does not bring an expired license back while commits keep arriving.
        var headTime = DateTimeOffset.FromUnixTimeSeconds(git.HeadCommitTime(repoRoot));
        var now = headTime > clock ? headTime : clock;

        // Committer times are no earlier than author times, so this covers every commit authored in
        // the 60 days the over-seats period looks back.
        var since = now - (ActiveCommitters.Window * 2);
        var authors = git.AuthorsSince(repoRoot, since.ToUnixTimeSeconds())
            .Select(static a => new CommitAuthor(DateTimeOffset.FromUnixTimeSeconds(a.AuthorTime), a.Email, a.Name))
            .ToList();

        var identity = environment("TESTDETTA_LICENSE_SCOPE") is { Length: > 0 } scope
            ? RepositoryIdentity.Normalize(scope.Contains("://", StringComparison.Ordinal) ? scope : "https://" + scope)
            : RepositoryIdentity.Normalize(git.OriginUrl(repoRoot));

        return new LicenseCheckInput(FindLicenses(repoRoot), identity, authors, now, IsPublic())
        {
            ExtraBots = ReadBots(repoRoot),
        };
    }

    /// <summary>
    /// Every license string found, in order of precedence: <c>TESTDETTA_LICENSE</c>, the file
    /// <c>TESTDETTA_LICENSE_FILE</c> names, <c>~/.testdetta/license</c> (installed or refreshed), <c>.testdetta/license</c>
    /// in the repository. <see cref="LicenseCheck.Best"/> picks the one to use.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    public IReadOnlyList<string> FindLicenses(string repoRoot)
    {
        var licenses = new List<string>();
        if (environment("TESTDETTA_LICENSE") is { Length: > 0 } text && License.Normalize(text) is { Length: > 0 } normalized)
        {
            licenses.Add(normalized);
        }

        foreach (var file in (string?[])[environment("TESTDETTA_LICENSE_FILE"), InstalledPath(home), Path.Combine(repoRoot, ".testdetta", "license")])
        {
            if (file is { Length: > 0 } && File.Exists(file) && License.Normalize(File.ReadAllText(file)) is { Length: > 0 } content)
            {
                licenses.Add(content);
            }
        }

        return licenses;
    }

    /// <summary>Where <c>td license install</c> keeps a license: <c>~/.testdetta/license</c>.</summary>
    /// <param name="home">The user's home directory.</param>
    public static string InstalledPath(string home) => Path.Combine(home, ".testdetta", "license");

    /// <summary>
    /// True when the CI says the repository is public: GitHub Actions' event payload
    /// (<c>repository.private</c> is false) or GitLab's <c>CI_PROJECT_VISIBILITY</c>. Anywhere else a
    /// repository counts as private.
    /// </summary>
    private bool IsPublic()
    {
        if (string.Equals(environment("CI_PROJECT_VISIBILITY"), "public", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (environment("GITHUB_EVENT_PATH") is not { Length: > 0 } eventPath || !File.Exists(eventPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(eventPath));
            return document.RootElement.TryGetProperty("repository", out var repository)
                   && repository.TryGetProperty("private", out var isPrivate)
                   && isPrivate.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The customer's bot patterns from <c>.testdetta/license.json</c> (<c>{ "bots": [...] }</c>); empty when none.</summary>
    /// <param name="repoRoot">Repository root.</param>
    private static List<string> ReadBots(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".testdetta", "license.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("bots", out var bots) && bots.ValueKind == JsonValueKind.Array
                ? [.. bots.EnumerateArray().Select(static b => b.GetString()).OfType<string>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
