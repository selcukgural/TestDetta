using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

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
/// <param name="graph">
/// Projects of the repository: a name is only looked up in the project that declares it and the
/// projects that depend on it, the only code that can name it (docs/design/run-once-cascades.md).
/// Null looks names up everywhere.
/// </param>
/// <param name="resolveByHandlers">
/// Asked before <paramref name="resolveByCoverage"/> for a member reached by a name it mentions only
/// inside its endpoint handlers (<see cref="IndexedMember.HandlerOnlyReferences"/>), when that name
/// carries no change of a declaration's shape: true when coverage of the member's lambdas answers
/// (docs/design/endpoint-handlers.md). Null disables it.
/// </param>
internal sealed partial class ImpactPropagation(
    SourceIndex index,
    ILogger logger,
    Func<IndexedMember, bool>? resolveByCoverage = null,
    ProjectGraph? graph = null,
    Func<IndexedMember, bool>? resolveByHandlers = null)
{
    /// <summary>
    /// Names whose declaration changed shape: types declared anew or with changed metadata, and members
    /// added, removed or with changed metadata. A framework reads shapes at start-up, through the
    /// handler signatures the route mapping registers, so these never use the handler rule.
    /// </summary>
    private readonly HashSet<string> _shapeNames = new(StringComparer.Ordinal);

    /// <summary>Members answered by their endpoint handlers' coverage alone, with the name that reached them.</summary>
    private readonly Dictionary<IndexedMember, string> _stoppedByHandlers = new(ReferenceEqualityComparer.Instance);

    /// <summary>Per declaring project, the projects whose code can name its members: itself and its dependents.</summary>
    private readonly Dictionary<string, HashSet<string>> _visibleFrom = new(Paths.Comparer);

    /// <summary>Members reached so far.</summary>
    private readonly HashSet<IndexedMember> _affected = new(ReferenceEqualityComparer.Instance);

    /// <summary>Reached members whose spread coverage stopped: it knows which tests run their code.</summary>
    private readonly HashSet<IndexedMember> _stoppedByCoverage = new(ReferenceEqualityComparer.Instance);

    /// <summary>Lookups already queued, so each is done once.</summary>
    private readonly HashSet<NameLookup> _seen = [];

    /// <summary>Lookups still to do.</summary>
    private readonly Queue<NameLookup> _pending = new();

    /// <summary>For each lookup, the member whose names queued it first; absent for lookups a change seeded.</summary>
    private readonly Dictionary<NameLookup, IndexedMember> _lookupOrigin = [];

    /// <summary>How each reached member was reached: the member it was reached from (null for a change) and the name.</summary>
    private readonly Dictionary<IndexedMember, (IndexedMember? From, string Name)> _reachedFrom = new(ReferenceEqualityComparer.Instance);

    /// <summary>For each affected type, the member that made it affected first, or null when the change itself names the type.</summary>
    private readonly Dictionary<string, IndexedMember?> _typeReachedBy = new(StringComparer.Ordinal);

    /// <summary>For each type in <see cref="SeededTypes"/>, the member whose spread seeded its name first, or null for a change.</summary>
    private readonly Dictionary<string, IndexedMember?> _typeSeededBy = new(StringComparer.Ordinal);

    /// <summary>Types in <see cref="SeededTypes"/> whose name a change or changed metadata spread, not only reached code.</summary>
    private readonly HashSet<string> _typesSeededNotByCode = new(StringComparer.Ordinal);

    /// <summary>The changes as seeded, by type, for the end of an explanation.</summary>
    private readonly Dictionary<string, SymbolChange> _seededChanges = new(StringComparer.Ordinal);

    /// <summary>How a member was reached, which decides whether coverage may answer for it.</summary>
    private enum Reach
    {
        /// <summary>The member is a change itself; the caller asked coverage about it before seeding it.</summary>
        Change,

        /// <summary>Its code names a reached name: coverage knows which tests run that code.</summary>
        Code,

        /// <summary>
        /// Its attributes or parameter defaults name a reached name, or are a change themselves
        /// (<see cref="SymbolChange.MetadataChanged"/>): that is metadata, which reflection reads without
        /// running the member, so coverage cannot say who depends on it.
        /// </summary>
        Metadata,
    }

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
    /// <param name="Project">Project that declares what the name stands for; only it and its dependents can name it.</param>
    private readonly record struct NameLookup(string Name, string? Owner, LookupScope Scope, string Project);

    /// <summary>
    /// Types whose name was spread, mapped to every project that declares (or declared) them: a file in
    /// a folder several projects compile (one project per Roslyn version) declares its types in each.
    /// </summary>
    public Dictionary<string, HashSet<string>> SeededTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// True when only members reached through their code spread the name of <paramref name="typeName"/>:
    /// neither the type nor a member of it changed, and no metadata of it names a changed name. Such a
    /// type behaves differently only where its code runs, which a coverage map records.
    /// </summary>
    /// <param name="typeName">Qualified name of a type in <see cref="SeededTypes"/>.</param>
    public bool SeededThroughCodeOnly(string typeName) =>
        SeededTypes.ContainsKey(typeName) && !_typesSeededNotByCode.Contains(typeName);

    /// <summary>Qualified names of every type that changed or contains an affected member.</summary>
    public HashSet<string> AffectedTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the spread reached <paramref name="member"/>: its code can lead to a seeded change.</summary>
    /// <param name="member">Indexed member.</param>
    public bool Reached(IndexedMember member) => _affected.Contains(member);

    /// <summary>Starts from one detected change.</summary>
    /// <param name="change">A changed type or member.</param>
    /// <param name="projectPath">Project that owns the changed file; used when the type no longer exists.</param>
    public void Seed(SymbolChange change, string projectPath)
    {
        AddAffectedType(change.TypeName, null);
        _seededChanges.TryAdd(change.TypeName, change);
        if (change.MemberName is null || change.MetadataChanged || change.Kind != SymbolChangeKind.Modified)
        {
            _shapeNames.Add(DeclarationWalker.SimpleTypeName(change.TypeName));
            if (change.MemberName is not null)
            {
                _shapeNames.UnionWith(DeclarationWalker.ReferenceNames(change.MemberName));
            }
        }

        // A removed member may have been reached through its type (an override, an interface
        // implementation), and nothing is left in the index to say which.
        if (change.MemberName is null || change.Kind == SymbolChangeKind.Removed)
        {
            SeedType(change.TypeName, projectPath, by: null, Reach.Change);
        }

        if (change.MemberName is null)
        {
            return;
        }

        if (index.FindMember(change.TypeName, change.MemberName, projectPath) is { } member)
        {
            // Changed metadata is read through the type, by whatever names it.
            MarkAffected(member, "it changed", from: null, change.MetadataChanged ? Reach.Metadata : Reach.Change);
            return;
        }

        foreach (var name in DeclarationWalker.ReferenceNames(change.MemberName))
        {
            Enqueue(new NameLookup(name, null, LookupScope.Anywhere, projectPath));
        }
    }

    /// <summary>
    /// Follows the queued names until no new member is reached. A member answered by its endpoint
    /// handlers through a name that turned out to carry a shape change (seen later in the spread) is
    /// answered as a whole after all, and the spread goes on from there.
    /// </summary>
    public void Run()
    {
        do
        {
            while (_pending.TryDequeue(out var lookup))
            {
                foreach (var member in index.ReferencesTo(lookup.Name))
                {
                    if (Matches(lookup, member))
                    {
                        MarkAffected(member, lookup.Name, _lookupOrigin.GetValueOrDefault(lookup), member.MetadataReferences.Contains(lookup.Name) ? Reach.Metadata : Reach.Code);
                    }
                }
            }

            foreach (var (member, name) in _stoppedByHandlers.Where(s => _shapeNames.Contains(s.Value)).ToList())
            {
                ResolveWhole(member, name, Reach.Code);
            }
        }
        while (_pending.Count > 0);
    }

    /// <summary>True when the handler rule may answer for <paramref name="member"/> reached by <paramref name="name"/>.</summary>
    /// <param name="member">A member reached through its code.</param>
    /// <param name="name">The name that reached it.</param>
    private bool ThroughHandlersOnly(IndexedMember member, string name) =>
        resolveByHandlers is not null && member.HandlerOnlyReferences.Contains(name) && !_shapeNames.Contains(name);

    /// <summary>
    /// Takes back the handler-only answer for <paramref name="member"/>: asks coverage about the whole
    /// member and spreads its names when that cannot answer either.
    /// </summary>
    /// <param name="member">A member answered by its handlers' coverage.</param>
    /// <param name="because">The name that reaches it now.</param>
    /// <param name="reach">How it is reached now.</param>
    private void ResolveWhole(IndexedMember member, string because, Reach reach)
    {
        _stoppedByHandlers.Remove(member);
        LogHandlersNotEnough(member.TypeName, member.MemberName ?? "(type)", because);
        if (reach == Reach.Code && resolveByCoverage?.Invoke(member) == true)
        {
            _stoppedByCoverage.Add(member);
            return;
        }

        Spread(member, reach);
    }

    /// <summary>Records <paramref name="member"/> as affected and queues the names it can be reached by.</summary>
    /// <param name="member">Newly reached member.</param>
    /// <param name="because">The name that reached it, for the debug log and the explanation.</param>
    /// <param name="from">The member whose names reached it; null for a change itself.</param>
    /// <param name="reach">How it was reached; only code reached by name is asked about coverage.</param>
    private void MarkAffected(IndexedMember member, string because, IndexedMember? from, Reach reach)
    {
        if (!_affected.Add(member))
        {
            // Coverage answered for the member's code; now its metadata is reached as well.
            if (reach == Reach.Metadata && _stoppedByCoverage.Remove(member))
            {
                LogReachedThroughMetadata(member.TypeName, member.MemberName ?? "(type)", because);
                Spread(member, reach);
            }

            // Its handlers answered; now a name outside them (or metadata) reaches it too.
            if (_stoppedByHandlers.ContainsKey(member) && (reach != Reach.Code || !ThroughHandlersOnly(member, because)))
            {
                ResolveWhole(member, because, reach);
            }

            return;
        }

        LogAffected(member.TypeName, member.MemberName ?? "(type)", because);
        _reachedFrom[member] = (from, because);
        AddAffectedType(member.TypeName, member);

        if (reach == Reach.Code && ThroughHandlersOnly(member, because) && resolveByHandlers!(member))
        {
            LogStoppedByHandlers(member.TypeName, member.MemberName ?? "(type)", because);
            _stoppedByHandlers[member] = because;
            return;
        }

        if (reach == Reach.Code && resolveByCoverage?.Invoke(member) == true)
        {
            LogStoppedByCoverage(member.TypeName, member.MemberName ?? "(type)");
            _stoppedByCoverage.Add(member);
            return;
        }

        Spread(member, reach);
    }

    /// <summary>Queues the names <paramref name="member"/> can be reached by, and its type's name when code may reach it without them.</summary>
    /// <param name="member">Affected member that coverage did not answer for.</param>
    /// <param name="reach">How it was reached; changed metadata is read through the type.</param>
    private void Spread(IndexedMember member, Reach reach)
    {
        // Reached through metadata, the member's own declaration reads differently: its names carry that.
        if (reach == Reach.Metadata)
        {
            _shapeNames.UnionWith(member.DeclaredNames);
            _shapeNames.Add(DeclarationWalker.SimpleTypeName(member.TypeName));
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
            Enqueue(new NameLookup(name, scope == LookupScope.Anywhere ? null : member.TypeName, scope, member.ProjectPath), member);
            referencedByName |= index.IsReferencedByOthers(name, member);
        }

        // Spread the type name when the member can run without being named, when nothing names it
        // (so something outside the repository calls it), for type-level changes, and for anything in
        // a test project: test classes inherit fixtures and setup from their base classes. Changed
        // metadata is read through the type (typeof(T).GetProperties()), by whatever names the type.
        if (member.MemberName is null || member.MayBeCalledImplicitly || !referencedByName || member.InTestProject || reach == Reach.Metadata)
        {
            SeedType(member.TypeName, member.ProjectPath, member, reach);
        }
    }

    /// <summary>Records <paramref name="typeName"/> as affected, remembering what made it so first.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="by">Reached member of the type; null when a change names the type.</param>
    private void AddAffectedType(string typeName, IndexedMember? by)
    {
        if (AffectedTypes.Add(typeName))
        {
            _typeReachedBy[typeName] = by;
        }
    }

    /// <summary>
    /// How the spread reached <paramref name="typeName"/>, from the type back to a change, at most
    /// <paramref name="limit"/> steps: <c>OrdersTests.Places names Checkout ← OrderService.Checkout names Total ← changed PriceCalculator.Total</c>.
    /// The breadth-first spread makes it a shortest path. Null when the type was not reached.
    /// </summary>
    /// <param name="typeName">Qualified name of an affected type, typically a test class.</param>
    /// <param name="limit">Most steps listed; a longer chain ends with "…".</param>
    public string? Explain(string typeName, int limit = 8)
    {
        if (!_typeReachedBy.TryGetValue(typeName, out var member))
        {
            return null;
        }

        return ExplainFrom(typeName, member, limit);
    }

    /// <summary>
    /// Why the name of <paramref name="typeName"/>, one of <see cref="SeededTypes"/>, was spread: the
    /// chain back to a change from the member that seeded it, as <see cref="Explain"/> writes it.
    /// Null when the type name was not spread.
    /// </summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="limit">Most steps listed; a longer chain ends with "…".</param>
    public string? ExplainSeeded(string typeName, int limit = 8) =>
        _typeSeededBy.TryGetValue(typeName, out var member) ? ExplainFrom(typeName, member, limit) : null;

    /// <summary>The chain from <paramref name="member"/> back to a change; see <see cref="Explain"/>.</summary>
    /// <param name="typeName">Qualified type name, for a type the change itself names.</param>
    /// <param name="member">First member of the chain; null when a change names the type.</param>
    /// <param name="limit">Most steps listed.</param>
    private string ExplainFrom(string typeName, IndexedMember? member, int limit)
    {
        if (member is null)
        {
            return _seededChanges.TryGetValue(typeName, out var change) ? $"changed {Describe(change)}" : $"changed {typeName}";
        }

        var steps = new List<string>();
        for (var current = member; current is not null; )
        {
            if (steps.Count == limit)
            {
                steps.Add("…");
                break;
            }

            var (from, name) = _reachedFrom.GetValueOrDefault(current);
            var label = $"{current.TypeName}.{current.MemberName ?? "(type)"}";
            steps.Add(from is not null ? $"{label} names {name}"
                : name == "it changed" ? $"changed {label}"
                : $"{label} names {name}, a changed name");
            current = from;
        }

        return string.Join(" ← ", steps);
    }

    /// <summary><c>Type.Member</c> of a change, or the type alone.</summary>
    /// <param name="change">A seeded change.</param>
    private static string Describe(SymbolChange change) => change.MemberName is null ? change.TypeName : $"{change.TypeName}.{change.MemberName}";

    /// <summary>Queues the simple name of <paramref name="typeName"/> and remembers where the type lives.</summary>
    /// <param name="typeName">Qualified type name.</param>
    /// <param name="projectPath">Project that declares the type.</param>
    /// <param name="by">The member whose spread seeded the type name; null for a change.</param>
    /// <param name="reach">How <paramref name="by"/> was reached; <see cref="Reach.Change"/> for a change.</param>
    private void SeedType(string typeName, string projectPath, IndexedMember? by, Reach reach)
    {
        if (reach != Reach.Code)
        {
            _typesSeededNotByCode.Add(typeName);
        }

        if (!SeededTypes.ContainsKey(typeName) && logger.IsEnabled(LogLevel.Debug))
        {
            LogTypeSpread(typeName, by is null ? "it changed" : $"{by.TypeName}.{by.MemberName ?? "(type)"}", reach);
        }

        if (!SeededTypes.TryGetValue(typeName, out var projects))
        {
            projects = new HashSet<string>(Paths.Comparer);
            SeededTypes[typeName] = projects;
            _typeSeededBy[typeName] = by;
        }

        projects.Add(projectPath);
        Enqueue(new NameLookup(DeclarationWalker.SimpleTypeName(typeName), null, LookupScope.Anywhere, projectPath), by);
    }

    /// <summary>Queues <paramref name="lookup"/> unless the same lookup was queued before.</summary>
    /// <param name="lookup">Name and scope.</param>
    /// <param name="origin">The member whose names queue it; null for a change.</param>
    private void Enqueue(NameLookup lookup, IndexedMember? origin = null)
    {
        if (_seen.Add(lookup))
        {
            _pending.Enqueue(lookup);
            if (origin is not null)
            {
                _lookupOrigin[lookup] = origin;
            }
        }
    }

    /// <summary>True when <paramref name="member"/>, which mentions the looked-up name, is within the lookup's scope.</summary>
    /// <param name="lookup">Name and scope.</param>
    /// <param name="member">A member that mentions <see cref="NameLookup.Name"/>.</param>
    private bool Matches(NameLookup lookup, IndexedMember member)
    {
        if (!CanName(member, lookup.Project))
        {
            return false;
        }

        return lookup.Scope switch
        {
            LookupScope.OwnerOnly => IsInScope(member.TypeName, lookup.Owner!),
            LookupScope.QualifiedByOwner => IsInScope(member.TypeName, lookup.Owner!)
                                            || index.TypeMentions(member.TypeName, DeclarationWalker.SimpleTypeName(lookup.Owner!)),
            _ => true,
        };
    }

    /// <summary>
    /// True when <paramref name="member"/>'s code can name something declared in <paramref name="project"/>:
    /// it is compiled into that project or one that depends on it. Code using <c>dynamic</c> binds
    /// names at run time, to objects from any project, so it can name anything.
    /// </summary>
    /// <param name="member">A member that mentions the looked-up name.</param>
    /// <param name="project">Project that declares what the name stands for.</param>
    private bool CanName(IndexedMember member, string project)
    {
        if (graph is null || Paths.Comparer.Equals(member.ProjectPath, project) || member.References.Contains("dynamic"))
        {
            return true;
        }

        if (!_visibleFrom.TryGetValue(project, out var visible))
        {
            // A project outside the graph (a removed one) is visible from everywhere: nothing says otherwise.
            visible = graph.Find(project) is null ? [] : graph.WithDependents([project]);
            _visibleFrom[project] = visible;
        }

        return visible.Count == 0 || visible.Contains(member.ProjectPath);
    }

    /// <summary>True when <paramref name="typeName"/> is <paramref name="scope"/> or nested inside it.</summary>
    /// <param name="typeName">Qualified name of the referencing type.</param>
    /// <param name="scope">Qualified name of the type that declares the private member.</param>
    private static bool IsInScope(string typeName, string scope) =>
        typeName.StartsWith(scope, StringComparison.Ordinal)
        && (typeName.Length == scope.Length || typeName[scope.Length] == '.');

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: {Because} appears only in its endpoint handlers; test classes taken from their coverage")]
    private partial void LogStoppedByHandlers(string typeName, string memberName, string because);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: reached through {Because} as well, outside its endpoint handlers or with a changed shape; answered as a whole")]
    private partial void LogHandlersNotEnough(string typeName, string memberName, string because);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: its metadata names {Because}, which coverage cannot see; spreading after all")]
    private partial void LogReachedThroughMetadata(string typeName, string memberName, string because);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Spreading the type name {TypeName}: {Member} may run without being named ({Reach})")]
    private partial void LogTypeSpread(string typeName, string member, Reach reach);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: coverage knows its tests, not spreading further")]
    private partial void LogStoppedByCoverage(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName} is affected because {Because}")]
    private partial void LogAffected(string typeName, string memberName, string because);
}
