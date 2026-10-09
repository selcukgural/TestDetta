# Endpoint handlers answered by their own coverage

Status: implemented 2026-10-09 (owner approved while dogfooding on AfterApply).

## Problem

AfterApply PR #205 changed a static field of `SiteTrafficNormalizer`. The type's name spread and reached
`SiteTrafficEndpoints.MapSiteTrafficEndpoints`:

```csharp
app.MapPost("/api/site-traffic", async (request, httpContext, service, ct) =>
{
    if (!SiteTrafficNormalizer.IsNonHumanUserAgent(httpContext.Request.Headers.UserAgent))
        await service.RecordAsync(request, ct);
});
```

The mapping runs whenever a test host starts: 29 classes and 54 fixtures ran it, so all 97 classes that boot
the app were selected. The handler, the only place the name appears, ran in 4. The map could not tell the two
apart: `MethodNames.Map` credits a lambda (`<MapSiteTrafficEndpoints>b__0_0`) to the member it is written in.

## Design

- **Map**: `nestedMethods`, the ids of methods compiled from lambdas, anonymous methods and local functions,
  or their state machines (`MethodNames.IsNested`). Optional; a map without it keeps today's answers.
- **Index**: `IndexedMember.HandlerOnlyReferences`, the names a C# member mentions only inside the block
  bodies of lambdas passed straight to a route mapping (`Map`, `MapGet`, `MapPost`, `MapPut`, `MapDelete`,
  `MapPatch`, `MapMethods`, `MapFallback`), outside their `return` statements.
- **Selection** (`ImpactPropagation`, `CoverageSelection.TryResolveNested`): a member reached through its
  code by such a name is answered by the classes that ran its nested methods. The answer is taken back, and
  the whole member asked about, when a name outside the handlers or its metadata reaches it later, or when
  the name turns out to carry a shape change.

## Why only endpoint handlers, and what stays out

- **Run-once lambdas.** A DI factory (`services.AddSingleton(sp => new X(...))`) or an options callback
  (`services.Configure<T>(o => ...)`) runs once and its result is reused by every later class; the classes
  that ran it are not all its users. Route handlers run per request, so the classes that ran them are.
- **What a handler returns.** Minimal APIs read the handler's signature at start-up (model binding, OpenAPI),
  and its return type comes from what it returns. Names in the handler's parameters, attributes and `return`
  statements count as the mapping's own; an expression-bodied handler is all return value.
- **Shape changes.** A type declared anew or with changed metadata, and a member added, removed or with
  changed metadata, may change what the framework reads at start-up through the handler's signature (a
  local variable can carry a type into the return value). Their names never use the rule.
- **Run-once nested methods.** A nested method that ran outside any test, inside a static constructor or
  cache fill, or that is a third-party callback makes the member's nested view unanswerable.
- All lambdas of the member count together, so a mapping with several handlers selects the classes that
  ran any of them: more than needed, never less.

## Results

AfterApply PR #205, maps at its merge base: integration tests 97 → 4 of 100 classes, the whole suite 84 %
of the recorded test time saved. PR #202 (a response DTO's shape) is unaffected: the DTO's name is a shape
change and appears in the mappings' own `.Produces<T>()` calls.

Tests: `EndpointHandlerTests` (a name only in a handler's body; in the mapping's own body; in a handler's
`return`; a shape change), sabotage-checked.
