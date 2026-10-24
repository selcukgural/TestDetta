using System.Diagnostics;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>A throw-away git repository in the temp directory.</summary>
public sealed class GitRepoFixture : IDisposable
{
    public GitRepoFixture()
    {
        Directory.CreateDirectory(Root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
        // The machine-wide config may convert line endings, which would change the committed bytes.
        Git("config", "core.autocrlf", "false");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "testdetta-repo-" + Guid.NewGuid().ToString("N"));

    public void Write(string relative, string content) => WriteBytes(relative, System.Text.Encoding.UTF8.GetBytes(content));

    public void WriteBytes(string relative, byte[] content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void Replace(string relative, string oldValue, string newValue)
    {
        var path = Path.Combine(Root, relative);
        var content = File.ReadAllText(path);
        Assert.Contains(oldValue, content);
        File.WriteAllText(path, content.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    public void Commit(string message = "commit")
    {
        Git("add", "-A");
        Git("commit", "-qm", message);
    }

    public void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
    }

    public void Dispose() => Paths.DeleteTree(Root);
}
