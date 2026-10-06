using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

/// <summary>
/// F# is analysed like C# (docs/design/vb-fsharp-support.md): changes are found per declaration with the
/// names the compiler gives them (modules as static classes, let functions as their members), indentation
/// counts, comments do not, and a change to an F# library selects only the tests that run or name it.
/// </summary>
public sealed class FSharpTests : IDisposable
{
    private const string Calculator = """
        namespace Shop.Calc

        open System

        type Calculator(rate: decimal) =
            let mutable calls = 0

            member _.Add(a: int, b: int) = a + b

            member _.Tax(net: decimal) = net * rate

            member _.Tax(net: decimal, extra: decimal) = net * rate + extra

            member val Name = "calc" with get, set

            static member Zero = Calculator(0m)

        type Calculator2() =
            member _.Run() = ()

        module Helpers =
            let twice x = x * 2

            let inline half x = x / 2

            let limit = 100

            module Nested =
                let deep () = 1

        [<RequireQualifiedAccess>]
        module Calculator =
            let create () = Calculator(0.2m)
        """;

    private readonly GitRepoFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Members_of_types_and_modules_get_compiled_names()
    {
        var changed = Calculator
            .Replace("a + b", "b + a", StringComparison.Ordinal)
            .Replace("x * 2", "x + x", StringComparison.Ordinal)
            .Replace("let deep () = 1", "let deep () = 2", StringComparison.Ordinal)
            .Replace("Calculator(0.2m)", "Calculator(0.3m)", StringComparison.Ordinal);

        var symbols = Detect(Calculator, changed).Symbols.Select(static s => (s.TypeName, s.MemberName)).ToList();

        Assert.Equal(4, symbols.Count);
        Assert.Contains(("Shop.Calc.Calculator", "Add(a:int,b:int)"), symbols);
        Assert.Contains(("Shop.Calc.Helpers", "twice"), symbols);
        Assert.Contains(("Shop.Calc.Helpers.Nested", "deep"), symbols);

        // A module beside a type of its name compiles with "Module" after its name.
        Assert.Contains(("Shop.Calc.CalculatorModule", "create"), symbols);
        Assert.Equal(["Add"], DeclarationWalker.CompiledNames("Add(a:int,b:int)"));
    }

    [Fact]
    public void Overloads_are_told_apart_by_their_arguments()
    {
        var symbol = Assert.Single(Detect(Calculator, Calculator.Replace("net * rate + extra", "extra + net * rate", StringComparison.Ordinal)).Symbols);

        Assert.Equal("Tax(net:decimal,extra:decimal)", symbol.MemberName);
        Assert.Equal(["Tax"], DeclarationWalker.CompiledNames(symbol.MemberName!));
    }

    [Fact]
    public void The_primary_constructor_and_class_lets_are_constructor_code()
    {
        var symbols = Detect(Calculator, Calculator.Replace("let mutable calls = 0", "let mutable calls = 1", StringComparison.Ordinal)).Symbols;

        var symbol = Assert.Single(symbols);
        Assert.Equal(("Shop.Calc.Calculator", "calls"), (symbol.TypeName, symbol.MemberName));
        Assert.Equal(SymbolChangeKind.Modified, symbol.Kind);
    }

    [Fact]
    public void Comments_and_spacing_within_a_line_are_cosmetic()
    {
        var changed = Calculator
            .Replace("a + b", "a+b // sum", StringComparison.Ordinal)
            .Replace("open System", "open System (* needed *)", StringComparison.Ordinal);

        Assert.True(Detect(Calculator, changed).IsCosmetic);
    }

    [Fact]
    public void Indentation_is_not_cosmetic()
    {
        const string before = "module Shop.Steps\n\nlet run () =\n    let a = 1\n    a\n\nlet other () = 2\n";
        const string after = "module Shop.Steps\n\nlet run () =\n    let a = 1\n    a\n\n    let other () = 2\n";

        Assert.False(Detect(before, after).IsCosmetic);
    }

    [Fact]
    public void An_inline_function_is_marked_inlined()
    {
        var symbol = Assert.Single(Detect(Calculator, Calculator.Replace("x / 2", "x / 3", StringComparison.Ordinal)).Symbols);

        Assert.Equal(("Shop.Calc.Helpers", "half"), (symbol.TypeName, symbol.MemberName));
        Assert.True(symbol.Inlined);
    }

    [Fact]
    public void An_attribute_change_is_a_metadata_change()
    {
        var changed = Calculator.Replace("    member _.Add", "    [<Obsolete>]\n    member _.Add", StringComparison.Ordinal);

        var symbol = Assert.Single(Detect(Calculator, changed).Symbols);
        Assert.Equal("Add(a:int,b:int)", symbol.MemberName);
        Assert.True(symbol.MetadataChanged);
    }

    [Fact]
    public void A_changed_open_marks_every_type_of_the_file()
    {
        var symbols = Detect(Calculator, Calculator.Replace("open System", "open System.IO", StringComparison.Ordinal)).Symbols;

        Assert.All(symbols, static s => Assert.Null(s.MemberName));
        Assert.Contains(symbols, static s => s.TypeName == "Shop.Calc.Calculator");
        Assert.Contains(symbols, static s => s.TypeName == "Shop.Calc.Helpers");
    }

    [Fact]
    public void An_added_type_is_reported_once()
    {
        var symbols = Detect(Calculator, Calculator + "\ntype Extra() =\n    member _.Go() = 1\n").Symbols;

        Assert.Equal([new SymbolChange("Shop.Calc.Extra", null, SymbolChangeKind.Added)], symbols);
    }

    [Fact]
    public void A_file_without_a_header_is_an_implicit_module_named_after_the_file()
    {
        var symbol = Assert.Single(new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
            .Detect("src/Calc/tools.fs", SourceText.From("let go () = 1\n"), SourceText.From("let go () = 2\n")).Symbols);

        Assert.Equal(("Tools", "go"), (symbol.TypeName, symbol.MemberName));
    }

    [Fact]
    public void An_assembly_attribute_affects_the_whole_project()
    {
        const string before = "namespace Shop\n\n[<assembly: System.CLSCompliant(true)>]\ndo ()\n";

        Assert.True(Detect(before, before.Replace("true", "false", StringComparison.Ordinal)).AffectsWholeProject);
    }

    [Fact]
    public void Code_under_an_if_counts_when_its_symbol_is_defined()
    {
        const string before = "module Shop.Steps\n\n#if NET8_0_OR_GREATER\nlet modern () = 1\n#endif\n";

        var symbol = Assert.Single(Detect(before, before.Replace("= 1", "= 2", StringComparison.Ordinal)).Symbols);
        Assert.Equal(("Shop.Steps", "modern"), (symbol.TypeName, symbol.MemberName));
    }

    [Fact]
    public void A_changed_fsharp_function_selects_the_csharp_test_classes_that_name_it()
    {
        WriteLibraryAndTests();
        _repo.Replace("src/Calc/Calculator.fs", "x * 2", "x + x");

        var selection = Select("Shop.Tests.csproj");

        Assert.Equal(TestRunMode.Filtered, selection.Mode);
        Assert.Equal(["Shop.Tests.TwiceTests"], selection.TestClasses);
    }

    [Fact]
    public void An_fsharp_test_module_that_names_a_member_is_selected()
    {
        WriteLibraryAndTests();
        _repo.Replace("src/Calc/Calculator.fs", "net * rate\n", "rate * net\n");

        var selection = Select("Shop.FsTests.fsproj");

        Assert.True(selection.Mode == TestRunMode.Filtered, selection.Reason);
        Assert.Equal(["Shop.FsTests.TaxTests"], selection.TestClasses);
    }

    private void WriteLibraryAndTests()
    {
        _repo.Write("src/Calc/Calc.fsproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="Calculator.fs" /></ItemGroup>
            </Project>
            """);
        _repo.Write("src/Calc/Calculator.fs", Calculator);
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.fsproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", """
            namespace Shop.Tests;
            public class AddTests { [Fact] public void Adds() => Assert.Equal(3, new Shop.Calc.Calculator(0.2m).Add(1, 2)); }
            public class TwiceTests { [Fact] public void Doubles() => Assert.Equal(4, Shop.Calc.Helpers.twice(2)); }
            """);
        _repo.Write("tests/Shop.FsTests/Shop.FsTests.fsproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Tests.fs" />
                <Compile Include="OtherTests.fs" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Calc/Calc.fsproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.FsTests/Tests.fs", """
            module Shop.FsTests.TaxTests

            open Xunit
            open Shop.Calc

            [<Fact>]
            let ``taxes`` () = Assert.Equal(2m, Calculator(0.2m).Tax(10m))
            """);
        _repo.Write("tests/Shop.FsTests/OtherTests.fs", """
            module Shop.FsTests.OtherTests

            open Xunit

            [<Fact>]
            let ``nothing here`` () = Assert.True(true)
            """);
        _repo.Commit("base");
    }

    private TestProjectSelection Select(string project) =>
        new AnalysisPipeline(NullLoggerFactory.Instance).Run(_repo.Root, "main", new AnalysisOptions()).Tests
            .Single(t => t.Project.Path.EndsWith(project, StringComparison.Ordinal));

    private static CodeChange Detect(string before, string after) =>
        new CodeChangeDetector(NullLogger<CodeChangeDetector>.Instance)
            .Detect("src/Calc/Calculator.fs", SourceText.From(before), SourceText.From(after), []);
}
