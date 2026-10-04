namespace TestDetta.Eval;

/// <summary>
/// Sets environment variables of this process while one repository is evaluated, so every child
/// inherits them, and puts the previous values back on disposal.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    /// <summary>Values the variables had before the scope; null for unset.</summary>
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    /// <summary>Sets <paramref name="variables"/>.</summary>
    /// <param name="variables">Names and values.</param>
    public EnvironmentScope(IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Restores the previous values.</summary>
    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _previous.Clear();
    }
}
