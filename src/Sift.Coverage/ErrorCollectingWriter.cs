using System.Text;

namespace Sift.Coverage;

/// <summary>
/// Passes whole lines on to another writer and keeps the first few MSBuild error lines, so a failed
/// build can say why without the caller reading the whole log.
/// </summary>
/// <param name="inner">Destination of every line.</param>
internal sealed class ErrorCollectingWriter(TextWriter inner) : TextWriter
{
    /// <summary>How many distinct error lines are kept.</summary>
    private const int MaxErrors = 3;

    private readonly List<string> _errors = [];

    /// <summary>Distinct error lines, first ones first.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Same encoding as the destination.</summary>
    public override Encoding Encoding => inner.Encoding;

    /// <summary>Forwards a line and keeps it when it is an MSBuild error.</summary>
    /// <param name="value">Line content.</param>
    public override void WriteLine(string? value)
    {
        inner.WriteLine(value);
        if (value is null || _errors.Count >= MaxErrors || !value.Contains(": error ", StringComparison.Ordinal))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!_errors.Contains(trimmed))
        {
            _errors.Add(trimmed);
        }
    }

    /// <summary>Single characters are not expected: <see cref="DotnetProcess"/> forwards whole lines.</summary>
    /// <param name="value">Character to write.</param>
    public override void Write(char value) => inner.Write(value);
}
