# Run-once cascades

Status: proposal, 2026-10-03. Not implemented. Precision round, NodaTime.

## Problem

On NodaTime, 83% of the classes Sift selects do not fail, and 76% of those come from "ran a member
the change reaches by name". The selection share is 42.6%. The cause is a chain reaction in the
coverage trace (the spread that stops wherever a coverage map knows the tests):

1. A reached member that coverage cannot answer alone is traced by name.
2. If that member may run without being named (an interface implementation, an override, a type
   declaration), its type name is spread, and every member naming the type is reached.
3. Among those, the next unanswerable member spreads its type, and so on.

In 37 of 100 NodaTime changes, the coverage trace spread almost every type name of the repository.
Once a widely used type such as `LocalDate` or `Duration` spreads, the union of the classes that ran
any member naming it is close to every test class. A type declaration is even reached by its own
name (`struct LocalDate : IEquatable<LocalDate>`).

### Why coverage could not answer (100 NodaTime changes, coverage trace)

Counted with the debug line `coverage cannot answer alone: <reason>` (added with this note):

| Reason | Reached members |
|---|---|
| it ran in a static constructor or cache fill | 44,264 |
| no affected test project with a map depends on it | 32,162 |
| a constructor or static member that ran in one bucket per process | 26,942 |
| a map does not know it, or it ran outside any test (fields, constants, abstract members) | 26,720 |
| a member of the type ran in a static constructor or cache fill (type-level) | 4,748 |
| a member of its type ran outside any test | 900 |
| a static constructor runs once per process | 444 |

Two patterns stand out:

- **Run-once code** (first, third and fifth rows; 76k). The rules were made for safety: code run
  once per process is credited only to the first class that needed it, so the change is traced by
  name as well. NodaTime is full of such code: time zone data, caches, static tables.
- **Members no map project depends on** (32k). These are in tools and benchmarks that no affected
  test project with a map depends on. The coverage trace can only reach them by name. Their names
  are then looked up across the whole repository and collide with NodaTime's own members. NodaTime
  does not reference the tools, so these collisions cannot be real references.

## Proposals

Ordered by expected gain per risk. Each one is measured on the corpus with `sift-eval --explain` and
`eval/precision/attribute.py`, and it ships only if no mutant is missed.

### A. Name lookups only reach projects that can see the member (sound)

A member of project P can only be named by code compiled into P or into a project that depends on
P (directly or transitively; `InternalsVisibleTo` still needs the reference). A lookup started by a
member of P would only match members of P and its dependents. Lookups get the origin project. The
dedupe key becomes (name, owner, scope, project), which costs more lookups, but each is cheaper.

- A file compiled into several projects already gives one member per project, so each copy looks up
  from its own project.
- Opaque references (analyzers, `ReferenceOutputAssembly="false"`) are project-level changes
  already.
- **Exception: `dynamic`.** `d.Foo()` binds by name at run time, to an object that may come from a
  project the code does not reference. Members whose code uses `dynamic` stay visible to every
  lookup (a new index flag). Names in strings (`GetMethod("Foo")`) are not traced today either.

This cuts the collision back-flow (the 32k row) in both traces. The name-only trace for projects
without maps benefits as well.

### B. A type declaration is not reached by its own name

`LocalDate`'s declaration names `LocalDate` only to state its own identity
(`IEquatable<LocalDate>`, `IComparable<LocalDate>`). When the spread of `LocalDate`'s own name
reaches its declaration, nothing in the declaration changed meaning. The lookup should skip the
declaration of the type whose name is spread; members of the type are still matched. Small and
sound. The gain is limited, since members naming the type are still reached.

### C. Answer run-once code through what it built (the large one)

Field-read probes already record which buckets read a value that run-once code built
(`builtValues`, docs/design/field-read-probes.md). The run-once rules still trace by name on top,
because a value can reach later classes without a probed field read. That is the cascade's main
fuel. Close those paths, then drop the name trace where the probes see the whole flow:

1. **Cache entries.** A cache fill (`GetOrAdd`, `TryGetValue` + `Add`, indexer store) puts its value
   into a collection held in a tracked field. Count a mutating call on an object loaded from a
   tracked field (`Add`, `set_Item`, `TryAdd`, `GetOrAdd`, `AddOrUpdate`, `Push`, `Enqueue`,
   `Insert`) as a store into that field. The builders of the entry are then credited to every bucket
   that reads the field. The instrumenter already finds such receivers for pools
   (`ReceiverField`).
2. **Lazy values.** `Lazy<T>.Value` and `LazyInitializer` fill a value inside an object held in a
   field. Treat the first `Value` call on a `Lazy` from a tracked field as a store into that field.
3. **Linked run-once code.** Run-once code whose effect the probes saw stored in a tracked field is
   answered by the classes that ran it plus the readers of what it built (`builtValues`). The name
   trace remains only for run-once code with no recorded store: its value left through a path the
   probes do not see (another thread, a return value kept in a local object). The map records,
   per method, whether a build it took part in ended in a probed store (`linkedBuilders`).
4. **Single-bucket constructors and statics** (26.9k) are the same question with weaker evidence.
   With 3 they are answered the same way when linked.

Risk: a value built once and handed over through an unprobed path would be missed. Mitigations: keep
the name trace for unlinked builders, and validate on the round-2 corpus, where every miss is
confirmed by running the mutant alone. Cost: recording time, measured on NodaTime, Newtonsoft.Json
and OpenTelemetry, like the probes were.

### D. Fields reached by name (part of the 26.7k)

A field has no method of its own in the map. Its initializer runs in the constructors (instance) or
the static constructor (static) of its type. A reached field whose initializer names the change
could be answered by the coverage of those constructors, under the run-once rules. Constants stay
name-traced, since they are compiled into their users.

## Plan

1. A and B, together with tests, a measurement on the four precision repositories, and the corpus
   rerun after round 2. Expected: NodaTime and protobuf-net shares drop, no miss.
2. C as its own design iteration, after A/B's numbers show how much of the cascade is left:
   recording probes first (cache entries, Lazy), then `linkedBuilders`, then the selection change
   behind a measurement.
3. D last; measure first how often fields carry a cascade.
