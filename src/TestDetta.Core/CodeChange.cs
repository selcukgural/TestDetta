namespace TestDetta.Core;

/// <summary>How a type or member differs between the base revision and the working tree.</summary>
public enum SymbolChangeKind
{
    /// <summary>The declaration exists only in the working tree.</summary>
    Added,

    /// <summary>The declaration exists only at the base revision.</summary>
    Removed,

    /// <summary>The declaration exists on both sides with different code.</summary>
    Modified,
}

/// <summary>A type or member whose code changed.</summary>
/// <param name="TypeName">
/// Namespace-qualified type name with nested types joined by '.' and generic arity as <c>`n</c>,
/// for example <c>App.Orders.OrderService</c> or <c>App.Box`1</c>.
/// </param>
/// <param name="MemberName">
/// Member signature such as <c>Place(Order,int)</c>, <c>.ctor()</c> or <c>Total</c>. Null when the
/// change is to the type declaration itself: attributes, modifiers, base types, primary constructor,
/// or the whole type being added or removed.
/// </param>
/// <param name="Kind">Whether the declaration was added, removed or modified.</param>
public sealed record SymbolChange(string TypeName, string? MemberName, SymbolChangeKind Kind)
{
    /// <summary>
    /// True for a <see cref="SymbolChangeKind.Modified"/> declaration whose metadata changed: its
    /// attributes or parameter default values (<see cref="DeclarationWalker.MetadataNodes"/>).
    /// Reflection reads them without running the declaration's code, so the classes a coverage map
    /// credits with running it are not all that see the change.
    /// </summary>
    public bool MetadataChanged { get; init; }

    /// <summary>
    /// True for an F# <c>inline</c> function or member: its body is compiled into its callers, so a
    /// coverage map credits them, not it, and its users are traced by name.
    /// </summary>
    public bool Inlined { get; init; }
}

/// <summary>The code-level difference of one C# file.</summary>
/// <param name="Path">File path relative to the repository root, as git prints it.</param>
/// <param name="IsCosmetic">
/// True when only whitespace or comments changed, including XML doc comments. Such a file cannot
/// change test results, so it does not affect its project.
/// </param>
/// <param name="AffectsWholeProject">
/// True when a <c>global using</c> or an assembly/module attribute changed; those apply to every
/// file of the project, so no symbol list can describe the impact.
/// </param>
/// <param name="Symbols">Changed types and members. Members of an added or removed type are not listed separately.</param>
public sealed record CodeChange(
    string Path,
    bool IsCosmetic,
    bool AffectsWholeProject,
    IReadOnlyList<SymbolChange> Symbols);
