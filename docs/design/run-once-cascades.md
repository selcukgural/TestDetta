# Run-once cascades

Status: A and C implemented 2026-10-03 (B measured and dropped); D proposed. Precision round, NodaTime.

## Problem

On NodaTime, 83% of the classes TestDetta selects do not fail, and 76% of those come from "ran a member
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

Ordered by expected gain per risk. Each one is measured on the corpus with `testdetta-eval --explain` and
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

## As built: A (2026-10-03)

`ImpactPropagation` takes the project graph. Every lookup carries the project that declares what
the name stands for, and matches only members of that project and its dependents
(`ImpactPropagation.CanName`), or members whose code uses `dynamic`. Tests:
`ProjectVisibilityTests` (a tool's `Run` no longer reaches a library test calling its own `Run`; a
`dynamic` call still does), sabotage-checked.

Measured on round-2 maps, 100 mutants each, no missed mutant:

| Repository | Before | A |
|---|---|---|
| NodaTime | 42.6% | 41.0% |
| protobuf-net | 89.5% | 87.6% |
| CommunityToolkit | 17.1% | 16.8% |
| MimeKit | 21.9% | 21.9% |

B (skipping a type's self-reference) changed nothing measurable on top of A, so it was dropped. It
could also have removed a type-wide answer that cascades lean on today. The cascade is run-once code
(C), as the counts above suggest.

## As built: C (2026-10-03)

Recording (`TestDettaTracker`, `AssemblyInstrumenter`, `CoverageRecorder`):

- **Scopes.** `EnterCache`, `EnterTypeInitializer` and third-party callbacks push a scope that
  remembers where it started in the thread's ring of recent hits. A fill whose target field is known
  ends with `ExitCacheInto(field, method)`: what ran since the scope started becomes the field's
  builders, and the scope is linked. A static constructor that stores its own fields is linked by
  that store (`Stored` with the scope's owner). Everything else, a wrapped ring and scopes abandoned
  by a throw or a new test end unlinked. The map's `linkedInitialization` holds the initialization
  methods that ran in a linked scope in some process and in an unlinked one in none.
- **Stores through a field's object.** `Add`, `TryAdd`, `set_Item`, `Insert`, `Push`, `Enqueue`,
  `AddOrUpdate`, `AddRange`, `TryUpdate` and `Set` on an object loaded from a tracked field run
  `Stored` for that field. A field that receives such calls or fills is tracked even when only a
  constructor assigns it.
- **Receivers on every path** (`StackOrigins`). Receivers and `ref` targets come from a forward
  pass over the method's stack, not from walking back from the call. The compiler's cached
  lambdas branch between the receiver and the call
  (`Labels.GetOrAdd(key, static k => ...)` loads the delegate with `ldsfld; dup; brtrue`), and the
  walk-back stopped there, which left the fill unlinked.
- **Values built from values.** A field read also enters the ring, so a value built while reading
  another field's value lists that field among its builders. The recorder follows such fields
  transitively. NodaTime builds its zone cache from the parsed TZDB data, and before this its
  readers were not credited with the parser.
- **A test class's static initializer is its data** (`DataSources`). A static field named as a
  data source has no code, and reading it runs the static constructor of its type. A concrete test
  class's own static constructor builds what its tests receive, and it runs whenever a framework
  first touches the class, often while another class is current. Both now enter the class's bucket
  as data members do. NodaTime's `TzdbDateTimeZoneTest` and `ZonedDateTimePatternTest` built their
  zones while `TzdbDateTimeZoneSourceTest` was current. Once the TZDB loading was linked, a change
  to it missed them.

- **Hand-rolled caches** (2b57cec). A build on a `TryGetValue`/`Pop` miss of a reused collection
  (`MarkPoolFills`) ends with `ExitCacheInto` for that collection when exactly one collection field
  the method loads holds the item type (NodaTime's `DateTimeZoneCache`).
- **Scopes longer than the ring.** The ring keeps the last 65,536 hits per thread; a scope longer
  than that (all of TZDB parsed in one static constructor, all zones built for a data source) was
  unlinked, as its builders were incomplete. Before each lap of the ring, the part of every open
  scope it would overwrite now goes into that scope's own bit set (`SpillScopes`), so a scope's
  contents are complete however long it runs. Short scopes pay nothing; recording time on NodaTime
  did not change measurably.

- **Readonly statics of any type and array elements.** Static `readonly` fields are tracked
  whatever their type (NodaTime's `Offset.Zero` is a struct built by the static constructor), and an
  element stored into an array held in a tracked field is a store into the field
  (`CalendarByOrdinal[ordinal] = this`). On NodaTime this moves about 80 classes from the name trace
  to direct coverage evidence (their readers are now credited with what built the values), so the
  share stays at 22.9%; it is what lets the single-bucket rule be narrowed later.

- **A static field depends on its whole static constructor.** A static constructor either
  completes or leaves its type unusable, so reading a field it stored early fails when a later part
  throws. NodaTime's `NodaConstants` stores `UnixEpoch`, then builds `BclEpoch` through the
  calendar; a broken `YearMonthDayCalculator` constructor fails every test reading `UnixEpoch`.
  When a static constructor ends, everything it ran becomes a builder of every field it stored
  (`ExitTypeInitializer`); before, a field had only what ran up to its own store. This was the last
  case on NodaTime where the single-bucket rule was needed (2 failing classes of 60 mutants, measured
  2026-10-03 with `seeds.py`); it is 0 now, the share stays at 22.9%, no mutant missed. An earlier
  reading blamed the constructor arguments below; the map showed the two classes never touched the
  calendar, only `NodaConstants`.
- **Every use of a type depends on its explicit static constructor.** A type with an explicit
  static constructor (no `beforefieldinit`) runs it before it is first created or any static member
  is used, and stays unusable when it throws, whether or not the user reads what it stored.
  MimeKit's `MimeEncoderTestsBase` builds test data through `FilteredStream` in its static
  constructor; `PassThroughEncoderTests` derives from it, never reads that data, and failed with
  every mutant of `MimeFilterBase.PreFilter`. Once long scopes were linked (49ff99d) that code was no
  longer run-once, and the class was missed (found 2026-10-03 with fresh recordings at 351f43f). Each
  such type gets a field index standing for its initialization (`GuardedTypes`): everything the
  static constructor runs builds it (`EnterTypeInitializerOf`), and the type's constructors, its
  static members and static field accesses from outside it read it. A derived class's constructor
  runs the base constructor, so derived test classes are credited too. The scope is not linked by
  this, since what the constructor built may still leave unseen. Not covered: an untracked static
  field of such a type used from another assembly. Measured: MimeKit 14.8% → 14.9% with the miss
  gone, CommunityToolkit 17.0% and NodaTime 22.9% unchanged.
- **Constructor arguments.** A constructor that stores its own object into a tracked place
  (`CalendarByOrdinal[ordinal] = this`, `Registry.Add(this)`, directly or through `: this(...)` /
  `: base(...)` in the same assembly) records as builders only what ran since it was entered; the
  caller built its arguments before that. Each `newobj` of such a constructor is surrounded by
  `EnterConstruction`/`ExitConstruction`, and `MarkArguments` goes where its arguments start to be
  built: the first instruction of the argument expressions or, earlier, of the definitions of the
  locals they read (and of the locals those read), in straight-line code (`var calculator = new X();
  new CalendarSystem(..., calculator)`). The self-store (`StoredSelf`) adds what the innermost
  construction ran since that mark. Only self-stores do, and only from the mark: crediting every
  constructor store with its whole open scope (tried first) raised NodaTime's share from 22.9% to
  25.2%. Not followed: arguments built by the enclosing method's caller (a parameter passed on), a
  chain into another assembly, construction by reflection; those stores keep `Stored`'s builders.
  NodaTime does not need it today (the single-bucket rule covers such objects), but narrowing that
  rule would turn this gap into misses.

Selection: `CoverageIndex` leaves linked methods out of the run-once set. Their users are the
classes that ran them plus the readers of what they built (`builtValues`).

Tests: `LinkedInitializationTests` covers `GetOrAdd`, `Lazy<T>`, a fill through a method (unlinked),
a mixed fill (unlinked), a list added to, a value built from another value, a static field stored
before a later part of its static constructor, a derived test class that never reads what its
base's static constructor built, and an object registering itself with its argument
built inline, in a local after unrelated work (which stays out), or through a chained constructor. The last test pins
the order of its classes, because xUnit orders them by an id that includes the temporary path.
`FrameworkMatrixTests` adds a field data source under NUnit. Every test was red first and
sabotage-checked: no transitive follow, no field read in the ring, no agreement where stack paths
meet, and no static constructor marker.

Measured with fresh recordings, 100 mutants each, no missed mutant:

| Repository | Before | A | C |
|---|---|---|---|
| NodaTime | 42.6% | 41.0% | 27.6%; 25.8% with hand-rolled caches; 22.5% with long scopes |
| protobuf-net | 89.5% | 87.6% | 92.3%, of it 4.6 points from a file the recording's tests leave in the tree (below) |
| CommunityToolkit | 17.1% | 16.8% | 17.1% |
| MimeKit | 21.9% | 21.9% | 22.1% |

The A column used round-2 maps; C needed new recordings, so small moves either way are recording
noise. protobuf-net's `BuildToolsUnitTests` writes `Aot/Data/Extensible.output.txt` into the
repository, and a later analysis on that tree sees it as a changed non-code file. Without those
selections C is at 74.7% against A's 79.3% by the same per-change measure. The evaluation now
deletes what test runs leave in its clone before it analyses (`EvalGit.RemoveUntracked`).

## C, design iteration 1 (2026-10-03, proposal for review)

### Where the probes stop today

- Cache fills (`GetOrAdd`, `Lazy<T>.Value`, `LazyInitializer`, `ConditionalWeakTable`, memory
  cache, pool policies, third-party callbacks) run inside a scope (`EnterCache`/`ExitCache`).
  Everything hit in it becomes `initialization`, which makes it run-once and name-traced.
- Field probes see direct stores only (`stsfld`/`stfld`). A value stored *into an object held in a
  field* is not a store: `_cache[key] = Build(key)`, `_cache.GetOrAdd(key, Build)`,
  `_lazy.Value`, `Registry.Items.Add(this)`. So the readers of `_cache` are not credited with
  `Build`. That is exactly where run-once code puts its results, so the safety net stays on and
  feeds the cascade.

### Recording

1. **Stores through a field's object.** A call is a store into field F when its receiver was loaded
   from tracked field F (`ldsfld`/`ldfld` feeding the call's `this`; the instrumenter already finds
   such receivers for pools, `ReceiverField`) and the method mutates or fills the object:
   - `Add`, `TryAdd`, `set_Item`, `Insert`, `Push`, `Enqueue`, `AddOrUpdate`;
   - the `CacheFills` list (`GetOrAdd`, `Lazy<T>.Value`, `LazyInitializer.EnsureInitialized`, ...).
   After such a call, the probe `Stored(F, method)` runs as for `stfld`. Its builders are the methods
   hit since the storing method was entered, which covers the factory a fill ran.
2. **Linked scopes.** Each initialization scope (cache fill, static constructor, third-party
   callback) remembers where it started in the per-thread ring of recent hits. When it ends:
   - it is **linked** if a store probe ran inside it and the ring did not wrap during it (builders
     are complete);
   - otherwise it is **unlinked**: the value left through a path the probes do not see, such as
     another thread, a local or argument object, or a closure's compiler-generated field.
3. **Map.** `linkedInitialization`: the methods whose every initialization run was in a linked scope.
   A method with one unlinked run is not in it.

### Selection

- `RunOnceReason` ("it ran in a static constructor or cache fill") no longer applies to methods in
  `linkedInitialization`. Their users are already in the map: the classes that ran them, plus the
  readers of the values they built (`builtValues`, merged into `ClassesRunning` today). So they are
  answered by coverage like any other method, and the name trace (the cascade) is not started from
  them.
- The same holds for the type-level rule: a type counts as run-once only if one of its
  initialization members is unlinked.
- Unchanged:
  - the single-bucket constructor and static rule (the third row of the counts; a second step
    once this one is measured);
  - static constructor changes;
  - third-party callbacks (the library keeps the value, not a field);
  - ambient code.

### Risks and how they are covered

| Risk | Covered by |
|---|---|
| A value leaves a linked scope through a second, unprobed path as well (stored in a field and handed to a callback) | Stays a gap in principle. Tests for the common shapes, and the corpus (every miss confirmed alone) decides whether it happens in practice |
| Builders cut by the ring (very long fills) | Ring wrap inside a scope makes it unlinked |
| A mutation probe on a hot path costs recording time | Measured on NodaTime, Newtonsoft.Json and OpenTelemetry, as the field probes were; at most one probe per call site |
| Maps recorded before this have no `linkedInitialization` | Absent means none linked, today's behaviour |

### Tests (red first)

- A static `ConcurrentDictionary` filled with `GetOrAdd` by the first class and read by two others:
  a change to the factory selects all three, and an unrelated class stays out (it was selected
  through the name trace before).
- The same with `Lazy<T>` in a field, and with `_cache[key] = Build(key)`.
- A fill into a local dictionary handed to another object: unlinked, so still run-once (safety).
- A fill longer than the ring: unlinked.
- `Registry.Items.Add(this)` in a static constructor: linked to `Registry.Items`.

### Measurement

- Precision on the four precision repositories with `testdetta-eval --explain` on new recordings, then
  the corpus with the outcome caches.
- Recording time on the three heavy repositories.
- Expected: NodaTime's cascade (37 of 100 changes) mostly gone. The gain is only worth it with no
  missed mutant.

## Plan

1. A and B, together with tests, a measurement on the four precision repositories, and the corpus
   rerun after round 2. Expected: NodaTime and protobuf-net shares drop, no miss.
2. C as its own design iteration, after A/B's numbers show how much of the cascade is left:
   recording probes first (cache entries, Lazy), then `linkedBuilders`, then the selection change
   behind a measurement.
3. D last; measure first how often fields carry a cascade.
