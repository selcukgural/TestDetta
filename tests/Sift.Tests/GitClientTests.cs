using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

public sealed class GitClientTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();
    private readonly GitClient _git = new(NullLogger<GitClient>.Instance);

    [Fact]
    public void ReadFiles_returns_exact_bytes_and_skips_missing_paths()
    {
        // Non-ASCII name, CRLF, a BOM and an empty file cover the byte-exact parsing of cat-file output.
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. "class A {}\r\n"u8];
        Write("src/Ağaç.cs", withBom);
        Write("src/Empty.cs", []);
        Git("add", "-A");
        Git("commit", "-qm", "base");

        var files = _git.ReadFiles(_repo.Root, "HEAD", ["src/Ağaç.cs", "src/Missing.cs", "src/Empty.cs"]);

        Assert.Equal(withBom, files["src/Ağaç.cs"]);
        Assert.Empty(files["src/Empty.cs"]);
        Assert.False(files.ContainsKey("src/Missing.cs"));
    }

    [Fact]
    public void Reader_marks_comment_only_edit_as_cosmetic_and_code_edit_as_change()
    {
        Write("A.cs", "class A { int M() => 1; }"u8.ToArray());
        Write("B.cs", "class B { int M() => 1; }"u8.ToArray());
        Git("add", "-A");
        Git("commit", "-qm", "base");
        Write("A.cs", "class A { int M() => 1; } // note"u8.ToArray());
        Write("B.cs", "class B { int M() => 2; }"u8.ToArray());

        var reader = new CodeChangeReader(_git, new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance),
            NullLogger<CodeChangeReader>.Instance);
        var changes = reader.Read(_repo.Root, _git.MergeBase(_repo.Root, "main"), _git.ChangedFiles(_repo.Root, "main"));

        Assert.True(changes.Single(c => c.Path == "A.cs").IsCosmetic);
        Assert.Equal([new SymbolChange("B", "M()", SymbolChangeKind.Modified)], changes.Single(c => c.Path == "B.cs").Symbols);
    }

    private void Write(string relative, byte[] content) => _repo.WriteBytes(relative, content);

    private void Git(params string[] args) => _repo.Git(args);

    public void Dispose() => _repo.Dispose();
}
