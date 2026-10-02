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
  scope (see below). Still ambient: fixtures a custom test framework creates without such an
  interface. Complete fix: credit code that runs before the first test of a class while a fixture
  type's constructor is on the stack to that fixture.
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
  Complete fix: detect "create on miss" call sites generally (a pool keeps its objects in a
  collection, so field-read probes do not see them).
- ~~Hand-rolled lazy initialisation~~ (completed by field-read probes): `_x ??= new X()` and
  `if (_x == null) _x = ...` on any field credit the builder to every reader. Not seen: a value
  built on another thread and handed over before the store; the run-once rules stay for it.
- ~~Helpers of another test class~~ and ~~test data credited to both buckets~~: done. An entry point
  of another class (constructor, attributed helper, data member) called while a test of the current
  class runs no longer switches the bucket: the tracker reads the stack when a switch would happen
  (never on the hit path) and keeps the bucket when a frame of the running class (its methods,
  lambdas, iterators, async state machines) lies below the entry point before any test-framework
  frame. Frames of the entry point's own class are skipped, so a data member declared on the
  running class but used by another still switches when the framework calls it. Still the old
  behaviour, which switches: calls from test methods declared on a base class of the running
  class (frames of the base type), and between classes nested in one outer class.
- **Startup time vs. discovery-time data** (interim: 7c33ec6). Theory data enumerated during
  discovery now ends the recorded startup time early and adds discovery time to the first data
  class. Complete fix: count time spent in data members during discovery as startup (it is paid
  by every run), so per-class times and the value report stay right.
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
  NSubstitute can be evaluated. Still left out and named: test hosts that die before running a test
  (protobuf-net.Test, exit 134).
- **Recording suites that only pass in parallel** (interim: this commit records a project again in
  one process when a shard crashes). NLog.UnitTests takes its own test host down ("Out of memory")
  whenever its classes run one at a time, without Sift too; recording requires that, so NLog gets
  no map and falls back to name tracing. Complete fix: when the sequential recording crashes,
  record with the suite's own parallelism, credit code inside a test flow (AsyncLocal) as now, and
  treat code outside every flow as ambient (selected for any change) instead of crediting the
  class entered last. Add a test with a suite that crashes when run sequentially.

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
   at the merge base. With AI-assisted teams merging dozens of commits a day, a PR's merge base
   rarely has a map, so Sift falls back to the full suite in exactly the setting it is for, and
   recording on every main commit is too expensive. Plan: take the newest map recorded at an
   ancestor of the merge base and cover the commits in between with static analysis (Roslyn
   reference graph: changed symbols → callers → test classes), widening wherever static analysis
   is blind (reflection, DI, source generators). The same bridge covers new code and new tests the
   map has not seen. Measure first: how many commits behind main a PR's merge base typically is,
   and how the miss rate and selection change as the map ages (corpus: record at N−k, evaluate at
   N). Safety rule unchanged: when the bridge is unsure, select more.
2. **Compare with dotnet-tia on the corpus.** dotnet-tia (github.com/SebHenn/dotnet-tia, MIT) is
   an open-source .NET tool doing the same job by static analysis only (method level, mutation
   gate, reports zero misses and 35.5% selection on NodaTime). Run it on the same repositories with
   the same mutants and report misses, selection share, precision and analysis time side by side,
   so the claims about runtime coverage (reflection, DI, F#, interface dispatch) rest on numbers.
   Its approach is also the reference for item 1.
3. **Precision round**: safety fixes made selection broader (NodaTime 16% → 34%); measure where
   the extra classes come from and narrow without losing safety (override names scoped to their
   hierarchy, run-once rules, name spreading through common member names like `Initialize`).
4. **Value reporting: show exactly what Sift saved, and why it was safe.** Not "you saved five
   minutes" but the breakdown behind it, in formats people and tools use:
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

## Later

- Windows: build, record and select on Windows; path and process differences.
- CI in practice: map cache hit rates and end-to-end times in a real pipeline.
- Product name before any public launch (Sift is taken on the Marketplace and NuGet).
- Paid tier: hosted map store and ROI history.
