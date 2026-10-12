# Backlog

Work agreed on but not started, in the order we plan to take it. Reliability and correctness
come first: the evaluation corpus and every finding there before anything below, and features
that report numbers (value reporting) only once the numbers are proven right.

## Next, to discuss with the owner: a record's shape change reaches startup code (AfterApply, 2026-10-09)

Dogfooding on AfterApply PR #202, after the initialization-use, lifecycle-callback and interface-member
fixes: the integration project still selects 96 of 99 classes. Adding optional parameters to the
response record `ApplicationDetailResponse` is changed metadata, which spreads the record's name; the
endpoint registrations name it (`MapApplicationEndpoints`, `Produces<ApplicationDetailResponse>()`,
handler lambdas), coverage knows who ran them, and every class's host runs them at startup.

Proposal (not started; it narrows selection, so the owner decides): for changed metadata of a record or
class whose members only carry data (constructor parameters, properties), answer through coverage of its
constructor and property accessors: the code that builds or reads the values, and the serializer that
reads them through the accessors, runs only in the classes the map names. Keep the name trace for
attributes and default values read through reflection without running the members (validation
attributes, `[JsonPropertyName]`), and for code that only names the type in metadata. Validate with a
corpus run on every repository, `--mutant-kinds all`, before shipping.

**Withdrawn as written (2026-10-09, before any code):** it would miss tests that read the type's shape
through reflection in third-party code. AfterApply's `SiteRootTests.Neither_Address_Is_In_The_OpenApi_Document`
downloads `/openapi/v1.json`, which ASP.NET builds from `.Produces<ApplicationDetailResponse>()` by
reflection: no constructor or accessor of the record runs, so coverage of them never names the test,
and only the name trace through the registration selects it. OpenAPI and Swagger snapshot tests are
common. A safe version needs a signal for "this class read the type's shape" (reflection inside
ASP.NET, Swashbuckle, serializers), which nothing records today.

## Next, to discuss with the owner: assemblies loaded where the recording cannot see who loaded them (owner, 2026-10-10)

The rules that narrow to the classes that loaded an output (opaque outputs, reflection-found types in a loaded output)
know only the loads TestDetta wraps in repository code. Since 2026-10-10 the tracker also lists every assembly loaded
without such a call (`CoverageMap.OutputLoadsUnseen`), and a map without the list or with the output in it runs the
project completely (docs/design/loaded-outputs.md). What remains: an assembly is loaded once per process, so a later
class that uses it through third-party code (a plugin host handing out the loaded assembly, a third-party scan of the
loaded assemblies, `AssemblyLoadContext.Assemblies`) is not recorded. The owner wants to talk about it: whether to
treat any third-party scan or host as "every class may use every loaded plugin", whether to wrap more enumeration
APIs, and whether the residual order-dependent case needs its own signal. Found with Carter's fix (2026-10-11): a test
that inspects a compiled-against output through `typeof(T).Assembly` (an architecture test written that way) loads
nothing a call shows, so only coverage selects it and a body change it would catch can be missed; and product code
that loads an output by name and hands it to such a test is counted as a catalog (docs/design/loaded-outputs.md).

## ~~Next, a possible miss: a test that constructs a fixture type itself~~ (found 2026-10-11, fixed 2026-10-12)

The fixture marker at the start of a fixture's constructor makes the fixture the current bucket wherever the
constructor runs. A test that creates a fixture type itself (`using (new StoreFixture()) { ... }`) then credits the
rest of that test to the fixture's users, not to its own class: a change to code it runs afterwards misses the test.
Found while adding the teardown guard (docs/design/between-tests-code.md, "Fixture teardown"); no corpus repository
showed it. The guard that `Dispose` has (enter only when no test code runs) is the likely fix, but in a suite recorded
with its own parallelism the flow a framework builds a fixture on must be checked first, or setup code would land in
the wrong class.

## ~~The test arguments a project already runs with~~ (done 2026-10-10)

TestDetta keeps the team's filters, run settings and testing platform arguments in every run it starts, and
`td record` takes the team's `dotnet test` arguments after `--`. EF Core's catch-all exit code 5 was a second
`--ignore-exit-code`. docs/design/team-test-configuration.md, site page "Your test configuration".

## Next: incremental recording on main (owner, 2026-10-09)

Every push to main records the whole suite instrumented (AfterApply: 5 min 38 s against 5 min 6 s for
the plain test job), so teams that merge often pay back on main what selection saves on pull
requests, and recording less often widens their pull request runs. Design:
docs/design/incremental-recording.md (select `A..H` against the previous map, re-record only those
classes, carry the rest over; members no fresh run has seen count as unknown; regular full recordings
check the incremental map). Next: the evaluator measurement, then thresholds and the action's default
with the owner.

## Later: Windows (owner, 2026-10-06: noted, to do later)

- A Windows CI job (manual or weekly; Windows minutes cost about 1.7x Linux) that runs the `Legacy`
  test category on .NET Framework without Mono, and a corpus entry recorded on Windows.
- NUnit 2 (NUnitTestAdapter 2.3.0) records no class on macOS/Mono: its test AppDomain is not unloaded
  before the host exits. Check on Windows (docs/design/framework-support.md, "Open").
- `msbuild.exe` from Visual Studio / Build Tools and `vstest.console.exe` end to end on Windows, once
  legacy project support (packages.config, non-SDK projects) is in.

## Now: evaluation corpus, round 1

- Finish the queue in `eval/corpus.json` (sequential, one repository at a time), fix what each
  repository reveals, with a test for every fix.
- Rerun the repositories evaluated before later fixes: Roslynator, Polly, NodaTime, Mapperly,
  Spectre.Console, CommunityToolkit, AutoMapper (round 2), and write the consolidated results into
  `docs/evaluation.md`.
- Track new findings per repository; maturity shows when several repositories in a row find none.
- Round 2 turns on every mutant kind (`--mutant-kinds all`; return-default, flip-branch and
  field-value exist since 1d54474).
## Known gaps behind shipped fixes
- **Order-dependent state between test classes** (NLog `LogManagerTests`, 1 of 2,562): accepted for v1 (owner,
  2026-10-08); a narrower signal than `leakedReads` (67 % of classes) when a user meets it.
  docs/design/order-dependent-state.md.
- ~~**Catch-all recording shard with an unexpected exit code**~~ (EF Core; done 2026-10-10): a second
  `--ignore-exit-code` next to the project's own; see "The test arguments a project already runs with".
- ~~**A test that loads a project's assembly by its path missed changes it sees through reflection**~~
  (done 2026-10-06, AspNetCoreOData `PublicApiTest`, a static field value changed): such classes are now
  selected for any change to that project, and an initializer change counts as a change of its
  constructor (docs/design/runtime-file-reads.md, last section). Open: F# `let` values. The site's
  limitations page described the removed single-bucket rule until the same day; it now says what replaced it.
- ~~**The user's own `--filter` broke every narrowed run**~~ (done 2026-10-06): `td test -- --filter
  "Category!=Integration"` added a second `--filter` next to TestDetta's class filter, and `dotnet test`
  stops with "Option '--filter' expects a single argument but 2 were provided" (exit 1): a CI that keeps
  integration tests out by category failed on every pull request TestDetta could narrow. The two are now
  joined (`(user)&(classes)`, `TestFilter.WithinUserFilter`); xUnit v3 trait, method and namespace filters
  already narrowed with "and"; a filter the runner cannot join (xUnit v3 class or query filter, which
  "or" or throw; TUnit's single tree-node filter) runs the project whole under the user's filter. The
  "matched nothing" rerun keeps the user's filter (`TestInvocation.UnfilteredArguments`); before, it
  dropped it, and a user's `--filter-not-class` too. Still open: `td record` takes no `dotnet test`
  arguments, so a category the CI leaves out (tests that need a database) is recorded too, and the
  evaluation has no per-repository test filter (Umbraco, Aspire, MassTransit and Orleans' CI filter by
  category).
- ~~**A deleted file an item glob brings in from outside the project folder selected nothing**~~
  (done 2026-10-05): MSBuild expands `<None Include="..\..\testdata\**" />` on the files present,
  so a deleted data file was in no evaluated item, and the XML item paths that would have matched
  were dropped from evaluated projects. They are now kept for files that no longer exist only
  (`ProjectNode.ItemsOfDeletedFiles`), so the deletion runs the project and nothing else widens.
  Found while designing run-time file reads (still open: files read by a path built in code and
  listed as no item select nothing; tracking reads while recording would give the precise signal).
- ~~**Project references used as an analyzer or source generator**~~ (done 2026-10-03,
  docs/design/opaque-project-references.md): a reference with `OutputItemType` or
  `ReferenceOutputAssembly="false"` is opaque, and an affected opaque reference makes its consumer
  a project-level change. Found in protobuf-net; 20 of 41 corpus repositories have such references.
  Narrowed 2026-10-06 for `ReferenceOutputAssembly="false"` (docs/design/runtime-file-reads.md):
  the tests that started or loaded the output, after a build-use check and a build-input comparison.
- **Framework caches that call repository code back** (2026-10-03, docs/design/external-callbacks.md):
  third-party callbacks are now run-once code. .NET's own callbacks are not, because most are
  per use. Those it caches (`JsonConverterFactory.CreateConverter`, `TypeConverter`, ...) and
  delegates handed to a third-party library (`AddSingleton(sp => ...)`) could still be credited
  to the first class only. Needs a list of caching framework contracts, or a measurement on the
  corpus.
- **Precision: run-once cascades** (NodaTime 76% of its extra selection, measured 2026-10-03 with
  the new "Spreading the type name" debug lines). A reached type declaration, or a member, whose
  type ran anything inside a static constructor or cache fill is not answered by coverage, so its
  type name spreads. Every member naming that type is reached, and the next type with run-once code
  spreads too. In 37 of 100 NodaTime changes the coverage trace spreads almost every type name. A
  type's own declaration is also reached by its own name (`IEquatable<LocalDate>`). Needs a design:
  answer run-once code through the values it built (field-read probes, builtValues) instead of
  names, and limit what a type-name spread may reach when maps exist.
- **Precision: MimeKit FailedWhileRecording (28% of its extra)** is the machine, not TestDetta: 8
  S/MIME classes need System.Data.SQLite's native library, which has no osx-arm64 build, so they
  fail in every run and are always selected. On Linux/Windows CI they pass.
- ~~**Precision: attribute polyfills reached by the name spread**~~ (done 2026-10-03): an
  attribute class nothing names (base certainly `System.Attribute`) has no instance, so it is dead
  code rather than "found through reflection" (`SourceIndex.IsAttributeClass`). NSubstitute's share
  did not move (73.8%): the same classes are reached through code run under Castle's interceptors,
  which the external-callback rule makes run-once. That is the real cost (see the next item).
- **Precision: per-call third-party callbacks** (NSubstitute 46% → 74%, docs/design/external-callbacks.md):
  interceptors are called per call, not cached, but count as run-once like the proxy generation
  hook. Options to measure: a recorded signal (a callback that runs in every bucket using the
  library vs. once per key), or a list of contracts known to be per call.
- **Precision: narrow the run-once rules** (measured 2026-10-03 at 2365bb0, fresh recordings,
  100 mutants each). For each rule that stops the coverage trace and traces by name, "needed" counts
  failing classes only that name trace selected, "extra" the classes it added that did not fail:

  | Repository | Single bucket (constructor/static) | Static constructor or cache fill | Map does not know it / outside tests |
  |---|---|---|---|
  | NodaTime | 0 needed / 589 extra | 0 / 258 | 0 / 0 |
  | CommunityToolkit | 0 / 121 | 0 / 90 | 0 / 0 |
  | MimeKit | 0 / 48 | 0 / 1 | 0 / 0 |
  | protobuf-net | 0 / 9 | 0 / 1 | 0 / 5 |

  No needed case is left on these four. On 26 more repositories measured the same way overnight
  (2026-10-04, 1676718) the rules are needed in two kinds of code, both confirmed by running the
  mutant alone: code run outside every test, such as NSec's algorithm singletons built as theory
  data during discovery ("map does not know it / outside tests", 161 needed classes), and code a
  test framework calls back, such as AutoFixture's NUnit 4 attributes and customizations (static
  constructor or cache fill 14, outside tests 13; `Scenario` fails alone with a throwing
  `FreezeOnMatchCustomization` constructor). Narrowing must keep the rules there; the other "needed"
  cases seen (cliwrap, FluentValidation, Markdig) were test interference or locale artifacts. The ones in ordinary code were real gaps and are closed: a
  static field depends on its whole static constructor (NodaConstants, 351f43f) and every use of a
  type depends on its explicit static constructor (MimeKit's `MimeEncoderTestsBase`, a miss,
  2365bb0). The rules still add about 850 extra classes on NodaTime and about 210 on
  CommunityToolkit. Before narrowing them: the corpus with fresh recordings
  (100 mutants per repository can miss a rare shape, as the MimeKit miss showed), and a design for
  what keeps the safety net where the probes see nothing, such as values a third-party library keeps
  (a first reading blamed protobuf-net's Roslyn generators; those classes failed while recording and
  are selected anyway). Measured with `seeds.py` (explain, verbose log, round-2 outcomes, maps);
  it compares classes as (project, class), since a test class compiled into several projects
  (CommunityToolkit's Roslyn variants) is one class per project, and leaves out classes that failed
  while recording, which `TestSelector.DecideClass` selects after the name trace in any case.
- ~~**Recording breaks protobuf-net's BuildTools tests**~~ (done 2026-10-03): 15 classes
  (`GrpcReferencedModelTests`, `DisableSwitchTests`, ...) failed only while recorded, since round 1,
  with CS1705 in `CompileWithModelGenerator`: the probes were imported through reflection, which gave
  every instrumented assembly a reference to the instrumenting process's System.Private.CoreLib
  10.0, and Roslyn refuses such an assembly as a metadata reference for a net8.0 compilation. Probe
  references are now built with the module's own core types (`AssemblyInstrumenter.Probe`). That
  alone broke Polly, which runs Microsoft's code coverage in every test run: the profiler corrupts a
  Cecil-written module that has no System.Private.CoreLib reference ("The metadata is corrupt", no
  map for Polly.Core.Tests). Each instrumented module now gets that reference at the test host's
  version, read from the test assembly's target framework: Roslyn accepts exactly that version (any
  other gives CS1705 or CS1701). 13 of the 15 protobuf-net classes now record; the other 2
  (`Should*DefaultCodeFixProviderTests`) fail without TestDetta too on a Turkish-locale machine (a code
  fix formats `2.2m` as "2,2") and pass under en-US.
- ~~**A test class the map never saw running is skipped**~~ (done 2026-10-04,
  docs/design/unrecorded-classes.md): a map recorded on a Turkish-locale machine names Markdig's
  generated spec class `TestBlocksAndİnlinesPrecedence`, an en-US build `...AndInlines...`, and the
  class never ran (round 2b's 26 misses); a class skipped on the recording platform was never
  selected on another. Selecting every class the map does not record was rejected (it widens with
  every `[Explicit]`-only or filtered class). Maps now keep the classes whose tests were all skipped,
  the recording platform and each reported class's place (from the PDB). A skipped class runs only
  on another platform; a class the map never reported is decided by the record at its place when one
  orphan (a recorded class the build lacks) sits there, and is selected with a warning when the map
  and the build disagree about its file. Elsewhere it stays skipped as before.
- ~~**Corpus runs depend on the machine's locale**~~ (done 2026-10-04: `eval/corpus.json` defaults set
  `LC_ALL`/`LANG` to en_US.UTF-8; found on a Turkish-locale machine):
  Markdig's build regenerates its spec test classes with a culture-sensitive `ToUpper`, so a class is
  `TestBlocksAndİnlinesPrecedence` in one build and `...AndInlines...` in another, and an evaluation
  that compares class names with stored outcomes reports false misses (26 of them in one explain
  run); protobuf-net's two `Should*DefaultCodeFixProviderTests` fail outright. CI runs under en-US.
  Run corpus evaluations with `LC_ALL=en_US.UTF-8` (pool scripts, or an evaluation default), so
  numbers match CI and do not move with the machine.
- **Microsoft code coverage next to TestDetta's recording** (Polly, 2026-10-04): with the fix above
  Polly.Core.Tests records fully (124 classes, none failed), but with some builds of
  TestDetta.Coverage.Runtime one of five shards still crashes at start ("Index not found", 0x80131124)
  and is rerun by the fallback. Two builds of the same source (same methods, IL sizes and references,
  different MVID) behaved differently in four runs each, so it lies in the profiler's handling of
  the loaded modules. Running two coverage tools at once is waste anyway: find a way to keep
  Microsoft's collector off during recording (the repository adds `--coverage` in
  `_TestingPlatformCommandLineArguments`), or measure how often it costs a shard.
- ~~**Direct changes to a member's metadata**~~ (done 2026-10-03,
  docs/design/reflection-types-with-coverage.md "Direct metadata changes"): the change detector
  reports `MetadataChanged` when a declaration's attributes or parameter defaults changed (or a
  using of its file). Such a change keeps the classes the map names and is traced through its type.
- ~~Stable mutant sample across TestDetta versions~~ (done, 99347df): each candidate's place and
  kind come from a hash of the member and the seed, so adding or dropping a candidate (a graph
  fix) changes at most that pick and stored outcomes stay usable. Samples differ from round 1's.
- **x64-only test projects are left out on Apple silicon** (Vogen's SnapshotTests: "Could not find
  'dotnet' host for the 'X64' architecture"). The evaluation now leaves out a project whose run
  without mutants aborted before running a test, instead of counting every later abort as a mutant
  caught by a crash. A corpus toolchain with `architecture: x64` now runs such suites under Rosetta
  (checked 2026-10-02: Vogen's SnapshotTests start and run). Vogen still gives no data: 744 of 749
  snapshots fail without any mutant, since they expect the Vogen package its build script packs
  locally (version 1.0.0, other formatting); its setup command would have to run that script.

Interim fixes that are safe for the common case; come back for the complete solution.

- **Fixtures declared by a base class** (interim: 514238e; FastEndpoints, 31 classes missed).
  `IClassFixture<T>`/`ICollectionFixture<T>`/`IAssemblyFixture<T>` implemented by a base class,
  also in another library, with the fixture as a generic argument (FastEndpoints.Testing's
  `TestBase<TAppFixture>`) now marks the fixture; xUnit v3 assembly fixtures are an assembly setup
  scope (see below). ~~Still not exact: objects a DI-based extension (Xunit.DependencyInjection and
  the like) builds once and injects through constructor parameters~~ (done 2026-10-02,
  docs/design/between-tests-code.md: code between tests goes to the next class and is run-once
  evidence; a constructor change selects every user of the type). Analysis
  (2026-10-02): treating test-class constructor parameter types as fixtures adds nothing for xUnit
  (its only concrete parameters are fixtures already found) and misses the interface case; the
  complete fix needs to know when the framework is between tests (exit probes on test methods, or
  the frameworks' own hooks) and credit that code to the class it builds for.
- ~~Recording can hang on a lingering MSBuild node~~ (done, 2cbf3bf). The cause was not node
  reuse but how TestDetta waited: after dotnet exited, it waited for its output to close, and a child
  left behind (MSBuild node, compiler server, a test's background process) kept it open. TestDetta now
  waits for the process only and reads its output for 10 more seconds at most; node reuse stays on,
  since it speeds builds up.
- ~~Linked files whose path needs evaluation~~: done with MSBuild evaluation (docs/design/msbuild-evaluation.md); the XML reading
  stays for imports and for projects MSBuild cannot evaluate.
- ~~Projects sharing a directory~~: done with MSBuild evaluation (docs/design/msbuild-evaluation.md); the XML reading
  stays for imports and for projects MSBuild cannot evaluate.
- ~~Setup that runs before every test of a scope~~: NUnit `[SetUpFixture]` setup (also inherited
  from a base class elsewhere), MSTest `[AssemblyInitialize]`, xUnit v3
  `[assembly: AssemblyFixture(typeof(X))]`, `[ModuleInitializer]` in test assemblies, and TUnit
  `[Before(Assembly|TestSession|TestDiscovery)]` and `[BeforeEvery(...)]` hooks record under a setup
  scope that selects every class of the namespace or assembly. Still ambient: discovery-time data
  and setup of custom frameworks TestDetta does not know; classify ambient code by what entered it if
  the corpus shows such cases.
- ~~Static constructors~~ (interim 7cd8528, completed by field-read probes, docs/design/field-read-probes.md):
  a value a static initializer stores is credited to every bucket that reads the field. Still by
  name only: static state read without a field read of an instrumented assembly.
- ~~Project graph from MSBuild files read as XML~~: done with MSBuild evaluation (docs/design/msbuild-evaluation.md); the XML reading
  stays for imports and for projects MSBuild cannot evaluate.
- **Object pools** (interim: d462594, 6badd5d; Meziantou.Analyzer, 5 classes missed). `Create` on
  a pool policy (`IPooledObjectPolicy<T>`, `PooledObjectPolicy<T>`, matched by name since libraries
  copy Microsoft.Extensions.ObjectPool) is now a cache fill, also when the pool binds it to a
  `Func<T>` and calls that on a miss, as `DefaultObjectPool` does. Other pools (`ArrayPool`, hand-rolled
  `Stack<T>` caches, `[ThreadStatic]` buffers) still credit what they build to the first class.
  `[ThreadStatic]` buffers stored in fields are covered by field-read probes now.
  ~~Complete fix: detect "create on miss" call sites generally~~ (done 2026-10-02,
  docs/design/between-tests-code.md "Pools and caches in collections": code that builds an item of
  a collection objects are taken back out of is a cache fill). `ArrayPool` and pools in other
  assemblies build nothing instrumented.
- ~~Hand-rolled lazy initialisation~~ (completed by field-read probes): `_x ??= new X()` and
  `if (_x == null) _x = ...` on any field credit the builder to every reader. Not seen: a value
  built on another thread and handed over before the store; the run-once rules stay for it.
- ~~Helpers of another test class~~ and ~~test data credited to both buckets~~: done. An entry point
  of another class (constructor, attributed helper, data member) called while a test of the current
  class runs no longer switches the bucket: the tracker reads the stack when a switch would happen
  (never on the hit path) and keeps the bucket when a frame of the running class (its methods,
  lambdas, iterators, async state machines) lies below the entry point before any test-framework
  frame. Frames of the entry point's own class are skipped, so a data member declared on the
  running class but used by another still switches when the framework calls it. Frames of
  the running instance's base classes (a test declared on an abstract base, also in another
  library) count as the running class too (2026-10-02). Still the old behaviour, which switches:
  calls between classes nested in one outer class.
- ~~Startup time vs. discovery-time data~~ (interim 7c33ec6, completed): data members enter their
  class through `EnterDataSource`; before the first test, framework discovery, their time stays
  start-up while their code still counts for the class. Data enumerated when its tests run is
  that class's time, as before.
- ~~Files rewritten for other tools match the SDK byte for byte~~ (interim 911e519, completed):
  a test rewrites a real SDK-written `deps.json` and gets every byte back; an entry the SDK wrote
  for the runtime (a project that references it) is now kept instead of replaced. `deps.json` is
  the only text file TestDetta rewrites; assemblies go through Cecil.
- ~~TUnit projects were not recorded or filtered~~ (found 2026-10-02): TUnit ignores the VSTest
  `--filter` and runs no test at all, and runs none either when given a run settings file. TestDetta now
  uses `--treenode-filter /*/*/(Class)|(Other)/**` (namespace left open, so classes sharing a simple
  name across namespaces go together) and `--maximum-parallel-tests 1` while recording. Add a TUnit
  repository to the corpus.
- ~~Eval: NUnit under the testing platform wrote no test report~~ (interim 871f6aa, completed): such
  runs now ask NUnit's adapter for its own result file through run settings (`TestOutputXml`), and
  reports are read from it, in the evaluation and when recording, without adding a package.
  NSubstitute can be evaluated. protobuf-net.Test's host died (exit 134) from a stack overflow in
  its DepthTests, since macOS gives threads a small stack; corpus entries now take an `environment`
  (`--env` for one repository), and protobuf-net sets `DOTNET_DefaultStackSize`, with which the
  class passes. Still left out and named: other hosts that die before running a test.
- ~~Recording suites that only pass in parallel~~ (completed): when the one-process recording
  crashes too, the project is recorded with the suite's own parallelism (`TESTDETTA_PARALLEL_FLOWS`):
  code inside a test flow counts for its class, code outside every flow counts as ambient instead of
  for the class entered last, and the map has no class times. To be checked on NLog in round 2.

## v1 (agreed 2026-10-04)

Reliability is shown (round 1: 3 misses of 2941 failing mutants, all fixed; round 2b: none real),
and the rest of this file is mostly precision, which keeps growing. v1 is the finish line:

1. Round 2c (every corpus repository, build of main, en-US) without a real miss.
2. A customer can install and use TestDetta: README and docs/ci.md checked end to end, the GitHub Action
   (`action.yml`) run on a real repository from a fresh clone. Status 2026-10-04: README brought up
   to date (MSBuild evaluation, symbols, license); the action runs on TestDetta itself (record mode
   2026-10-03, pull request path 2026-09-30). Open: how customers get the action and the tool while
   the repository is private (public action repository, released binaries or a NuGet tool).
3. A licence can be bought and works: purchase through the Merchant of Record, key installed,
   `td license status` green (see "Licensing independent of the payment provider"). Status: the
   format, the check, the service, refresh and Polar webhooks are built
   (docs/design/licensing.md), and the license string is e-mailed to the buyer through Resend
   (2026-10-04; the portal comes later). Open: the production key (trusted at launch) and the
   Polar product setup (tax exclusive, no trial on the products). Done 2026-10-07: the service on Cloud Run,
   the sending domain, card-free trials from the site (docs/design/licensing.md, "Trials (as built)").
4. A simple "what you saved" report: the existing per-class decisions and time estimate in the job
   summary, checked on a real run (item 4 below goes further after v1). Status: checked on Markdig
   2026-10-04 (16 of 177 classes, 93% of the recorded time saved, a reason per class).
5. The name before any public launch: TestDetta is in use (see Later); open: the trademark search and
   reserving the NuGet ID prefix.
6. **Shadow mode as the way in** (owner, 2026-10-07: "work on it seriously"): cautious organisations
   see TestDetta select on their own pull requests while everything still runs, with every claim
   measured, explained and reproducible, misses told apart from flaky and pre-existing failures, a
   roll-up over weeks with the statistical bound, and documentation for reviewers. Design and open
   decisions: docs/design/shadow-mode.md. Status 2026-10-07: `td test --shadow` with classification and
   the report, and the Action's `mode: shadow` with the artifact, are built (as-built section of the
   design); every classification time limit is an option and an Action input, with defaults, under an
   overall budget (45 min) that never touches the full run. Since the owner's decisions of 2026-10-07:
   shadow mode needs no license and TestDetta makes no network call in it, shows decisions only for the
   classes the job ran (nothing of the selection before the full run ends), runs the team's own `dotnet test`
   command as given (a solution, a project, a directory or no argument; otherwise per project, with the
   reason in the report), and reports the whole run's wall time. Review fixes (2026-10-07, schema 3): the
   team's results directory and files are never touched (reports are read in place and copied); fail-open
   before the run (the team's plain command), after it (its exit code, `compared: false`) and in the Action
   (a step before the tests that cannot do its part runs plain `dotnet test`; unique, non-failing report
   artifacts per job and matrix leg); the per-project run keeps to the command's projects; verdicts compare
   failing tests, and a class that passes alone is checked in its full-run context before it is called
   flaky; reruns write only into TestDetta's folder and the base rerun builds on its own; an analysis time
   limit; Ctrl+C and SIGTERM clean up. Graduation: within 30 days, 40 pull requests with a shadow result and
   no unexplained miss, plus one `td audit` run that meets the audit's criterion (item 7). Open: the roll-up (`td shadow report`), the
   shadow-mode and reviewer pages, replaying real pull request histories before a partner sees it; the base
   worktree does not see files above the repository root.
7. **`td audit` on the team's own code** (owner, 2026-10-07: part of v1): the corpus method run on the
   customer's repository (docs/design/shadow-mode.md, "`td audit`"): IL mutants of the team's members, the
   tests run the full way, and the selection checked against every class that failed. Graduation needs one
   run with at least 100 changes that broke tests, 0 missed, and the worst-case 95% upper bound (inconclusive
   and stopped changes counted as misses) at most 3.0% as shown, rounded up (owner, 2026-10-07; 2.95% at
   n = 100 with none unjudged); the report says per run whether it meets that and why not. The audit is needed
   because real failing pull requests in 30 days are too few for a meaningful bound (about 3/n for n of them). Status
   2026-10-07: built (docs/design/audit.md): `td audit` and the Action's `mode: audit` (for a schedule, the
   report as an artifact for 90 days), no license and no network call of its own; every mutant lives in a copy of the
   working tree, so the team's sources and build output are only read (tested byte for byte after a normal run,
   a cancellation, a timeout and an exception); the selection per change is `td test --base HEAD` with the
   evaluator's edit given in memory (`AnalysisOptions.WorkingTreeOverlay`); the sample is uniform over every
   member, coverage not used; stop at `--target-broken` (100), `--max-mutants`, `--budget` (4h); JSON schema 1.
   The mutant code is shared with the evaluator through `src/TestDetta.Mutants`. Review fixes (2026-10-07,
   schema 2): every doubt goes against TestDetta (a run that does not complete runs again without the
   change: a left-out part that completes only then is a miss; a crash counts as caught only when the
   selected part crashes again; selected failures are confirmed alone with and without the change like
   misses; a class that ran none of its tests alone is inconclusive; a project whose own filter cannot be
   joined with a class filter is not audited); a confirmed miss survives a later cancellation, error or
   failed restore; the budget only stops new changes, so the judged changes are a prefix of the order; the
   verdict shows the inconclusive and stopped counts and a worst-case bound counting them as misses; runs in
   English, completeness from the TRX outcome, the exit code and the classes reported; MSBuild asked where
   every project writes before anything builds (refused when outside the copy), links never written
   through, free space checked before every copy and change, the commit pinned for every analysis, the
   work folder 0700 with a locked owner file. Open: a first run on a real team repository (and on
   TestDetta itself) to see the time per change; generator and legacy projects are not audited yet;
   combining several runs into one bound. Decided 2026-10-07 (schema 3): graduation needs the worst-case
   bound at most 3.0%, and the report says whether a run meets it and why not; a run past `--budget` keeps
   the prefix rule and the report says by how much and which changes ran on.

Until v1 only misses are fixed. Parked until after v1: narrowing the run-once rules, Microsoft
code coverage next to recording, per-call third-party callbacks, the precision round (3),
method-level selection (5) and the dotnet-tia comparison (2). Day-to-day checks run the corpus
repositories a change touches; the full corpus runs at milestones.

## Next

- **Shape rule for data-only types (branch feat/shape-rule, owner 2026-10-12: kept until AfterApply can be measured).**
  Answers a metadata change of a record that only carries data through its member users and shape readers instead of
  the name trace through the endpoint registrations (AfterApply 96 of 99). Needs AfterApply's integration tests, which
  need Docker (Testcontainers) or CI (closed until 2026-11-01). docs/design/shape-reads.md.
- **A timing test that fails under heavy load.** `ShadowRunTests.A_checkout_of_the_base_commit_that_runs_out_of_time_leaves_no_worktree_registered`
  failed twice on 2026-10-12 at load averages of 20 and 50 and passed alone each time (3 of 3). Give it a margin that does
  not depend on the machine's load.

- ~~**A fixture whose server another class built first (FastEndpoints, 2026-10-12).**~~ Fixed the same day: a test
  that builds a fixture type is its user, and its flow counts for the fixture too (docs/design/between-tests-code.md). `TestServerTests` builds
  `FixtureA` itself (`Only_One_TestServer_Is_Created_Per_Fixture_Type`), and FastEndpoints.Testing keeps one test server
  per fixture type in a static cache. With `parallelizeAssembly`, the class order differs between recordings: in one of
  four, `TestServerTests` ran first, its own bucket got the host's start-up (1,017 methods, rightly since #36), and
  `FixtureA`'s setup took the cached server and ran none of it. A change to that code then missed `FixtureATests`
  (7 of 67 failing changes in the scan A/B's side B, a recording of the scan branch; main can record the same order).
  The once-per-process shape of the Limitations page; `builtValues` does not see a value kept in a dictionary. Open:
  a signal for a fixture that reads what a cache fill of another class stored.

- **Test platform code in Newtonsoft.Json (measured 2026-10-12, not merged; owner).** VSTest's testhost serializes with
  the repository's own Newtonsoft.Json. A diagnostic (diag/platform-channels) split the platform's hits: before the
  first test 111,248 (99.5 % platform alone, 661 methods), on the host's threads during the tests 12.4 million (the
  test framework's message code on the stack too), between tests 0. Keeping the first channel apart
  (feat/platform-startup-code, docs/design/test-platform-code.md there) moved newtonsoft-json from 36.0 % to 35.7 %
  (66 changes narrower by one class, 0 of 66 missed; carter, cliwrap, nodatime, serilog unchanged): too little for the
  added code. The width comes from the second channel, whose hits go to the class running at the time; separating it
  needs a rule for the test framework's frames and is open.
- ~~**License service behind Cloud Run: forwarded headers not applied?**~~: done (2026-10-07). They were applied,
  but `UseHsts()` ran before `UseForwardedHeaders`, so it saw plain HTTP. Forwarded headers now come first; HSTS
  matches the site (1 year, subdomains). Checked on the deployed sandbox: HSTS sent, the per-address limit partitions
  by client (this machine at 429 while Cloud Shell got 200 in the same minute), a forged `X-Forwarded-For` does not
  escape it.
- ~~**License service: `/healthz` does not reach the service on Cloud Run**~~: done (2026-10-07), now `/v1/health`.
- ~~**Eval: crash and confirmation rules are not symmetric**~~: done (2026-10-07, owner: switch, with a marked break
  in the round history). The evaluator judges by `td audit`'s rules (`MutantJudge`; docs/evaluation.md, "How mutants
  are judged"): a part left out that completes only without the mutant is a miss, a crash counts as caught only when
  the selected part crashes again, selected failures are confirmed alone with and without the mutant (one run each
  per project, skipped once the verdict cannot change), a class whose run alone ran none of its tests is
  inconclusive; runs in English, completeness also from the TRX outcome, the exit code and the class count. Reports
  state `judging: strict (2026-10-07)`, the inconclusive mutants and a worst-case bound. Outcome cache version 3 keeps
  raw runs; version 2 files are not reused, so the next round runs every mutant again. Published numbers up to
  `runs-full-1006` are marked as lenient until the next round replaces them.
- ~~**Eval incidents**~~: done (2026-10-07). Per repository and per round: a project not recorded on TestDetta's side
  (above all with passing tests), maps with 0 classes, all failed or unnamed failures, and with `--compare` fewer
  recorded classes than the previous round (docs/evaluation.md, "Incidents").
- ~~**B1: recording fails loudly**~~: done (2026-10-07). A test assembly that is not instrumented, a tracker that wrote
  nothing although the tests passed, or no class recorded makes the project "not recorded" with that reason
  (docs/design/unrecorded-classes.md).
- ~~**B2: the map knows what it instrumented**~~: done (2026-10-07). `instrumentedProjects` in the map; a changed member
  of a project the test project compiles against but the map did not instrument is traced by name (meziantou #17).
  Old maps answer as before.
- ~~**B3: using-alias names**~~: done (2026-10-07). Type names in a file's using alias (and a VB `Imports` alias) count
  as named by the declarations of that file that use the alias (meziantou #18, #27); global aliases keep the
  global-using rules. Side effect, measured in explain mode on meziantou with the fix-owners maps: a type named only
  in an alias (its analyzers and fixers) was "nothing names it, may be found by reflection" and ran whole projects;
  now it counts as named and coverage answers for it. Mean share 37% → 6.5% (failing mutants 45% → 8.7%), and all
  529 failing classes of that round's sample are still selected; the next strict round measures it.
- **Eval: mutants do not reach MSBuild's bootstrap copy** (2026-10-06): the eval rewrites the
  assemblies in the test output folders; MSBuild tests that run the bootstrapped MSBuild
  (`ExecBootstrapedMSBuild`, about 60 methods, at most ~3% of the suite) load the copy under
  `artifacts/bin/bootstrap/core/sdk/<version>`, built once in setup. A miss there would go uncounted (no
  false misses). Fix: a corpus field naming extra folders whose copies of the repository's assemblies get
  the same mutants.
- **Recording fingerprint is wider than recording** (owner, 2026-10-06: next job): the eval re-records
  every corpus map when `RecordingFingerprint` changes. It counts the recorder assemblies in full and the
  TestDetta.Core types they reach, type by type (an abstract type brings every implementation). So a
  selection-only change can still re-record everything: 7b455e8 (initializer rule in `SourceLanguage`,
  `VisualBasicLanguage`, `CodeChangeDetector`) re-recorded AspNetCoreOData (new key `5956183f…`) because the
  recorder reaches the language classes. Narrow it: reach by method instead of by type (hash only the
  members the recorder can call), or move what the recorder shares with selection (parsing helpers) out
  of the types selection changes. Check with a selection-only commit that the key stays the same.
- ~~**Job summary evidence for classes the name spread selects**~~ (done 2026-10-12: the other classes were not
  the name spread's but the map's `builtValues`, classes that read a field filled while `GetWeekdays` ran; with the
  map at 01027ef 9 of the 11 selected classes now say "read a value built by …" and only CalendarTests and TreeTests
  "ran …"; the selection of 100 sampled changes is the same, `BuiltValueEvidenceTests`). (seen 2026-10-04 on Spectre.Console,
  a refactor of `Calendar.GetWeekdays`, map at 01027ef): 15 classes were selected, but the map
  records only CalendarTests and TreeTests running `GetWeekdays`. The debug log shows the name
  spread going through `Emoji._emojis` ("names Calendar", an emoji constant with the type's name),
  yet the summary says of every class "ran Spectre.Console.Calendar.GetWeekdays(), which changed".
  The selection is safe (more, not less); find where the coverage evidence comes from for the
  other 13 and make the text name the real reason. The name collision is precision, after v1.

0. **Fast, reliable evaluation environment (top priority, user and Claude agreed 2026-10-02).**
   Every other item (precision round, static bridge, dotnet-tia comparison) and every release after
   launch must answer "did this change cost safety?" across many real repositories, in minutes,
   not the hours a corpus round takes now. Round 1 spent most of its time re-running tests and
   recovering from environment accidents; the real findings took minutes each. In order of payoff:
   - ~~Mutant schemata~~ and ~~parallel mutants~~: done (docs/design/mutant-schemata.md; d7530a9,
     089a2e0, 302abe6); each worker runs in its own copy-on-write copy of the clone (ba00cae), since
     a check run missed a race on a fixed publish folder (Scriban's TestAot). Default parallelism
     to be set from the measurements in the design doc.
   - ~~Stable sample + outcome cache~~: done (f12e713, 99347df).
   - ~~Cache recordings~~: done (7f38a58), keyed by commit and recording settings. Baselines
     are not cached: one or two suite runs per commit, and they also measure the time limits and
     anchor the schema and parallel checks.
   - ~~Reuse across rounds~~ (2026-10-05, docs/design/eval-result-cache.md): corpus entries can pin a
     `commit`, and `--cache <dir>` keeps outcomes and maps outside the round, so a fix costs only
     selection and the mutants it newly reaches. The recording key takes the coverage and tracker
     assemblies and only the core code they reach (`RecordingFingerprint`), so a selection fix no
     longer records again. Next: pin the corpus to the round 2c commits, seed the shared cache from
     runs-r2c, pass `--cache` in the pool script, and measure a round.
   - ~~Tiers~~: nightly smoke corpus on GitHub done (6a1a57e; Scriban, Fluid, Carter, 20 mutants each,
     about 18 minutes on a 2-core runner; first run 0 of 47 missed). User chose nightly over per-commit
     and kept the repository private (Actions minutes). On 2026-10-03 TestDetta had used 1,635 of the
     3,000 monthly minutes in three days, so the smoke corpus and the coverage recording became
     weekly (Monday) or by hand; a push runs only the quick job (no Slow tests, under 3 minutes). A run is compared with an earlier one
     (`--compare`, comparison.md: failures, new misses, wider or narrower selection and changed
     outcomes at the same commit and toolchain, slow runs); the nightly compares with the previous
     successful night. Still: the full corpus on a schedule on the dedicated machine.
   - ~~Hermetic, reproducible runners~~: done for toolchains (docs/design/hermetic-toolchains.md):
     SDK and runtimes pinned per corpus entry, installed side by side into directories never
     changed afterwards, archives checked against `eval/toolchains.lock.json` (read-only in CI),
     `architecture: x64` for x64-only suites, MSBuild node reuse off; parallel mutant workers get
     their own clone copy and temporary directory. Still: try `x64` on a real x64-only suite, warm
     the NuGet cache of the nightly run (actions/cache keyed by the corpus), and fixed ports, which
     only the parallel check catches.
   - **Watchdogs and clean failure**: done for the evaluation: time limits on builds (30 min), test
     runs (adaptive, scaled with parallel mutants), recording, and each analysis (bb5b676, reports a
     hang in TestDetta); no wait on a child's output (2cbf3bf); the queue's fast-failure guard. Still:
     a heartbeat over the queue and an incident log, with the runner work below.
   - ~~Dashboard~~: done. `--history <dir>` keeps every corpus run's summary.json and redraws a static
     `dashboard.html` (per repository: status, commit, misses, selected share, precision, time,
     reuse, with trends over the last 30 runs). Still: run it from the scheduled full corpus.

1. **Tolerate map drift with a static bridge (priority).** Maps are used only when recorded exactly
   at the merge base; otherwise the test project falls back to the name-only trace (broad, and
   blind where the map is not). Measured (docs/design/map-drift-bridge.md): with nightly recording
   only about half of CI runs find an exact map even at open-source cadence, and none do right
   after a merge; the newest map is at most 9 commits behind at p90 (38 right after merges).
   Design: take the newest map at an ancestor of the merge base and add, for every member the
   drift changed that can reach the PR's change, the classes the map credits to it (name trace
   where the map cannot answer); project-level drift and large gaps take the no-map path. Next:
   measurement 2 (record at N−k, evaluate at N: exact / no map / bridge / naive), then the
   implementation. Safety rule unchanged: when the bridge is unsure, select more.
2. **Compare with the tools teams actually use, then with dotnet-tia.** Market check 2026-10-02:
   dotnet-affected (project-level, 408 stars, 4.15 M NuGet downloads) is the free tool .NET teams
   use today, so the first comparison is TestDetta vs dotnet-affected on the same synthetic changes
   (selected share and time; both should miss nothing). From round 2 every synthetic run measures
   project-level selection (the `ProjectLevel` selector: whole affected test projects, from TestDetta's
   own MSBuild graph and impact) next to TestDetta's, per mutant and per repository in summary.md;
   confirming with the real dotnet-affected tool on a few repositories is the next step: built
   (`--compare-affected <dotnet-affected executable>`, version 6.2.0, run without `--from` so the
   working tree is compared with HEAD, report written outside the clone since v6 counts files in it
   as changes); on a two-project probe it matched the ProjectLevel selector exactly (56% vs TestDetta 33%). Datadog Test Impact Analysis is the
   commercial one (SaaS, needs an account; later). dotnet-tia (3 stars, 541 downloads, created
   2026-07) is not a market player but the best open-source example of the static-analysis
   approach, so it stays as a technical check of "why record coverage", at low priority; the
   evaluator side is built (docs/design/tia-comparison.md).
   Earlier text: **Compare with dotnet-tia on the corpus.** dotnet-tia (github.com/SebHenn/dotnet-tia, MIT) is
   an open-source .NET tool doing the same job by static analysis only (method level, mutation
   gate, reports zero misses and 35.5% selection on NodaTime). Run it on the same repositories with
   the same mutants and report misses, selection share, precision and analysis time side by side,
   so the claims about runtime coverage (reflection, DI, F#, interface dispatch) rest on numbers.
   Its approach is also the reference for item 1.
3. **Precision round**: safety fixes made selection broader (NodaTime 16% → 34%); measure where
   the extra classes come from and narrow without losing safety (override names scoped to their
   hierarchy, run-once rules, name spreading through common member names like `Initialize`).
4. **Value reporting: show exactly what TestDetta saved, and why it was safe.** Not "you saved five
   minutes" but the breakdown behind it, in formats people and tools use. First slice done
   (2026-10-02, docs/design/value-reporting.md): a decision and its evidence per test class in JSON
   and the Markdown summary. Still to do:
   - Per run: every test project and class that ran or was skipped, the reason for each decision
     (the changed member, the coverage map and commit it came from, or the fallback rule), and
     time per project measured against the recorded full-suite time.
   - Measured, not only estimated: main runs the full suite when recording, so the real duration of
     what was skipped is known; report both and the error of the estimate.
   - Over time: per day, week and month; per repository, team and test project; trends of saved
     time, CI minutes and their cost (runner price per minute), and of selection size.
   - Evidence of safety: failures found on main that a PR's selection skipped (should be none), how
     often a fallback ran everything and why, so the numbers can be trusted.
   - Formats: GitHub job summary and PR comment (Markdown), JSON and CSV export, an HTML dashboard,
     a badge, a weekly digest (Slack/e-mail), and an API for the hosted tier.
5. **Method-level selection, measured first**: record buckets per test method as an option and
   compute how much a method-level filter would save over class-level before building it.

- **Licensing independent of the payment provider** (2026-10-02): keys sold through a Merchant of
  Record (Polar.sh primary, Paddle fallback) and keys from direct sales (Turkish corporate customers
  with a TL e-Fatura) must work the same way, so the license check cannot depend on one provider's API.

- ~~**Hang detection by idleness**~~ (done 2026-10-03): a mutant that deadlocked the test host (0% CPU)
  was only killed after the run timeout (80+ minutes on Newtonsoft.Json). Test runs of the evaluation
  are now killed as stuck when their process tree uses under 0.2 s of CPU for `--idle-timeout`
  (default 5 minutes; `ps`, so not on Windows). Same outcome as a timeout, much sooner.

- ~~**Parse sources with each target framework's real symbols (priority)**~~ (done 2026-10-03: the
  MSBuild evaluation reports DefineConstants per target framework, after the SDK's implicit
  framework symbols; the index and the change detector parse once more per distinct relevant set)
  (round 2, 2026-10-03):
  files are parsed twice, with no symbols and with every symbol their conditions name. Code under a
  mix (`#if NET6_0_OR_GREATER && !NETSTANDARD2_0`, Newtonsoft.Json's DateOnlyTests) is entered by
  neither, so the name trace never sees it. Shipped: classes the map recorded are candidates even
  when the index cannot see them (coverage decides them). Still open: a caller inside such code is
  invisible to the name trace. Fix: read `DefineConstants` per evaluated target framework from the
  MSBuild graph evaluation and parse once per distinct symbol set.

## Later

- Windows: build, record and select on Windows; path and process differences.
- CI in practice: map cache hit rates and end-to-end times in a real pipeline.
- Name: TestDetta is in use (testdetta.com, the social accounts, the GitHub organisation); the nuget.org
  package id `testdetta` and the Marketplace slug `testdetta` are free (both 404 on 2026-10-07). Open:
  a trademark search (owner: TÜRKPATENT, WIPO) and reserving the `TestDetta` ID prefix on nuget.org.
- Paid tier: hosted map store and ROI history.
