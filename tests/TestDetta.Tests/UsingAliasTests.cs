using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// A test class that names a type only through a using alias of its file
/// (<c>using CodeFixTest = Harness.CodeFixTest&lt;MyAnalyzer, MyFixer&gt;;</c>) uses that type all the same: name-only
/// selection sees it (meziantou-analyzer, round runs-full-1006, mutants 18 and 27). The alias is the file's own: a
/// class in another file with an alias of the same name for another type is not selected.
/// </summary>
public sealed class UsingAliasTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_type_named_only_in_a_using_alias_selects_the_classes_of_that_file_that_use_the_alias()
    {
        WriteLibrary();
        _repo.Write("tests/T/T.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/T/Harness.cs", "namespace Harness; public class CodeFixTest<TAnalyzer, TFixer> { public void Run() { } }");
        _repo.Write("tests/T/MyAnalyzerTests.cs", """
            using CodeFixTest = Harness.CodeFixTest<
                Rules.MyAnalyzer,
                Rules.MyFixer>;

            namespace T;

            public sealed class MyAnalyzerTests
            {
                private static CodeFixTest CreateTest() => new();

                [Fact]
                public void Reports() => CreateTest().Run();
            }
            """);
        _repo.Write("tests/T/OtherAnalyzerTests.cs", """
            namespace T
            {
                using CodeFixTest = Harness.CodeFixTest<Rules.OtherAnalyzer, Rules.MyFixer>;

                public sealed class OtherAnalyzerTests
                {
                    [Fact]
                    public void Reports() => new CodeFixTest().Run();
                }
            }
            """);
        _repo.Commit("base");
        _repo.Replace("src/Lib/MyAnalyzer.cs", "=> 1", "=> 2");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["T.MyAnalyzerTests"], selection.TestClasses);
    }

    [Fact]
    public void A_visual_basic_import_alias_works_the_same_way()
    {
        WriteLibrary();
        _repo.Write("tests/VbT/VbT.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><RootNamespace>VbT</RootNamespace></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/VbT/Harness.vb", """
            Public Class CodeFixTest(Of TAnalyzer, TFixer)
                Public Sub Run()
                End Sub
            End Class
            """);
        _repo.Write("tests/VbT/MyAnalyzerTests.vb", """
            Imports Xunit
            Imports Verifier = VbT.CodeFixTest(Of Rules.MyAnalyzer, Rules.MyFixer)
            Public Class MyAnalyzerTests
                <Fact>
                Public Sub Reports()
                    Dim test As New Verifier()
                    test.Run()
                End Sub
            End Class
            Public Class PlainTests
                <Fact>
                Public Sub Nothing_here()
                End Sub
            End Class
            """);
        _repo.Commit("base");
        _repo.Replace("src/Lib/MyAnalyzer.cs", "=> 1", "=> 2");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests.Single(static t => t.Project.IsTestProject);

        Assert.True(selection.Mode == TestRunMode.Filtered, selection.Reason);
        Assert.Equal(["VbT.MyAnalyzerTests"], selection.TestClasses);
    }

    private void WriteLibrary()
    {
        _repo.Write("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Lib/MyAnalyzer.cs", "namespace Rules; public class MyAnalyzer { public int Analyze() => 1; }");
        _repo.Write("src/Lib/OtherAnalyzer.cs", "namespace Rules; public class OtherAnalyzer { public int Analyze() => 1; }");
        _repo.Write("src/Lib/MyFixer.cs", "namespace Rules; public class MyFixer { public int Fix() => 1; }");
        _repo.Write(".gitignore", ".testdetta/\n");
    }
}
