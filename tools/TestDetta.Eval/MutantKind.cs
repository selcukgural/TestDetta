namespace TestDetta.Eval;

/// <summary>How a synthetic mutant breaks its member.</summary>
internal enum MutantKind
{
    /// <summary>Every compiled method of the member throws at its start.</summary>
    Throw,

    /// <summary>The member returns the default of its type (or returns at once when void) without running its body.</summary>
    ReturnDefault,

    /// <summary>The first <c>brtrue</c>/<c>brfalse</c> (or <c>beq</c>/<c>bne.un</c>) of the member is inverted.</summary>
    FlipBranch,

    /// <summary>A field's constant initial value changes: numbers by one, booleans flip, strings get a suffix.</summary>
    FieldValue,

    /// <summary>
    /// A data file outside every project is emptied (<see cref="DataFileSampler"/>, <c>--data-files</c>);
    /// not one of the member kinds, so <c>all</c> leaves it out.
    /// </summary>
    EmptyFile,
}

/// <summary>Names of <see cref="MutantKind"/> on the command line and in reports.</summary>
internal static class MutantKinds
{
    /// <summary>Kinds and their names.</summary>
    private static readonly (MutantKind Kind, string Name)[] Names =
    [
        (MutantKind.Throw, "throw"),
        (MutantKind.ReturnDefault, "return-default"),
        (MutantKind.FlipBranch, "flip-branch"),
        (MutantKind.FieldValue, "field-value"),
        (MutantKind.EmptyFile, "empty-file"),
    ];

    /// <summary>The name of <paramref name="kind"/>.</summary>
    /// <param name="kind">Mutant kind.</param>
    public static string Name(MutantKind kind) => Array.Find(Names, n => n.Kind == kind).Name;

    /// <summary>Parses a comma-separated list such as <c>throw,field-value</c>; <c>all</c> means every kind.</summary>
    /// <param name="value">List from the command line or corpus.</param>
    /// <param name="kinds">Parsed kinds.</param>
    /// <param name="error">The first unknown name.</param>
    public static bool TryParse(string value, out HashSet<MutantKind> kinds, out string? error)
    {
        kinds = [];
        error = null;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "all")
            {
                kinds.UnionWith(Names.Select(static n => n.Kind).Where(static k => k != MutantKind.EmptyFile));
                continue;
            }

            var match = Array.FindIndex(Names, n => n.Kind != MutantKind.EmptyFile && string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
            if (match < 0)
            {
                error = $"Unknown mutant kind '{part}'; use {string.Join(", ", Names.Select(static n => n.Name))} or all";
                return false;
            }

            kinds.Add(Names[match].Kind);
        }

        if (kinds.Count == 0)
        {
            error = "No mutant kind given";
            return false;
        }

        return true;
    }
}

/// <summary>Which sampled changes a quick check keeps (<c>--focus</c>).</summary>
internal enum EvalFocus
{
    /// <summary>Every sampled change.</summary>
    All,

    /// <summary>Changes in a project another project uses as a source generator (docs/design/generator-mutants.md).</summary>
    Generator,

    /// <summary>Emptied data files (<c>--data-files</c>; docs/design/runtime-file-reads.md).</summary>
    DataFiles,
}

/// <summary>Names of <see cref="EvalFocus"/> on the command line and in logs.</summary>
internal static class EvalFocusNames
{
    /// <summary>The names, for messages.</summary>
    public const string All = "all, generator or data-files";

    /// <summary>The name of <paramref name="focus"/>.</summary>
    /// <param name="focus">The focus.</param>
    public static string Name(EvalFocus focus) => focus switch
    {
        EvalFocus.Generator => "generator",
        EvalFocus.DataFiles => "data-files",
        _ => "all",
    };

    /// <summary>Parses a name.</summary>
    /// <param name="value">The name.</param>
    /// <param name="focus">The focus.</param>
    public static bool TryParse(string value, out EvalFocus focus)
    {
        focus = value.ToLowerInvariant() switch
        {
            "all" => EvalFocus.All,
            "generator" => EvalFocus.Generator,
            "data-files" => EvalFocus.DataFiles,
            _ => (EvalFocus)(-1),
        };
        return Enum.IsDefined(focus);
    }
}
