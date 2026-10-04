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
                kinds.UnionWith(Names.Select(static n => n.Kind));
                continue;
            }

            var match = Array.FindIndex(Names, n => string.Equals(n.Name, part, StringComparison.OrdinalIgnoreCase));
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
