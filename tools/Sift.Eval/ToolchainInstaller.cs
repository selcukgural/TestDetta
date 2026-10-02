using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sift.Eval;

/// <summary>
/// Installs toolchains side by side under one root, each into its own directory that is never
/// changed once it is complete: a new runtime is a new toolchain. Archives are downloaded from
/// Microsoft and installed only when their SHA-512 matches the lock. This replaces installing into
/// a shared <c>dotnet</c> directory, which replaced the host under running evaluations (2026-10-02).
/// </summary>
/// <param name="root">Directory that holds the toolchains, <c>&lt;rid&gt;/&lt;name&gt;</c>.</param>
/// <param name="lockFile">Expected URLs and hashes.</param>
/// <param name="source">Where release metadata and archives come from.</param>
/// <param name="logger">Receives installs.</param>
internal sealed partial class ToolchainInstaller(string root, ToolchainLock lockFile, IReleaseSource source, ILogger logger)
{
    /// <summary>The only host archives are downloaded from.</summary>
    private const string DownloadHost = "builds.dotnet.microsoft.com";

    /// <summary>
    /// The toolchain's directory, installed first when it is missing. Built in a scratch directory
    /// under the root and renamed into place, so a reader sees a complete toolchain or none; when
    /// another evaluation finishes the same toolchain first, its copy is used and this one dropped.
    /// </summary>
    /// <param name="toolchain">What to install.</param>
    /// <exception cref="InvalidOperationException">An archive is unknown, not locked in locked mode, or its hash differs.</exception>
    public string Install(Toolchain toolchain)
    {
        var target = Path.Combine(root, toolchain.Rid, toolchain.DirectoryName);
        if (Directory.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(Path.Combine(root, toolchain.Rid));
        var scratch = Path.Combine(root, ".partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            var built = Path.Combine(scratch, "toolchain");
            Extract(Download(ArchiveKind.Sdk, toolchain.Sdk, toolchain.Rid, scratch), built);
            foreach (var runtime in toolchain.Runtimes.Distinct(StringComparer.Ordinal))
            {
                AddRuntime(Download(ArchiveKind.AspNetCoreRuntime, runtime, toolchain.Rid, scratch), runtime, built, scratch);
            }

            if (!File.Exists(Path.Combine(built, "dotnet")))
            {
                throw new InvalidOperationException($"The SDK {toolchain.Sdk} archive holds no dotnet host.");
            }

            try
            {
                Directory.Move(built, target);
                LogInstalled(toolchain.Description, target);
            }
            catch (IOException) when (Directory.Exists(target))
            {
                // Another evaluation installed the same toolchain meanwhile; both are identical.
            }

            return target;
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>
    /// Adds the shared frameworks of a runtime archive (<c>shared/&lt;framework&gt;/&lt;version&gt;</c>)
    /// to the toolchain, and nothing else: the SDK's host and <c>hostfxr</c> are newer and stay.
    /// </summary>
    /// <param name="archive">The downloaded runtime archive.</param>
    /// <param name="version">Its version.</param>
    /// <param name="toolchain">Toolchain being built.</param>
    /// <param name="scratch">Scratch directory of the install.</param>
    private static void AddRuntime(string archive, string version, string toolchain, string scratch)
    {
        var extracted = Path.Combine(scratch, "runtime-" + version);
        Extract(archive, extracted);
        var frameworks = Path.Combine(extracted, "shared");
        var added = 0;
        foreach (var framework in Directory.Exists(frameworks) ? Directory.EnumerateDirectories(frameworks) : [])
        {
            var source = Path.Combine(framework, version);
            if (!Directory.Exists(source))
            {
                continue;
            }

            added++;
            var destination = Path.Combine(toolchain, "shared", Path.GetFileName(framework), version);
            if (Directory.Exists(destination))
            {
                // The SDK already carries this runtime.
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
        }

        if (added == 0)
        {
            throw new InvalidOperationException($"The runtime {version} archive holds no shared framework of that version.");
        }
    }

    /// <summary>Extracts a <c>.tar.gz</c> archive; entries pointing outside the directory are refused by the reader.</summary>
    /// <param name="archive">The archive.</param>
    /// <param name="destination">New directory.</param>
    private static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    /// <summary>Downloads an archive into the scratch directory and checks its hash against the lock.</summary>
    /// <param name="kind">Kind of archive.</param>
    /// <param name="version">Its version.</param>
    /// <param name="rid">Runtime identifier.</param>
    /// <param name="scratch">Scratch directory of the install.</param>
    private string Download(ArchiveKind kind, string version, string rid, string scratch)
    {
        var archive = Locked(kind, version, rid);
        if (!Uri.TryCreate(archive.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, DownloadHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{archive.Url} is not an https URL on {DownloadHost}.");
        }

        Directory.CreateDirectory(scratch);
        var path = Path.Combine(scratch, Path.GetFileName(url.AbsolutePath));
        LogDownloading(url);
        using (var file = File.Create(path))
        {
            source.Download(url, file);
        }

        string actual;
        using (var file = File.OpenRead(path))
        {
            actual = Convert.ToHexStringLower(SHA512.HashData(file));
        }

        if (!string.Equals(actual, archive.Sha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{url} has SHA-512 {actual}, the lock expects {archive.Sha512}; not installed.");
        }

        return path;
    }

    /// <summary>
    /// The lock entry of an archive. A missing one is looked up in the release metadata and added for
    /// every supported runtime identifier, unless the lock is read-only.
    /// </summary>
    /// <param name="kind">Kind of archive.</param>
    /// <param name="version">Its version.</param>
    /// <param name="rid">Runtime identifier.</param>
    private LockedArchive Locked(ArchiveKind kind, string version, string rid)
    {
        var key = ToolchainLock.Key(kind, version, rid);
        if (lockFile.Find(key) is { } locked)
        {
            return locked;
        }

        if (lockFile.Locked)
        {
            throw new InvalidOperationException(
                $"{key} is not in {lockFile.Path}, which is read-only with CI=true; run once without CI=true and commit the lock.");
        }

        var found = FromMetadata(kind, version);
        if (!found.TryGetValue(key, out var archive))
        {
            throw new InvalidOperationException($"Microsoft's release metadata lists no {key} archive.");
        }

        lockFile.Add(found);
        LogLocked(version, found.Count);
        return archive;
    }

    /// <summary>Archives of a version for every supported runtime identifier, from the release metadata of its channel.</summary>
    /// <param name="kind">Kind of archive.</param>
    /// <param name="version">Its version.</param>
    private Dictionary<string, LockedArchive> FromMetadata(ArchiveKind kind, string version)
    {
        var parts = version.Split('.');
        var channel = $"{parts[0]}.{parts[1]}";
        using var metadata = JsonDocument.Parse(source.Metadata(channel));
        var found = new Dictionary<string, LockedArchive>(StringComparer.Ordinal);
        foreach (var release in metadata.RootElement.GetProperty("releases").EnumerateArray())
        {
            foreach (var product in Products(release, kind))
            {
                if (!product.TryGetProperty("version", out var productVersion) || productVersion.GetString() != version)
                {
                    continue;
                }

                foreach (var rid in Toolchain.SupportedRids)
                {
                    var name = (kind == ArchiveKind.Sdk ? "dotnet-sdk-" : "aspnetcore-runtime-") + rid + ".tar.gz";
                    foreach (var file in product.GetProperty("files").EnumerateArray())
                    {
                        if (file.GetProperty("name").GetString() == name)
                        {
                            found[ToolchainLock.Key(kind, version, rid)] =
                                new LockedArchive(file.GetProperty("url").GetString()!, file.GetProperty("hash").GetString()!.ToLowerInvariant());
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The products of a release that may hold the archive: its SDKs, or its ASP.NET Core runtime.</summary>
    /// <param name="release">One release of the metadata.</param>
    /// <param name="kind">Kind of archive.</param>
    private static List<JsonElement> Products(JsonElement release, ArchiveKind kind)
    {
        if (kind == ArchiveKind.AspNetCoreRuntime)
        {
            return release.TryGetProperty("aspnetcore-runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object ? [runtime] : [];
        }

        return release.TryGetProperty("sdks", out var sdks) && sdks.ValueKind == JsonValueKind.Array ? [.. sdks.EnumerateArray()] : [];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading {Url}")]
    private partial void LogDownloading(Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Locked {Count} archive(s) of {Version} from Microsoft's release metadata")]
    private partial void LogLocked(string version, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed {Toolchain} into {Directory}")]
    private partial void LogInstalled(string toolchain, string directory);
}

/// <summary>Where release metadata and archives come from; tests serve their own.</summary>
internal interface IReleaseSource
{
    /// <summary>The <c>releases.json</c> of a channel, such as <c>10.0</c>.</summary>
    /// <param name="channel">Major and minor version.</param>
    string Metadata(string channel);

    /// <summary>Writes the file at <paramref name="url"/> into <paramref name="destination"/>.</summary>
    /// <param name="url">Download URL.</param>
    /// <param name="destination">Receives the bytes.</param>
    void Download(Uri url, Stream destination);
}

/// <summary>Microsoft's release metadata and download server.</summary>
internal sealed class MicrosoftReleaseSource : IReleaseSource, IDisposable
{
    /// <summary>Client for all requests; an SDK archive takes minutes on a slow line.</summary>
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <inheritdoc/>
    public string Metadata(string channel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json");
        using var response = _client.Send(request);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return reader.ReadToEnd();
    }

    /// <inheritdoc/>
    public void Download(Uri url, Stream destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        response.Content.ReadAsStream().CopyTo(destination);
    }

    /// <summary>Releases the client.</summary>
    public void Dispose() => _client.Dispose();
}
