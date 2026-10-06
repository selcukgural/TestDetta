# Shadow mode: select, run everything, prove it

Status: first sketch 2026-10-05; widened 2026-10-07 at the owner's request: shadow mode is the way into
cautious organisations, and it must put the evidence in front of them in full detail, with no room
for misreading. Owner decisions (2026-10-07, all as proposed): classification on by default; history in
CI artifacts for v1; graduation at 200 pull requests, 20 of them with failing tests, no unexplained miss;
`td audit` right after v1.

## Problem

A team will not let a new tool skip its tests before it has seen the tool be right on its own pull
requests. The corpus evaluation shows TestDetta is safe on open-source repositories, but a buyer wants
to see it on their own code, with no risk while they look, and in a form they can check and forward to
whoever signs off (a lead, an architect, security, an auditor). "Test skipping" alarms such readers;
every claim must therefore be measured, reproducible and explained, and anything that could look like a
miss but is not (a flaky test, a failure already on the base branch) must be told apart before it is
reported.

## Promises (what the documentation states and the tests hold)

1. **Nothing changes for the team.** The full suite runs exactly as without TestDetta: the same
   `dotnet test` commands and arguments, every project, every class. The job passes or fails on that run
   alone. TestDetta failing (a crash, a timeout, a missing map) never fails the job; it only says that
   this pull request has no shadow result, and its own analysis time is reported.
2. **Nothing leaves the machine.** Reports are files in the job (summary and artifact). No upload, no
   telemetry; the license check is the only network call, as today.
3. **Every number is measured, not estimated.** Time saved comes from the full run's own test reports.
4. **Every decision can be checked.** Each skipped class says why, and the same decision can be
   reproduced locally from the same commit and map.

## Per pull request

`td test --shadow` (Action `mode: shadow`):

1. **Select** as `td test` does, and keep the decisions (class, selected or not, kind, evidence).
2. **Run everything** with one test report (TRX, or the runner's own) per project.
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
     time; every failing test was selected", or the misses, plainly;
   - misses with the diff hunk, the decision chain that left the class out and the map commit, and a
     prepared report to send us (nothing is sent on its own);
   - failures caught, pre-existing and flaky ones, separately;
   - time per project and class from the full run, and the part the selection would have run, next to
     TestDetta's own estimate (so the estimate's accuracy is visible too);
   - inputs for reproduction: TestDetta version and binary hash, base and head commits, map commits and
     hashes, options; and the command that reproduces the decision locally (`td affected --explain`
     at that commit with that map).

## Over time

Single pull requests convince nobody; a month of them does.

- **Roll-up**: `td shadow report <folder>` reads the `testdetta-shadow.json` files of many runs (Actions
  artifacts downloaded, or a folder the CI keeps) and writes one report: pull requests analysed,
  failures caught, misses, flaky classes seen, minutes the selection would have saved (total, median,
  per pull request), selection share distribution, and the statistical bound: with 0 misses in n
  pull requests that had failures, the miss rate is below 3/n at 95% confidence, stated with n.
  History lives in CI artifacts for v1 (no setup); a branch or the hosted tier may come later.
- **Graduation criteria**, written in the documentation before the team starts, so the exit is not a
  sales argument: at least 200 pull requests, 20 of them with failing tests, and no unexplained miss.

## Optional: TestDetta's own audit on the team's code

The corpus method brought to the customer: on a schedule (nightly or weekly), `td audit` breaks members
of the team's own code one at a time (the evaluator's IL mutants, no source change committed), runs the
tests the full way, and checks that TestDetta's selection contains every class that failed. Result:
"On your code: 100 changes, 63 broke tests, 0 missed (95% upper bound 4.6%)". This is the strongest
evidence we can give, because it does not wait for real failures to happen. It runs on the team's
runners, so it costs their CI minutes: off by default, with a cap. Built right after v1.

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
6. Later: `td audit`, run-first mode.

## As built (steps 1 and 2, 2026-10-07)

- **CLI** (`ShadowRun`, `ShadowReport`, `CliApp.Shadow`): `td test --shadow [--shadow-no-classify]
  [--shadow-report <path>]`. Any exception in the analysis or the license check is caught; the full run
  goes on and the report has `compared: false` with the reason. Without a readable project graph the full
  run is a plain `dotnet test <args>` in the repository. Every test project runs through `TestRunner`
  (legacy projects as `td test` runs them) with the user's arguments and a report: VSTest gets `--logger
  trx` and a results directory unless the user names their own (kept; the run's new `.trx` files are read
  there); a testing-platform project gets the report option its build output supports (xUnit v3's own,
  the TRX extension, NUnit's result XML), found with `dotnet msbuild -getProperty:TargetPath`, and none
  when the user already asks for a TRX report or nothing fits (its failures are then "unexplained").
- **Classification**: candidates of a project rerun together, inside the user's own filter
  (`TestFilter.WithinUserFilter`; a filter that cannot be joined leaves them unclassified), with
  `--no-build`, at most 15 minutes. Classes that pass rerun once more: passing again is flaky ("failed
  only next to the other tests"), failing is flaky ("passed once and failed once"). Classes that fail
  alone rerun on the merge base in a detached `git worktree` under the temp folder, built there, at most
  30 minutes: failing is pre-existing, passing is a miss, no test of it there (a new class) is a miss, a
  base run that does not build, times out or writes no report leaves them unclassified. The worktree and
  every report are deleted at the end. Legacy (full MSBuild) projects are not rerun; their left-out
  failures are unclassified.
- **Times**: a project's `wallSeconds` is its measured `dotnet test` duration (build included); a class's
  `seconds` the sum of its tests' durations in the report. The selection's part: everything for `all`,
  nothing for `skipped` and `not-affected`, and for `filtered` the time outside test classes plus the
  selected classes' time (all of it when the project had no report).
- **Action**: `mode: shadow` shares the base and map steps of `test`, runs `td test --shadow` with the
  report in `$RUNNER_TEMP`, and keeps it as the artifact `testdetta-shadow-<run id>-<attempt>` for 90
  days, also when the tests failed.

### `testdetta-shadow.json` (schema 1)

```jsonc
{
  "schema": 1,
  "verdict": "Would have run 14 of 212 test classes (6.6%), 1 m 10 s of 18 m 30 s of test time; …",
  "compared": true,                 // false: no selection, see noResultReason
  "noResultReason": "…",            // only when compared is false
  "classified": true,               // false with --shadow-no-classify
  "analysisSeconds": 4.2,
  "totals": {
    "projects": 3, "failedProjects": 1, "classes": 212, "selectedClasses": 14,
    "fullSeconds": 1110.0, "selectedSeconds": 70.0,
    "estimatedFullSeconds": 900.0, "estimatedSelectedSeconds": 55.0,   // when maps have timings
    "caught": 1, "misses": 0, "flaky": 0, "preExisting": 0, "unclassified": 0, "unexplainedProjects": 0
  },
  "projects": [{
    "path": "tests/Shop.Tests/Shop.Tests.csproj",
    "mode": "filtered",             // all | filtered | skipped | not-affected | unknown
    "reason": "…", "exitCode": 1, "wallSeconds": 30.5, "selectedSeconds": 4.0,
    "estimatedFullSeconds": 25.0, "estimatedSelectedSeconds": 3.1,
    "reported": true,               // false: no test report, failures per class unknown
    "classes": [{
      "name": "Shop.Tests.TaxTests", "selected": true,
      "decision": "Coverage",       // ClassDecisionKind, or not-affected / unknown
      "evidence": "ran Shop.Calc.Tax, which changed (map 9f3c…)",
      "seconds": 0.8, "failed": true,
      "verdict": "caught",          // failed classes only: caught | miss | flaky | pre-existing | unclassified
      "note": "…"                   // what the reruns showed
    }]
  }],
  "reproduction": {
    "testDettaVersion": "…", "testDettaSha256": "…", "baseRef": "origin/main",
    "mergeBase": "…", "head": "…",
    "maps": [{ "testProject": "…", "commit": "…", "sha256": "…" }],
    "options": ["--base origin/main", "--max-map-age 50", "--drift-fallback all", "-- -c Release"],
    "command": "td affected --base <merge base> --max-map-age 50 --drift-fallback all --format json"
  }
}
```
