using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Files that reach a project other than through its directory: linked sources, shared projects,
/// imported MSBuild files, and projects in languages TestDetta cannot read. Every case used to select
/// nothing or too little.
/// </summary>
public sealed class BuildInputsTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    public BuildInputsTests()
    {
        _repo.Write("src/Calc/Calculator.cs", "namespace Calc; public class Calculator { public int Add(int a, int b) => a + b; }");
        _repo.Write("src/Other/Other.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Other/Thing.cs", "namespace Other; public class Thing { }");
        _repo.Write("tests/Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.Tests/Tests.cs", """
            namespace Calc.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Calculator().Add(1, 2)); }
            public class FormatTests { [Fact] public void Formats() => Assert.Equal("7", Formatter.Format(7)); }
            public class HelperTests { [Fact] public void Helps() => Assert.True(Helper.Ok()); }
            """);
        _repo.Write("tests/Other.Tests/Other.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Other/Other.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Other.Tests/Tests.cs", "namespace Other.Tests; public class ThingTests { [Fact] public void Exists() => Assert.NotNull(new Thing()); }");
        _repo.Write("src/Shared/Formatter.cs", "namespace Calc; public static class Formatter { public static string Format(int v) => v.ToString(); }");
        _repo.Write("src/Shared/Shared.projitems", """
            <Project>
              <ItemGroup><Compile Include="$(MSBuildThisFileDirectory)Helper.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Shared/Helper.cs", "namespace Calc; public static class Helper { public static bool Ok() => true; }");
        _repo.Write("build/common.props", "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
        _repo.Write("src/Calc/Calc.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\..\build\common.props" />
              <Import Project="../Shared/Shared.projitems" Label="Shared" />
              <ItemGroup><Compile Include="..\Shared\Format*.cs" /></ItemGroup>
            </Project>
            """);
        _repo.Commit("base");
    }

    [Fact]
    public void Linked_source_file_is_traced_to_its_tests()
    {
        _repo.Replace("src/Shared/Formatter.cs", "v.ToString()", "v.ToString(System.Globalization.CultureInfo.InvariantCulture)");

        var tests = Selections();
        Assert.Equal(["Calc.Tests.FormatTests"], tests.Single(t => Name(t) == "Calc.Tests").TestClasses);
        Assert.DoesNotContain(tests, t => Name(t) == "Other.Tests");
    }

    [Fact]
    public void Source_of_a_shared_project_is_traced_to_its_tests()
    {
        _repo.Replace("src/Shared/Helper.cs", "=> true;", "=> 1 == 1;");

        Assert.Equal(["Calc.Tests.HelperTests"], Selection("Calc.Tests").TestClasses);
    }

    [Theory]
    [InlineData("build/common.props")]
    [InlineData("src/Shared/Shared.projitems")]
    public void Imported_build_file_runs_everything_that_imports_it(string file)
    {
        _repo.Replace(file, "<Project>", "<Project><!-- changed --><PropertyGroup><Deterministic>true</Deterministic></PropertyGroup>");

        var calc = Selection("Calc.Tests");
        Assert.Equal(TestRunMode.All, calc.Mode);
        Assert.Contains(file, calc.Reason);
        Assert.DoesNotContain(Selections(), t => Name(t) == "Other.Tests");
    }

    [Fact]
    public void File_imported_by_a_directory_build_props_affects_the_projects_below_it()
    {
        _repo.Write("src/Directory.Build.props", """
            <Project>
              <Import Project="$([MSBuild]::GetPathOfFileAbove('versions.props', '$(MSBuildThisFileDirectory)../'))" />
            </Project>
            """);
        _repo.Write("versions.props", "<Project />");
        _repo.Commit("versions");
        _repo.Replace("versions.props", "<Project />", "<Project><PropertyGroup><Version>2.0</Version></PropertyGroup></Project>");

        // Both libraries sit below src/Directory.Build.props, the test projects do not; they still
        // run completely because what they reference changed how it builds.
        Assert.Equal(TestRunMode.All, Selection("Calc.Tests").Mode);
        Assert.Equal(TestRunMode.All, Selection("Other.Tests").Mode);
    }

    [Fact]
    public void Unresolvable_import_makes_unclaimed_build_files_count()
    {
        _repo.Write("src/Other/Other.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(RepoToolsDir)/signing.props" />
            </Project>
            """);
        _repo.Write("eng/signing.props", "<Project />");
        _repo.Commit("signing");
        _repo.Replace("eng/signing.props", "<Project />", "<Project><PropertyGroup><SignAssembly>true</SignAssembly></PropertyGroup></Project>");

        Assert.Equal(TestRunMode.All, Selection("Other.Tests").Mode);
    }

    [Fact]
    public void Test_project_in_fsharp_is_selected_by_name_like_any_other()
    {
        _repo.Write("tests/Calc.FsTests/Calc.FsTests.fsproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Tests.fs" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Calc.FsTests/Tests.fs", """
            namespace Calc.FsTests

            module AddTests =
                [<Xunit.Fact>]
                let adds () = Xunit.Assert.Equal(3, Calc.Calculator().Add(1, 2))

            module OtherTests =
                [<Xunit.Fact>]
                let other () = ()
            """);
        _repo.Commit("fsharp tests");
        _repo.Replace("src/Calc/Calculator.cs", "a + b", "b + a");

        var fsharp = Selection("Calc.FsTests");
        Assert.Equal(TestRunMode.Filtered, fsharp.Mode);
        Assert.Equal(["Calc.FsTests.AddTests"], fsharp.TestClasses);
        Assert.Equal(["Calc.Tests.AddTests"], Selection("Calc.Tests").TestClasses);
    }

    [Fact]
    public void Build_configuration_wins_over_a_direct_change_in_the_same_change_set()
    {
        // "src/Calc/Calculator.cs" sorts before "src/Directory.Build.props"; the order must not matter.
        _repo.Write("src/Directory.Build.props", "<Project />");
        _repo.Commit("props");
        _repo.Replace("src/Calc/Calculator.cs", "a + b", "b + a");
        _repo.Replace("src/Directory.Build.props", "<Project />", "<Project><PropertyGroup><Optimize>true</Optimize></PropertyGroup></Project>");

        Assert.Equal(TestRunMode.All, Selection("Calc.Tests").Mode);
    }

    private TestProjectSelection Selection(string testProject) => Selections().Single(t => Name(t) == testProject);

    private IReadOnlyList<TestProjectSelection> Selections() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main").Tests;

    private static string Name(TestProjectSelection selection) => Path.GetFileNameWithoutExtension(selection.Project.Path);

    public void Dispose() => _repo.Dispose();
}
