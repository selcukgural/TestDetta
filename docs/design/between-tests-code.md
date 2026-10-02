# Code that runs between tests

Status: implemented 2026-10-02 (see "As built"). Closes the "DI-built fixtures" gap (backlog, Known gaps behind shipped
fixes: Fixtures declared by a base class, "Still not exact").

## Problem

A test framework runs code of the repository between two tests: a DI extension
(Xunit.DependencyInjection and the like) builds the services it injects into the next test class's
constructor; a container disposes what the previous test used. None of it runs inside test-class
code, so the tracker credits it to the class entered last, the previous one.

Proven by `InjectedServiceTests`: three classes take a singleton `IClock` from the container.
The map credits `Clock..ctor` (and what it calls) to `PlainTests`, the class that happened to run
before the first user. A change to the constructor selects `PlainTests` and misses all three users.
A transient service is worse: each instance is credited to the class before the one it was built
for, and with two such classes the bucket count no longer marks the code as run-once.

## Design

### Recording: know when no test code runs

- Test-class entry points (test methods, lifecycle methods, static class setup) get an exit:
  `EnterTestCode` after the existing probes and a `finally` that calls `ExitTestCode`, so failing
  tests (which throw) exit too. An `async` method that returns `Task` exits when its task completes
  (`ExitTestCodeWhenComplete(task)` before each `ret` of the kickoff method); one returning
  `ValueTask` gets no exit tracking at all, since its task may be awaited only once (that test
  keeps today's behaviour).
- A test-class constructor sets "an instance is being prepared"; the next exit of tracked test code
  clears it. The framework builds constructor arguments before the constructor runs, so the
  arguments' code is outside test code, as it should be.
- Test code is running while the depth is above zero or an instance is being prepared. Once tests
  have started, a hit that would go to a class bucket while no test code runs is between-tests code:
  - it still goes to that bucket (teardown of the previous class stays credited to it);
  - it goes to a pending set, which is added to the next class entered;
  - it is marked as initialization, the existing run-once evidence: the selection traces it by
    name as well as selecting the classes the map knows.
- Fixture and setup-scope buckets are unchanged: their code already serves their users. Suites
  recorded with their own parallelism (`SIFT_PARALLEL_FLOWS`) keep crediting out-of-flow code to
  start-up; between-tests detection needs tests one at a time.

### Selection: a constructor serves every user of the instance

A singleton is built once and handed to later classes without running its constructor again. Those
classes still use the instance, so they run its instance members (`Clock.Now`). A change to an
instance constructor now selects the classes that ran the constructor and the classes that ran any
member of the type. This mirrors the static-constructor rule (7cd8528).

Left out on purpose: an instance only used through members inherited from a base type (the map
records the base type's members). The run-once mark still traces the constructor by name in that
case.

## Tests

- `InjectedServiceTests` (red today): a singleton injected into three classes. A constructor
  change must select all three.
- A transient service variant: built per class. The constructor and a helper it calls must be
  credited to each class it was built for, not to the class before it.
- Tracker unit tests:
  - between-tests hits are pending until the next class and marked as initialization;
  - a throwing test method exits;
  - an async test exits on completion.
- Selection unit test: a constructor change selects users of the type's instance members.

## As built

- `SiftTracker`: `EnterTestCode`/`ExitTestCode`/`ExitTestCodeWhenComplete`/`PrepareTestInstance`,
  `IsBetweenTests` on every hit and field read, `BetweenBits` taken by the next class entered
  (`TakeBetweenTests`, also when the same class is entered again for its next test), between-tests
  hits also marked as initialization.
- `AssemblyInstrumenter`: the begin probe goes before the hit (the entry point's own hit is test
  code); `TrackTestCode` wraps the body: `finally` for methods that return normally, `fault` plus
  `ExitTestCodeWhenComplete` for methods returning `Task`/`Task<T>` (async or not), `PrepareTestInstance`
  only for constructors, `ValueTask` and `async void`.
- `CoverageSelection.TryResolve`: an instance constructor change also selects every class that ran
  a member of the type, also when the constructor itself ran before any test (ambient).
- Tests: `InjectedServiceTests`, end to end with real recordings instead of the planned tracker unit
  tests (the tracker is static and configured by environment): singleton (red before), transient
  with an async test (the helper the constructor calls is initialization), and a failing test
  followed by a user in a fixed class order (the exception ends the test code; the user gets the
  construction). Sabotage-checked: without between detection, without the constructor rule, and
  with exits only on normal returns, one test each fails. 287 tests green. Round 2 is the first corpus run with it: watch recording failures
  (IL wrapping of unusual test methods, F# test assemblies) and the precision change.

## Pools and caches in collections (2026-10-02)

The same "built for one, reused by others" shape without a framework: a hand-rolled pool keeps
objects in a `Stack<T>`/`ConcurrentBag<T>`, a cache in a `Dictionary`; the first test that misses
builds the object, later tests take it out without running that code. Field-read probes do not see
it (the collection field is stored once; items go in through method calls). Proven by
`PooledObjectTests` (stack + factory, dictionary cache, generic `Pool<T>` over a `Func<T>`): the
factory's code was credited to the first renter only.

- Instrumenter, per module: a field holding a known collection (`Stack`, `Queue`, `List`,
  `LinkedList`, `HashSet`, `Dictionary`, `SortedDictionary` and the concurrent ones) that some
  method takes an object back out of (`Pop`, `TryPop`, `Dequeue`, `TryDequeue`, `TryTake`,
  `TryGetValue`, `TryRemove` on a receiver loaded from the field) is a reused collection, with
  its item type.
- In every method that loads a reused collection field, each call or `newobj` producing the item
  type (the same type, a derived or implementing one; for a generic item type any generic
  parameter or `object`) is surrounded by `EnterCache`/`ExitCache`, from where its arguments start
  when they are built by calls. Everything that runs inside is initialization: the selection
  traces it by name too, which reaches the renting method, run by every renter.
- `Peek` and `RemoveAt` do not count: parsers use them on stacks of objects built and used within
  one parse. Precision cost to watch in round 2: parsers that `Pop` (Markdig, Scriban, Fluid) and
  dictionary caches in libraries (Newtonsoft, NodaTime).
- Sabotage-checked: without the marking, all three shapes fail.
