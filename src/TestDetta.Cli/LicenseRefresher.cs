using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using TestDetta.Core.Licensing;

namespace TestDetta.Cli;

/// <summary>
/// Fetches the newest version of the license in use from the URL it carries, at most once a day,
/// and stores it in <c>~/.testdetta/license</c> (docs/design/licensing.md, "Refresh"). An update channel,
/// never a gate: a 2-second timeout, no retries, every failure ignored, so an offline or blocked network
/// costs at most 2 seconds a day. <c>TESTDETTA_LICENSE_REFRESH=off</c> turns it off; <c>HTTPS_PROXY</c> is
/// honoured by the default handler. The license string goes in a header, never in the URL, and nothing
/// of it is logged.
/// </summary>
/// <param name="handler">Sends the request; the default socket handler outside tests.</param>
/// <param name="environment">Reads environment variables.</param>
/// <param name="home">The user's home directory.</param>
/// <param name="clock">The current time.</param>
/// <param name="logger">Receives what happened, without the license.</param>
internal sealed partial class LicenseRefresher(HttpMessageHandler handler, Func<string, string?> environment, string home, TimeProvider clock, ILogger logger)
{
    /// <summary>Shortest time between two attempts.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Longest an attempt may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>When the last attempt was made: <c>~/.testdetta/license-refresh</c>.</summary>
    private string StatePath => Path.Combine(home, ".testdetta", "license-refresh");

    /// <summary>Refreshes the license that <see cref="LicenseCheck.Best"/> picks of <paramref name="licenses"/>, when due.</summary>
    /// <param name="licenses">License strings found, in order of precedence.</param>
    /// <param name="trustedKeys">Public keys TestDetta trusts.</param>
    public async Task RefreshAsync(IReadOnlyList<string> licenses, IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        if (string.Equals(environment("TESTDETTA_LICENSE_REFRESH"), "off", StringComparison.OrdinalIgnoreCase)
            || LicenseCheck.Best(licenses, trustedKeys, out _) is not { } current
            || current.Claims.Refresh is not { Scheme: "https" } refresh
            || !Due())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(StatePath, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, refresh);
            request.Headers.Authorization = new AuthenticationHeaderValue("TestDetta", current.Text);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LogNotRefreshed(logger, (int)response.StatusCode);
                return;
            }

            var text = (await response.Content.ReadAsStringAsync()).Trim();
            if (License.Verify(text, trustedKeys, out _) is not { } newer || newer.Id != current.Claims.Id || newer.Issued < current.Claims.Issued)
            {
                LogRejected(logger);
                return;
            }

            // Written next to its final place and moved, so a reader never sees half a license.
            var path = LicenseInputs.InstalledPath(home);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, text);
            File.Move(temporary, path, overwrite: true);
            LogRefreshed(logger, newer.Id, newer.ValidUntil);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // The exception holds the service's address and the failure, never the license.
            LogFailed(logger, ex);
        }
    }

    /// <summary>True when no attempt was made in the last <see cref="Interval"/>.</summary>
    private bool Due()
    {
        try
        {
            return !File.Exists(StatePath)
                   || !DateTimeOffset.TryParse(File.ReadAllText(StatePath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                   || clock.GetUtcNow() - last >= Interval;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "License refresh: the service answered {StatusCode}; the license in hand is used")]
    private static partial void LogNotRefreshed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License refresh: the answer is not a newer version of the same genuine license; ignored")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "License refresh: stored {Id}, valid until {ValidUntil:yyyy-MM-dd}")]
    private static partial void LogRefreshed(ILogger logger, string id, DateTimeOffset validUntil);

    [LoggerMessage(Level = LogLevel.Debug, Message = "License refresh failed; the license in hand is used")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
