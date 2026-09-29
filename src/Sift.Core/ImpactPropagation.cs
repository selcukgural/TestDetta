using Microsoft.Extensions.Logging;

namespace Sift.Core;

/// <summary>
/// Spreads a set of code changes through a <see cref="SourceIndex"/> by name: a member that
/// mentions the name of a changed member or type is itself changed, and so on until nothing new is
/// reached. Name collisions select too much, which is the safe direction.
/// </summary>
/// <param name="index">Source code to spread through.</param>
/// <param name="logger">Receives one debug line per affected member with the name that reached it.</param>
/// <param name="resolveByCoverage">
/// Asked for every reached member; when it returns true, recorded coverage already knows which
/// tests run the member, so the spread stops there instead of continuing by name. Null disables it.
/// </param>
internal sealed partial class ImpactPropagation(SourceIndex index, ILogger logger, Func<IndexedMember, bool>? resolveByCoverage = null)
{
    /// <summary>Members reached so far.</summary>
    private readonly HashSet<IndexedMember> _affected = new(ReferenceEqualityComparer.Instance);

    /// <summary>Lookups already queued, so each is done once.</summary>
    private readonly HashSet<NameLookup> _seen = [];

    /// <summary>Lookups still to do.</summary>
    private readonly Queue<NameLookup> _pending = new();

    /// <summary>Which referencing members a lookup accepts.</summary>
    private enum LookupScope
    {
        /// <summary>Any member that mentions the name.</summary>
        Anywhere,

        /// <summary>Only members of the owner type and its nested types: the name belongs to a private member.</summary>
        OwnerOnly,

        /// <summary>
        /// Members of the owner type, or of a type that also names the owner: the name belongs to a
        /// static member, which is written <c>Owner.Name</c> from outside.
        /// </summary>
        QualifiedByOwner,
    }

    /// <summary>A name to look up and the members that may match it.</summary>
    /// <param name="Name">Simple identifier.</param>
    /// <param name="Owner">Qualified name of the type that declares the member, when <paramref name="Scope"/> needs it.</param>
    /// <param name="Scope">Which referencing members count.</param>
    private readonly record struct NameLookup(string Name, string? Owner, LookupScope Scope);

    /// <summary>Types whose name was spread, mapped to the project that declares (or declared) them.</summary>
    public Dictionary<string, string> SeededTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Qualified names of every type that changed or contains an affected member.</summary>
    public HashSet<string> AffectedTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Starts from one detected change.</summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="projectPath">Project that owns the changed file; used when the type no longer exists.</param>
    public void Seed(SymbolChange change, string projectPath)
    {
        AffectedTypes.Add(change.TypeName);

        // A removed member may have been reached through its type (an override, an interface
        // implementation), and nothing is left in the index to say which.
        if (change.MemberName is null || change.Kind == SymbolChangeKind.Removed)
        {
            SeedType(change.TypeName, projectPath);
        }

        if (change.MemberName is null)
        {
            return;
        }

        if (index.FindMember(change.TypeName, change.MemberName) is { } member)
        {
            MarkAffected(member, "it changed");
            return;
        }

        foreach (var name in DeclarationWalker.ReferenceNames(change.MemberName))
        {
            Enqueue(new NameLookup(name, null, LookupScope.Anywhere));
        }
    }

    /// <summary>Follows the queued names until no new member is reached.</summary>
    public void Run()
    {
        while (_pending.TryDequeue(out var lookup))
        {
            foreach (var member in index.ReferencesTo(lookup.Name))
            {
                if (Matches(lookup, member))
                {
                    MarkAffected(member, lookup.Name);
                }
            }
        }
    }

    /// <summary>Records <paramref name="member"/> as affected and queues the names it can be reached by.</summary>
    /// <param name="member">Newly reached member.</param>
    /// <param name="because">The name that reached it, for the debug log.</param>
    private void MarkAffected(IndexedMember member, string because)
    {
        if (!_affected.Add(member))
        {
            return;
        }

        LogAffected(member.TypeName, member.MemberName ?? "(type)", because);
        AffectedTypes.Add(member.TypeName);

        if (resolveByCoverage?.Invoke(member) == true)
        {
            LogStoppedByCoverage(member.TypeName, member.MemberName ?? "(type)");
            return;
        }

        var referencedByName = false;
        var scope = member switch
        {
            { IsPrivate: true } => LookupScope.OwnerOnly,
            { IsStatic: true } => LookupScope.QualifiedByOwner,
            _ => LookupScope.Anywhere,
        };

        foreach (var name in member.DeclaredNames)
        {
            Enqueue(new NameLookup(name, scope == LookupScope.Anywhere ? null : member.TypeName, scope));
            referencedByName |= index.IsReferencedByOthers(name, member);
        }

        // Spread the type name when the member can run without being named, when nothing names it
        // (so something outside the repository calls it), for type-level changes, and for anything in
        // a test project: test classes inherit fixtures and setup from their base classes.
        if (member.MemberName is null || member.MayBeCalledImplicitly || !referencedByName || member.InTestProject)
        {
            SeedType(member.TypeName, member.ProjectPath);
        }
    }

    /// <summary>Queues the simple name of <paramref name="typeName"/> and remembers where the type lives.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="projectPath">Project that declares the type.</param>
    private void SeedType(string typeName, string projectPath)
    {
        SeededTypes.TryAdd(typeName, projectPath);
        Enqueue(new NameLookup(DeclarationWalker.SimpleTypeName(typeName), null, LookupScope.Anywhere));
    }

    /// <summary>Queues <paramref name="lookup"/> unless the same lookup was queued before.</summary>
    /// <param name="lookup">Name and scope.</param>
    private void Enqueue(NameLookup lookup)
    {
        if (_seen.Add(lookup))
        {
            _pending.Enqueue(lookup);
        }
    }

    /// <summary>True when <paramref name="member"/>, which mentions the looked-up name, is within the lookup's scope.</summary>
    /// <param name="lookup">Name and scope.</param>
    /// <param name="member">A member that mentions <see cref="NameLookup.Name"/>.</param>
    private bool Matches(NameLookup lookup, IndexedMember member) => lookup.Scope switch
    {
        LookupScope.OwnerOnly => IsInScope(member.TypeName, lookup.Owner!),
        LookupScope.QualifiedByOwner => IsInScope(member.TypeName, lookup.Owner!)
                                        || index.TypeMentions(member.TypeName, DeclarationWalker.SimpleTypeName(lookup.Owner!)),
        _ => true,
    };

    /// <summary>True when <paramref name="typeName"/> is <paramref name="scope"/> or nested inside it.</summary>
    /// <param name="typeName">Qualified name of the referencing type.</param>
    /// <param name="scope">Qualified name of the type that declares the private member.</param>
    private static bool IsInScope(string typeName, string scope) =>
        typeName.StartsWith(scope, StringComparison.Ordinal)
        && (typeName.Length == scope.Length || typeName[scope.Length] == '.');

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: coverage knows its tests, not spreading further")]
    private partial void LogStoppedByCoverage(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName} is affected because {Because}")]
    private partial void LogAffected(string typeName, string memberName, string because);
}
