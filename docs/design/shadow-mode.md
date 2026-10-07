# Shadow mode: select, run everything, prove it

Status: first sketch 2026-10-05; widened 2026-10-07 at the owner's request: shadow mode is the way into
cautious organisations, and it must put the evidence in front of them in full detail, with no room
for misreading. Owner decisions (2026-10-07, all as proposed): classification on by default; history in
CI artifacts for v1. Later the same day: shadow mode never needs a license, and in return shows decisions
only for the tests the job ran; the team's own `dotnet test` command runs as given; graduation is 40 pull
requests in 30 days plus a `td audit` run on the team's code (below); `td audit` is part of v1. Reviewed
2026-10-07 and fixed: the team's results are never touched, fail-open holds before and after the full run
and in the Action, the per-project run keeps to the command's projects, and verdicts compare failing tests
(as built, below).

## Problem

A team will not let a new tool skip its tests before it has seen the tool be right on its own pull
requests. The corpus evaluation shows TestDetta is safe on open-source repositories, but a buyer wants
to see it on their own code, with no risk while they look, and in a form they can check and forward to
whoever signs off (a lead, an architect, security, an auditor). "Test skipping" alarms such readers;
every claim must therefore be measured, reproducible and explained, and anything that could look like a
miss but is not (a flaky test, a failure already on the base branch) must be told apart before it is
reported.

## Promises (what the documentation states and the tests hold)

1. **The team's own test command, and the job's result is its result.** When the team's `dotnet test`
   arguments name a solution (`.sln`, `.slnx`, `.slnf`), one project, a directory, or nothing (the one
   solution or project in the working directory), TestDetta runs exactly that one command in the same
   directory, adding only what it needs for a test report per project: VSTest `--logger trx` (nothing when the
   team names a TRX logger), the testing platform its report option (nothing when the team asks for a TRX
   report). The team's results directory, loggers and files stay as they are: nothing of theirs is redirected,
   moved or deleted; TestDetta reads the reports where the run wrote them and copies them, and deletes only the
   reports its own added report option made (none when the team asks for a TRX report). Otherwise (the
   command names test assemblies, no single solution is there to run, a legacy project needs the full
   MSBuild, two test projects build assemblies of the same name, or testing-platform projects need different
   report options) each test project the command runs runs on its own, one after another, from the same
   directory with the team's arguments, and the report says so and why; when the command's projects cannot
   be told (several candidates, a solution file that cannot be read, test assemblies), every test project
   under the directory (for assemblies, of the repository) runs, and the report says that too. The job passes
   or fails on that run alone (exit code 0, or 3 when it failed). The job can take longer by TestDetta's
   analysis before the run, which `--shadow-analysis-timeout` caps (10 minutes by default), and by the
   classification reruns after it, which the classification budget caps (45 minutes by default); both times
   are in the report. Fail-open, as far as it is built: any TestDetta error before the run leaves the team's
   plain command to run as given; one after it leaves the run's exit code as it is and writes a report with
   `compared: false` and the reason. In the Action, a step before the tests that cannot do its part (a shallow
   checkout, the tool's download or checksum, a missing base branch, an invalid input) and a `td` that ends
   without a result of the tests make the step run the team's plain `dotnet test` with the same arguments
   instead, with a warning in the summary; the report artifact's upload cannot fail the job. What is not
   covered: the team's own command failing because of something TestDetta added (an option a runner rejects;
   TestDetta adds options only when the build output shows they are known), and a runner killed from outside
   (the job's own timeout).
2. **Nothing leaves the machine, and no license is needed.** Reports are files in the job (summary and
   artifact). No upload, no telemetry, and in shadow mode TestDetta itself makes no network call: no license
   is checked, refreshed or read, so the Action does not pass the license secret to that step. What does use
   the network in a shadow job is not TestDetta's: the Action downloads the TestDetta package from nuget.org
   (checked against the SHA-512 in `tool.lock`) and lists and restores the maps in the Actions cache, as in
   test mode; the team's builds restore packages as without TestDetta; and the classification's rerun on the
   base commit restores that commit's packages like any build.
3. **Every number is measured, not estimated.** Time saved comes from the full run's own test reports, and
   the whole run's wall time is measured and shown next to it.
4. **Every decision can be checked.** Each skipped class that ran says why, and the same decision can be
   reproduced locally, without a license, from the same commit and maps.

## Free, but no free selection

Shadow mode needs no license, so it must not hand out what the license pays for: the selection, for tests
the job did not pay to run. Two paths would do that, and both are closed.

- **Nothing about the selection appears before the full run has finished**: not the plan, not the
  analysis's log (any level, debug included), not a file. The analysis still runs first, on the tree as the
  pull request has it, but its log is held in memory; someone who cancels the job during the full run has
  seen nothing. The plan is printed with the report.
- **Decisions are shown only for classes in this job's own test reports.** A filter that matches nothing
  (`td test --shadow -- --filter "FullyQualifiedName~Nothing"`) makes the full run cost nothing, so the
  report then shows no decision at all: classes the selection knows that did not run are listed by name only
  ("not run in this job"), a project with no test in the reports shows its run's result but no decision
  (`not-shown`), and every total (classes, test time, the selection's part) counts only classes that ran.
  TestDetta's own estimate is shown only for a project all of whose known classes ran, since it covers them
  all; for such a project neither its own mode nor its reason is shown either (a filter that runs one cheap
  class per project would otherwise reveal every project's decision for nothing): what is shown is derived
  from the classes that ran (`partial`). The analysis's held log is written after the run only when every
  class it decided about ran; otherwise one line says it is not shown. A real filter such as
  `Category!=Integration` keeps working for the classes that ran.
- **Limit.** Which classes ran is read from the test reports the run wrote: the `.trx` files written or
  rewritten during the full run anywhere under the repository or in the team's results directory. A job whose
  own steps write such files during the run can make classes look as if they ran, and so see their
  decisions. The protection is meant against a cheap run of the team's ordinary job, not against a job built
  to fake its reports; a team that goes that far can as well buy the license.

## Per pull request

`td test --shadow` (Action `mode: shadow`):

1. **Select** as `td test` does, and keep the decisions (class, selected or not, kind, evidence).
2. **Run everything**: the team's own command as given, with one test report (TRX, or the runner's own)
   per project, or each test project on its own (promise 1).
3. **Compare** each failed test class with the selection:
   - selected: **caught**;
   - not selected: a **miss candidate**, classified before anything is called a miss. Failing tests are
     compared, not classes: a class with one test broken before the change and another broken by it is not
     excused by the first.
     - rerun alone with the change; when a test that failed in the full run fails again, it reruns alone on
       the base commit: when every test that fails with the change fails there too, **pre-existing**, not a
       miss; when one passes there or does not exist there, **miss**;
     - when it passes alone, its project reruns with the change as in the full run (the classes the full run
       ran, under the team's own filter): passing there too is **flaky** (it failed at random), not a miss,
       listed; failing again is **order-dependent**, and the same run on the base commit decides as above
       (failing there too: pre-existing; passing: a miss, "order-dependent, caused by the change");
     - a rerun that cannot run, or that the budget or a cancellation leaves no time for, leaves the class
       **unclassified**, which counts as a miss until checked.

     The classification reruns only projects with candidates, so it costs nothing unless there are
     candidates; it is on by default and can be switched off.
4. **Report** in the job summary and `testdetta-shadow.json`:
   - the verdict line first: "Would have run 14 of 212 classes (6.6%), 1 min 10 s of 18 min 30 s of test
     time; the whole test run took 9 min 40 s; every failing test was selected", or the misses, plainly;
   - how the full run was made: the command as run, or "each test project on its own, because …";
   - misses with the diff hunk, the decision chain that left the class out and the map commit, and a
     prepared report to send us (nothing is sent on its own);
   - failures caught, pre-existing and flaky ones, separately;
   - time per project and class from the full run, and the part the selection would have run, next to
     TestDetta's own estimate (so the estimate's accuracy is visible too);
   - inputs for reproduction: TestDetta version and binary hash, base and head commits, map commits and
     hashes, options; the command that reproduces the run and its decisions without a license (the same
     `td test --shadow`, compared with the merge base, at the head commit with the same maps, whose hashes
     are listed to compare), and, as an extra line, `td affected … --format json`, which lists every
     decision without running tests and needs a license.

## Over time

Single pull requests convince nobody; a month of them does.

- **Roll-up**: `td shadow report <folder>` reads the `testdetta-shadow.json` files of many runs (Actions
  artifacts downloaded, or a folder the CI keeps) and writes one report: pull requests analysed,
  failures caught, misses, flaky classes seen, minutes the selection would have saved (total, median,
  per pull request), selection share distribution, and the statistical bound: with 0 misses in n
  pull requests that had failures, the miss rate is below 3/n at 95% confidence, stated with n.
  History lives in CI artifacts for v1 (no setup); a branch or the hosted tier may come later.
- **Graduation criteria**, written in the documentation before the team starts, so the exit is not a
  sales argument: within 30 days, at least 40 pull requests with a shadow result and no unexplained miss,
  plus one `td audit` run on the team's own code with at least 100 changes that broke tests and 0 missed
  (95% upper bound at most 3.0%, as the audit shows it: rounded up to a tenth, 2.95% at n = 100). The audit
  is needed because real failing pull requests are too few in 30 days for a meaningful bound: with n
  failing pull requests and 0 misses the bound is about 3/n, so even 20 of them leave 15%; the audit's 100
  breaking changes bring it to 3.0% in one run.

## `td audit`: TestDetta's own audit on the team's code (part of v1)

The corpus method brought to the customer: on a schedule (nightly or weekly), `td audit` breaks members
of the team's own code one at a time (the evaluator's IL mutants, no source change committed), runs the
tests the full way, and checks that TestDetta's selection contains every class that failed. Result:
"On your code: 160 changes, 104 broke tests, 0 missed (95% upper bound 2.8%)". This is the strongest
evidence we can give, because it does not wait for real failures to happen. It runs on the team's
runners, so it costs their CI minutes: off by default, with a cap. It is part of v1 (owner, 2026-10-07),
because the graduation criteria need one run of it. Built 2026-10-07 (`td audit`, Action `mode: audit`); design and as built:
docs/design/audit.md (the team's build output is never written: every mutant lives in a copy of the working
tree; the selection per change is `td test`'s own, from the team's maps; the sample is uniform, not weighted
toward covered members; no license and no network call).

## Next step after shadow: run first, then the rest

A second mode with no skipping either: the selected classes run first and report (fast feedback on the
pull request), then the rest of the suite runs in the same job. The job's result is still the full
run's. It gives a real benefit (minutes to the first red) while the team still runs everything, and it
measures the same comparison. Skipping comes after, with the full suite kept on the default branch
(which recording already does).

## Documentation and trust material (five languages)

- A shadow-mode page: the promises above, how to read the report, what each classification means,
  how to reproduce a decision, the graduation criteria.
- A page for reviewers (security and architecture): what TestDetta reads, writes and executes; that it
  rewrites test-output assemblies only while recording and restores them; network use; how binaries
  are built and verified (hash in `tool.lock`).
- What happens after a miss: the report, our fix, a test that keeps it fixed, a note in the changelog.

## Build plan

1. CLI: `--shadow` (select, full run with reports, compare, classify, report, JSON), fail-open behaviour,
   reproduction block. Tests on the sample repository: a caught failure, a miss forced through a stale
   map, a flaky class, a failure already on the base commit, TestDetta crashing.
2. Action `mode: shadow`, the artifact, the summary.
3. `td shadow report` roll-up with the bound.
4. Documentation pages in five languages.
5. Shadow mode on TestDetta itself and on corpus repositories' real pull request histories (replaying
   merged pull requests) before any partner sees it.
6. `td audit` (v1, needed for graduation): built 2026-10-07, docs/design/audit.md. Later: run-first mode.

## As built (steps 1 and 2, 2026-10-07; review fixes the same day)

- **CLI** (`ShadowRun`, `ShadowCommand`, `ShadowReport`, `CliApp.Shadow`): `td test --shadow [--shadow-no-classify]
  [--shadow-report <path>] [--shadow-analysis-timeout <time>] [--shadow-rerun-timeout <time>]
  [--shadow-rerun-idle-timeout <time>] [--shadow-base-timeout <time>] [--shadow-base-idle-timeout <time>]
  [--shadow-classify-budget <time>]`.
- **Fail-open** (`ShadowRun.Run`): three layers. (1) The analysis runs on a task within
  `--shadow-analysis-timeout` (10 minutes; `CliApp.ShadowAnalyze`): an exception or the limit leaves the
  selection empty (`compared: false` with the reason); at the limit `AnalysisOptions.Cancellation` stops it
  at its next step, it is waited for up to a minute (so it does not build next to the team's run), and its log
  is dropped, since what it logged so far could name decisions. (2) Planning (`Plan`: the repository root,
  the project graph, the command's target and scope, the team's directory and its runner, the verbatim plan)
  runs under a catch-all: any exception, or a project graph that cannot be read, makes the full run the
  team's plain `dotnet test <args>` in the working directory (`run.kind: plain`) with the reason. (3) After
  that a catch-all around the full run, the comparison, the classification and the report: an exception
  before the team's command has run (or before every project of the per-project run has) runs the plain
  command instead; one after it writes a report with `compared: false` and the reason and returns the run's
  exit mapping. Inside, each project of the per-project run guards its own steps (its location, its report
  option, finding and reading its reports), so a failure there costs only that project's report; a report or
  folder that cannot be read is skipped (`TrxReport.Facts` returns null for XML it cannot parse, DTDs
  included; the walks skip folders they cannot read); `ShadowCommand.Projects` turns any exception into an
  unknown scope; the classification of a project runs under a catch-all that leaves its candidates
  unclassified. Tests: `ShadowRunTests` injects an unparsable and a DTD report, an unreadable folder, a
  solution filter of an unexpected shape, a broken `global.json` beside a project and in the team's
  directory, an exception before the run and one after it.
- **No license** (owner decision 2026-10-07): `CliApp.Run` sends `--shadow` to `CliApp.Shadow` before the
  license refresh is started, so shadow mode reads no license variable or file, refreshes nothing, and
  TestDetta makes no network call; `td test` without `--shadow` and `td affected` check as before. `CliHost`
  (environment and home directory) lets the tests see which variables a command reads.
- **Held until the full run ends**: the analysis logs into a `StderrLoggerFactory` over a `StringWriter`
  (`ShadowAnalysis.Log`) instead of stderr; nothing is printed before the full run (the plan is no longer
  printed first). After the run the held log goes to stderr only when every class the selection decided
  about appears in the job's reports and every project's selection is shown; otherwise one line says it is
  not shown. The plan is printed with the report: per project its mode, the selected classes among those
  that ran, and how many known classes did not run.
- **The team's command as given** (`ShadowCommand.Target`, `ShadowRun.Verbatim`): the `--` arguments are
  read for their target, in both runners' spelling and every form the `dotnet` command line takes
  (`--project x`, `--project=x`, `--project:x`; the same for `--solution`, `--directory`, `--test-modules`;
  or VSTest's positional path; option values and run settings after `--` are skipped). A solution (its
  projects read from `.sln`, `.slnx` or `.slnf`), a project, a directory or no target (the one solution or
  project in the working directory) runs as that one command in that directory (`run.kind: verbatim`). VSTest
  gets `--logger trx` unless the user names a TRX logger in any spelling (`-l`, `--logger`, `--logger:`,
  `--logger=`, `-l:`, `-l=`, by name or URI; `TestRunArguments.HasTrxLogger`), and no results directory: the
  reports go where the team's run puts them (VSTest's `TestResults` beside each project, the repository's
  own setting, or the user's `--results-directory`). Under the testing platform (the `global.json` of the
  working directory) every test project the command runs must take the same report option
  (`TestReports.PlatformReportOption`: `--report-xunit-trx` for xUnit v3, `--report-trx` with the TRX
  extension), given without a file name; nothing is added when the user already asks for a TRX report. The
  reports are the `.trx` files new or rewritten after the run anywhere under the repository (`.git`, links
  and unreadable folders skipped; `TestRunArguments.TrxFiles`, `ReportTimes`, `Changed`) and in the user's
  results directory, matched to test projects by the test assembly their tests ran from (`TrxReport.Facts`:
  `TestMethod/@codeBase`), by assembly name, and by folder when two projects build the same name; a report
  that matches no single project is listed in `run.unmappedReports` and not compared. Several reports of one
  project (target frameworks) are read together. Every report is read in place and copied into TestDetta's
  temp folder: nothing of the team's in its results location is moved, redirected or deleted, so a later step
  that uploads test results or coverage finds everything the team's run wrote. The reports TestDetta asked for
  itself are deleted once read (`ShadowRun.IsOwnReport`, `DeleteOwnReports`, after review 2026-10-07): when
  TestDetta added its report option (the team has no TRX logger or report option of its own), every `.trx` file
  that did not exist before the run and lies directly in the user's results directory or in a `TestResults`
  folder (VSTest's beside the project, the testing platform's in the build output) is TestDetta's; with the
  team's own logger nothing is added and nothing deleted. Folders, other loggers' files, attachments, older
  reports and a `.trx` a test writes anywhere else stay. Checked on the SDK here (10.0.105):
  VSTest over a `.slnx` with two projects, one of them multi-targeted, writes one TRX per project and
  framework with distinct names; `dotnet test --solution` in testing-platform mode with xUnit v3 projects
  writes one per project under `bin/…/TestResults`; a solution mixing xUnit v3 and MSTest (TRX extension)
  fails with `--report-trx` (xUnit v3 does not know it), so such a solution runs per project.
- **Scope** (`ShadowRun.Scope` → `ShadowScope(Projects, Known, Under)`): the test projects the command runs,
  from its solution, project or the one candidate in its directory. When that cannot be told (several
  candidates, a solution file that cannot be read or has an unexpected shape, a traversal project), every test
  project under the directory (for a solution file, its folder); for test assemblies or modules, every test
  project of the repository; `run.scopeKnown: false` and `run.scope` (that folder) say so, and every report
  adds "Which test projects your command runs could not be told, so every test project under … counts". In a
  verbatim run a test project outside the scope without a report shows "your command does not run it".
- **Per project instead** (`run.kind: per-project`, with `run.fallbackReason`, shown in every report as "Ran
  each test project your command runs on its own, one after another, because …"): when the target is test
  assemblies or modules, when there is no single solution or project to run without a target (the Action
  passes only options unless `dotnet-test-args` names a solution), when a test project in the command needs
  the full MSBuild (legacy), when an assembly name is known only to MSBuild or two test projects build the
  same name, or when testing-platform projects need different report options, one has none, one has no build
  output yet, or looking its build output up ran past the analysis time limit. Only the scope's projects run
  (a solution that leaves a test project out does not run it here either), each from the team's working
  directory (`ShadowPlan.Team`: that directory relative to the root, and the runner its `global.json`
  selects), so relative arguments such as `--settings ci.runsettings` mean what they mean in the team's
  command. The testing platform's report option is read from the build output (`dotnet msbuild
  -getProperty:TargetPath`, at most 2 minutes per lookup during the run), because an option a test project
  does not know fails it; a fresh checkout therefore has none to read, and a job should build first (as one
  that runs `dotnet test --no-build` does). Open: per project, such a project gets no report either, so its
  failures are unexplained; choosing the option from package references would avoid the build but a wrong
  guess would fail the team's run. Legacy projects run through `TestRunner` as `td test` runs them. VSTest
  gets `--logger trx` unless the user names a TRX logger; a testing-platform project gets the report option
  its build output supports (xUnit v3's own, the TRX extension; NUnit's result XML through run settings only
  when the user gives no `--settings`, which it would replace), and none when the user already asks for a TRX
  report (theirs are read) or nothing fits (its failures are then "unexplained"). A project's reports are the
  `.trx` files its run wrote or rewrote (under the repository and the user's results directory) whose
  assembly is its own, copied as above; those its added VSTest logger made are deleted as above (a
  testing-platform report goes into TestDetta's temp folder).
- **Exit code**: the full run's: the verbatim command's own, or 1 when any project failed in per-project
  runs; `td` returns 0 or 3, and 1 only when cancelled before the tests ran. In a verbatim run a project's
  `exitCode` comes from its reports (1 when a test failed or the run's outcome is Failed, Error, Aborted or
  Timeout, else 0) and is null without a report. A project the command runs that wrote no report while the
  command failed (a build error, a crashed host) is `missingReport` and counts among `unexplainedProjects`;
  a run that failed while no project's report says so and none is missing a report is `unexplainedRun`.
- **Classification** (`ShadowRun.Classify`): candidates of a project rerun together, inside the user's own
  filter (`TestFilter.WithinUserFilter`; a filter that cannot be joined leaves them unclassified), with
  `--no-build`, at most 15 minutes (`--shadow-rerun-timeout`), always one project at a time, from the team's
  working directory, with the user's arguments without their solution or project. Failing tests are compared
  by name (`TrxOutcome.FailedTests`, `ShadowClass.FailedTests`): a class "fails again" when a test that failed
  in the full run fails again; when none matches by name but some fail, those count (names can differ between
  runs, and a failure must not be explained away by a name). A class that fails again alone reruns alone on
  the merge base; one that passes alone reruns in its project's full-run context (the project under the
  user's arguments alone: the classes the full run ran) with the change: passing is **flaky**, failing again
  is order-dependent and reruns in the same context on the merge base. On the base commit, in a detached
  `git worktree` under the temp folder, built there, at most 30 minutes (`--shadow-base-timeout`): every test
  that fails with the change failing there too is **pre-existing**; a test that passes there, or a class or
  test project that does not exist there, is a **miss** (for an order-dependent one: "order-dependent, caused
  by the change"); a base run that does not build, times out or writes no report leaves them unclassified.
  Legacy (full MSBuild) projects are not rerun; their left-out failures are unclassified.
- **Reruns write only into TestDetta's folder** (`ShadowRun.RerunArguments`): the user's results directory
  (every spelling), TRX loggers (they may name a file of their own) and, under the testing platform, report
  options are taken out for a rerun, and TestDetta's own are added: VSTest `--logger trx --results-directory
  <temp>`; the testing platform its report option with a unique file name (and `--results-directory <temp>`
  when the user had one), its file moved out of wherever the run wrote it. On the base commit `--no-build`,
  `--no-restore`, `-o`/`--output` and `--artifacts-path` (with their `=` and `:` forms) are taken out too, so
  the worktree restores and builds into its own folders and never over the working tree's build (an absolute
  output path would otherwise make the next rerun with the change run the base commit's code). The base
  worktree lives under the temp folder, outside the repository: files above the repository root (a
  `Directory.Build.props`, `global.json` or `NuGet.config` in a parent folder of the checkout) are not seen
  there, so a repository that depends on them may fail to build on the base commit, which leaves its
  candidates unclassified. A sibling folder of the checkout would see them, but it would write next to the
  team's checkout, which shadow mode avoids.
- **Time limits** (owner decision 2026-10-07: sensible defaults, every limit configurable): the analysis at
  most 10 minutes (`--shadow-analysis-timeout`; the build-output lookups that plan a verbatim run share what
  is left of it), a rerun with the change at most 15 minutes and 5 minutes idle, the base rerun 30 and 10
  (`ShadowLimits.Default`). "Idle" is `DotnetProcess`'s watchdog: the process tree using almost no CPU, not
  silent output. The idle limits are two options, not one, because their defaults differ for a reason: the
  base rerun restores in a fresh checkout, which waits on the network with the CPU nearly still. Over all of
  the classification a budget (`--shadow-classify-budget`, 45 minutes; `ShadowBudget`) starts when the full
  run ends and covers every rerun, the base checkout (`GitClient.AddWorktree` with a time limit: git is
  killed at the budget's end) and build-output lookups during reruns: each rerun's timeout is cut to what is
  left, a rerun killed at that cut, or a step with less than a second left, leaves the remaining candidates
  unclassified with "the classification time budget (45 min) ran out" (a class that already passed alone and
  in context stays flaky). The full run has no TestDetta limit and its exit code is never touched. Syntax:
  `90s`, `15m`, `1h`, `1h30m`, a plain number of minutes, `0` or `none` for no limit, at most `168h`; the
  options need `--shadow`. The Action passes its inputs of the same names (`shadow-analysis-timeout`,
  `shadow-rerun-timeout` …) only when they are set. The limits used appear in the console, the job summary
  (with the time spent of the budget), the JSON's `classification` object and `analysisTimeoutSeconds`, and
  the reproduction options.
- **Cancellation**: `CliApp.Shadow` registers SIGINT and SIGTERM (as `td audit` does) and keeps the process
  alive: the analysis is stopped at its next step, a running `dotnet test` (the team's command, a project, a
  rerun) or `git worktree add` is killed with its children, the remaining candidates are unclassified ("the
  run was cancelled"), the report is written, and the temp folder and the base worktree are removed
  (`GitClient.DiscardWorktree`: `git worktree remove --force --force`, which also removes a checkout stopped
  half way, else the folder, `git worktree prune`, and a registration git left before it was complete). Cancelled before the tests ran, `td` runs nothing and
  returns 1.
- **Times**: `run.wallSeconds` (and `totals.wallSeconds`) is the measured time of the whole full run, from
  its start to its end, build included: the number to compare with the team's usual job time, shown in the
  verdict as "the whole test run took …". A project's `wallSeconds` is, in a per-project run, its measured
  `dotnet test` (build included); in a verbatim run, from the earliest start to the latest finish its
  reports give (`TestRun/Times`), which is the test host's time without the build the command shares, and
  overlaps other projects' time when they run in parallel. A class's `seconds` is the sum of its tests'
  durations in the report. The selection's part of a project: everything for `all`, nothing for `skipped`,
  `not-affected` and `not-shown`, and for `filtered` the project's time minus its classes' time (build in a
  per-project run; start-up and discovery in both) plus the selected classes' time. `totals.fullSeconds`
  and `totals.selectedSeconds` add these up over the projects whose selection is shown, so in a verbatim run
  they are test-host time summed over processes, not wall time; the share in the verdict is
  `selectedSeconds / fullSeconds`, and the whole run's wall time stands next to it, not divided.
- **Reports**: commands are quoted for a shell (`TestRunArguments.Quoted`: spaces, quotes, `;`, `&`, `|`,
  redirections, globs and `$` put an argument in double quotes), so a copied command runs as it ran. In the
  Markdown summary every repository value is a code span (`MarkdownSummary.Code`), and in a table cell its
  pipes are escaped as well (`MarkdownSummary.TableCode`), so a path with `|` cannot split a row.
- **Action**: `mode: shadow` shares the base and map steps of `test`, runs `td test --shadow` with the
  report in `$RUNNER_TEMP` and without the license secret. In shadow mode no step before the tests fails the
  job: a shallow checkout, a .NET setup that fails, a tool download, unpack or checksum failure, a missing or
  unrelated base branch, an invalid `max-map-age` and a failed cache restore each set a `skip` reason (or are
  `continue-on-error`), and the shadow step then runs the team's plain `dotnet test -c <configuration>
  <dotnet-test-args>` with a warning and a line in the job summary; so does a `td` exit other than 0 or 3 (a
  crash, an invalid option). The checksum check is unchanged in every mode: a package that does not match is
  deleted and never unpacked or run. The report is kept as an artifact for 90 days, also when the tests
  failed, named `testdetta-shadow-<run id>-<attempt>-<job>-<hash>-<check run id or random>[-<suffix>]` (the
  hash covers the runner's OS and architecture, the working directory, the configuration, the test arguments
  and the optional `shadow-artifact-suffix` input), so two jobs or matrix legs of one run never ask for the
  same name; the upload has `continue-on-error`. It passes `-c <configuration>` and `dotnet-test-args` after
  `--`; naming the solution there makes it one command.

### `testdetta-shadow.json` (schema 3)

Schema 2 (2026-10-07) against 1: decisions only for classes in the job's reports (`notRun` lists the rest
by name), the project mode `not-shown`, a project `exitCode` that may be null, estimates only for projects
all of whose known classes ran, the `run` object and `totals.wallSeconds`, `totals.notRunClasses` and
`totals.unexplainedRun`, and `reproduction.command` is now the license-free `td test --shadow` with
`reproduction.explainCommand` next to it.

Schema 3 (2026-10-07, review fixes) against 2: verdicts compare failing tests (`classes[].failedTests`, the
full run's failing tests of the class); `flaky` now needs a pass alone and in the project's full-run context,
and an order-dependent failure is `miss` or `pre-existing` (its note says "order-dependent"); a project with
`notRun` classes is `partial: true`, its `mode` and `reason` derived from the classes that ran; a project the
failed command runs that wrote no report is `missingReport: true` and counts in `totals.unexplainedProjects`;
`run.scopeKnown` and `run.scope`; `analysisTimeoutSeconds`; `run.command` no longer holds a results directory
TestDetta added.

```jsonc
{
  "schema": 3,
  "verdict": "Would have run 14 of 212 test classes (6.6%), 1 m 10 s of 18 m 30 s of test time; the whole test run took 9 m 40 s; …",
  "compared": true,                 // false: no selection, see noResultReason
  "noResultReason": "…",            // only when compared is false (the analysis failed or ran past its limit, a TestDetta error, a cancellation)
  "classified": true,               // false with --shadow-no-classify
  "analysisSeconds": 4.2,
  "analysisTimeoutSeconds": 600,    // null: no limit
  "run": {
    "kind": "verbatim",             // verbatim | per-project | plain
    "command": "dotnet test Shop.slnx -c Release --logger trx",
    "fallbackReason": "…",          // only for per-project
    "scopeKnown": true,             // false: the command's test projects could not be told
    "scope": "/repo",               // only when scopeKnown is false: every test project under it counts
    "wallSeconds": 580.2, "exitCode": 1,
    "unmappedReports": []           // test assemblies whose reports matched no single test project
  },
  "classification": {               // only when the classification ran; a limit is null when switched off
    "rerunTimeoutSeconds": 900, "rerunIdleSeconds": 300,
    "baseTimeoutSeconds": 1800, "baseIdleSeconds": 600,
    "budgetSeconds": 2700, "spentSeconds": 48.6,
    "budgetRanOut": false           // true: the budget left failures unclassified or stopped a rerun
  },
  "totals": {                       // classes and times: only classes that ran, in projects whose selection is shown
    "projects": 3, "failedProjects": 1, "classes": 212, "selectedClasses": 14, "notRunClasses": 0,
    "fullSeconds": 1110.0, "selectedSeconds": 70.0, "wallSeconds": 580.2,
    "estimatedFullSeconds": 900.0, "estimatedSelectedSeconds": 55.0,   // when maps have timings
    "caught": 1, "misses": 0, "flaky": 0, "preExisting": 0, "unclassified": 0, "unexplainedProjects": 0,
    "unexplainedRun": false
  },
  "projects": [{
    "path": "tests/Shop.Tests/Shop.Tests.csproj",
    "mode": "filtered",             // all | filtered | skipped | not-affected | unknown | not-shown
    "reason": "…",                  // for not-shown: why (no test of it ran, no report, not in the command)
    "exitCode": 1,                  // null: not known (no report from one command)
    "wallSeconds": 30.5, "selectedSeconds": 4.0,
    "estimatedFullSeconds": 25.0, "estimatedSelectedSeconds": 3.1,   // only when every known class ran
    "reported": true,               // false: no test report, failures per class unknown
    "partial": false,               // true: some known classes did not run; mode and reason come from those that ran
    "missingReport": false,         // true: the command failed and this project, which it runs, wrote no report
    "classes": [{                   // only classes in this job's reports
      "name": "Shop.Tests.TaxTests", "selected": true,
      "decision": "Coverage",       // ClassDecisionKind, or not-affected / unknown
      "evidence": "ran Shop.Calc.Tax, which changed (map 9f3c…)",
      "seconds": 0.8, "failed": true,
      "failedTests": ["Shop.Tests.TaxTests.Taxes"],   // the full run's failing tests, by the report's names
      "verdict": "caught",          // failed classes only: caught | miss | flaky | pre-existing | unclassified
      "note": "…"                   // what the reruns showed
    }],
    "notRun": ["Shop.Tests.SlowTests"]   // classes the selection knows that did not run here: names only
  }],
  "reproduction": {
    "testDettaVersion": "…", "testDettaSha256": "…", "baseRef": "origin/main",
    "mergeBase": "…", "head": "…",
    "maps": [{ "testProject": "…", "commit": "…", "sha256": "…" }],
    "options": ["--base origin/main", "--max-map-age 50", "--drift-fallback all",
                "--shadow-analysis-timeout 10m", "--shadow-rerun-timeout 15m", "--shadow-rerun-idle-timeout 5m",
                "--shadow-base-timeout 30m", "--shadow-base-idle-timeout 10m", "--shadow-classify-budget 45m",   // or --shadow-no-classify
                "-- Shop.slnx -c Release"],
    "command": "td test --shadow --base <merge base> --max-map-age 50 --drift-fallback all … -- Shop.slnx -c Release",   // no license needed
    "explainCommand": "td affected --base <merge base> --max-map-age 50 --drift-fallback all --format json"         // needs a license
  }
}
```
