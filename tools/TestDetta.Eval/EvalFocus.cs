namespace TestDetta.Eval;

/// <summary>Which sampled changes a quick check keeps (<c>--focus</c>).</summary>
internal enum EvalFocus
{
    /// <summary>Every sampled change.</summary>
    All,

    /// <summary>Changes in a project another project uses as a source generator (docs/design/generator-mutants.md).</summary>
    Generator,

    /// <summary>Emptied data files (<c>--data-files</c>; docs/design/runtime-file-reads.md).</summary>
    DataFiles,

    /// <summary>
    /// Changes in a project another project references without compiling against it, not as a source
    /// generator (<c>ReferenceOutputAssembly="false"</c>; docs/design/runtime-file-reads.md).
    /// </summary>
    Output,
}

/// <summary>Names of <see cref="EvalFocus"/> on the command line and in logs.</summary>
internal static class EvalFocusNames
{
    /// <summary>The names, for messages.</summary>
    public const string All = "all, generator, data-files or output";

    /// <summary>The name of <paramref name="focus"/>.</summary>
    /// <param name="focus">The focus.</param>
    public static string Name(EvalFocus focus) => focus switch
    {
        EvalFocus.Generator => "generator",
        EvalFocus.DataFiles => "data-files",
        EvalFocus.Output => "output",
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
            "output" => EvalFocus.Output,
            _ => (EvalFocus)(-1),
        };
        return Enum.IsDefined(focus);
    }
}
