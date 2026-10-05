using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TestDetta.Core;

namespace TestDetta.Tests;

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
    public void Extension_block_members_are_members_of_the_enclosing_class()
    {
        // C# 14 compiles them to static methods of TextExtensions, where a coverage map records them;
        // the block itself has no name, and was read as a nested type called "".
        const string extensions = """
            namespace App;
            public static class TextExtensions
            {
                extension(string text)
                {
                    public bool IsBlank => text.Trim().Length == 0;
                    public string Shout() => text.ToUpperInvariant();
                }

                extension(int number)
                {
                    public string Shout() => number.ToString();
                }
            }
            """;

        var change = Detect(extensions, extensions.Replace("text.ToUpperInvariant()", "text.ToUpperInvariant() + \"!\""));

        Assert.Equal([new SymbolChange("App.TextExtensions", "Shout(string)", SymbolChangeKind.Modified)], change.Symbols);
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

    [Theory]
    [InlineData("public int Place(string sku) => 1;", "[Obsolete] public int Place(string sku) => 1;")]
    [InlineData("public int Place(string sku) => 1;", "public int Place([NotNull] string sku) => 1;")]
    [InlineData("public int Place(string sku, int quantity) => quantity;", "public int Place(string sku, int quantity = 1) => quantity;")]
    [InlineData("public string Name { get; } = \"orders\";", "public string Name { [Pure] get; } = \"orders\";")]
    public void Attribute_or_default_value_change_is_a_metadata_change(string before, string after)
    {
        var change = Detect(Original, Original.Replace(before, after));

        var symbol = Assert.Single(change.Symbols);
        Assert.Equal(SymbolChangeKind.Modified, symbol.Kind);
        Assert.True(symbol.MetadataChanged);
    }

    [Fact]
    public void Body_change_of_an_attributed_member_is_not_a_metadata_change()
    {
        var edited = Original.Replace("public int Place(string sku) => 1;", "[Obsolete] public int Place(string sku) => 1;");

        var symbol = Assert.Single(Detect(edited, edited.Replace("=> 1;", "=> 2;")).Symbols);
        Assert.False(symbol.MetadataChanged);
    }

    [Fact]
    public void Type_attribute_change_is_a_type_level_metadata_change()
    {
        var symbol = Assert.Single(Detect(Original, Original.Replace("public class OrderService", "[Serializable] public class OrderService")).Symbols);

        Assert.Equal(new SymbolChange("App.Orders.OrderService", null, SymbolChangeKind.Modified) { MetadataChanged = true }, symbol);
    }

    [Fact]
    public void Using_change_marks_every_type_in_the_file()
    {
        var change = Detect(Original, Original.Replace("using System;", "using System;\nusing System.Linq;"));

        Assert.False(change.AffectsWholeProject);
        Assert.Equal(
            ["App.Orders.OrderService", "App.Orders.Status"],
            change.Symbols.Where(s => s is { MemberName: null, Kind: SymbolChangeKind.Modified }).Select(s => s.TypeName).Order());
        // A using can change which attribute type a name binds to.
        Assert.All(change.Symbols, static s => Assert.True(s.MetadataChanged));
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

    [Fact]
    public void Change_in_a_file_wrapped_in_a_conditional_reports_the_member()
    {
        // Parsed without symbols the whole file is disabled text; the project defines ROSLYN_4_12.
        const string before = """
            #if ROSLYN_4_12
            namespace App;
            public class Analyzer
            {
                public int Run() => 1;
            #if ROSLYN_5 && NET
                public int Newer() => 1;
            #endif
            }
            #endif
            """;

        var change = Detect(before, before.Replace("Run() => 1", "Run() => 2"));

        Assert.False(change.AffectsWholeProject);
        Assert.Equal([new SymbolChange("App.Analyzer", "Run()", SymbolChangeKind.Modified)], change.Symbols);
        Assert.Equal(
            [new SymbolChange("App.Analyzer", "Newer()", SymbolChangeKind.Modified)],
            Detect(before, before.Replace("Newer() => 1", "Newer() => 2")).Symbols);
    }

    [Fact]
    public void Change_in_the_other_branch_of_a_conditional_reports_the_member()
    {
        const string before = """
            namespace App;
            public class Clock
            {
            #if NET
                public int Now() => 1;
            #else
                public int Now() => 2;
            #endif
            }
            """;

        // A disabled branch is trivia of the next token, which may be the type's closing brace, so the
        // type can be reported too: more tests, never fewer.
        Assert.Contains(
            new SymbolChange("App.Clock", "Now()", SymbolChangeKind.Modified),
            Detect(before, before.Replace("=> 1", "=> 3")).Symbols);
        Assert.Contains(
            new SymbolChange("App.Clock", "Now()", SymbolChangeKind.Modified),
            Detect(before, before.Replace("=> 2", "=> 3")).Symbols);
    }

    [Fact]
    public void Change_only_active_under_a_mix_of_symbols_affects_the_whole_project()
    {
        const string before = """
            #if A && !B
            namespace App;
            public class Hidden { public int Run() => 1; }
            #endif
            """;

        var change = Detect(before, before.Replace("=> 1", "=> 2"));

        Assert.False(change.IsCosmetic);
        Assert.True(change.AffectsWholeProject);
    }

    [Fact]
    public void Change_under_a_mix_of_symbols_is_seen_with_the_frameworks_real_symbols()
    {
        const string before = """
            namespace Shop;
            #if NET6_0_OR_GREATER && !NETSTANDARD2_0
            public static class DateParts { public static int Year() => 2026; }
            #endif
            """;
        var after = before.Replace("=> 2026;", "=> 2027;", StringComparison.Ordinal);

        // Neither the bare parse nor the all-symbols one enters the region: the change cannot be named.
        Assert.True(Detect(before, after).AffectsWholeProject);

        var change = _detector.Detect("DateParts.cs", SourceText.From(before), SourceText.From(after), [["NET", "NET6_0_OR_GREATER", "NET8_0_OR_GREATER"]]);
        Assert.False(change.AffectsWholeProject);
        Assert.Equal(new SymbolChange("Shop.DateParts", "Year()", SymbolChangeKind.Modified), Assert.Single(change.Symbols));
    }

    private CodeChange Detect(string? before, string? after) =>
        _detector.Detect("File.cs", before is null ? null : SourceText.From(before), after is null ? null : SourceText.From(after));
}
