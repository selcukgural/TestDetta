using System.Text;

namespace TestDetta.Cli;

/// <summary>When the command colours its output (<c>--color</c>).</summary>
internal enum ColorMode
{
    /// <summary>Colour a terminal and GitHub Actions' log, nothing else; <c>NO_COLOR</c> and <c>TERM=dumb</c> turn it off.</summary>
    Auto,

    /// <summary>Always colour, also into a file or a pipe.</summary>
    Always,

    /// <summary>Never colour.</summary>
    Never,
}

/// <summary>Where an output stream goes, which decides whether it gets colour by default.</summary>
internal enum StreamKind
{
    /// <summary>An interactive console.</summary>
    Terminal,

    /// <summary>The process's own stream sent to a file or a pipe (a CI log among them).</summary>
    Redirected,

    /// <summary>A writer that is not the process's stream at all (tests, captured output).</summary>
    Captured,
}

/// <summary>What a piece of console text means; a theme decides how each looks.</summary>
internal enum Role
{
    /// <summary>Ordinary text: project paths, values.</summary>
    Plain,

    /// <summary>Section titles and table headers.</summary>
    Heading,

    /// <summary>Supporting text: reasons, separators, table borders.</summary>
    Muted,

    /// <summary>A number the reader looks for.</summary>
    Number,

    /// <summary>Good news: skipped work, saved time, passed tests.</summary>
    Good,

    /// <summary>Something to notice: a whole project running, a license problem.</summary>
    Notice,

    /// <summary>A failure.</summary>
    Bad,

    /// <summary>A test project, set apart from the projects under test.</summary>
    Test,
}

/// <summary>
/// The colours of the console output (docs/design/console-output.md), as the 16 basic ANSI colours: every terminal draws
/// them in its own palette, so they read on dark and light backgrounds alike. Null codes leave a role unstyled.
/// </summary>
/// <param name="Name">Name for <c>--theme</c> and <c>TESTDETTA_THEME</c>.</param>
/// <param name="Codes">SGR parameters per role, such as <c>32</c> (green) or <c>1</c> (bold).</param>
internal sealed record ConsoleTheme(string Name, IReadOnlyDictionary<Role, string> Codes)
{
    /// <summary>Green for good, yellow for notice, red for failures.</summary>
    public static readonly ConsoleTheme Default = new("default", new Dictionary<Role, string>
    {
        [Role.Heading] = "1",
        [Role.Muted] = "90",
        [Role.Number] = "1",
        [Role.Good] = "32",
        [Role.Notice] = "33",
        [Role.Bad] = "1;31",
        [Role.Test] = "34",
    });

    /// <summary>Good and bad told apart as blue and yellow/magenta instead of green and red, which most colour vision deficiencies confuse.</summary>
    public static readonly ConsoleTheme Colorblind = new("colorblind", new Dictionary<Role, string>
    {
        [Role.Heading] = "1",
        [Role.Muted] = "90",
        [Role.Number] = "1",
        [Role.Good] = "34",
        [Role.Notice] = "33",
        [Role.Bad] = "1;35",
        [Role.Test] = "36",
    });

    /// <summary>No colour, only bold, faint and underline: the layout without the palette.</summary>
    public static readonly ConsoleTheme Mono = new("mono", new Dictionary<Role, string>
    {
        [Role.Heading] = "1",
        [Role.Muted] = "2",
        [Role.Number] = "1",
        [Role.Notice] = "1",
        [Role.Bad] = "1;4",
    });

    /// <summary>Every theme by name.</summary>
    public static readonly IReadOnlyList<ConsoleTheme> All = [Default, Colorblind, Mono];

    /// <summary>The theme called <paramref name="name"/> (case does not matter), or null.</summary>
    /// <param name="name">Theme name.</param>
    public static ConsoleTheme? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The characters that draw tables and marks: box drawing where the output is UTF-8, ASCII elsewhere.</summary>
internal sealed record Glyphs
{
    /// <summary>Box drawing and symbols.</summary>
    public static readonly Glyphs Unicode = new();

    /// <summary>Plain ASCII for consoles that are not UTF-8 (the legacy Windows console).</summary>
    public static readonly Glyphs Ascii = new()
    {
        Horizontal = "-", Vertical = "|", TopLeft = "+", TopRight = "+", BottomLeft = "+", BottomRight = "+",
        LeftJoin = "+", RightJoin = "+", Cross = "+", TopJoin = "+", BottomJoin = "+",
        Pass = "v", Fail = "x", Warn = "!", Dot = "-", Arrow = "->", Full = '#', Empty = '.',
    };

    /// <summary>Horizontal line.</summary>
    public string Horizontal { get; init; } = "─";

    /// <summary>Vertical line.</summary>
    public string Vertical { get; init; } = "│";

    /// <summary>Top-left corner.</summary>
    public string TopLeft { get; init; } = "┌";

    /// <summary>Top-right corner.</summary>
    public string TopRight { get; init; } = "┐";

    /// <summary>Bottom-left corner.</summary>
    public string BottomLeft { get; init; } = "└";

    /// <summary>Bottom-right corner.</summary>
    public string BottomRight { get; init; } = "┘";

    /// <summary>Tee on the left edge, under the header.</summary>
    public string LeftJoin { get; init; } = "├";

    /// <summary>Tee on the right edge, under the header.</summary>
    public string RightJoin { get; init; } = "┤";

    /// <summary>Crossing of two lines.</summary>
    public string Cross { get; init; } = "┼";

    /// <summary>Tee on the top edge.</summary>
    public string TopJoin { get; init; } = "┬";

    /// <summary>Tee on the bottom edge.</summary>
    public string BottomJoin { get; init; } = "┴";

    /// <summary>Mark for passed tests.</summary>
    public string Pass { get; init; } = "✓";

    /// <summary>Mark for failed tests.</summary>
    public string Fail { get; init; } = "✗";

    /// <summary>Mark for something to notice.</summary>
    public string Warn { get; init; } = "⚠";

    /// <summary>Separator between facts on one line.</summary>
    public string Dot { get; init; } = "·";

    /// <summary>Arrow.</summary>
    public string Arrow { get; init; } = "→";

    /// <summary>A full cell of a bar.</summary>
    public char Full { get; init; } = '█';

    /// <summary>An empty cell of a bar.</summary>
    public char Empty { get; init; } = '░';
}

/// <summary>
/// How console text is drawn: whether it carries colour, in which theme, with which characters, and how wide the
/// console is. Colour never reaches a file, a pipe or JSON unless asked for, so stdout stays machine readable.
/// </summary>
/// <param name="Color">True to write ANSI colour codes.</param>
/// <param name="Theme">Colours per role.</param>
/// <param name="Glyphs">Characters for tables and marks.</param>
/// <param name="Width">Columns available; tables wrap their last column to fit.</param>
internal sealed record ConsoleStyle(bool Color, ConsoleTheme Theme, Glyphs Glyphs, int Width)
{
    /// <summary>Width assumed when the output is not a terminal: CI logs are wide.</summary>
    public const int DefaultWidth = 120;

    /// <summary>No colour, box drawing, the default width: what tests and redirected output get.</summary>
    public static readonly ConsoleStyle Plain = new(false, ConsoleTheme.Default, Glyphs.Unicode, DefaultWidth);

    /// <summary><paramref name="text"/> in the look of <paramref name="role"/>; unchanged without colour.</summary>
    /// <param name="role">What the text means.</param>
    /// <param name="text">Text to write.</param>
    public string Paint(Role role, string text) =>
        Color && text.Length > 0 && Theme.Codes.TryGetValue(role, out var code) ? $"\e[{code}m{text}\e[0m" : text;

    /// <summary>
    /// The style for one output stream. <c>--color</c> wins, then <c>NO_COLOR</c> (off), <c>FORCE_COLOR</c> (on),
    /// <c>TERM=dumb</c> (off); otherwise a terminal or GitHub Actions' log (which shows ANSI colour) gets colour.
    /// </summary>
    /// <param name="settings">Display options from the command line.</param>
    /// <param name="environment">Reads environment variables.</param>
    /// <param name="stream">Where the stream goes.</param>
    /// <param name="encoding">The stream's encoding; anything but UTF-8 gets ASCII glyphs.</param>
    /// <param name="width">The console's width when it is a terminal; null otherwise.</param>
    public static ConsoleStyle For(DisplaySettings settings, Func<string, string?> environment, StreamKind stream, Encoding encoding, int? width)
    {
        var color = settings.Color switch
        {
            ColorMode.Always => true,
            ColorMode.Never => false,
            _ => AutoColor(environment, stream),
        };
        var theme = settings.Theme ?? (environment("TESTDETTA_THEME") is { Length: > 0 } named ? ConsoleTheme.Find(named) : null) ?? ConsoleTheme.Default;
        var glyphs = encoding.CodePage == Encoding.UTF8.CodePage ? Glyphs.Unicode : Glyphs.Ascii;
        var columns = width is > 20 ? width.Value
            : int.TryParse(environment("COLUMNS"), System.Globalization.CultureInfo.InvariantCulture, out var given) && given > 20 ? given
            : DefaultWidth;
        return new ConsoleStyle(color, theme, glyphs, columns);
    }

    /// <summary>Colour for <see cref="ColorMode.Auto"/>, following https://no-color.org and the common <c>FORCE_COLOR</c>.</summary>
    /// <param name="environment">Reads environment variables.</param>
    /// <param name="stream">Where the stream goes.</param>
    private static bool AutoColor(Func<string, string?> environment, StreamKind stream)
    {
        if (stream == StreamKind.Captured)
        {
            return false;
        }

        if (environment("NO_COLOR") is { Length: > 0 })
        {
            return false;
        }

        if (environment("FORCE_COLOR") is { Length: > 0 } force)
        {
            return force != "0";
        }

        if (string.Equals(environment("TERM"), "dumb", StringComparison.Ordinal))
        {
            return false;
        }

        // GitHub Actions' log is a pipe, but its viewer shows ANSI colour.
        return stream == StreamKind.Terminal || string.Equals(environment("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Display options from the command line.</summary>
/// <param name="Color">When to colour.</param>
/// <param name="Theme">Theme from <c>--theme</c>; null takes <c>TESTDETTA_THEME</c>, then the default.</param>
internal sealed record DisplaySettings(ColorMode Color = ColorMode.Auto, ConsoleTheme? Theme = null);
