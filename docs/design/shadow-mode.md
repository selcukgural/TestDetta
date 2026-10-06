# Shadow mode: select, run everything, prove it

Status: first sketch 2026-10-05; widened 2026-10-07 at the owner's request: shadow mode is the way into
cautious organisations, and it must put the evidence in front of them in full detail, with no room
for misreading. Owner decisions (2026-10-07, all as proposed): classification on by default; history in
CI artifacts for v1. Later the same day: shadow mode never needs a license, and in return shows decisions
only for the tests the job ran; the team's own `dotnet test` command runs as given; graduation is 40 pull
requests in 30 days plus a `td audit` run on the team's code (below); `td audit` is part of v1.

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
   directory, adding only what it needs for a test report per project (VSTest: `--logger trx` and a results
   directory, unless the team names its own; the testing platform: its report option). Otherwise (the
   command names test assemblies, no single solution is there to run, a legacy project needs the full
   MSBuild, two test projects build assemblies of the same name, or testing-platform projects need different
   report options) every test project runs on its own, one after another, with the team's arguments, and the
   report says so and why. Either way every test the command runs, runs. The job passes or fails on that
   run alone (exit code 0, or 3 when it failed). The job can take longer by TestDetta's analysis before the
   run and by the classification reruns after it, which the classification budget caps (45 minutes by
   default); both times are in the report. TestDetta failing (a crash, a timeout, a missing map) never fails
   the job; it only says that this pull request has no shadow result.
2. **Nothing leaves the machine, and no license is needed.** Reports are files in the job (summary and
   artifact). No upload, no telemetry, and in shadow mode TestDetta makes no network call at all: no license
   is checked, refreshed or read, so the Action does not pass the license secret to that step. (The builds
   are `dotnet`'s as without TestDetta; the classification's rerun on the base commit restores that commit's
   packages like any build.)
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
  all. The analysis's held log is written after the run only when every class it decided about ran;
  otherwise one line says it is not shown. A real filter such as `Category!=Integration` keeps working for
  the classes that ran.

## Per pull request

`td test --shadow` (Action `mode: shadow`):

1. **Select** as `td test` does, and keep the decisions (class, selected or not, kind, evidence).
2. **Run everything**: the team's own command as given, with one test report (TRX, or the runner's own)
   per project, or each test project on its own (promise 1).
3. **Compare** each failed test class with the selection:
   - selected: **caught**;
   - not selected: a **miss candidate**, classified before anything is called a miss:
     - fails on the base commit too (the failure was already there): **pre-existing**, not a miss;
     - passes when run alone with the change, twice: **flaky or order-dependent**, not a miss, listed;
     - fails alone with the change and passes alone without it: **miss**.

     The classification reruns only the candidate classes, so it costs seconds unless there are
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
  (95% upper bound below 3%). The audit is needed because real failing pull requests are too few in 30 days
  for a meaningful bound: with n failing pull requests and 0 misses the bound is about 3/n, so even 20 of
  them leave 15%; the audit's 100 breaking changes bring it under 3% in one run.

## `td audit`: TestDetta's own audit on the team's code (part of v1)

The corpus method brought to the customer: on a schedule (nightly or weekly), `td audit` breaks members
of the team's own code one at a time (the evaluator's IL mutants, no source change committed), runs the
tests the full way, and checks that TestDetta's selection contains every class that failed. Result:
"On your code: 160 changes, 104 broke tests, 0 missed (95% upper bound 2.8%)". This is the strongest
evidence we can give, because it does not wait for real failures to happen. It runs on the team's
runners, so it costs their CI minutes: off by default, with a cap. It is part of v1 (owner, 2026-10-07),
because the graduation criteria need one run of it; it is built after shadow mode. Design and as built:
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
6. `td audit` (v1, needed for graduation); later: run-first mode.

## As built (steps 1 and 2, 2026-10-07)

- **CLI** (`ShadowRun`, `ShadowCommand`, `ShadowReport`, `CliApp.Shadow`): `td test --shadow [--shadow-no-classify]
  [--shadow-report <path>] [--shadow-rerun-timeout <time>] [--shadow-rerun-idle-timeout <time>]
  [--shadow-base-timeout <time>] [--shadow-base-idle-timeout <time>] [--shadow-classify-budget <time>]`.
  Any exception in the analysis is caught; the full run goes on and the report has `compared: false` with
  the reason. Without a readable project graph the full run is a plain `dotnet test <args>` in the
  working directory (`run.kind: plain`).
- **No license** (owner decision 2026-10-07): `CliApp.Run` sends `--shadow` to `CliApp.Shadow` before the
  license refresh is started, so shadow mode reads no license variable or file, refreshes nothing and makes
  no network call; `td test` without `--shadow` and `td affected` check as before. `CliHost` (environment and
  home directory) lets the tests see which variables a command reads.
- **Held until the full run ends**: the analysis logs into a `StderrLoggerFactory` over a `StringWriter`
  (`ShadowAnalysis.Log`) instead of stderr; nothing is printed before the full run (the plan is no longer
  printed first). After the run the held log goes to stderr only when every class the selection decided
  about appears in the job's reports and every project's selection is shown; otherwise one line says it is
  not shown. The plan is printed with the report: per project its mode, the selected classes among those
  that ran, and how many known classes did not run.
- **The team's command as given** (`ShadowCommand.Target`, `ShadowRun.Verbatim`): the `--` arguments are
  read for their target, in both runners' spelling (`--project`, `--solution`, `--directory`,
  `--test-modules`, or VSTest's positional path; option values and run settings after `--` are skipped). A
  solution (its projects read from `.sln`, `.slnx` or `.slnf`), a project, a directory or no target (the one
  solution or project in the working directory) runs as that one command in that directory
  (`run.kind: verbatim`). VSTest gets `--logger trx --results-directory <temp>` unless the user names a TRX
  logger or a results directory (kept; the run's new reports are read there). Under the testing platform
  (`global.json` of the working directory) every test project the command runs must take the same report
  option (`TestReports.PlatformReportOption`: `--report-xunit-trx` for xUnit v3, `--report-trx` with the
  TRX extension), given without a file name, so each writes its own report next to its output; nothing is
  added when the user already asks for a TRX report. The reports are the `.trx` files that are new or
  rewritten after the run (under the results directory for VSTest, under the working directory and the
  user's results directory for the testing platform) and are matched to test projects by the test assembly
  their tests ran from (`TrxReport.Facts`: `TestMethod/@codeBase`, which VSTest and both testing-platform
  writers give with its case kept), by assembly name, and by folder when two projects build the same name; a
  report that matches no single project is listed in `run.unmappedReports` and not compared. Several reports
  of one project (target frameworks) are read together. Reports TestDetta asked for are moved out of the
  tree; the user's own are copied. Checked on the SDK here (10.0.105): VSTest over a `.slnx` with two
  projects, one of them multi-targeted, writes one TRX per project and framework with distinct names;
  `dotnet test --solution` in testing-platform mode with xUnit v3 projects writes one per project under
  `bin/…/TestResults`; a solution mixing xUnit v3 and MSTest (TRX extension) fails with `--report-trx`
  (xUnit v3 does not know it), so such a solution runs per project.
- **Per project instead** (`run.kind: per-project`, with `run.fallbackReason`, shown in every report as "Ran
  each test project on its own, one after another, because …"): when the target is test assemblies or
  modules, when there is no single solution or project to run without a target (the Action passes only
  options unless `dotnet-test-args` names a solution), when a test project in the command needs the full
  MSBuild (legacy), when an assembly name is known only to MSBuild or two test projects build the same
  name, or when testing-platform projects need different report options, one has none, or one has no build
  output yet. The testing platform's report option is read from the build output, because an option a test
  project does not know fails it; a fresh checkout therefore has none to read, and a job should build first
  (as one that runs `dotnet test --no-build` does). Open: per project, such a project gets no report either,
  so its failures are unexplained; choosing the option from package references would avoid the build but
  a wrong guess would fail the team's run. Then every test
  project runs through `TestRunner` (legacy projects as `td test` runs them) with the user's arguments
  without their target and a report, as before: VSTest gets `--logger trx` and a results directory unless
  the user names their own; a testing-platform project gets the report option its build output supports
  (xUnit v3's own, the TRX extension, NUnit's result XML), found with `dotnet msbuild -getProperty:TargetPath`,
  and none when the user already asks for a TRX report or nothing fits (its failures are then "unexplained").
- **Exit code**: the full run's: the verbatim command's own, or 1 when any project failed in per-project
  runs; `td` returns 0 or 3. In a verbatim run a project's `exitCode` comes from its reports (1 when a test
  failed or the run's outcome is Failed, Error, Aborted or Timeout, else 0) and is null without a report; a
  run that failed while no project's report says so is `unexplainedRun`.
- **Classification**: candidates of a project rerun together, inside the user's own filter
  (`TestFilter.WithinUserFilter`; a filter that cannot be joined leaves them unclassified), with
  `--no-build`, at most 15 minutes (`--shadow-rerun-timeout`), always one project at a time with the user's
  arguments without their solution or project. Classes that pass rerun once more:
  passing again is flaky ("failed only next to the other tests"), failing is flaky ("passed once and
  failed once"). Classes that fail alone rerun on the merge base in a detached `git worktree` under the
  temp folder, built there, at most 30 minutes (`--shadow-base-timeout`): failing is pre-existing,
  passing is a miss, no test of it there (a new class) is a miss, a base run that does not build, times
  out or writes no report leaves them unclassified. The worktree and every report are deleted at the
  end. Legacy (full MSBuild) projects are not rerun; their left-out failures are unclassified.
- **Time limits** (owner decision 2026-10-07: sensible defaults, every limit configurable): a rerun with
  the change at most 15 minutes and 5 minutes idle, the base rerun 30 and 10 (`ShadowLimits.Default`).
  "Idle" is `DotnetProcess`'s watchdog: the process tree using almost no CPU, not silent output. The
  idle limits are two options, not one, because their defaults differ for a reason: the base rerun
  restores in a fresh checkout, which waits on the network with the CPU nearly still. Over all of it
  a budget (`--shadow-classify-budget`, 45 minutes; `ShadowBudget`) starts when the full run ends and
  covers every rerun and the base checkout: each rerun's timeout is cut to what is left, a rerun killed
  at that cut, or a step with less than a second left, leaves the remaining candidates unclassified
  with "the classification time budget (45 min) ran out" (a class that already passed alone stays
  flaky, its second rerun skipped). The full run has no TestDetta limit and its exit code is never
  touched. Syntax: `90s`, `15m`, `1h`, `1h30m`, a plain number of minutes, `0` or `none` for no limit,
  at most `168h`; the options need `--shadow`. The Action passes its inputs of the same names
  (`shadow-rerun-timeout` …) only when they are set. The limits used appear in the console, the job
  summary (with the time spent of the budget), the JSON's `classification` object and the reproduction
  options.
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
- **Action**: `mode: shadow` shares the base and map steps of `test`, runs `td test --shadow` with the
  report in `$RUNNER_TEMP` and without the license secret, and keeps the report as the artifact
  `testdetta-shadow-<run id>-<attempt>` for 90 days, also when the tests failed. It passes `-c
  <configuration>` and `dotnet-test-args` after `--`; naming the solution there makes it one command.

### `testdetta-shadow.json` (schema 2)

Schema 2 (2026-10-07) against 1: decisions only for classes in the job's reports (`notRun` lists the rest
by name), the project mode `not-shown`, a project `exitCode` that may be null, estimates only for projects
all of whose known classes ran, the `run` object and `totals.wallSeconds`, `totals.notRunClasses` and
`totals.unexplainedRun`, and `reproduction.command` is now the license-free `td test --shadow` with
`reproduction.explainCommand` next to it.

```jsonc
{
  "schema": 2,
  "verdict": "Would have run 14 of 212 test classes (6.6%), 1 m 10 s of 18 m 30 s of test time; the whole test run took 9 m 40 s; …",
  "compared": true,                 // false: no selection, see noResultReason
  "noResultReason": "…",            // only when compared is false
  "classified": true,               // false with --shadow-no-classify
  "analysisSeconds": 4.2,
  "run": {
    "kind": "verbatim",             // verbatim | per-project | plain
    "command": "dotnet test Shop.slnx -c Release --logger trx --results-directory /tmp/…",
    "fallbackReason": "…",          // only for per-project
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
    "classes": [{                   // only classes in this job's reports
      "name": "Shop.Tests.TaxTests", "selected": true,
      "decision": "Coverage",       // ClassDecisionKind, or not-affected / unknown
      "evidence": "ran Shop.Calc.Tax, which changed (map 9f3c…)",
      "seconds": 0.8, "failed": true,
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
                "--shadow-rerun-timeout 15m", "--shadow-rerun-idle-timeout 5m", "--shadow-base-timeout 30m",
                "--shadow-base-idle-timeout 10m", "--shadow-classify-budget 45m",   // or --shadow-no-classify
                "-- Shop.slnx -c Release"],
    "command": "td test --shadow --base <merge base> --max-map-age 50 --drift-fallback all … -- Shop.slnx -c Release",   // no license needed
    "explainCommand": "td affected --base <merge base> --max-map-age 50 --drift-fallback all --format json"         // needs a license
  }
}
```
