using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>
/// Toolchains are installed side by side, each into a directory that is never changed afterwards,
/// from archives whose hash is in the lock; a runtime archive adds only its shared frameworks.
/// </summary>
public sealed class ToolchainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdetta-toolchains-" + Guid.NewGuid().ToString("N"));
    private readonly FakeReleases _releases = new();

    private string LockPath => Path.Combine(_root, "toolchains.lock.json");

    private static Toolchain Pinned => new("10.0.401", ["8.0.31"], "arm64");

    public ToolchainTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Installs_the_sdk_with_the_shared_frameworks_of_its_runtimes_and_locks_every_rid()
    {
        var directory = Installer(locked: false).Install(Pinned);

        Assert.Equal(Path.Combine(_root, Pinned.Rid, "sdk-10.0.401+8.0.31"), directory);
        Assert.Equal("host 10.0.12", File.ReadAllText(Path.Combine(directory, "dotnet")));
        Assert.True(File.Exists(Path.Combine(directory, "shared", "Microsoft.NETCore.App", "10.0.12", "System.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "shared", "Microsoft.NETCore.App", "8.0.31", "System.Runtime.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "shared", "Microsoft.AspNetCore.App", "8.0.31", "Microsoft.AspNetCore.dll")));
        // The runtime archive's host and hostfxr never replace the SDK's.
        Assert.False(Directory.Exists(Path.Combine(directory, "host", "fxr", "8.0.31")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(directory, "dotnet")).HasFlag(UnixFileMode.UserExecute));
        }

        var archives = JsonDocument.Parse(File.ReadAllText(LockPath)).RootElement.GetProperty("archives");
        foreach (var rid in Toolchain.SupportedRids)
        {
            Assert.True(archives.TryGetProperty($"sdk/10.0.401/{rid}", out _), rid);
            Assert.True(archives.TryGetProperty($"aspnetcore-runtime/8.0.31/{rid}", out _), rid);
        }

        Assert.Empty(Directory.GetDirectories(_root, ".partial-*"));
    }

    [Fact]
    public void An_installed_toolchain_is_used_as_it_is()
    {
        var first = Installer(locked: false).Install(Pinned);
        var downloads = _releases.Downloads;

        var second = Installer(locked: true).Install(Pinned);

        Assert.Equal(first, second);
        Assert.Equal(downloads, _releases.Downloads);
    }

    [Fact]
    public void An_archive_whose_hash_differs_from_the_lock_is_not_installed()
    {
        // The same architecture as Pinned: the machine's may differ (x64 on CI, arm64 on this Mac).
        Installer(locked: false).Install(Pinned with { Runtimes = [] });
        File.WriteAllText(LockPath, File.ReadAllText(LockPath).Replace(_releases.Hash($"dotnet-sdk-{Pinned.Rid}.tar.gz"), new string('0', 128), StringComparison.Ordinal));
        Directory.Delete(Path.Combine(_root, Pinned.Rid), recursive: true);

        var error = Assert.Throws<InvalidOperationException>(() => Installer(locked: true).Install(Pinned));

        Assert.Contains("SHA-512", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, Pinned.Rid, Pinned.DirectoryName)));
        Assert.Empty(Directory.GetDirectories(_root, ".partial-*"));
    }

    [Fact]
    public void Locked_mode_refuses_a_version_missing_from_the_lock()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Installer(locked: true).Install(Pinned));

        Assert.Contains("CI=true", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _releases.Downloads);
        Assert.False(File.Exists(LockPath));
    }

    [Fact]
    public void Archives_are_downloaded_only_from_microsofts_server()
    {
        File.WriteAllText(LockPath, $$"""
            { "version": 1, "archives": { "sdk/10.0.401/{{Pinned.Rid}}": { "url": "https://example.org/dotnet-sdk.tar.gz", "sha512": "{{new string('0', 128)}}" } } }
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Installer(locked: true).Install(Pinned with { Runtimes = [] }));

        Assert.Contains("builds.dotnet.microsoft.com", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _releases.Downloads);
    }

    [Fact]
    public void Two_installs_of_one_toolchain_at_once_both_get_the_complete_one()
    {
        Directory.Delete(Installer(locked: false).Install(Pinned), recursive: true);

        var results = new string[2];
        Parallel.For(0, 2, i => results[i] = Installer(locked: true).Install(Pinned));

        Assert.Equal(results[0], results[1]);
        Assert.True(File.Exists(Path.Combine(results[0], "shared", "Microsoft.AspNetCore.App", "8.0.31", "Microsoft.AspNetCore.dll")));
        Assert.Empty(Directory.GetDirectories(_root, ".partial-*"));
    }

    [Theory]
    [InlineData(true, "arm64")]
    [InlineData(true, "x64")]
    [InlineData(false, "arm64")]
    [InlineData(false, "x64")]
    public void Every_platform_a_toolchain_can_run_on_is_supported_and_locked(bool macOS, string architecture)
    {
        // A toolchain pinned to arm64 resolved to linux-arm64 on Linux CI, which was neither supported
        // nor locked; only the Mac's identifiers were ever exercised locally.
        var rid = Toolchain.RidFor(macOS, architecture);
        Assert.Contains(rid, Toolchain.SupportedRids);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "eval", "toolchains.lock.json")))
        {
            root = root.Parent ?? throw new InvalidOperationException("eval/toolchains.lock.json not found above the test output");
        }

        var archives = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "eval", "toolchains.lock.json"))).RootElement.GetProperty("archives");
        Assert.True(archives.TryGetProperty($"sdk/10.0.401/{rid}", out _), rid);
        Assert.True(archives.TryGetProperty($"aspnetcore-runtime/8.0.31/{rid}", out _), rid);
    }

    [Theory]
    [InlineData("10.0.401", "arm64", true)]
    [InlineData("11.0.100-preview.1.25120.13", null, true)]
    [InlineData("../../etc", null, false)]
    [InlineData("10.0", null, false)]
    [InlineData("10.0.401", "x86", false)]
    public void Versions_and_architectures_are_checked(string sdk, string? architecture, bool valid) =>
        Assert.Equal(valid, new Toolchain(sdk, [], architecture).IsValid(out _));

    [Fact]
    public void Directory_name_does_not_depend_on_the_order_of_runtimes() =>
        Assert.Equal(
            new Toolchain("10.0.401", ["9.0.12", "8.0.31"]).DirectoryName,
            new Toolchain("10.0.401", ["8.0.31", "9.0.12", "8.0.31"]).DirectoryName);

    private ToolchainInstaller Installer(bool locked) =>
        new(_root, new ToolchainLock(LockPath, locked), _releases, NullLogger.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Release metadata and archives of SDK 10.0.401 (runtime 10.0.12) and ASP.NET Core runtime 8.0.31.</summary>
    private sealed class FakeReleases : IReleaseSource
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private int _downloads;

        public FakeReleases()
        {
            foreach (var rid in Toolchain.SupportedRids)
            {
                _files[$"dotnet-sdk-{rid}.tar.gz"] = Archive(
                    ("dotnet", "host 10.0.12"),
                    ("host/fxr/10.0.12/libhostfxr", "fxr 10.0.12"),
                    ("sdk/10.0.401/dotnet.dll", "sdk"),
                    ("shared/Microsoft.NETCore.App/10.0.12/System.Runtime.dll", "runtime 10.0.12"),
                    ("shared/Microsoft.AspNetCore.App/10.0.12/Microsoft.AspNetCore.dll", "aspnetcore 10.0.12"));
                _files[$"aspnetcore-runtime-{rid}.tar.gz"] = Archive(
                    ("dotnet", "host 8.0.31"),
                    ("host/fxr/8.0.31/libhostfxr", "fxr 8.0.31"),
                    ("shared/Microsoft.NETCore.App/8.0.31/System.Runtime.dll", "runtime 8.0.31"),
                    ("shared/Microsoft.AspNetCore.App/8.0.31/Microsoft.AspNetCore.dll", "aspnetcore 8.0.31"));
            }
        }

        public int Downloads => Volatile.Read(ref _downloads);

        public string Hash(string name) => Convert.ToHexStringLower(SHA512.HashData(_files[name]));

        public string Metadata(string channel)
        {
            object Files(string prefix) => Toolchain.SupportedRids.Select(rid => new
            {
                name = $"{prefix}-{rid}.tar.gz",
                rid,
                url = $"https://builds.dotnet.microsoft.com/fake/{prefix}-{rid}.tar.gz",
                hash = Hash($"{prefix}-{rid}.tar.gz").ToUpperInvariant(),
            }).ToList();

            object release = channel == "10.0"
                ? new { sdks = new[] { new { version = "10.0.401", files = Files("dotnet-sdk") } } }
                : new Dictionary<string, object> { ["aspnetcore-runtime"] = new { version = "8.0.31", files = Files("aspnetcore-runtime") } };
            return JsonSerializer.Serialize(new { releases = new[] { release } });
        }

        public void Download(Uri url, Stream destination)
        {
            Interlocked.Increment(ref _downloads);
            destination.Write(_files[Path.GetFileName(url.AbsolutePath)]);
        }

        private static byte[] Archive(params (string Path, string Text)[] entries)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var (path, text) in entries)
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    };
                    tar.WriteEntry(entry);
                }
            }

            return buffer.ToArray();
        }
    }
}

/// <summary>A toolchain scope points the process environment at the toolchain and puts it back afterwards.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class ToolchainScopeTests
{
    [Fact]
    public void Scope_sets_the_toolchain_environment_and_restores_it()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var path = Environment.GetEnvironmentVariable("PATH");
        var directory = Path.Combine(Path.GetTempPath(), "toolchain");

        using (new ToolchainScope(directory, "x64"))
        {
            Assert.Equal(directory, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            Assert.Equal(directory, Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
            Assert.Equal(Path.Combine(directory, "dotnet"), Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"));
            Assert.StartsWith(directory + Path.PathSeparator, Environment.GetEnvironmentVariable("PATH"), StringComparison.Ordinal);
            Assert.Equal("1", Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE"));
        }

        Assert.Equal(root, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Assert.Equal(path, Environment.GetEnvironmentVariable("PATH"));
    }
}
