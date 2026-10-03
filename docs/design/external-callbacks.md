# Code a third-party library calls back and caches

Status: implemented 2026-10-03. Found by round 2 on NSubstitute (2 of 63 failing mutants missed, 12 test classes).

## Problem

NSubstitute builds substitutes with Castle DynamicProxy. Castle consults NSubstitute's
`IProxyGenerationHook` (`AllMethodsExceptCallRouterCallsHook.ShouldInterceptMethod` and the
private helpers it calls) only when it first generates the proxy type for an interface. After
that, the type is cached for the process. `ThrowingExceptions` substitutes `ISomething`, which an
earlier class had already proxied, so it never ran the hook. The map correctly says so, and a
mutant in the hook changes the cached proxy type every later class uses. Six classes failed, none
were selected.

Sift's run-once rules (static constructors, `Lazy`, caches in fields and collections, pools) see
caches in the repository's code. This cache is in a third-party library, where nothing is
instrumented.

## Design (option 1, user decision 2026-10-03)

- **Which methods.** At instrumentation, Cecil marks a repository method as an *external callback*
  in two cases:
  - it implements an interface method of a third-party assembly (implicitly, matched by name, or
    explicitly);
  - it overrides a virtual method of a third-party base class.

  A third-party assembly is neither one of the assemblies being instrumented (the repository's)
  nor one of the .NET shared framework's (`ExternalContracts`). Matching by name may flag an
  unrelated overload, which is the safe direction. A contract that cannot be loaded counts as
  matching.
- **Recording.** An external callback's body is wrapped in the cache-fill scope
  (`SiftTracker.EnterCache`/`ExitCache`), like a `GetOrAdd` call. The callback and everything it
  runs are recorded as initialization, so the private helpers count as run-once code too (the
  second NSubstitute mutant was in one). The ids are also kept in the map as `externalCallbacks`.
  That covers callbacks no test ran, and lets a type-level change see that the type has one.
- **Selection.** A member that ran inside a callback, or is one, may be run-once code. The classes
  the map names stay selected, and the change is traced by name as well. A type with a callback
  member is not answered by type-level coverage: the constructor and type rules and
  `TryResolveType` keep their fallbacks.

The .NET shared framework is excluded on purpose. Its callbacks (`IComparer`, `IDisposable`,
LINQ delegates) are overwhelmingly called per use, and including them would make most code
run-once. Framework caches that call repository code back, such as `JsonConverterFactory` and
`TypeConverter`, are a known gap (see the backlog). Delegates handed to a third-party library
(`services.AddSingleton(sp => new X())`) are not interface implementations and are not covered
either. The between-tests rules cover DI singletons built outside tests.

## Results

NSubstitute, the round-2 commit, a fresh recording, 100 mutants, `sift-eval --explain`:

| | Missed | Mean selected share |
|---|---|---|
| Before | 2 | 46.3% |
| After | 0 | 73.8% |

Interceptors (`CastleForwardingInterceptor.Intercept`) are external callbacks too, and almost all
of NSubstitute runs under them. They are called per call, not cached, but nothing tells the two
apart from the outside. 98.5% of the extra selection, though, comes from the name spread reaching
a repository-declared `MaybeNullAttribute` polyfill, which the reflection fallback turns into
whole-project runs. That is a separate precision item.

Tests: `ExternalCallbackTests` (a vendor library built outside the repository that caches per type;
the explicit, override, framework and own-interface cases; sabotage-checked), and
`TestSelectorTests.Member_a_third_party_library_calls_back_is_traced_by_name_too`.
