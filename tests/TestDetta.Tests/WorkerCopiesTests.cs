using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Eval;

namespace TestDetta.Tests;

/// <summary>Copies of a built clone, one per extra worker of a parallel evaluation.</summary>
public sealed class WorkerCopiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "testdetta-worker-copies-" + Guid.NewGuid().ToString("N"));

    private string Clone => Path.Combine(_root, "repo");

    [Fact]
    public void Copies_the_clone_next_to_it_once_per_extra_worker_and_removes_them()
    {
        Directory.CreateDirectory(Path.Combine(Clone, "bin"));
        File.WriteAllText(Path.Combine(Clone, "bin", "Tests.dll"), "binary");
        File.WriteAllText(Path.Combine(Clone, "readonly.txt"), "object");
        File.SetAttributes(Path.Combine(Clone, "readonly.txt"), FileAttributes.ReadOnly);
        File.CreateSymbolicLink(Path.Combine(Clone, "link.dll"), "bin/Tests.dll");

        string[] roots;
        string[] temporary;
        using (var copies = WorkerCopies.Create(Clone, 3, [Path.Combine(Clone, "bin")], NullLogger.Instance))
        {
            Assert.NotNull(copies);
            roots = [.. copies.Roots];
            Assert.Equal([Clone, Clone + ".worker-2", Clone + ".worker-3"], roots);
            foreach (var copy in roots.Skip(1))
            {
                Assert.Equal("binary", File.ReadAllText(Path.Combine(copy, "bin", "Tests.dll")));
                Assert.Equal("bin/Tests.dll", new FileInfo(Path.Combine(copy, "link.dll")).LinkTarget);
            }

            // Every worker has its own temporary directory, the clone's included.
            temporary = [.. copies.Temporary];
            Assert.Equal(3, temporary.Distinct().Count());
            Assert.All(temporary, static t => Assert.True(Directory.Exists(t)));
            Assert.Equal(temporary[1] + Path.DirectorySeparatorChar, copies.Environment(1)["TMPDIR"]);
            // Tests compare temporary paths with their ToUpper(); a Turkish culture must not change them.
            var turkish = new System.Globalization.CultureInfo("tr-TR");
            Assert.All(temporary, t => Assert.Equal(t.ToUpperInvariant(), t.ToUpper(turkish)));

            // A write in one copy stays there.
            File.WriteAllText(Path.Combine(roots[1], "bin", "Tests.dll"), "changed");
            Assert.Equal("binary", File.ReadAllText(Path.Combine(Clone, "bin", "Tests.dll")));
        }

        Assert.True(Directory.Exists(Clone));
        Assert.All(roots.Skip(1), static copy => Assert.False(Directory.Exists(copy)));
        Assert.All(temporary, static t => Assert.False(Directory.Exists(t)));
    }

    [Fact]
    public void Replaces_a_copy_left_behind_by_an_earlier_run()
    {
        Directory.CreateDirectory(Clone);
        File.WriteAllText(Path.Combine(Clone, "a.txt"), "new");
        Directory.CreateDirectory(Clone + ".worker-2");
        File.WriteAllText(Path.Combine(Clone + ".worker-2", "stale.txt"), "old");

        using var copies = WorkerCopies.Create(Clone, 2, [], NullLogger.Instance);

        Assert.False(File.Exists(Path.Combine(Clone + ".worker-2", "stale.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Clone + ".worker-2", "a.txt")));
    }

    [Fact]
    public void Makes_no_copies_when_a_test_output_lies_outside_the_clone()
    {
        Directory.CreateDirectory(Clone);

        var copies = WorkerCopies.Create(Clone, 2, [Path.Combine(_root, "artifacts", "bin")], NullLogger.Instance);

        Assert.Null(copies);
        Assert.False(Directory.Exists(Clone + ".worker-2"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
