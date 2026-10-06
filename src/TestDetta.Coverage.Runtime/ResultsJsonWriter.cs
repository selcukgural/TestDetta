using System.Globalization;
using System.Text;

namespace TestDetta.Coverage.Runtime;

/// <summary>
/// Writes the tracker's results as JSON with the few calls it needs. The tracker runs inside every test
/// host, .NET Framework included, where System.Text.Json is not part of the runtime; a package would be
/// one more assembly to load next to the tests' own copy (docs/design/framework-support.md).
/// </summary>
/// <param name="stream">Stream the UTF-8 text goes to; left open.</param>
internal sealed class ResultsJsonWriter(Stream stream) : IDisposable
{
    /// <summary>The text, written to the stream on <see cref="Dispose"/>.</summary>
    private readonly StreamWriter _writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 65536, leaveOpen: true);

    /// <summary>Per open object or array, whether it has a value yet (a comma goes before the next one).</summary>
    private readonly Stack<bool> _hasValue = new();

    /// <summary>Starts the root object.</summary>
    public void WriteStartObject() => Open('{');

    /// <summary>Starts an object property.</summary>
    /// <param name="name">Property name.</param>
    public void WriteStartObject(string name)
    {
        Name(name);
        Open('{', named: true);
    }

    /// <summary>Ends the current object.</summary>
    public void WriteEndObject() => Close('}');

    /// <summary>Starts an array property.</summary>
    /// <param name="name">Property name.</param>
    public void WriteStartArray(string name)
    {
        Name(name);
        Open('[', named: true);
    }

    /// <summary>Ends the current array.</summary>
    public void WriteEndArray() => Close(']');

    /// <summary>Writes a number property.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="value">Value.</param>
    public void WriteNumber(string name, double value)
    {
        Name(name);
        _writer.Write(Number(value));
    }

    /// <summary>Writes a number property.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="value">Value.</param>
    public void WriteNumber(string name, long value)
    {
        Name(name);
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a number in the current array.</summary>
    /// <param name="value">Value.</param>
    public void WriteNumberValue(long value)
    {
        Separate();
        _writer.Write(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a string in the current array.</summary>
    /// <param name="value">Value.</param>
    public void WriteStringValue(string value)
    {
        Separate();
        String(value);
    }

    /// <summary>Writes the text to the stream.</summary>
    public void Dispose() => _writer.Dispose();

    /// <summary>A double as JSON: shortest round-trip form, never NaN or infinity.</summary>
    /// <param name="value">Value.</param>
    private static string Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Opens an object or array, after a comma when it is an array element.</summary>
    /// <param name="bracket">'{' or '['.</param>
    /// <param name="named">True when <see cref="Name"/> already placed it.</param>
    private void Open(char bracket, bool named = false)
    {
        if (!named)
        {
            Separate();
        }

        _writer.Write(bracket);
        _hasValue.Push(false);
    }

    /// <summary>Closes the current object or array.</summary>
    /// <param name="bracket">'}' or ']'.</param>
    private void Close(char bracket)
    {
        _hasValue.Pop();
        _writer.Write(bracket);
    }

    /// <summary>Writes the comma before a value when the container has one already.</summary>
    private void Separate()
    {
        if (_hasValue.Count == 0)
        {
            return;
        }

        if (_hasValue.Pop())
        {
            _writer.Write(',');
        }

        _hasValue.Push(true);
    }

    /// <summary>Writes a property name and its colon.</summary>
    /// <param name="name">Property name.</param>
    private void Name(string name)
    {
        Separate();
        String(name);
        _writer.Write(':');
    }

    /// <summary>Writes a JSON string: quotes, backslashes and control characters escaped.</summary>
    /// <param name="value">Text.</param>
    private void String(string value)
    {
        _writer.Write('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    _writer.Write("\\\"");
                    break;
                case '\\':
                    _writer.Write("\\\\");
                    break;
                case '\n':
                    _writer.Write("\\n");
                    break;
                case '\r':
                    _writer.Write("\\r");
                    break;
                case '\t':
                    _writer.Write("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        _writer.Write("\\u");
                        _writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _writer.Write(c);
                    }

                    break;
            }
        }

        _writer.Write('"');
    }
}
