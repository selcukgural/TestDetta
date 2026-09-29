using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sift.Core;

namespace Sift.Tests;

public sealed class CodeChangeDetectorTests
{
    private const string Original = """
        using System;

        namespace App.Orders;

        /// <summary>Places orders.</summary>
        public class OrderService
        {
            private readonly int _limit = 10;

            public OrderService() { }

            public int Place(string sku, int quantity) => quantity;

            public int Place(string sku) => 1;

            public string Name { get; } = "orders";
        }

        public enum Status { Open, Closed }
        """;

    private readonly CodeChangeDetector _detector = new(NullLogger<CodeChangeDetector>.Instance);

    [Fact]
    public void Comment_and_whitespace_edits_are_cosmetic()
    {
        var edited = Original
            .Replace("/// <summary>Places orders.</summary>", "/// <summary>Places and tracks orders.</summary>")
            .Replace("=> quantity;", "=>   quantity; // echo")
            .Replace("public enum Status { Open, Closed }", "public enum Status\n{\n    Open,\n    Closed\n}");

        var change = Detect(Original, edited);

        Assert.True(change.IsCosmetic);
        Assert.Empty(change.Symbols);
    }

    [Fact]
    public void Body_change_reports_only_that_overload()
    {
        var change = Detect(Original, Original.Replace("=> quantity;", "=> quantity * 2;"));

        Assert.False(change.IsCosmetic);
        Assert.Equal([new SymbolChange("App.Orders.OrderService", "Place(string,int)", SymbolChangeKind.Modified)], change.Symbols);
    }

    [Fact]
    public void Added_and_removed_members_are_reported()
    {
        var edited = Original
            .Replace("public int Place(string sku) => 1;", "public void Cancel(Guid id) { }")
            .Replace("private readonly int _limit = 10;", "");

        var change = Detect(Original, edited);

        Assert.Equal(
            [
                new SymbolChange("App.Orders.OrderService", "Place(string)", SymbolChangeKind.Removed),
                new SymbolChange("App.Orders.OrderService", "_limit", SymbolChangeKind.Removed),
                new SymbolChange("App.Orders.OrderService", "Cancel(Guid)", SymbolChangeKind.Added),
            ],
            change.Symbols.OrderByDescending(s => s.Kind == SymbolChangeKind.Removed).ThenByDescending(s => s.MemberName));
    }

    [Fact]
    public void Type_declaration_change_is_a_type_level_change()
    {
        var change = Detect(Original, Original.Replace("public class OrderService", "public sealed class OrderService : IDisposable"));

        Assert.Equal([new SymbolChange("App.Orders.OrderService", null, SymbolChangeKind.Modified)], change.Symbols);
    }

    [Fact]
    public void Added_enum_member_is_reported()
    {
        var change = Detect(Original, Original.Replace("Open, Closed", "Open, Closed, Archived"));

        Assert.Contains(new SymbolChange("App.Orders.Status", "Archived", SymbolChangeKind.Added), change.Symbols);
    }

    [Fact]
    public void Preprocessor_change_is_not_cosmetic()
    {
        var withDirective = Original.Replace("public int Place(string sku) => 1;", "#if DEBUG\n    public int Place(string sku) => 1;\n#endif");

        var change = Detect(Original, withDirective);

        Assert.False(change.IsCosmetic);
        Assert.NotEmpty(change.Symbols);
    }

    [Fact]
    public void Using_change_marks_every_type_in_the_file()
    {
        var change = Detect(Original, Original.Replace("using System;", "using System;\nusing System.Linq;"));

        Assert.False(change.AffectsWholeProject);
        Assert.Equal(
            ["App.Orders.OrderService", "App.Orders.Status"],
            change.Symbols.Where(s => s is { MemberName: null, Kind: SymbolChangeKind.Modified }).Select(s => s.TypeName).Order());
    }

    [Fact]
    public void Global_using_or_assembly_attribute_affects_the_whole_project()
    {
        Assert.True(Detect(Original, "global using System.Text;\n" + Original).AffectsWholeProject);
        Assert.True(Detect(Original, Original.Replace("using System;", "using System;\n[assembly: System.CLSCompliant(false)]")).AffectsWholeProject);
    }

    [Fact]
    public void Added_file_reports_its_types_without_their_members()
    {
        var change = Detect(null, Original);

        Assert.Equal(
            [
                new SymbolChange("App.Orders.OrderService", null, SymbolChangeKind.Added),
                new SymbolChange("App.Orders.Status", null, SymbolChangeKind.Added),
            ],
            change.Symbols);
    }

    [Fact]
    public void Deleted_file_reports_its_types_as_removed()
    {
        Assert.All(Detect(Original, null).Symbols, s => Assert.Equal(SymbolChangeKind.Removed, s.Kind));
    }

    [Fact]
    public void Nested_and_generic_types_get_qualified_names()
    {
        const string before = "namespace N { class Outer { class Box<T> { int M() => 1; } } }";
        var change = Detect(before, before.Replace("=> 1", "=> 2"));

        Assert.Equal([new SymbolChange("N.Outer.Box`1", "M()", SymbolChangeKind.Modified)], change.Symbols);
    }

    [Fact]
    public void Top_level_statement_change_marks_the_program_type()
    {
        var change = Detect("""Console.WriteLine("a");""", """Console.WriteLine("b");""");

        Assert.Equal([new SymbolChange("Program", null, SymbolChangeKind.Modified)], change.Symbols);
    }

    private CodeChange Detect(string? before, string? after) =>
        _detector.Detect("File.cs", before is null ? null : SourceText.From(before), after is null ? null : SourceText.From(after));
}
