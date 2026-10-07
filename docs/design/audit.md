# `td audit`: the corpus method on the team's own code

Status: designed and built 2026-10-07 (owner decision the same day: part of v1 and of shadow mode's graduation
criteria, docs/design/shadow-mode.md). "As built" at the end records where the build differs from the plan.

## Problem

Shadow mode shows TestDetta on a team's real pull requests, but real failing pull requests are rare: with n of
them and no miss the miss rate is bounded only to about 3/n, so a month gives a weak bound. The corpus evaluation
gives hundreds of failures per repository by breaking code on purpose (IL mutants) and checking that the
selection contains every test class that failed. `td audit` runs that method on the team's own repository, on
their CI runner or a developer machine, so the graduation criterion "at least 100 changes that broke tests and
0 missed (95% upper bound below 3%)" can be met in one run:

> On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103).

## Command

```
td audit [options] [-- <dotnet test arguments>]
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--target-broken <n>` | 100 | Stop once n changes broke tests (conclusively, see "Judging"). |
| `--max-mutants <n>` | no limit | Stop after n changes that ran tests. |
| `--budget <time>` | 4h | Start no change after this long; a change still running is stopped and not counted. `90s`, `15m`, `1h30m`, a plain number of minutes, `0`/`none` for no limit (`Durations.TryParseLimit`, at most 168h). |
| `--parallel <n>` | 1 | Changes tested at once, each in its own working copy. Every one runs test hosts: memory grows with n (the machine once ran out of memory with parallel test hosts), so 1 unless the runner has room. |
| `--seed <n>` | 1 | Seed of the sample order. |
| `--kinds <list>` | all | `throw`, `return-default`, `flip-branch`, `field-value`, or `all`. |
| `--member <Type.Member>` | | Only this member (repeatable): reproduces one change of a report. |
| `--report <path>` | `.testdetta/testdetta-audit.json` | The JSON report. |
| `--summary-file <path>` | | Append the Markdown summary (e.g. `$GITHUB_STEP_SUMMARY`). |
| `--work-dir <dir>` | `/tmp` (Windows: the temp folder) | Where the working copy goes; same disk as the repository for copy-on-write. |
| `--repo`, `--max-map-age`, `--drift-fallback`, `--maps-dir`, `-v`, `--log-level` | as `td test` | The selection is made exactly as `td test` makes it. |
| `-- <args>` | | Passed to every `dotnet test`, as `td test` does (`-c Release`, `--filter "Category!=Integration"`). |

`--base` is refused: every change is compared with `HEAD`. Exit codes: 0 no miss, 3 at least one miss, 1 the
audit could not run or was cancelled (the reason is in the report when one could be written), 2 usage.

## What it reads, writes and executes

- **Reads** the repository: sources, project files (MSBuild evaluation, as `td test`), the coverage maps
  (`.testdetta/coverage`, or `--maps-dir` restored there as `td test` does), and git (`status`, `rev-parse`,
  `ls-files`, the first-parent history for the map age). Nothing is changed in the repository's files or in git:
  no source edit, no commit, stash or worktree.
- **Writes** a work folder `td-audit-<pid>-<random>` under `--work-dir` (a copy of the working tree, backups of
  rewritten assemblies, test reports and logs), deleted at the end; the JSON report; the summary when asked. In
  the repository only TestDetta's own `.testdetta/`: the project-graph cache every `td` command keeps
  (`.testdetta/cache`), the report by default, and with `--maps-dir` the restored maps, as `td test` does.
- **Executes** `cp` (the copy), `dotnet test` and `dotnet msbuild -getProperty` in the copy, `git` read-only.
- **No license and no network call.** Owner decision 2026-10-07: shadow mode needs no license because it skips
  nothing; the audit skips nothing either (every test that could see a change runs, with and without it), so it
  is dispatched before the license refresh like `td test --shadow`: no license variable or file is read, nothing
  is refreshed or sent. The report says so. Like shadow mode, it shows decisions only for classes that ran and
  failed, never a selection for tests that did not run.

## Mutating binaries without touching the team's build output

The team's build output is never written. Every mutant lives in a **copy of the working tree** (sources, build
output and all, `.git` included so tests that look for the repository root still find it):

1. **Start**: earlier work folders under `--work-dir` whose owner file names a process that no longer runs (a
   crash, a `kill -9`, a power cut) are deleted; only folders with that owner file are touched.
2. **Copy**: `cp -cpR` on macOS (APFS clones), `cp -a --reflink=auto` on Linux (Btrfs, XFS), file by file on
   Windows: the evaluator's `WorkerCopies.Copy`, moved into the shared library. Without copy-on-write the copy
   costs the tree's size once; the audit refuses to start when the disk has less free space than the tree plus
   1 GB.
3. **Baseline** in the copy: every test project runs once as `dotnet test <project> <args>` with a test report:
   this builds in the copy (unless `--no-build` is among the arguments, then the copied build output is used), so
   the binaries match `HEAD`.
4. **Outputs**: each audited project's output folders are asked from MSBuild in the copy. An output folder or an
   assembly to rewrite outside the copy (an absolute `OutputPath` or artifacts path) stops the audit before any
   rewrite: it would be the team's own output.
5. **Per change**: the evaluator's `MutationInjector.Inject` rewrites the copies of the member's assembly in the
   copy's test outputs, its backups in the work folder; after the change's runs the backups are copied back in a
   `finally`. A restore that fails stops the audit (the copy can no longer be trusted); nothing outside the copy
   is affected either way.
6. **Ctrl+C, SIGTERM, timeouts**: a cancellation token stops the loop and kills the running `dotnet test`
   process tree; a run past its time limit (five times the project's baseline run, at least two minutes, cut to
   what is left of the budget) is killed the same way. The work folder is deleted in a `finally`, the partial
   report is written (`stopReason: cancelled`).
7. **End**: the work folder is deleted. A folder that cannot be deleted is named in the report; the next run
   removes it (step 1).

So a crash at any point leaves at most a folder in the temp directory, never a mutated assembly in the team's
build. The tests check that the team's build output is byte-identical after a normal run, a cancellation, a
timeout and an exception in the middle of a change.

## The selection for a change: `td test`'s own decision

For each member the evaluator's behaviour-neutral source edit (`TargetSampler`: an empty statement after a
block's brace, parentheses around an expression body, `If False Then` in Visual Basic, `|> id` in F#) is applied
**in memory**: `AnalysisOptions.WorkingTreeOverlay` gives the pipeline the edited text of that file as the working
tree's version, and the file counts as changed. Everything else is `td test --base HEAD` with the edit made on
disk: the same `AnalysisPipeline`, the user's maps, `--max-map-age`, `--drift-fallback`, the project graph. The
only differences, both on the side of selecting more: no source generator is built to compare its output
(generator projects are left out of the sample, below), and the source index reads the edited file's
declarations from disk, whose line numbers it uses only to match test-project classes (edits are in library
code).

Because the analysis compares with `HEAD`, uncommitted changes would join every change and widen every
selection; the audit refuses to run on a working tree with changes to tracked files or untracked files git does
not ignore (`.testdetta/` aside), and says which.

## Which tests run for a change

Every audited test project whose build output in the copy holds a rewritten assembly runs **completely**, the
way the baseline ran it: `dotnet test <project> --no-build <args> <report>`. A project whose output does not
hold the member's assembly cannot load the mutant. Audited are the test projects that wrote a readable report
without crashing in the baseline and do not need the full MSBuild (legacy projects); the rest are named in the
report with the reason and their tests are not part of the audit.

## Judging (the evaluator's rules)

- A class that fails with the change and did not fail in the baseline **broke**.
- Broke and selected: **caught**.
- Broke and left out: run alone with the change; a class that passes then failed only next to other tests and is
  not counted. A class that fails alone runs alone without the change (the original assemblies back for that
  run); failing there too, it fails alone anyway and is not counted. What remains is a **miss**.
- A project run that did not complete (crash, hang, no report): its classes the selection left out run with the
  change; all passing means the failure was in the selection (**caught**); failures are judged as above; an
  incomplete run there too makes the change **inconclusive** (listed, not counted).
- A change with a miss is **missed**; with only caught classes **caught**; with no failure **did not break**; a
  member no audited output holds is **not reached** (listed, not counted).

n is the number of changes that broke tests (caught + missed). The bound is the evaluator's exact one-sided
Clopper-Pearson bound (`MissRate.UpperBound`), stated with n: with 0 misses it is about 3/n.

## The sample, and how it stays unbiased

- **Candidates**: the evaluator's members (methods, constructors, properties, events of C#, Visual Basic and F#
  library projects some test project depends on, with a behaviour-neutral edit; with `field-value` also fields
  with a constant initial value), minus members compiled by a project used as a source generator or analyzer
  (the mutant would have to be compiled into its consumers; the corpus evaluation covers that path).
- **Order**: a stable hash of the member and the seed, the same on every machine; the mutant kind per member
  from another hash among `--kinds`. The order is fixed before anything runs.
- **No coverage in the sample.** The corpus evaluation takes 80% of its sample from members the maps say some
  test ran, to spend its runs where failures are likely. That would bias an audit toward caught: members the
  map says no test runs are exactly where a miss would show (a test that does run them but is not in the map).
  The audit samples **uniformly over all candidates**; coverage is reported per change, never used to pick or
  skip one. It costs runs (uncovered members break tests less often), which the target and budget absorb.
- **Stopping**: the sample is walked in order; the stop conditions look at counts, never at which change missed.
  At the target the counted changes are a prefix of the order (with `--parallel`, changes already running
  finish and count). Changes cut by the budget or a cancellation are listed as stopped, not counted.
- What is left out is left out by construction (no edit, generator, not reached, project not audited), the same
  whatever the tests do, and every such count is in the report.

## Stop conditions

In order of checking before each change: cancelled; `--target-broken` reached; `--max-mutants` reached; budget
spent (it starts with the command: copy and baseline included); sample exhausted. The verdict names the reason,
and says when the target was not reached.

## Report

Console (the verdict, then one line per change as it finishes), Markdown summary, and `testdetta-audit.json`
(schema below). Per miss: the member and its file, the kind, the failing classes, the decision chain (what
TestDetta saw change, how the test project was decided and why, the class's decision and its evidence, the map's
commit), the edit to make by hand, and the command that reproduces it (`td audit --member … --kinds …`).

## Relation to the evaluator

The mutant code is shared, not copied: `TestDetta.Mutants` (new library under `src/`, used by the CLI and the
evaluator) holds the mutant kinds, the injector (`MutationInjector`, `Mutation`, schemas), the target sampler and
its edits, the repository-assembly finder, the tree copy, the covered-member and miss-rate math. The evaluator
keeps its behaviour and its tests (they only gain a `using`); its default sample (80% covered) is unchanged, the
audit asks the sampler for its uniform order.

## As built (2026-10-07)

- **Code**: `CliApp.Audit` (dispatched before the license refresh, with SIGINT and SIGTERM registered through
  `PosixSignalRegistration` so the process lives on to clean up), `AuditRun` (checks, sample, baseline, report),
  `AuditWorkspace` (work folder, owner file, stale sweep, copies, free-space check), `AuditTestRun` (one `dotnet
  test` of a project in a copy, with a report, read and deleted at once; output folders from `dotnet msbuild
  -getProperty:TargetPath`, one per target framework), `AuditProcess` (dotnet with a limit and the cancellation
  token, killing the whole tree; no MSBuild node, build server or compiler server outlives a command), `AuditSelector`
  (the overlay pipeline), `AuditTrial` (one change), `AuditLoop` (the walk and the stop conditions), `AuditReport`
  and `AuditResult`. The report-argument helpers of shadow mode moved to `TestRunArguments` for both. The shared
  library is `src/TestDetta.Mutants` (`MutantKind`, `MutationInjector`, `TargetSampler` with
  `SampleRequest.Uniform`, `RepositoryAssemblies`, `TreeCopy`, `Selections`, `MissRate`); the evaluator forwards to
  it and its tests are unchanged but for a `using`. The recording fingerprint did not change (TestDetta.Coverage
  and TestDetta.Core only gained an `InternalsVisibleTo`; `AnalysisOptions`, `AnalysisPipeline` and
  `CodeChangeReader`, which gained the overlay, are not reached by the recorder).
- **Dirty tree**: `GitClient.ChangedFiles(root, "HEAD")` without `.testdetta/`, the same files `td test --base HEAD`
  would add; any refuses the audit (exit 1, with a report naming up to five).
- **Baseline**: per test project in the copy, `dotnet test <project> <args>` with a report (VSTest TRX, the testing
  platform's own report option, NUnit's XML), building there unless `--no-build` is among the arguments; a
  testing-platform project is built first, since its report option is read from its build output. Not audited, with
  the reason in the report: a legacy project (full MSBuild), one that does not build, one that writes no report,
  crashes or times out, one in whose report no test ran. Its time is the baseline's wall time, build included.
- **Rewriting**: `MutationInjector.Inject` on the repository's assemblies in the audited outputs of the worker's
  copy (`RepositoryAssemblies.Find`, which leaves copies that came from a NuGet package alone), backups under the
  work folder, restored by `Mutation.Dispose` in a `using`. A member whose mutant changes no compiled code (a
  `flip-branch` on a member without a two-way branch, a `return-default` on a by-reference return), or whose
  assembly only unaudited projects hold, is **not reached**: listed, not counted. A member Cecil cannot rewrite is
  not reached too, with the error; a file error while rewriting or restoring stops the audit (`stopReason: error`).
- **Time limits**: a change's run may take `max(2 min, 5 × baseline × workers)`, cut to what is left of the budget
  (`ShadowBudget.For`); a run killed at a cut that was the budget's makes the change **stopped**. No idle limit: a
  hung test host is killed at its time limit.
- **Judging** as designed. A left-out class that fails alone with the change and whose run without it does not
  complete stays a miss ("the run without it: …"). A class filter that cannot be joined with the user's own filter
  (`TestFilter.WithinUserFilter`), or a crash outside the selection under a user filter, makes the change
  inconclusive. `notCounted` lists the classes that failed only next to other tests or fail alone anyway.
- **Parallel**: worker k > 1 gets a copy of the first copy made after the baseline (same binaries) and, with more
  than one worker, its own `TMPDIR` under the work folder. One analysis at a time (`AuditSelector` holds a lock).
- **Not done in v1**: generator and analyzer projects (left out of the sample, counted as `generatorMembers`),
  legacy projects, data-file mutants (`empty-file`), the evaluator's schema (every mutant is written in alone; the
  copy makes that cheap and the result the same). The owner may want a `td audit` path for generator code later.
- **Measured**: the fixture repository of the tests (4 members, 1 test project) audits in about 8 s on this machine,
  copy and baseline included; one change costs a full run of the projects that load it plus an analysis.

### `testdetta-audit.json` (schema 1)

```jsonc
{
  "schema": 1,
  "verdict": "On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); target of 100 reached.",
  "stopReason": "target",             // target | max-mutants | budget | sample | cancelled | error
  "error": "…",                       // only with an error
  "license": "No license is needed and none was read: … TestDetta made no network call.",
  "totals": {
    "changes": 112,                   // changes that ran tests to a judgement (caught + missed + didNotBreak + inconclusive)
    "broke": 103,                     // n: caught + missed
    "caught": 103, "missed": 0, "didNotBreak": 7, "inconclusive": 2, "notReached": 31, "stopped": 0,
    "brokeUncovered": 4,              // broke although no map said a test ran the member
    "upperBound95": 0.028666,         // MissRate.UpperBound(missed, broke); null with broke = 0
    "targetBroken": 100, "targetReached": true
  },
  "run": {
    "wallSeconds": 9120.4, "baselineSeconds": 312.0, "parallelism": 1,
    "budgetSeconds": 14400,           // null: no limit
    "maxMutants": null,
    "workFolder": "/tmp/td-audit-4242-1a2b3c4d", "workFolderRemoved": true,
    "dotnetTestArguments": ["-c", "Release"]
  },
  "sample": {
    "seed": 1, "kinds": "all",
    "order": "uniform over every candidate by a stable hash of the member and the seed; coverage not used",
    "candidates": 4210, "coveredCandidates": 2950, "generatorMembers": 12,
    "members": []                     // the --member filter
  },
  "projects": [{
    "path": "tests/Shop.Tests/Shop.Tests.csproj", "audited": true,
    "reason": "…",                    // only when not audited
    "classes": 212, "baselineSeconds": 41.2, "baselineFailed": [], "mapCommit": "9f3c…"
  }],
  "mutants": [{
    "index": 7, "member": "Shop.Discount.Apply", "kind": "return-default", "file": "src/Shop/Calc.cs",
    "covered": true, "outcome": "missed",   // caught | missed | did-not-break | not-reached | inconclusive | stopped
    "note": "…",                      // why not reached, inconclusive or stopped
    "mutatedMethods": 1, "seconds": 38.5,
    "selectedShare": 0.12, "caughtByCrash": false,   // these four only when a project was reached
    "reached": ["tests/Shop.Tests/Shop.Tests.csproj"],
    "seen": ["Shop.Discount.Apply(decimal) modified"],
    "broken": [{
      "project": "tests/Shop.Tests/Shop.Tests.csproj", "class": "Shop.Tests.DiscountTests", "selected": false,
      "chain": { "projectMode": "Filtered", "projectReason": "…", "decision": "Skipped", "evidence": "…", "mapCommit": "9f3c…" },
      "note": "fails alone with the change and passes alone without it"
    }],
    "notCounted": [],                 // failed only next to other tests, or fails alone anyway
    "edits": [{ "file": "src/Shop/Calc.cs", "line": 3, "column": 58, "replaces": 0, "insert": "(" }],   // misses only
    "reproduce": "td audit --member Shop.Discount.Apply --kinds return-default --max-map-age 50 --drift-fallback all"
  }],
  "reproduction": {
    "testDettaVersion": "…", "testDettaSha256": "…", "head": "…",
    "maps": [{ "testProject": "…", "commit": "…", "sha256": "…" }],
    "options": ["--seed 1", "--kinds all", "--max-map-age 50", "--drift-fallback all", "-- -c Release"],
    "command": "td audit --seed 1 --kinds all --max-map-age 50 --drift-fallback all -- -c Release"
  }
}
```

### For the owner

- **No license, no network** (as decided for shadow mode): the audit reveals decisions only for classes that ran
  and failed with a change, never a selection for a pull request. Someone could still learn from it which test
  classes cover which member; that is what a coverage map already says, on their own machine.
- **Cost**: a change runs every test project that loads the member's assembly, completely; on a large suite with
  one big test project that is the whole suite per change. 100 breaking changes with about 30% of changes not
  breaking anything means 130 or more full runs; the budget (4 h) and the target bound it. Teams with long suites
  will need `--parallel` (memory permitting) or a longer budget; the report says when the target was not reached.
  Open: combining several runs into one bound (a roll-up like `td shadow report`), which needs disjoint samples.
