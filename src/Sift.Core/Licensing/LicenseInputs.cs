using System.Text.Json;

namespace Sift.Core.Licensing;

/// <summary>
/// Gathers what a license check needs from the environment, the files and git, the same way on any
/// CI (docs/design/licensing.md, "Verification in Sift"). Nothing here uses the network.
/// </summary>
/// <param name="git">Runs git; its output for remotes and authors is never logged.</param>
/// <param name="environment">Reads an environment variable; null when it is not set.</param>
/// <param name="home">The user's home directory, where <c>~/.sift/license</c> lives.</param>
public sealed class LicenseInputs(GitClient git, Func<string, string?> environment, string home)
{
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

        var identity = environment("SIFT_LICENSE_SCOPE") is { Length: > 0 } scope
            ? RepositoryIdentity.Normalize(scope.Contains("://", StringComparison.Ordinal) ? scope : "https://" + scope)
            : RepositoryIdentity.Normalize(git.OriginUrl(repoRoot));

        return new LicenseCheckInput(FindLicense(repoRoot), identity, authors, now, IsPublic())
        {
            ExtraBots = ReadBots(repoRoot),
        };
    }

    /// <summary>
    /// The license string, from the first of: <c>SIFT_LICENSE</c>, the file <c>SIFT_LICENSE_FILE</c>
    /// names, <c>~/.sift/license</c> (installed or refreshed), <c>.sift/license</c> in the repository.
    /// </summary>
    /// <param name="repoRoot">Repository root.</param>
    public string? FindLicense(string repoRoot)
    {
        if (environment("SIFT_LICENSE") is { Length: > 0 } text)
        {
            return text.Trim();
        }

        foreach (var file in (string?[])[environment("SIFT_LICENSE_FILE"), InstalledPath(home), Path.Combine(repoRoot, ".sift", "license")])
        {
            if (file is { Length: > 0 } && File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } content)
            {
                return content;
            }
        }

        return null;
    }

    /// <summary>Where <c>sift license install</c> keeps a license: <c>~/.sift/license</c>.</summary>
    /// <param name="home">The user's home directory.</param>
    public static string InstalledPath(string home) => Path.Combine(home, ".sift", "license");

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

    /// <summary>The customer's bot patterns from <c>.sift/license.json</c> (<c>{ "bots": [...] }</c>); empty when none.</summary>
    /// <param name="repoRoot">Repository root.</param>
    private static List<string> ReadBots(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".sift", "license.json");
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
