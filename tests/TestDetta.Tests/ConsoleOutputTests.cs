using System.Text;
using TestDetta.Cli;

namespace TestDetta.Tests;

/// <summary>
/// Colour, themes and tables of the console output (docs/design/console-output.md): colour only where a reader sees it,
/// never in JSON or captured output unless asked for, and tables that wrap instead of cutting text off.
/// </summary>
public sealed class ConsoleOutputTests : IDisposable
{
    private readonly GitRepoFixture _repo = new();

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private static ConsoleStyle Style(StreamKind kind, DisplaySettings? display = null, params (string, string)[] env) =>
        ConsoleStyle.For(display ?? new DisplaySettings(), Env(env), kind, Encoding.UTF8, width: null);

    [Fact]
    public void A_terminal_gets_colour_and_a_file_or_pipe_does_not()
    {
        Assert.True(Style(StreamKind.Terminal).Color);
        Assert.False(Style(StreamKind.Redirected).Color);
        Assert.False(Style(StreamKind.Captured).Color);
    }

    [Fact]
    public void GitHub_Actions_logs_get_colour_but_captured_output_never_does()
    {
        Assert.True(Style(StreamKind.Redirected, env: ("GITHUB_ACTIONS", "true")).Color);
        Assert.False(Style(StreamKind.Captured, env: ("GITHUB_ACTIONS", "true")).Color);
        Assert.False(Style(StreamKind.Captured, env: ("FORCE_COLOR", "1")).Color);
    }

    [Fact]
    public void No_color_force_color_and_a_dumb_terminal_follow_the_conventions()
    {
        Assert.False(Style(StreamKind.Terminal, env: ("NO_COLOR", "1")).Color);
        Assert.False(Style(StreamKind.Terminal, env: ("TERM", "dumb")).Color);
        Assert.True(Style(StreamKind.Redirected, env: ("FORCE_COLOR", "1")).Color);
        Assert.False(Style(StreamKind.Terminal, env: ("FORCE_COLOR", "0")).Color);
    }

    [Fact]
    public void The_color_option_wins_over_everything()
    {
        Assert.True(Style(StreamKind.Captured, new DisplaySettings(ColorMode.Always), ("NO_COLOR", "1")).Color);
        Assert.False(Style(StreamKind.Terminal, new DisplaySettings(ColorMode.Never), ("FORCE_COLOR", "1")).Color);
    }

    [Fact]
    public void The_theme_comes_from_the_option_then_the_environment_then_the_default()
    {
        Assert.Equal("default", Style(StreamKind.Terminal).Theme.Name);
        Assert.Equal("mono", Style(StreamKind.Terminal, env: ("TESTDETTA_THEME", "Mono")).Theme.Name);
        Assert.Equal("colorblind", Style(StreamKind.Terminal, new DisplaySettings(Theme: ConsoleTheme.Colorblind), ("TESTDETTA_THEME", "mono")).Theme.Name);
        Assert.Equal("default", Style(StreamKind.Terminal, env: ("TESTDETTA_THEME", "neon")).Theme.Name);
    }

    [Fact]
    public void A_console_that_is_not_utf8_gets_ascii_and_columns_set_the_width()
    {
        var style = ConsoleStyle.For(new DisplaySettings(), Env(("COLUMNS", "80")), StreamKind.Redirected, Encoding.Latin1, width: null);

        Assert.Same(Glyphs.Ascii, style.Glyphs);
        Assert.Equal(80, style.Width);
        Assert.Equal(132, ConsoleStyle.For(new DisplaySettings(), Env(("COLUMNS", "80")), StreamKind.Terminal, Encoding.UTF8, width: 132).Width);
    }

    [Fact]
    public void Paint_wraps_text_in_the_theme_code_only_with_colour()
    {
        var colour = ConsoleStyle.Plain with { Color = true };

        Assert.Equal("\e[32mok\e[0m", colour.Paint(Role.Good, "ok"));
        Assert.Equal("plain", colour.Paint(Role.Plain, "plain"));
        Assert.Equal("ok", ConsoleStyle.Plain.Paint(Role.Good, "ok"));
        Assert.Equal("\e[34mok\e[0m", (colour with { Theme = ConsoleTheme.Colorblind }).Paint(Role.Good, "ok"));
        Assert.Equal("ok", (colour with { Theme = ConsoleTheme.Mono }).Paint(Role.Good, "ok"));
    }

    [Fact]
    public void A_table_sizes_columns_to_their_content_and_wraps_the_wrapping_column_to_the_width()
    {
        const string why = "ran Shop.Calc.Tax, which changed in this pull request";
        var table = new TextTable(ConsoleStyle.Plain with { Width = 44 }, new TextColumn("Project"), new TextColumn("N", AlignRight: true), new TextColumn("Why", Wrap: true));
        table.Add(new Cell("tests/Shop.Tests"), new Cell("4"), new Cell(why));
        using var output = new StringWriter();

        table.Write(output);

        var lines = output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.All(lines, static l => Assert.Equal(44, l.Length));
        Assert.Equal("┌──────────────────┬───┬───────────────────┐", lines[0]);
        Assert.Equal("│ Project          │ N │ Why               │", lines[1]);
        Assert.StartsWith("│ tests/Shop.Tests │ 4 │ ", lines[3], StringComparison.Ordinal);
        Assert.Equal(why, string.Join(" ", lines[3..^1].Select(static l => l.Split('│')[3].Trim())));
    }

    [Fact]
    public void Wrap_breaks_at_spaces_and_inside_words_only_when_it_must()
    {
        Assert.Equal(["short"], TextTable.Wrap("short", 10));
        Assert.Equal(["one two", "three"], TextTable.Wrap("one two three", 8));
        Assert.Equal(["abcdefgh", "ij"], TextTable.Wrap("abcdefghij", 8));
    }

    [Fact]
    public void Ascii_tables_draw_with_plus_minus_and_bar()
    {
        var table = new TextTable(ConsoleStyle.Plain with { Glyphs = Glyphs.Ascii }, new TextColumn("A"));
        table.Add(new Cell("x"));
        using var output = new StringWriter();

        table.Write(output);

        Assert.Equal("+---+\n| A |\n+---+\n| x |\n+---+\n", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Project_names_drop_the_file_when_the_folder_already_names_it()
    {
        Assert.Equal("tests/Shop.Tests", TextReport.Display("/r", "/r/tests/Shop.Tests/Shop.Tests.csproj"));
        Assert.Equal("tests/Shop/Shop.UnitTests.csproj", TextReport.Display("/r", "/r/tests/Shop/Shop.UnitTests.csproj"));
    }

    [Fact]
    public void The_affected_report_is_a_table_and_carries_colour_only_when_asked()
    {
        WriteRepository();
        _repo.Replace("src/Shop/Calc.cs", "net * 0.2m", "net * 0.25m");

        var plain = Run("affected", "--base", "main");
        var coloured = Run("affected", "--base", "main", "--color", "always");
        var json = Run("affected", "--base", "main", "--format", "json", "--color", "always");

        Assert.DoesNotContain("\e[", plain, StringComparison.Ordinal);
        Assert.Contains("Affected projects", plain, StringComparison.Ordinal);
        Assert.Matches(@"│ tests/Shop\.Tests +│ all +│", plain);
        Assert.Contains("\e[", coloured, StringComparison.Ordinal);
        Assert.DoesNotContain("\e[", json, StringComparison.Ordinal);
        Assert.StartsWith("{", json.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Color_and_theme_options_are_checked()
    {
        Assert.True(CliOptions.TryParse(["affected", "--color", "NEVER", "--theme", "colorblind"], "/r", out var options, out _));
        Assert.Equal(new DisplaySettings(ColorMode.Never, ConsoleTheme.Colorblind), options.Display);
        Assert.False(CliOptions.TryParse(["affected", "--color", "sometimes"], "/r", out _, out var color));
        Assert.Contains("auto, always or never", color, StringComparison.Ordinal);
        Assert.False(CliOptions.TryParse(["test", "--theme", "neon"], "/r", out _, out var theme));
        Assert.Contains("default, colorblind, mono", theme, StringComparison.Ordinal);
        Assert.True(CliOptions.TryParse(["audit", "--color", "always"], "/r", out var audit, out _));
        Assert.Equal(ColorMode.Always, audit.Display.Color);
    }

    private string Run(params string[] args)
    {
        var stdout = new StringWriter();
        var host = new CliHost(static name => name == "CI_PROJECT_VISIBILITY" ? "public" : null, Path.Combine(_repo.Root, ".testdetta", "home"));
        var exit = CliApp.Run([.. args, "--repo", _repo.Root], stdout, TextWriter.Null, host);
        Assert.Equal(0, exit);
        return stdout.ToString();
    }

    private void WriteRepository()
    {
        _repo.Write("src/Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        _repo.Write("src/Shop/Calc.cs", "namespace Shop; public static class Calc { public static decimal Tax(decimal net) => net * 0.2m; }");
        _repo.Write("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <ProjectReference Include="../../src/Shop/Shop.csproj" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("tests/Shop.Tests/Tests.cs", "namespace Shop.Tests; public class TaxTests { [Fact] public void Taxes() => Assert.Equal(2m, Shop.Calc.Tax(10m)); }");
        _repo.Write(".gitignore", ".testdetta/\n");
        _repo.Commit("base");
    }

    public void Dispose() => _repo.Dispose();
}
