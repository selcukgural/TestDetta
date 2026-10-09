namespace TestDetta.Cli;

/// <summary>One cell of a <see cref="TextTable"/>.</summary>
/// <param name="Text">What the cell says.</param>
/// <param name="Role">How it looks.</param>
internal readonly record struct Cell(string Text, Role Role = Role.Plain);

/// <summary>A column of a <see cref="TextTable"/>.</summary>
/// <param name="Header">Header text.</param>
/// <param name="Wrap">True when the column may wrap onto more lines to keep the table within the console's width.</param>
/// <param name="AlignRight">True to right-align, for numbers.</param>
internal sealed record TextColumn(string Header, bool Wrap = false, bool AlignRight = false);

/// <summary>
/// A boxed table for the console (docs/design/console-output.md): columns as wide as their content, the wrapping columns
/// narrowed and wrapped at spaces when the table would be wider than the console, borders muted. Nothing is cut off: a
/// reason that does not fit goes onto the next line of its cell.
/// </summary>
/// <param name="style">Colour, characters and width.</param>
/// <param name="columns">The columns, left to right.</param>
internal sealed class TextTable(ConsoleStyle style, params IReadOnlyList<TextColumn> columns)
{
    /// <summary>Narrowest a wrapping column gets, so a long word is not split into slivers.</summary>
    private const int MinimumWrapWidth = 16;

    /// <summary>Rows added so far.</summary>
    private readonly List<Cell[]> _rows = [];

    /// <summary>Adds a row; missing cells are empty.</summary>
    /// <param name="cells">Cells, left to right.</param>
    public void Add(params ReadOnlySpan<Cell> cells)
    {
        var row = new Cell[columns.Count];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = i < cells.Length ? cells[i] : new Cell(string.Empty);
        }

        _rows.Add(row);
    }

    /// <summary>Writes the table, each line starting with <paramref name="indent"/>.</summary>
    /// <param name="writer">Destination.</param>
    /// <param name="indent">Spaces before every line.</param>
    public void Write(TextWriter writer, string indent = "")
    {
        var widths = Widths(indent.Length);
        var g = style.Glyphs;
        writer.WriteLine(indent + Rule(widths, g.TopLeft, g.TopJoin, g.TopRight));
        WriteRow(writer, indent, widths, [.. columns.Select(static c => new Cell(c.Header, Role.Heading))]);
        writer.WriteLine(indent + Rule(widths, g.LeftJoin, g.Cross, g.RightJoin));
        foreach (var row in _rows)
        {
            WriteRow(writer, indent, widths, row);
        }

        writer.WriteLine(indent + Rule(widths, g.BottomLeft, g.BottomJoin, g.BottomRight));
    }

    /// <summary>
    /// Column widths: each as wide as its longest cell; when the table is wider than the console, the wrapping columns
    /// give up width, widest first, down to <see cref="MinimumWrapWidth"/>.
    /// </summary>
    /// <param name="indent">Width of the indent before the table.</param>
    private int[] Widths(int indent)
    {
        var widths = new int[columns.Count];
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = columns[i].Header.Length;
            foreach (var row in _rows)
            {
                widths[i] = Math.Max(widths[i], row[i].Text.Length);
            }
        }

        // Each column costs its width plus a space on both sides and a border; one more border closes the row.
        var excess = indent + widths.Sum() + (3 * widths.Length) + 1 - style.Width;
        while (excess > 0)
        {
            var widest = -1;
            for (var i = 0; i < widths.Length; i++)
            {
                if (columns[i].Wrap && widths[i] > MinimumWrapWidth && (widest < 0 || widths[i] > widths[widest]))
                {
                    widest = i;
                }
            }

            if (widest < 0)
            {
                break;
            }

            var cut = Math.Min(excess, widths[widest] - MinimumWrapWidth);
            widths[widest] -= cut;
            excess -= cut;
        }

        return widths;
    }

    /// <summary>A horizontal border.</summary>
    /// <param name="widths">Column widths.</param>
    /// <param name="left">Left end.</param>
    /// <param name="join">Between columns.</param>
    /// <param name="right">Right end.</param>
    private string Rule(int[] widths, string left, string join, string right) =>
        style.Paint(Role.Muted, left + string.Join(join, widths.Select(w => string.Concat(Enumerable.Repeat(style.Glyphs.Horizontal, w + 2)))) + right);

    /// <summary>Writes one row, on as many lines as its tallest wrapped cell needs.</summary>
    /// <param name="writer">Destination.</param>
    /// <param name="indent">Spaces before every line.</param>
    /// <param name="widths">Column widths.</param>
    /// <param name="row">Cells.</param>
    private void WriteRow(TextWriter writer, string indent, int[] widths, Cell[] row)
    {
        var lines = new List<string>[row.Length];
        var height = 1;
        for (var i = 0; i < row.Length; i++)
        {
            lines[i] = Wrap(row[i].Text, widths[i]);
            height = Math.Max(height, lines[i].Count);
        }

        var border = style.Paint(Role.Muted, style.Glyphs.Vertical);
        for (var line = 0; line < height; line++)
        {
            var text = new System.Text.StringBuilder(indent).Append(border);
            for (var i = 0; i < row.Length; i++)
            {
                var part = line < lines[i].Count ? lines[i][line] : string.Empty;
                var padded = columns[i].AlignRight ? part.PadLeft(widths[i]) : part.PadRight(widths[i]);
                text.Append(' ').Append(part.Length == 0 ? padded : style.Paint(row[i].Role, padded)).Append(' ').Append(border);
            }

            writer.WriteLine(text.ToString());
        }
    }

    /// <summary>The text cut into lines of at most <paramref name="width"/>, at spaces where it can be, inside words where it must.</summary>
    /// <param name="text">Cell text.</param>
    /// <param name="width">Column width.</param>
    internal static List<string> Wrap(string text, int width)
    {
        if (text.Length <= width)
        {
            return [text];
        }

        var lines = new List<string>();
        var rest = text.AsSpan();
        while (rest.Length > width)
        {
            var space = rest[..(width + 1)].LastIndexOf(' ');
            var take = space > 0 ? space : width;
            lines.Add(rest[..take].TrimEnd().ToString());
            rest = rest[take..].TrimStart();
        }

        if (rest.Length > 0)
        {
            lines.Add(rest.ToString());
        }

        return lines;
    }
}
