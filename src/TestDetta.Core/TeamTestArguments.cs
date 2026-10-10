namespace TestDetta.Core;

/// <summary>
/// The <c>dotnet test</c> arguments a team runs its tests with, split the way TestDetta uses them in runs it starts itself
/// (docs/design/team-test-configuration.md): the filter, which TestDetta joins with its own; the run settings file, which
/// TestDetta's settings are added to; the options TestDetta controls in such a run, which it drops; and everything else,
/// which runs as given. Inline run settings after <c>--</c> stay last.
/// </summary>
/// <param name="Filter">The value of <c>--filter</c>, or null.</param>
/// <param name="Settings">The value of <c>--settings</c> (<c>-s</c>), or null.</param>
/// <param name="Others">The other options, in order, without the ones TestDetta controls.</param>
/// <param name="InlineSettings">The arguments after <c>--</c>, with it; empty for none.</param>
public sealed record TeamTestArguments(string? Filter, string? Settings, IReadOnlyList<string> Others, IReadOnlyList<string> InlineSettings)
{
    /// <summary>
    /// Options a recording sets itself, with a value: its configuration and framework, its loggers and result folder,
    /// the project, and the testing platform's report options, which name the files recording reads.
    /// </summary>
    private static readonly string[] ControlledWithValue =
        ["-c", "--configuration", "-f", "--framework", "-l", "--logger", "--results-directory", "-r", "--project", "--report-trx-filename", "--report-xunit-trx-filename"];

    /// <summary>Options a recording sets itself, without a value.</summary>
    private static readonly string[] ControlledFlags = ["--no-build", "--report-trx", "--report-xunit-trx", "--no-restore"];

    /// <summary>Splits <paramref name="arguments"/>; a repeated filter or settings option takes the last value, as <c>dotnet test</c> does.</summary>
    /// <param name="arguments">The team's <c>dotnet test</c> arguments.</param>
    public static TeamTestArguments Parse(IReadOnlyList<string> arguments)
    {
        string? filter = null;
        string? settings = null;
        var others = new List<string>();
        var inline = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument == "--")
            {
                inline.AddRange(arguments.Skip(i));
                break;
            }

            if (Value(arguments, ref i, "--filter") is { } filterValue)
            {
                filter = filterValue;
            }
            else if ((Value(arguments, ref i, "--settings") ?? Value(arguments, ref i, "-s")) is { } settingsValue)
            {
                settings = settingsValue;
            }
            else if (Array.Exists(ControlledFlags, option => option == argument))
            {
                // TestDetta's own value is used.
            }
            else if (Array.Find(ControlledWithValue, option => argument == option || argument.StartsWith(option + "=", StringComparison.Ordinal) || argument.StartsWith(option + ":", StringComparison.Ordinal)) is { } controlled)
            {
                i += argument == controlled ? 1 : 0;
            }
            else
            {
                others.Add(argument);
            }
        }

        return new TeamTestArguments(filter, settings, others, inline);
    }

    /// <summary>The value of <paramref name="option"/> at <paramref name="i"/> (<c>--x v</c>, <c>--x=v</c>, <c>--x:v</c>), moving past it; null when it is another argument.</summary>
    /// <param name="arguments">All arguments.</param>
    /// <param name="i">Position; moved past a separate value.</param>
    /// <param name="option">The option.</param>
    private static string? Value(IReadOnlyList<string> arguments, ref int i, string option)
    {
        var argument = arguments[i];
        if (argument == option && i + 1 < arguments.Count)
        {
            return arguments[++i];
        }

        return argument.StartsWith(option + "=", StringComparison.Ordinal) || argument.StartsWith(option + ":", StringComparison.Ordinal)
            ? argument[(option.Length + 1)..]
            : null;
    }
}
