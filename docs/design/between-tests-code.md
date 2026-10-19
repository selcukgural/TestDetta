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
  recorded with their own parallelism (`TESTDETTA_PARALLEL_FLOWS`) keep crediting out-of-flow code to
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

- `TestDettaTracker`: `EnterTestCode`/`ExitTestCode`/`ExitTestCodeWhenComplete`/`PrepareTestInstance`,
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

## Fixture teardown (2026-10-11)

xUnit disposes a class fixture when its class finishes (`XunitTestClassRunner.BeforeTestClassFinishedAsync`), with no
test code running. The fixture marker was only on constructors and `InitializeAsync`, so a teardown went to the bucket
of code outside every test (in a suite recorded with its own parallelism, start-up), and so did every value it read:
GraphQL's `ComplexityMetaDataFixture` and `AutomaticPersistedQueriesFixture` dispose a schema, `Schema.Dispose` reads its
fields, and all the code that built the schema (`DefaultNameConverter.NameForField` among it) counted as having built a
value outside the tests. Coverage could not answer for it, and the name trace selected 99.7 % of the classes for a
change that one class ran (found with a probe that wrote the stack of every read outside the tests).

A fixture's synchronous `Dispose()` now enters the fixture's bucket (`TestDettaTracker.EnterFixtureTeardown`) when no
test code runs: no test is running or being prepared, or, recorded with the suite's parallelism, the flow is outside
every test. The body is wrapped in `try`/`finally`, and `ExitFixtureTeardown` puts the flow's bucket and the last bucket
entered back, so what the framework runs next between tests (the next class's injected services) still goes to that
class. A test that disposes its fixture itself keeps the code in its own class. `DisposeAsync` is left as it was: its
end, a `ValueTask` awaited once, cannot be seen. GraphQL, fresh recordings, seed 1: 45.5 % to 40.9 %, 0 of 53 missed.

A test that constructs a fixture type itself (`new StoreFixture()` inside a test) entered the fixture's bucket through
the constructor marker, so the rest of that test counted for the fixture's users and a change to code it ran afterwards
missed it. Since 2026-10-12 the constructor marker (`EnterFixtureInstance`) keeps the running test's bucket when the
call stack shows the test's own code below it (`CalledFromRunningTest`, as for a shared data member): a fixture the
framework builds still enters its own bucket, also when a framework builds it while preparing a test class.

Keeping the test's bucket alone was not enough where a fixture type shares what any instance builds. FastEndpoints'
`AppFixture` keeps one server per fixture type in a static cache of `Lazy<Task>`, booted by whichever instance awaits it
first; `TestServerTests` builds `FixtureA` itself. When it ran first (class order differs between recordings under
`parallelizeAssembly`, one of four), the host's start-up went to its bucket only, `FixtureA`'s own setup took the cached
server, and a change to the start-up missed `FixtureATests` (7 of 67 failing changes). Since 2026-10-12 a test that builds
a fixture type itself is recorded as a user of that fixture (`CoverageMap.FixtureBuilders`, which the selection adds to
the fixture's users), and the rest of that test's flow, the tasks it starts included (`TestBuiltFixture`, an
`AsyncLocal`), counts for the fixture as well as for the test's class. Either order now selects both classes
(`TestBuiltFixtureTests`).

## Shared data sources (2026-10-11)

A test data member (`[ClassData]`, `[MemberData]`, `[TestCaseSource]` and the like) that one class uses was already
credited to that class; one that several classes use, or an abstract base declares, counted as code outside every test,
so coverage did not answer for what it ran or the values it read (GraphQL's `GraphQLSerializersTestData`, the
`[ClassData]` of 13 classes, enumerated while xUnit discovers tests and again while it formats the test names).

`DataSources.Find` now gives such a member a bucket of its own, `[data-source]` and the type declaring it
(`CoverageMap.DataSourcePrefix`; an iterator's state machine belongs to the type around it), entered through
`TestDettaTracker.EnterSharedDataSource` with the guard `EnterAmbient` has (a test calling the member itself keeps its
bucket). Its users go into the map (`dataSourceUsers`): the classes naming it in a data attribute and every concrete
class deriving from one that does, found by the recorder in the test assembly, so a class two levels below the base that
declares the data is among them although it never names that base. The selection reads them as a fixture's users
(`CoverageSelection.ClassesUsing`). A member a generic class uses keeps the old rule (its runtime names may differ from
the map's), and so does a map recorded before (no `dataSourceUsers`).

## Members of an xUnit collection (2026-10-19)

xUnit sets a collection's fixtures (`ICollectionFixture<T>` on the `[CollectionDefinition]` class) up for every class
of the collection, also for a class that never asks for the fixture in its constructor. Such a class may still depend on
what the setup did: Wolverine's NATS tests (37 classes) find the container `NatsContainerFixture` started through an
environment variable, and a class can read a static field the setup filled. The setup's code was already in the
fixture's bucket, but the selection found the fixture's users by name only (`CoverageSelection.ClassesUsing`), so a
change to the setup, or to code only the setup runs, missed these classes. A reader of the dev.to introduction asked
about it; a sample confirmed it (a class reading the filled field was missed for both changes, one calling a method on
it only for the second).

`CollectionFixtures.Find` reads the membership from the test assembly, where constants and `nameof` in collection names
are resolved: `[Collection("name")]` naming a `[CollectionDefinition("name")]`, or in xUnit v3 `[Collection(typeof(T))]`
and `[Collection<T>]` naming the definition type, on the class or a base class in the module. Each concrete member goes
into the map under the fixture's name as the tracker spells it (`collectionFixtureUsers`), and the selection adds them to
the fixture's users, as it does for `dataSourceUsers` and `fixtureBuilders`. A member that does not depend on the fixture
is selected for a change to its setup too: the framework sets the fixture up for it, which is the evidence. The corpus
repositories with collection fixtures (alba, automapper, fastendpoints, graphql, unitsnet) have no member that does not
name its fixture, so their shares do not move. Maps are of format 4 since: a format 3 map has no `collectionFixtureUsers`
and nothing tells it from one of a suite without collections, so it is not read and its project is selected by names until
recorded again. `CollectionFixtureTests` (xUnit v2 and v3).

## Test framework extensions the repository ships (2026-10-12)

Some repositories ship extensions of a test framework and use them on their own tests: AutoFixture's
`AutoFixture.NUnit4.AutoDataAttribute` builds a `Fixture` while NUnit builds the test cases, FsCheck's
`FsCheck.Xunit.PropertyAttribute` reads its configuration and runs the properties through its own runner, and a test
project may declare its own attributes (GraphQL's `TheoryExAttribute`). That code ran outside every test, and with it
everything it called (AutoFixture's whole `Fixture`), so coverage could not answer for any of it. Found with the probe
that writes the stack of every read outside the tests.

`ExtensionAttributes.Find` reads the test assemblies before instrumenting and collects the attribute types of the
repository (declared in a test module or another repository assembly; `System.*` and `Microsoft.CodeAnalysis.*` ones,
which the compiler embeds, left out) that the tests put on test classes, test methods or their parameters, with their
users: those classes and every concrete class deriving from one. Each such attribute type, the closures and state
machines inside it too, gets the shared bucket of data sources (`[data-source]` and the type), so what it runs counts
for its users. Its methods are not counted as third-party callbacks (run-once code) any more: the framework keeps what
they build for those classes alone. The bucket is entered only while no test code runs: a test of the extension itself
calls it through the framework's own API (AutoFixture's `AutoDataAttributeTest` calls `BuildFrom`), where the stack shows
the framework first, and the first version, guarded by the stack alone, switched there and credited the rest of that test
to the attribute's users (6 of 56 FsCheck mutants and 3 of 71 AutoFixture ones missed in its measurement). A static class
counts as a user (an F# module compiles to an abstract sealed class: FsCheck's `Internals`, whose properties use
`[<Property>]`, was left out and missed 2). An attribute a generic class uses keeps the old rule.

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
