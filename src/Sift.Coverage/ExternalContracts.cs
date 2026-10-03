using Mono.Cecil;

namespace Sift.Coverage;

/// <summary>
/// Finds the methods a third-party library calls back: those implementing an interface method or
/// overriding a method declared in an assembly that is neither one of the repository's (the
/// assemblies being instrumented) nor part of the .NET shared framework. Such a library may run the
/// method once and keep what it produced for every later caller: Castle DynamicProxy consults an
/// <c>IProxyGenerationHook</c> only when it first builds the proxy type for an interface, so the test
/// class that happened to substitute the interface first is the only one the map credits
/// (NSubstitute; docs/design/external-callbacks.md).
/// </summary>
internal static class ExternalContracts
{
    /// <summary>File names (without extension) of the shared framework's assemblies, read once.</summary>
    private static readonly Lazy<HashSet<string>> FrameworkAssemblies = new(static () =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in AssemblyInstrumenter.SharedFrameworkDirectories.Value)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return names;
    });

    /// <summary>
    /// True when <paramref name="method"/> implements or overrides a member of a third-party type.
    /// Members are matched by name, which may flag an unrelated overload: that only makes the method
    /// count as possibly run once, the safe direction. A contract whose definition cannot be loaded
    /// counts as matching when it comes from a third-party assembly.
    /// </summary>
    /// <param name="method">A method of the module being instrumented.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    public static bool IsCallback(MethodDefinition method, IReadOnlySet<string> ownAssemblies)
    {
        // Constructors and static methods are not called through a contract (static abstract
        // interface members are rare enough to leave out); non-virtual methods neither.
        if (method.IsConstructor || method.IsStatic || !method.IsVirtual)
        {
            return false;
        }

        // An explicit implementation names what it implements.
        foreach (var implemented in method.Overrides)
        {
            if (IsThirdParty(implemented.DeclaringType, ownAssemblies))
            {
                return true;
            }
        }

        for (var type = method.DeclaringType; type is not null; type = BaseOf(type))
        {
            foreach (var contract in type.Interfaces)
            {
                if (IsThirdParty(contract.InterfaceType, ownAssemblies) && Declares(contract.InterfaceType, method.Name))
                {
                    return true;
                }
            }

            // A method overrides a base method unless it starts a new slot.
            if (!method.IsNewSlot && type != method.DeclaringType && IsThirdParty(type, ownAssemblies) && Declares(type, method.Name))
            {
                return true;
            }
        }

        // The base chain may leave the loaded assemblies: a third-party base that cannot be resolved
        // still declares what it declares.
        return !method.IsNewSlot && UnresolvedThirdPartyBase(method.DeclaringType, ownAssemblies);
    }

    /// <summary>True when <paramref name="type"/> comes from neither the repository nor the shared framework.</summary>
    /// <param name="type">A type reference or definition.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    private static bool IsThirdParty(TypeReference type, IReadOnlySet<string> ownAssemblies)
    {
        var name = type.GetElementType().Scope switch
        {
            AssemblyNameReference reference => reference.Name,
            ModuleDefinition module => module.Assembly.Name.Name,
            var other => other?.Name,
        };

        return name is not null && !ownAssemblies.Contains(name) && !FrameworkAssemblies.Value.Contains(name);
    }

    /// <summary>True when the type declares a method named <paramref name="name"/>, or cannot be loaded to tell.</summary>
    /// <param name="type">A contract type.</param>
    /// <param name="name">Method name.</param>
    private static bool Declares(TypeReference type, string name)
    {
        var definition = TryResolve(type);
        return definition is null || definition.Methods.Any(m => m.Name == name);
    }

    /// <summary>The resolved base type, or null at the root or when it cannot be loaded.</summary>
    /// <param name="type">A type definition.</param>
    private static TypeDefinition? BaseOf(TypeDefinition type) => type.BaseType is { } baseType ? TryResolve(baseType) : null;

    /// <summary>True when the base chain stops at a third-party type that could not be loaded.</summary>
    /// <param name="type">The method's declaring type.</param>
    /// <param name="ownAssemblies">Names of the repository's assemblies.</param>
    private static bool UnresolvedThirdPartyBase(TypeDefinition type, IReadOnlySet<string> ownAssemblies)
    {
        for (var current = type; current.BaseType is { } baseType; )
        {
            if (TryResolve(baseType) is not { } resolved)
            {
                return IsThirdParty(baseType, ownAssemblies);
            }

            current = resolved;
        }

        return false;
    }

    /// <summary>Resolves a type, or null when its assembly cannot be found.</summary>
    /// <param name="type">A type reference.</param>
    private static TypeDefinition? TryResolve(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }
}
