using Mono.Cecil;

namespace TestDetta.Coverage;

/// <summary>
/// Cecil's metadata resolver with a guard against type forwarding loops: resolving a forwarded type follows the
/// forwarder into the next assembly, and assemblies that forward a type to each other (facades of two frameworks)
/// made that recursion endless, which no catch can stop (a stack overflow ends the process). A type already being
/// resolved on this thread resolves to null, as one whose assembly cannot be found does, and callers treat it the
/// same way.
/// </summary>
/// <param name="assemblyResolver">The assembly resolver.</param>
internal sealed class CycleSafeMetadataResolver(IAssemblyResolver assemblyResolver) : MetadataResolver(assemblyResolver)
{
    /// <summary>The type references being resolved on this thread, by scope and full name.</summary>
    [ThreadStatic]
    private static HashSet<string>? _resolving;

    /// <summary>Resolves <paramref name="type"/>, or null when resolving it would enter a forwarding loop.</summary>
    /// <param name="type">The type reference.</param>
    public override TypeDefinition? Resolve(TypeReference type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var key = $"{type.Scope?.Name}|{type.FullName}";
        _resolving ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_resolving.Add(key))
        {
            return null;
        }

        try
        {
            return base.Resolve(type);
        }
        finally
        {
            _resolving.Remove(key);
        }
    }
}
