using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// Visual Basic is analysed like C# (docs/design/vb-fsharp-support.md): changes are found per member with
/// the same identities, the project's root namespace goes before the file's namespaces, names bind
/// ignoring case, and a change to a Visual Basic library selects only the tests that run or name it.
/// </summary>
public sealed class VisualBasicTests : IDisposable
{
    private const string Calculator = """
        Namespace Calc
            Public Class Calculator
                Private ReadOnly _rate As Decimal = 0.2D

                Public Sub New(rate As Decimal)
                    _rate = rate
                End Sub

                Public Function Add(a As Integer, b As Integer) As Integer
                    Return a + b
                End Function

                Public Function Tax(net As Decimal) As Decimal
                    Return net * _rate
                End Function

                Public Shared Operator +(left As Calculator, right As Calculator) As Calculator
                    Return left
                End Operator

                Public Property Name As String
            End Class

            Public Module Helpers
                Public Function Twice(x As Integer) As Integer
                    Return x * 2
                End Function
            End Module
        End Namespace
        """;

    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Members_are_found_with_the_root_namespace_and_csharp_identities()
    {
        var change = Detect(Calculator, Calculator.Replace("Return a + b", "Return b + a", StringComparison.Ordinal), rootNamespace: "Shop");

        var symbol = Assert.Single(change.Symbols);
        Assert.Equal(new SymbolChange("Shop.Calc.Calculator", "Add(Integer,Integer)", SymbolChangeKind.Modified), symbol with { MetadataChanged = false });
        Assert.Equal(["Add"], DeclarationWalker.CompiledNames(symbol.MemberName!));
    }

    [Fact]
    public void Constructors_operators_and_module_members_get_compiled_names()
    {
        var changed = Calculator
            .Replace("_rate = rate", "_rate = rate * 1D", StringComparison.Ordinal)
            .Replace("Return left", "Return right", StringComparison.Ordinal)
            .Replace("Return x * 2", "Return x + x", StringComparison.Ordinal);

        var symbols = Detect(Calculator, changed, rootNamespace: string.Empty).Symbols.Select(static s => (s.TypeName, s.MemberName)).ToList();

        Assert.Contains(("Calc.Calculator", ".ctor(Decimal)"), symbols);
        Assert.Contains(("Calc.Calculator", "op_Addition(Calculator,Calculator)"), symbols);
        Assert.Contains(("Calc.Helpers", "Twice(Integer)"), symbols);
        Assert.Equal([".ctor"], DeclarationWalker.CompiledNames(".ctor(Decimal)"));
        Assert.Equal(["op_Addition"], DeclarationWalker.CompiledNames("op_Addition(Calculator,Calculator)"));
    }

    [Fact]
    public void Case_comments_and_whitespace_are_cosmetic()
    {
        var changed = Calculator
            .Replace("Return a + b", "return   a+b ' sum", StringComparison.Ordinal)
            .Replace("Public Function Tax", "PUBLIC FUNCTION Tax", StringComparison.Ordinal);

        Assert.True(Detect(Calculator, changed, "Shop").IsCosmetic);
    }

    [Fact]
    public void An_attribute_change_is_a_metadata_change()
    {
        var changed = Calculator.Replace("Public Function Add", "<System.Obsolete> Public Function Add", StringComparison.Ordinal);

        var symbol = Assert.Single(Detect(Calculator, changed, "Shop").Symbols);
        Assert.True(symbol.MetadataChanged);
    }

    [Fact]
    public void A_global_namespace_ignores_the_root_namespace()
    {
        var before = "Namespace Global.Other\n Class Thing\n Sub Run()\n End Sub\n End Class\nEnd Namespace";
        var symbol = Assert.Single(Detect(before, before.Replace("Sub Run()\n", "Sub Run()\n Dim x = 1\n", StringComparison.Ordinal), "Shop").Symbols);

        Assert.Equal("Other.Thing", symbol.TypeName);
    }

    [Fact]
    public void A_changed_visual_basic_member_selects_the_test_classes_that_name_it_in_any_case()
    {
        WriteLibraryAndTests();
        _repo.Replace("src/Calc/Calculator.vb", "Return a + b", "Return b + a");

        var selection = Select();

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.AddTests"], selection.TestClasses);
    }

    [Fact]
    public void A_visual_basic_test_class_that_names_a_member_in_another_case_is_selected()
    {
        WriteLibraryAndTests();
        _repo.Write("tests/Shop.VbTests/Shop.VbTests.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><RootNamespace>Shop.VbTests</RootNamespace></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.vbproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.VbTests/TaxTests.vb", """
            Imports Xunit
            Public Class TaxTests
                <Fact>
                Public Sub Taxes()
                    Assert.Equal(2D, New Shop.Calc.Calculator(0.2D).TAX(10D))
                End Sub
            End Class
            Public Class OtherTests
                <Fact>
                Public Sub Nothing_here()
                End Sub
            End Class
            """);
        _repo.Commit("vb tests");
        _repo.Replace("src/Calc/Calculator.vb", "Return net * _rate", "Return _rate * net");

        var selection = new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests
            .Single(static t => t.Project.Path.EndsWith("Shop.VbTests.vbproj", StringComparison.Ordinal));

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.VbTests.TaxTests"], selection.TestClasses);
    }

    private void WriteLibraryAndTests()
    {
        _repo.Write("src/Calc/Calc.vbproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Shop</RootNamespace></PropertyGroup></Project>
            """);
        _repo.Write("src/Calc/Calculator.vb", Calculator);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.vbproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", """
            namespace Shop.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Shop.Calc.Calculator(0.2m).Add(1, 2)); }
            public class TwiceTests { [Fact] public void Doubles() => Assert.Equal(4, Shop.Calc.Helpers.Twice(2)); }
            """);
        _repo.Commit("base");
    }

    private TestProjectSelection Select() =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests
            .Single(static t => t.Project.Path.EndsWith("Shop.Tests.csproj", StringComparison.Ordinal));

    private static CodeChange Detect(string before, string after, string rootNamespace) =>
        new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
            .Detect("src/Calc/Calculator.vb", SourceText.From(before), SourceText.From(after), [], rootNamespace);
}
