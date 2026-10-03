# Backlog

Work agreed on but not started, in the order we plan to take it. Reliability and correctness
come first: the evaluation corpus and every finding there before anything below, and features
that report numbers (value reporting) only once the numbers are proven right.

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
- ~~**Project references used as an analyzer or source generator**~~ (done 2026-10-03,
  docs/design/opaque-project-references.md): a reference with `OutputItemType` or
  `ReferenceOutputAssembly="false"` is opaque, and an affected opaque reference makes its consumer
  a project-level change. Found in protobuf-net; 20 of 41 corpus repositories have such references.
- **Framework caches that call repository code back** (2026-10-03, docs/design/external-callbacks.md):
  third-party callbacks are now run-once code. .NET's own callbacks are not, because most are
  per use. Those it caches (`JsonConverterFactory.CreateConverter`, `TypeConverter`, ...) and
  delegates handed to a third-party library (`AddSingleton(sp => ...)`) could still be credited
  to the first class only. Needs a list of caching framework contracts, or a measurement on the
  corpus.
- ~~**Precision: attribute polyfills reached by the name spread**~~ (done 2026-10-03): an
  attribute class nothing names (base certainly `System.Attribute`) has no instance, so it is dead
  code rather than "found through reflection" (`SourceIndex.IsAttributeClass`). NSubstitute's share
  did not move (73.8%): the same classes are reached through code run under Castle's interceptors,
  which the external-callback rule makes run-once. That is the real cost (see the next item).
- **Precision: per-call third-party callbacks** (NSubstitute 46% → 74%, docs/design/external-callbacks.md):
  interceptors are called per call, not cached, but count as run-once like the proxy generation
  hook. Options to measure: a recorded signal (a callback that runs in every bucket using the
  library vs. once per key), or a list of contracts known to be per call.
- ~~**Direct changes to a member's metadata**~~ (done 2026-10-03,
  docs/design/reflection-types-with-coverage.md "Direct metadata changes"): the change detector
  reports `MetadataChanged` when a declaration's attributes or parameter defaults changed (or a
  using of its file). Such a change keeps the classes the map names and is traced through its type.
- ~~Stable mutant sample across Sift versions~~ (done, 99347df): each candidate's place and
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
  reuse but how Sift waited: after dotnet exited, it waited for its output to close, and a child
  left behind (MSBuild node, compiler server, a test's background process) kept it open. Sift now
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
  and setup of custom frameworks Sift does not know; classify ambient code by what entered it if
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
  the only text file Sift rewrites; assemblies go through Cecil.
- ~~TUnit projects were not recorded or filtered~~ (found 2026-10-02): TUnit ignores the VSTest
  `--filter` and runs no test at all, and runs none either when given a run settings file. Sift now
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
  crashes too, the project is recorded with the suite's own parallelism (`SIFT_PARALLEL_FLOWS`):
  code inside a test flow counts for its class, code outside every flow counts as ambient instead of
  for the class entered last, and the map has no class times. To be checked on NLog in round 2.

## Next

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
   - ~~Cache recordings~~: done (7f38a58), keyed by commit, recording settings and the contents
     of Sift's core, coverage and tracker assemblies, so any Sift change records again. Baselines
     are not cached: one or two suite runs per commit, and they also measure the time limits and
     anchor the schema and parallel checks.
   - ~~Tiers~~: nightly smoke corpus on GitHub done (6a1a57e; Scriban, Fluid, Carter, 20 mutants each,
     about 18 minutes on a 2-core runner; first run 0 of 47 missed). User chose nightly over per-commit
     and kept the repository private (Actions minutes). A run is compared with an earlier one
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
     hang in Sift); no wait on a child's output (2cbf3bf); the queue's fast-failure guard. Still:
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
   use today, so the first comparison is Sift vs dotnet-affected on the same synthetic changes
   (selected share and time; both should miss nothing). From round 2 every synthetic run measures
   project-level selection (the `ProjectLevel` selector: whole affected test projects, from Sift's
   own MSBuild graph and impact) next to Sift's, per mutant and per repository in summary.md;
   confirming with the real dotnet-affected tool on a few repositories is the next step: built
   (`--compare-affected <dotnet-affected executable>`, version 6.2.0, run without `--from` so the
   working tree is compared with HEAD, report written outside the clone since v6 counts files in it
   as changes); on a two-project probe it matched the ProjectLevel selector exactly (56% vs Sift 33%). Datadog Test Impact Analysis is the
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
4. **Value reporting: show exactly what Sift saved, and why it was safe.** Not "you saved five
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
- Product name before any public launch (Sift is taken on the Marketplace and NuGet).
- Paid tier: hosted map store and ROI history.
