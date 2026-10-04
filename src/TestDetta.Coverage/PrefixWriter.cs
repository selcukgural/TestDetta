using System.Text;

namespace TestDetta.Coverage;

/// <summary>Writes whole lines to an inner writer with a fixed prefix, so interleaved shard output stays readable.</summary>
/// <param name="inner">Destination; must be safe to use from several threads.</param>
/// <param name="prefix">Text put in front of every line.</param>
internal sealed class PrefixWriter(TextWriter inner, string prefix) : TextWriter
{
    /// <summary>Same encoding as the destination.</summary>
    public override Encoding Encoding => inner.Encoding;

    /// <summary>Writes one prefixed line.</summary>
    /// <param name="value">Line content.</param>
    public override void WriteLine(string? value) => inner.WriteLine(prefix + value);

    /// <summary>Single characters are not expected: <see cref="DotnetProcess"/> forwards whole lines.</summary>
    /// <param name="value">Character to write.</param>
    public override void Write(char value) => inner.Write(value);
}
