using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;
using TestDetta.Core.Licensing;

namespace TestDetta.Tests;

/// <summary>Licenses: signed strings verified offline, scoped to git remotes, priced by active committers (docs/design/licensing.md).</summary>
public sealed class LicensingTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly GitRepoFixture _repo = new();

    private Dictionary<string, byte[]> Trusted => new() { ["test-1"] = _key.ExportSubjectPublicKeyInfo() };

    private static LicenseClaims Claims(int seats = 5, string scope = "github.com/acme/*", int daysLeft = 60) => new(
        "L-1", "test-1", "team", LicenseKind.Subscription, "Acme", seats, [scope],
        Now.AddDays(-10), Now.AddDays(daysLeft), new Uri("https://license.example/v1/licenses/L-1"));

    [Fact]
    public void Signed_license_verifies_and_keeps_its_claims()
    {
        var text = License.Sign(Claims(), _key);

        var claims = License.Verify(text, Trusted, out var error);

        Assert.Null(error);
        Assert.Equal(Claims() with { Scopes = claims!.Scopes }, claims);
        Assert.Equal(["github.com/acme/*"], claims.Scopes);
    }

    [Fact]
    public void A_license_broken_across_lines_or_indented_when_copied_still_verifies()
    {
        var text = License.Sign(Claims(), _key);
        // As an e-mail client or a secret form may hand it back: wrapped at 76 characters, indented, CRLF.
        var copied = "  " + string.Join("\r\n    ", text.Chunk(76).Select(static chunk => new string(chunk))) + "\t\n";

        Assert.NotNull(License.Verify(copied, Trusted, out var error));
        Assert.Null(error);
        Assert.Equal(text, License.Normalize(copied));
        Assert.Equal(text, LicenseCheck.Best([copied], Trusted, out _)!.Value.Text);
    }

    [Fact]
    public void Changed_payload_unknown_key_and_garbage_are_rejected()
    {
        var text = License.Sign(Claims(), _key);
        var parts = text.Split('.');
        var forged = License.Sign(Claims(seats: 50), ECDsa.Create(ECCurve.NamedCurves.nistP256)).Split('.')[1];

        Assert.Null(License.Verify($"{parts[0]}.{forged}.{parts[2]}", Trusted, out var tampered));
        Assert.Contains("signature", tampered, StringComparison.Ordinal);
        Assert.Null(License.Verify(text, new Dictionary<string, byte[]>(), out var unknown));
        Assert.Contains("unknown key", unknown, StringComparison.Ordinal);
        Assert.Null(License.Verify("not a license", Trusted, out _));
        Assert.Null(License.Verify("testdetta1.!!.??", Trusted, out _));
    }

    [Theory]
    [InlineData("https://user:s3cret@github.com/Acme/App.git", "github.com/acme/app")]
    [InlineData("git@github.com:Acme/App.git", "github.com/acme/app")]
    [InlineData("ssh://git@git.acme.corp:7999/scm/pay/app.git", "git.acme.corp/scm/pay/app")]
    [InlineData("https://gitlab.acme.local/group/sub/app", "gitlab.acme.local/group/sub/app")]
    [InlineData("/srv/mirrors/app.git", null)]
    [InlineData("", null)]
    public void Remote_url_is_reduced_to_host_and_path_without_credentials(string url, string? identity) =>
        Assert.Equal(identity, RepositoryIdentity.Normalize(url));

    [Theory]
    [InlineData("github.com/acme/app", "github.com/acme/*", true)]
    [InlineData("gitlab.acme.local/group/sub/app", "gitlab.acme.local/*", true)]
    [InlineData("github.com/acme/app", "GitHub.com/Acme/App", true)]
    [InlineData("github.com/acme-evil/app", "github.com/acme/*", false)]
    [InlineData("github.com/other/app", "github.com/acme/*", false)]
    [InlineData("git.acme.corp/scm/pay/app", "git.acme.corp/scm/*/app", true)]
    public void Scope_patterns_match_whole_segments(string identity, string pattern, bool matches) =>
        Assert.Equal(matches, RepositoryIdentity.Matches(identity, pattern));

    [Fact]
    public void Work_merged_now_counts_its_authors_only_when_they_wrote_it_recently()
    {
        // Five people left long ago; their branch is merged this month by today's five developers.
        List<CommitAuthor> authors =
        [
            .. Enumerable.Range(1, 5).Select(i => new CommitAuthor(Now.AddDays(-200), $"gone{i}@acme.com", $"Gone {i}")),
            .. Enumerable.Range(1, 5).Select(i => new CommitAuthor(Now.AddDays(-i), $"dev{i}@acme.com", $"Dev {i}")),
            new CommitAuthor(Now.AddDays(-1), "49699333+dependabot[bot]@users.noreply.github.com", "dependabot[bot]"),
            new CommitAuthor(Now.AddDays(-2), "project_42_bot_1234@noreply.gitlab.acme.local", "project_42_bot"),
            new CommitAuthor(Now.AddDays(-3), "release@acme.com", "Release Robot"),
            new CommitAuthor(Now.AddDays(-4), "DEV1@acme.com", "Dev One"),
        ];

        var active = new ActiveCommitters(authors, ["Release Robot"]).At(Now);

        Assert.Equal(5, active.Count);
        Assert.DoesNotContain(active, static a => a.StartsWith("gone", StringComparison.Ordinal));
    }

    [Fact]
    public void Days_over_the_seats_are_counted_back_from_the_history()
    {
        // A sixth and seventh developer joined 10 days ago: over 5 + 1 tolerance for 10 days.
        List<CommitAuthor> authors =
        [
            .. Enumerable.Range(1, 5).Select(i => new CommitAuthor(Now.AddDays(-20 - i), $"dev{i}@acme.com", $"Dev {i}")),
            .. Enumerable.Range(1, 5).Select(i => new CommitAuthor(Now.AddDays(-1), $"dev{i}@acme.com", $"Dev {i}")),
            new CommitAuthor(Now.AddDays(-9.5), "dev6@acme.com", "Dev 6"),
            new CommitAuthor(Now.AddDays(-9.5), "dev7@acme.com", "Dev 7"),
        ];

        Assert.Equal(10, new ActiveCommitters(authors, []).DaysOver(Now, allowed: 6, limit: 31));
    }

    [Fact]
    public void Check_reports_each_outcome_and_only_a_covering_license_allows_selection()
    {
        var license = License.Sign(Claims(), _key);
        var five = Enumerable.Range(1, 5).Select(i => new CommitAuthor(Now.AddDays(-i), $"dev{i}@acme.com", $"Dev {i}")).ToList();
        LicenseStatus Run(string? text, string? identity = "github.com/acme/app", IReadOnlyList<CommitAuthor>? authors = null, bool isPublic = false) =>
            LicenseCheck.Check(new LicenseCheckInput(text is null ? [] : [text], identity, authors ?? five, Now, isPublic), Trusted);

        Assert.Equal(LicenseState.PublicRepository, Run(null, isPublic: true).State);
        Assert.Equal(LicenseState.Missing, Run(null).State);
        Assert.Equal(LicenseState.Invalid, Run("testdetta1.x.y").State);
        Assert.Equal(LicenseState.WrongScope, Run(license, "github.com/other/app").State);
        Assert.Equal(LicenseState.WrongScope, Run(license, identity: null).State);
        Assert.Equal(LicenseState.Expired, Run(License.Sign(Claims(daysLeft: -1), _key)).State);
        Assert.Equal(LicenseState.NotYetValid, Run(License.Sign(Claims() with { ValidFrom = Now.AddDays(1) }, _key)).State);
        Assert.Equal(LicenseState.Warning, Run(License.Sign(Claims(daysLeft: 10), _key)).State);
        var valid = Run(license);
        Assert.Equal((LicenseState.Valid, 5), (valid.State, valid.ActiveCommitters));

        // Six is within the tolerance of five seats; eight is not.
        var six = five.Append(new CommitAuthor(Now.AddDays(-1), "dev6@acme.com", "Dev 6")).ToList();
        Assert.Equal(LicenseState.Valid, Run(license, authors: six).State);
        var eightRecently = six.Concat([new CommitAuthor(Now.AddDays(-2), "dev7@acme.com", "Dev 7"), new CommitAuthor(Now.AddDays(-2), "dev8@acme.com", "Dev 8")]).ToList();
        Assert.Equal(LicenseState.Warning, Run(license, authors: eightRecently).State);
        var eightForLong = Enumerable.Range(0, 70).SelectMany(d => Enumerable.Range(1, 8).Select(i => new CommitAuthor(Now.AddDays(-d), $"dev{i}@acme.com", $"Dev {i}"))).ToList();
        var over = Run(license, authors: eightForLong);
        Assert.Equal(LicenseState.OverSeats, over.State);
        Assert.False(over.AllowsSelection);
        Assert.True(Run(license, authors: eightRecently).AllowsSelection);

        // Before launch nothing changes what runs, so no message may say every test runs.
        var input = new LicenseCheckInput([], "github.com/acme/app", five, Now, false);
        Assert.EndsWith("; every test runs", LicenseCheck.Check(input, Trusted, enforced: true).Message, StringComparison.Ordinal);
        Assert.DoesNotContain("every test runs", LicenseCheck.Check(input, Trusted, enforced: false).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_genuine_version_of_the_first_license_counts()
    {
        var older = License.Sign(Claims() with { Issued = Now.AddDays(-30) }, _key);
        var newer = License.Sign(Claims(seats: 7) with { Issued = Now.AddDays(-1) }, _key);
        var otherLicense = License.Sign(Claims(seats: 50) with { Id = "L-2", Issued = Now }, _key);

        // The CI secret holds the older version; a refreshed copy holds the newer one.
        Assert.Equal(7, LicenseCheck.Best(["garbage", older, otherLicense, newer], Trusted, out _)!.Value.Claims.Seats);
        // A seat reduction is a newer license with fewer seats.
        var reduced = License.Sign(Claims(seats: 3) with { Issued = Now }, _key);
        Assert.Equal(3, LicenseCheck.Best([newer, reduced], Trusted, out _)!.Value.Claims.Seats);
        Assert.Null(LicenseCheck.Best(["garbage"], Trusted, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Inputs_come_from_the_environment_files_and_git_without_the_network()
    {
        _repo.Write("a.txt", "a");
        _repo.Git("add", "-A");
        _repo.Git("-c", "user.name=Old", "-c", "user.email=old@acme.com", "commit", "-q", "-m", "old", "--date=2020-01-01T00:00:00Z");
        _repo.Write(".mailmap", "Dev One <dev1@acme.com> <dev.one@personal.example>\n");
        _repo.Git("add", "-A");
        _repo.Git("-c", "user.name=Dev", "-c", "user.email=dev.one@personal.example", "commit", "-q", "-m", "new");
        _repo.Git("remote", "add", "origin", "https://ci-token:s3cret@github.com/Acme/App.git");
        var home = Path.Combine(_repo.Root, "home");
        Dictionary<string, string> variables = [];
        var inputs = new LicenseInputs(new GitClient(NullLogger<GitClient>.Instance), name => variables.GetValueOrDefault(name), home);

        var gathered = inputs.Gather(_repo.Root, DateTimeOffset.UtcNow);

        Assert.Equal("github.com/acme/app", gathered.Identity);
        Assert.Empty(gathered.Licenses);
        Assert.False(gathered.IsPublic);
        // The old work was committed today but written in 2020: only the recent author is active,
        // under the .mailmap identity.
        Assert.Equal(["dev1@acme.com"], new ActiveCommitters(gathered.Authors, []).At(gathered.Now));

        _repo.Write(".testdetta/license", "from-repo");
        Directory.CreateDirectory(Path.Combine(home, ".testdetta"));
        File.WriteAllText(LicenseInputs.InstalledPath(home), "instal\nled\n");
        variables["TESTDETTA_LICENSE"] = " from-\r\nvariable ";
        Assert.Equal(["from-variable", "installed", "from-repo"], inputs.FindLicenses(_repo.Root));

        variables["TESTDETTA_LICENSE_SCOPE"] = "git.acme.corp/scm/pay/app";
        variables["CI_PROJECT_VISIBILITY"] = "public";
        var overridden = inputs.Gather(_repo.Root, DateTimeOffset.UtcNow);
        Assert.Equal("git.acme.corp/scm/pay/app", overridden.Identity);
        Assert.True(overridden.IsPublic);
    }

    [Fact]
    public void A_clock_moved_back_does_not_revive_an_expired_license_while_commits_arrive()
    {
        _repo.Write("a.txt", "a");
        _repo.Commit("today");
        var inputs = new LicenseInputs(new GitClient(NullLogger<GitClient>.Instance), static _ => null, _repo.Root);

        var gathered = inputs.Gather(_repo.Root, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(gathered.Now > DateTimeOffset.UtcNow.AddDays(-1));
    }

    public void Dispose()
    {
        _key.Dispose();
        _repo.Dispose();
    }
}
