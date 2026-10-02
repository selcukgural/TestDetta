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
- **x64-only test projects are left out on Apple silicon** (Vogen's SnapshotTests: "Could not find
  'dotnet' host for the 'X64' architecture"). The evaluation now leaves out a project whose run
  without mutants aborted before running a test, instead of counting every later abort as a mutant
  caught by a crash. Evaluate such repositories once on an x64 runner (CI) to cover them.

Interim fixes that are safe for the common case; come back for the complete solution.

- **Fixtures declared by a base class** (interim: 514238e; FastEndpoints, 31 classes missed).
  `IClassFixture<T>`/`ICollectionFixture<T>`/`IAssemblyFixture<T>` implemented by a base class,
  also in another library, with the fixture as a generic argument (FastEndpoints.Testing's
  `TestBase<TAppFixture>`) now marks the fixture. Still ambient: xUnit v3
  `[assembly: AssemblyFixture(typeof(X))]` and fixtures created by a custom test framework without
  such an interface. Complete fix: treat assembly fixtures as a setup scope for the assembly, and
  credit code that runs before the first test of a class while a fixture type's constructor is on
  the stack to that fixture.
- **Recording can hang on a lingering MSBuild node** (seen in Sift.Tests while the corpus ran:
  the test host waited at 0 % CPU with no child process; `MSBUILDDISABLENODEREUSE=1` fixed it).
  `sift record` and the evaluation build with node reuse on and read the child's output, which a
  reused node or compiler server may keep open. Fix: pass `-nodeReuse:false` (and
  `UseSharedCompilation=false` if needed) to the builds Sift starts, with a test that builds two
  projects in parallel and must finish.
- **Linked files whose path needs evaluation** (interim: next commit; OpenTelemetry, 7 classes
  missed). `<Compile Include="$(RepoRoot)\test\OpenTelemetry.Tests\Metrics\MetricApiTests.cs" />`
  with `RepoRoot` from a property function now becomes a path pattern, and the project owns every
  file it matches, when enough of the path is literal (two segments or a file name). Paths with
  nothing literal (`@(Bundle)`, `$(Shared)**\*.cs`) and non-Compile items keep the older rule (the
  project claims files nobody else does), since a match-everything pattern made every project own
  every file. Complete fix: MSBuild evaluation, as for project references.
- **Projects sharing a directory** (interim: 232ee1a; Meziantou.Analyzer, every mutant missed
  in 4 of 5 test projects). A file in a directory with several projects now belongs to all of
  them, so each Roslyn-version project and each of its test projects is affected and indexed.
  Projects that split one directory with `Compile Remove`/`Include` are over-selected. Complete fix:
  honour each project's compile items when they exclude files of its own directory.
- **Setup that runs before every test of a scope** (interim: 7cd8528; MimeKit, 60 classes
  missed). NUnit `[SetUpFixture]` setup (constructor, `[OneTimeSetUp]`) and MSTest
  `[AssemblyInitialize]` now record under a setup scope that selects every class in its namespace
  (or assembly). Still ambient, so traced by name only: xUnit v3 assembly fixtures,
  `[ModuleInitializer]` in test assemblies, TUnit `[Before(Assembly)]`/`[Before(TestSession)]`,
  NUnit `[OneTimeSetUp]` inherited from a base class in another namespace. Complete fix: classify
  every ambient method by what entered it (discovery data, framework setup, module initializer)
  and let setup-like ambient code select its whole scope; measure the precision cost first
  (AutoFixture.NUnit4 has 81 % of its covered code ambient, mostly discovery data).
- **Static constructors** (interim: same commit; MimeKit HtmlAttributeIdExtensions, 12 classes
  missed). A change to a static constructor now selects every class that ran a member of the type,
  besides name tracing. Still missed: static field initializers without an explicit static
  constructor when the field is read only from other types' code, and types whose static state is
  read without calling any of their methods. Complete fix: probes on static field reads (`ldsfld`)
  per bucket, so the readers of what a static constructor set up are known.
- **Project graph from MSBuild files read as XML** (interim: 24df010; Meziantou.Analyzer had
  no test project at all). Test projects marked only by a company SDK (`Meziantou.NET.Sdk.Test`)
  or named `X.Test.roslyn5.9` were not recognised, and `ProjectReference` items declared in imports
  or written with properties were dropped, so a change selected nothing. Now: an SDK or file name
  with a `Test`/`Tests` part marks a test project, references in imports are followed, `@(Item)`
  lists are expanded, and a reference only MSBuild can compute (`$(RoslynVersion)` from a property
  function) becomes a path pattern whose unknown parts match anything, the project depending on
  every project it matches. Still guessed: conditions are not evaluated, a test SDK without "Test"
  in its name is missed, and a property used in the directory part widens the pattern. Complete
  fix: evaluate projects with MSBuild (`dotnet msbuild -getProperty:IsTestProject
  -getItem:ProjectReference`, or MSBuildLocator in-process) when the XML reading is unsure, cache
  per commit, and fall back to the pattern only when evaluation fails. Measure the cost on large
  solutions first.
- **Object pools** (interim: d462594, 6badd5d; Meziantou.Analyzer, 5 classes missed). `Create` on
  a pool policy (`IPooledObjectPolicy<T>`, `PooledObjectPolicy<T>`, matched by name since libraries
  copy Microsoft.Extensions.ObjectPool) is now a cache fill, also when the pool binds it to a
  `Func<T>` and calls that on a miss, as `DefaultObjectPool` does. Other pools (`ArrayPool`, hand-rolled
  `Stack<T>` caches, `[ThreadStatic]` buffers) still credit what they build to the first class.
  Complete fix: same as hand-rolled lazy initialisation below (field-read probes), or detect
  "create on miss" call sites generally.
- **Hand-rolled lazy initialisation** (interim: 5d01d7a; Open-XML-SDK, 1 of 80
  missed). `Interlocked.CompareExchange(ref _x, new X(), null)` is now a cache fill from where its
  arguments start, when they are straight-line code that constructs or calls something. Still not
  seen: `_x ??= new X()` and `if (_x == null) _x = new X()` on an instance field, and arguments with
  branches inside; an instance method that only such a builder runs is credited to the first class
  (static members and constructors are covered by the run-once rule). Complete fix: probes on
  reads of fields that are written once, crediting the code that built the value to every bucket
  that reads it, or an IL pattern match of null check → store. Red test: an instance method
  reached only from a `??=` builder on a shared instance, used by three classes in two processes.
- **Test data credited to both buckets** (interim: 7c33ec6). A data member enters the class that
  uses it and the marker is not undone on return, so lazily built rows stay with that class. A
  test that calls another class's data member directly therefore credits the rest of its own code
  to that class: a possible miss. Complete fix: while data code runs, and until the next class or
  fixture is entered, credit hits to both the data's class and the bucket that was current
  before. Measure the cost on the hit path first (one extra field read per hit) and add a test
  where a test calls another class's data member.
- **Startup time vs. discovery-time data** (interim: 7c33ec6). Theory data enumerated during
  discovery now ends the recorded startup time early and adds discovery time to the first data
  class. Complete fix: count time spent in data members during discovery as startup (it is paid
  by every run), so per-class times and the value report stay right.
- **Files rewritten for other tools match the SDK byte for byte** (interim: 911e519). The patched
  `deps.json` broke xUnit 2.3's own JSON reader through `+` escaping. Complete fix: a test
  that patches real SDK-written `deps.json` files and compares them with the original except for
  the added entries, and the same check for any other file Sift rewrites.
- **Eval: test projects without a test report are left out** (interim: 871f6aa). NUnit
  under Microsoft.Testing.Platform has no TRX extension unless the repository adds it
  (NSubstitute: every test project, so no data at all), and some hosts die at start
  (protobuf-net.Test, exit 134). Complete fix: make the evaluation add
  `Microsoft.Testing.Extensions.TrxReport` to such projects at build time (a
  `CustomAfterMicrosoftCommonTargets` file, `VersionOverride` under central package management),
  so they can be evaluated instead of skipped.
- **Recording suites that only pass in parallel** (interim: this commit records a project again in
  one process when a shard crashes). NLog.UnitTests takes its own test host down ("Out of memory")
  whenever its classes run one at a time, without Sift too; recording requires that, so NLog gets
  no map and falls back to name tracing. Complete fix: when the sequential recording crashes,
  record with the suite's own parallelism, credit code inside a test flow (AsyncLocal) as now, and
  treat code outside every flow as ambient (selected for any change) instead of crediting the
  class entered last. Add a test with a suite that crashes when run sequentially.

## Next

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
