# `td audit`: the corpus method on the team's own code

Status: designed and built 2026-10-07 (owner decision the same day: part of v1 and of shadow mode's graduation
criteria, docs/design/shadow-mode.md); reviewed and fixed the same day (judging, the work folder, the commit;
schema 2). Owner decisions later the same day (schema 3): graduation asks for the worst-case bound at most 3.0%,
and the report states whether a run meets it and why not; a run past the budget keeps the prefix rule and says
by how much and why. "As built" at the end records where the build differs from the plan.

## Problem

Shadow mode shows TestDetta on a team's real pull requests, but real failing pull requests are rare: with n of
them and no miss the miss rate is bounded only to about 3/n, so a month gives a weak bound. The corpus evaluation
gives hundreds of failures per repository by breaking code on purpose (IL mutants) and checking that the
selection contains every test class that failed. `td audit` runs that method on the team's own repository, on
their CI runner or a developer machine, so the graduation criterion can be met in one run: **at least 100
changes that broke tests, 0 missed, and the worst-case 95% upper bound at most 3.0%** (owner, 2026-10-07). The
worst case counts every inconclusive and stopped change as one that broke tests and was missed ("Judging"): a
change the runs could not judge might be a miss, so a run that leaves many unjudged must not pass on the judged
ones alone. Bounds are shown rounded up to a tenth of a percent, so a shown bound is never below the real one:
n = 100 with no miss is 2.95%, shown as 3.0%, which is why the criterion says "at most 3.0%" (n = 102 is the first
to show 2.9%); the criterion compares the shown figure. With no unjudged change the worst case is the bound
itself; one inconclusive change needs at least 156 changes that broke tests next to it (1 miss in 157 is 2.99%,
shown as 3.0%). A run with
`--member` tests the members asked for, not the sample, and is never the evidence; `--target-broken` does not
change the criterion. The evidence is one run as it came out: the report gives the seed, and a run repeated with
other seeds until one passes is not the evidence.

> On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); 2 inconclusive, 0 stopped
> (worst case, counting them as misses: 95% upper bound 5.9%, n = 105); target of 100 reached.
> Graduation evidence: no: worst case 5.9% > 3.0% because 2 inconclusive and 0 stopped are counted as misses

## Command

```
td audit [options] [-- <dotnet test arguments>]
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--target-broken <n>` | 100 | Stop once n changes broke tests (conclusively, see "Judging"). |
| `--max-mutants <n>` | no limit | Stop after n changes that ran tests. |
| `--budget <time>` | 4h | Start no change after this long; a change still running finishes within its own run limits and counts (the audit can run past the budget by that change; the report says by how much and which changes). `90s`, `15m`, `1h30m`, a plain number of minutes, `0`/`none` for no limit (`Durations.TryParseLimit`, at most 168h). The copy, the baseline's builds and runs draw from it too. Leave room under a CI job's `timeout-minutes` (below, "Stop conditions"). |
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
  `cat-file`, `ls-files`, the first-parent history for the map age). TestDetta changes nothing in the
  repository's files or in git: no source edit, no commit, stash or worktree. One exception it cannot rule out:
  when the repository is a linked worktree, the copy's `.git` file points into the team's git directory, so a
  test that runs git in the copy reads it, and one that writes to git (a commit, a tag) would write there.
  Every process the audit starts has `GIT_OPTIONAL_LOCKS=0`, so git's own index refreshes do not.
- **Writes** a work folder `td-audit-<pid>-<random>` under `--work-dir` (a copy of the working tree, backups of
  rewritten assemblies, test reports and logs), readable by its owner only (`0700`: the copy holds whatever the
  tree holds, ignored secret files included), deleted at the end; the JSON report; the summary when asked. In
  the repository only TestDetta's own `.testdetta/`: the project-graph cache every `td` command keeps
  (`.testdetta/cache`), the report by default, and with `--maps-dir` the restored maps, as `td test` does.
- **Executes** `cp` (the copy), `dotnet test`, `dotnet build` (testing-platform projects) and `dotnet msbuild`
  (a driver project that only evaluates where the projects write) in the copy, `git` read-only.
- **No license, and no network call of TestDetta's own.** Owner decision 2026-10-07: shadow mode needs no license
  because it skips nothing; the audit skips nothing either (every test that could see a change runs, with and
  without it), so it is dispatched before the license refresh like `td test --shadow`: no license variable or file
  is read, nothing is refreshed or sent. The `dotnet build` and `dotnet test` runs it starts restore packages as
  the team's own do (from the local cache, or from the team's feeds when a package is missing there). The report
  says both. Like shadow mode, it shows decisions only for classes that ran and
  failed, never a selection for tests that did not run.

## Mutating binaries without touching the team's build output

The team's build output is never written. Every mutant lives in a **copy of the working tree** (sources, build
output and all, `.git` included so tests that look for the repository root still find it):

1. **Start**: earlier work folders under `--work-dir` whose owner file names a process of this machine (same host
   name) that no longer runs (a crash, a `kill -9`, a power cut) and that no process holds locked are deleted;
   only folders with that owner file are touched. A running audit keeps its owner file open with an exclusive
   lock (`FileShare.None`, `flock` on Unix), so a sweep from another PID namespace (a container sharing `/tmp`),
   to which its process id looks dead, cannot delete it; a folder another host made is never swept.
2. **Copy**: `cp -cpR` on macOS (APFS clones), `cp -a --reflink=auto` on Linux (Btrfs, XFS), file by file on
   Windows: the evaluator's `WorkerCopies.Copy`, moved into the shared library. Before every copy (each worker's
   too) the disk must have room for it plus 1 GB: the tree's size without copy-on-write, the size of its `bin` and
   `obj` folders on a clone (the baseline's build rewrites them).
3. **Where the copy writes**, asked before anything builds or runs there (`AuditOutputPaths`): one `dotnet msbuild`
   of a driver project evaluates every SDK-style project of the copy, every target framework, with the user's
   `-c`, `-f` (test projects), `-r` and `-p` properties, and reports `TargetPath`, `OutputPath`, `OutDir`,
   `IntermediateOutputPath`, `BaseOutputPath`, `BaseIntermediateOutputPath`, `MSBuildProjectExtensionsPath`, the
   artifacts path and a documentation file. Any of them outside the copy once links are resolved (an absolute
   `OutputPath` or artifacts path, a `bin` folder that is a link to the team's own), a project MSBuild gives no
   answer for, or an `-o`, `--output`, `--artifacts-path`, `--results-directory` or `--diag` that leads out of the
   copy refuses the audit: the copy's build would write the team's files.
4. **Links**: in those folders every file link is replaced by a real file before the baseline (a build writes
   files in place, and `cp` keeps the team's links, which `CreateSymbolicLinksForCopyLocalIfPossible` points into
   the team's own build output); a folder link out of the copy refuses the audit.
5. **Baseline** in the copy: every test project runs once as `dotnet test <project> <args>` with a test report:
   this builds in the copy (unless `--no-build` is among the arguments, then the copied build output is used), so
   the binaries match `HEAD`. Each build and run may take what is left of the budget.
6. **Per change**: every assembly the changes rewrite and its `.pdb` is first made a file of its own in each copy
   (a symbolic or hard link replaced by a copy of its content), and must lie inside the copy once links are
   resolved. The evaluator's `MutationInjector.Inject` rewrites the copies of the member's assembly in the copy's
   test outputs, its backups in the work folder; after the change's runs the backups are copied back. A restore
   that fails stops the audit (the copy can no longer be trusted), after the change's result is recorded; nothing
   outside the copy is affected either way.
7. **Ctrl+C, SIGTERM, timeouts**: a cancellation token stops the loop and kills the running `dotnet test`
   process tree; a run past its time limit (five times the project's baseline run, at least two minutes; the
   budget does not cut it) is killed the same way. The partial report is written (`stopReason: cancelled`)
   before the work folder is deleted, so a CI job killed while a large copy is deleted still has it.
8. **End**: the work folder is deleted. A folder that cannot be deleted is named in the report; the next run
   removes it (step 1).

So a crash at any point leaves at most a folder in the temp directory, never a mutated assembly in the team's
build. The tests check that the team's build output is byte-identical after a normal run, a cancellation, a
timeout and an exception in the middle of a change, and while a change is in when the team's test output holds
its assemblies as links into its own build; and that an absolute `BaseOutputPath` is refused before anything
builds in the copy.

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
not ignore (`.testdetta/` aside), and says which. The commit is recorded when the audit starts: every analysis
compares with that commit (not with whatever `HEAD` is by then), reads the edited file's text from it (`git
cat-file --filters`, so line endings match the offsets the sampler took from the clean tree), and before and
after each analysis the repository must still be at it with a clean working tree; otherwise the audit stops
with an error (a developer committing or editing during an hours-long run would otherwise widen every later
selection).

## Which tests run for a change

Every audited test project whose build output in the copy holds a rewritten assembly runs **completely**, the
way the baseline ran it: `dotnet test <project> --no-build <args> <report>`. A project whose output does not
hold the member's assembly cannot load the mutant. Audited are the test projects that wrote a readable report
of a complete run in the baseline, do not need the full MSBuild (legacy projects), and whose own filter (after
`--`) can be joined with a class filter; the rest are named in the report with the reason and their tests are not
part of the audit. An xUnit v3 class or query filter and a TUnit tree-node filter cannot be joined: in such a
project the classes the selection left out could not be run apart, so it could show catches but never a miss.

## Judging

The rule behind every case: **every doubt goes against TestDetta**. A failure the selection left out must be
shown not to be the change's before it is dropped, and a failure the selection ran must be shown to be the
change's before it counts as caught, by the same runs from the other side.

- A class that fails with the change and did not fail in the baseline **failed**.
- **Left out** and failed: run alone with the change. A class that passes then failed only next to other tests and
  is not counted; one whose run alone ran none of its tests (a class filter that matched nothing) makes the change
  **inconclusive**. A class that fails alone runs alone without the change (the original assemblies back for that
  run): failing there too, it fails alone anyway and is not counted; passing there, or when that run cannot tell
  (it does not complete, or runs none of its tests), it is a **miss**.
- **Selected** and failed: the same two runs. It counts as **caught** only when it fails alone with the change and
  passes alone without it. Passing alone with the change, or failing alone without it: not counted. When the run
  without the change cannot tell, the catch is unconfirmed (the change is inconclusive unless something else
  decides it). These runs are made only while the change has no miss (with a miss the change is missed anyway).
- A **run that does not complete** (a crashed or hung test host, a run that ends without a failed test but with a
  failing exit code, a report that says the run was aborted, or a run of the whole project that reports fewer
  classes than the baseline did) runs again without the change, the same invocation:
  - the selection ran **nothing** in that project (skipped, not affected): the run of the whole project is the part
    left out. Completing without the change, the run is repeated with the change: not completing again makes the
    change a **miss** (an entry for the part, since no class can be named); a repeat that completes is judged by its
    failures, and the change is **inconclusive** at least (a crash that did not come back proves no miss and rules
    none out; 2026-10-08, after a test host that died once under load made a mutant of a member nothing calls a
    miss). Not completing without the change, **inconclusive**.
  - it ran the **whole project**: nothing is left out, so no miss is possible here. The run is repeated with the
    change; not completing again, and completing without the change, is **caught** (`caughtByCrash`); a repeat
    that completes is judged by its failures (none: not counted).
  - it ran **some classes**: the classes it left out run apart with the change (within the user's filter): not
    completing there, completing without the change and not completing with it a second time is a **miss**; a
    repeat that completes leaves the change **inconclusive** and its failures are judged as above. When
    no miss showed there, the classes it ran run apart the same way: not completing with the change and
    completing without it is **caught**, failures are judged as above. When neither part fails apart, the
    change is **inconclusive** (a crash only when both run together).
- A change with a miss is **missed**; else with a doubt that could hide a miss **inconclusive**; else with a
  confirmed catch **caught**; else with an unconfirmed catch **inconclusive**; else **did not break**. A member no
  audited output holds is **not reached** (listed, not counted).
- **Cut short**: a change a cancellation or an error stops while it runs is **stopped**; one that already had a
  confirmed miss stays **missed** (its result is kept before the mutant is restored, also when the restore fails).
  A miss whose runs ended after a cancellation does not count (Ctrl+C reaches the test hosts too).

n is the number of changes that broke tests (caught + missed). The bound is the evaluator's exact one-sided
Clopper-Pearson bound (`MissRate.UpperBound`), stated with n: with 0 misses it is about 3/n. Next to it, the
**worst-case bound** counts every inconclusive and stopped change as one that broke tests and was missed (n plus
those changes): what the result would be if every unjudged change were a miss. Both are shown rounded up.

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
- **Stopping, and the prefix rule**: the sample is walked in order; the stop conditions look at counts and time,
  never at which change missed, and decide only whether a further change starts. A change that started always
  runs to its end within its own run limits, whatever stops the audit meanwhile (the target, `--max-mutants`,
  the budget, another worker's error): so the changes taken are exactly positions 1 to k of the order, k being the
  last one started, each judged, with none in between left out (with `--parallel`, the changes running when a
  condition holds finish and count). Without that, the budget would end most often in the middle of a miss (a
  miss takes two more runs than a catch) and drop it. Only a cancellation (Ctrl+C, SIGTERM, a cancelled CI job)
  or an error cuts running changes short: they are listed as stopped, outside n, and the worst-case bound counts
  them as misses.
- What is left out is left out by construction (no edit, generator, not reached, project not audited), the same
  whatever the tests do, and every such count is in the report.

## Stop conditions

In order of checking before each change: cancelled; `--target-broken` reached; `--max-mutants` reached; budget
spent (it starts with the command: copy and baseline included); sample exhausted; less than 1 GB free on the work
folder's disk (an error). The verdict names the reason, and says when the target was not reached.

**Past the budget** (owner, 2026-10-07: keep the rule, report it): the budget only decides whether a further change
starts. The changes running when it runs out finish within their own run limits (a run may take
`max(2 min, 5 × baseline × workers)`, and a change takes several runs: the full runs, then the confirmations), so
the audit can end after its budget; a hard stop would drop the change in the middle, most often a miss (it takes
more runs), and break the prefix rule. When it ends at least a second after the budget, the console, the summary
and the JSON (`run.overBudgetSeconds`, `run.finishedAfterBudget`, `run.overBudget`) say by how much and why: the
changes, by index, that were running when it ran out, or, with none, the steps the budget does not cut (copying the
working tree and preparing the copies, the analyses, writing the report). Leave that room under a CI job's
`timeout-minutes`: at least one change's runs of the projects that load it, a few times the slowest baseline run;
a job killed at its timeout cancels the audit, and the change then running is reported as stopped.

## Report

Console (the verdict, then one line per change as it finishes), Markdown summary, and `testdetta-audit.json`
(schema below). The verdict line, the summary's table and the JSON totals give the inconclusive and stopped
counts and the worst-case bound next to the bound. One line, in all three, says whether the run is the graduation
evidence and, if not, every condition it misses (`AuditGraduation`): "yes: 103 changes broke tests (100 needed), 0
missed, worst case 2.9% (at most 3.0% needed)", "not yet: 87 changes broke tests, 100 needed" (only n is short),
"no: 1 missed", "no: worst case 4.1% > 3.0% because 5 inconclusive and 0 stopped are counted as misses", "no: only
the members asked for with --member were audited, not the sample"; several are joined with "; ". A "yes" adds that
it covers the audited test projects only when some are not audited. A run past its budget gets one more line
("Past the budget"). Per miss: the member and its file, the kind, the failing classes, the decision chain (what
TestDetta saw change, how the test project was decided and why, the class's decision and its evidence, the map's
commit), the edit to make by hand, and the command that reproduces it (`td audit --member … --kinds …`).

## Relation to the evaluator

The mutant code is shared, not copied: `TestDetta.Mutants` (new library under `src/`, used by the CLI and the
evaluator) holds the mutant kinds, the injector (`MutationInjector`, `Mutation`, schemas), the target sampler and
its edits, the repository-assembly finder, the tree copy, the covered-member and miss-rate math. The evaluator
keeps its behaviour and its tests (they only gain a `using`); its default sample (80% covered) is unchanged, the
audit asks the sampler for its uniform order. Since 2026-10-07 the evaluator judges by the same rules ("Judging"):
the owner chose the strict rules for both, with a marked break in the round history (docs/evaluation.md, "How mutants
are judged"). What decides whether a run completed is shared code (`TestDetta.Coverage.TestRunCompleteness`: the
English runs, the crash lines, the report's outcome, the exit code); the judging itself is written twice, for the
audit's one project at a time with its report entries (`AuditTrial`) and for the evaluator's several projects,
selectors compared on the same failures, and runs stored for later rounds (`MutantJudge`), and the same cases are tested
for both (`AuditJudgingTests`, `EvalJudgingTests`).

## As built (2026-10-07)

- **Code**: `CliApp.Audit` (dispatched before the license refresh, with SIGINT and SIGTERM registered through
  `PosixSignalRegistration` so the process lives on to clean up), `AuditRun` (checks, sample, baseline, report),
  `AuditWorkspace` (work folder, locked owner file, stale sweep, copies, free-space checks, `Detach` and
  `MaterializeLinks`), `AuditOutputPaths` (where every project of the copy writes, one MSBuild driver),
  `AuditTestRun` (one `dotnet test` of a project in a copy, with a report, read and deleted at once; complete or
  not, `Problem`, `ReportProblem`), `AuditProcess` (dotnet with a limit and the cancellation token, killing the
  whole tree; no MSBuild node, build server or compiler server outlives a command; English output,
  `GIT_OPTIONAL_LOCKS=0`), `AuditSelector` (the overlay pipeline at the recorded commit, its checks), `AuditTrial`
  (one change: inject, select, `JudgeRuns`, restore; `AuditHooks.Runs` replaces the runs in tests), `AuditLoop`
  (the walk and the stop conditions), `AuditReport` and `AuditResult`. The report-argument helpers of shadow mode moved to `TestRunArguments` for both. The shared
  library is `src/TestDetta.Mutants` (`MutantKind`, `MutationInjector`, `TargetSampler` with
  `SampleRequest.Uniform`, `RepositoryAssemblies`, `TreeCopy`, `Selections`, `MissRate`); the evaluator forwards to
  it and its tests are unchanged but for a `using`. The recording fingerprint did not change (TestDetta.Coverage
  and TestDetta.Core only gained an `InternalsVisibleTo`; `AnalysisOptions`, `AnalysisPipeline` and
  `CodeChangeReader`, which gained the overlay, are not reached by the recorder).
- **Work folder**: refused inside the repository (the copy would contain itself), checked before anything is
  created; created `0700`; the owner file (`pid`, `host`, `repository`, `started`) stays open with
  `FileShare.None` until the folder is deleted.
- **Dirty tree and the commit**: `HEAD` recorded first; `GitClient.ChangedFiles(root, <commit>)` without
  `.testdetta/`, the same files `td test --base HEAD` would add; any refuses the audit (exit 1, with a report naming
  up to five). Before and after each analysis the same check and `HEAD` again (`AuditSelector.Check`); a difference
  stops the audit with `AuditTreeChangedException` (`stopReason: error`, the change stopped). The pipeline runs
  with the recorded commit as its base.
- **Baseline**: per test project in the copy, `dotnet test <project> <args>` with a report (VSTest TRX, the testing
  platform's own report option, NUnit's XML), building there unless `--no-build` is among the arguments; a
  testing-platform project is built first, since its report option is read from its build output. Builds and runs
  may take what is left of the budget; once it is spent the audit stops (`budget`) before any change. Not audited,
  with the reason in the report: a legacy project (full MSBuild), one whose own filter cannot be joined
  (`AuditTrial.Unjoinable`), one that does not build, one that writes no report, does not complete or times out,
  one in whose report no test ran. Its time is the baseline's wall time, build included.
- **Complete runs** (`AuditTestRun.Problem`): the run finished within its limit and wrote a report; its log has no
  "Test host process crashed", "Test Run Aborted" or "The active test run was aborted" (the SDK and VSTest are
  set to English, `DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033`, last, over the team's variables); its TRX outcome
  is not `Aborted`, `Error` or `Timeout` and no run message says the run was aborted (other run messages of
  outcome `Error` are adapters' lines, such as xUnit's "[FAIL]" for every failed test); an NUnit result is not
  `Cancelled`; a failing exit code comes with a failed class (exit 8 of the testing platform, no test ran, is
  not complete); and a run of the whole project reports at least as many classes as the baseline
  (`AuditTrial.Whole`), which catches a crash whose message is in another language.
- **Rewriting**: `MutationInjector.Inject` on the repository's assemblies in the audited outputs of the worker's
  copy (`RepositoryAssemblies.Find`, which leaves copies that came from a NuGet package alone), each detached
  first (`AuditWorkspace.Detach`, in every copy), backups under the work folder, restored by `Mutation.Dispose`
  after the change's result is recorded (a failed restore throws `AuditChangeFailedException` with the result). A member whose mutant changes no compiled code (a
  `flip-branch` on a member without a two-way branch, a `return-default` on a by-reference return), or whose
  assembly only unaudited projects hold, is **not reached**: listed, not counted. A member Cecil cannot rewrite is
  not reached too, with the error; a file error while rewriting or restoring stops the audit (`stopReason: error`).
- **Time limits**: a change's run may take `max(2 min, 5 × baseline × workers)`; the budget does not cut it, so a
  change that started finishes (an audit can run past `--budget` by up to one change's runs; leave that room
  under a CI job's timeout). No idle limit: a hung test host is killed at its time limit. `AuditRun.Progress` notes
  every change that ends after the budget ran out (`AuditResult.FinishedAfterBudget`); `AuditReport.OverBudget`
  writes the line from it and the wall time.
- **Graduation evidence**: `AuditGraduation.Judge` (constants `BrokeNeeded` 100 and `WorstCaseTenthsAtMost` 30,
  compared with the figure as shown, `AuditReport.Tenths`).
- **Judging** as designed (`AuditTrial`: `JudgeProject`, `Incomplete`, `Part`, `ConfirmLeftOut`, `ConfirmSelected`,
  `Verdict`). Run names in logs and hooks: `m<i>-full`, `-full-without`, `-again`, `-again-without`, `-left-out`,
  `-left-out-without`, `-selected`, `-selected-without`, `-alone`, `-alone-without`, `-control`,
  `-selected-alone`, `-selected-alone-without`, `-selected-control`. The classes a filtered selection left out run
  with the user's VSTest filter joined as `(user)&(excluding)` (`AuditTrial.ExcludingWithin`); the other runners take
  `--filter-not-class` or a tree-node filter next to the user's options (a conflicting user filter is refused up
  front). `notCounted` lists the classes that failed only next to other tests or fail alone anyway, and a crash of a
  whole-project selection that did not come back.
- **Cost of the confirmations**: a caught change takes two more runs (the failing classes alone with and without
  the change), a crash or hang one to three more (one with the change, which may run to its limit again). On the
  test fixture a caught change took 2.2 s instead of about 1 s; on a real suite the two runs cost a test host
  start and the failing classes' tests each, small next to the full run of the project.
- **Parallel**: worker k > 1 gets a copy of the first copy made after the baseline (same binaries, space checked
  first) and, with more than one worker, its own `TMPDIR` under the work folder. One analysis at a time
  (`AuditSelector` holds a lock).
- **Report first**: the JSON report and the summary are written before the work folder is deleted, the JSON again
  after it with `workFolderRemoved`.
- **Not done in v1**: generator and analyzer projects (left out of the sample, counted as `generatorMembers`),
  legacy projects, data-file mutants (`empty-file`), the evaluator's schema (every mutant is written in alone; the
  copy makes that cheap and the result the same). The owner may want a `td audit` path for generator code later.
- **Measured**: the fixture repository of the tests (3 members, 1 test project) audits in about 9 s on this machine,
  copy and baseline included; a change that did not break anything costs a full run of the projects that load it
  plus an analysis (1 s there), a caught or missed one two filtered runs more (2.2 s).

### `testdetta-audit.json` (schema 3)

Schema 2 (2026-10-07): `totals.unjudged`, `totals.worstCaseN`, `totals.worstCaseUpperBound95`; a `broken` or
`notCounted` entry can be a part of a project (`class: null`, `part`, `crash: true`); `caughtByCrash` means a
catch that rests on a repeated crash of the selected part; the verdict names the inconclusive and stopped counts
and the worst case; `edits` positions come from the audited commit's text. Schema 3 (2026-10-07): `graduation`
(whether the run meets the graduation criterion, and why not); `run.overBudgetSeconds`, `run.finishedAfterBudget`
and `run.overBudget`. Nothing else changed meaning.

```jsonc
{
  "schema": 3,
  "verdict": "On your code: 112 changes, 103 broke tests, 0 missed (95% upper bound 2.9%, n = 103); 2 inconclusive, 0 stopped (worst case, counting them as misses: 95% upper bound 5.9%, n = 105); target of 100 reached.",
  "stopReason": "target",             // target | max-mutants | budget | sample | cancelled | error
  "error": "…",                       // only with an error
  "license": "No license is needed and none was read: … TestDetta itself made no network call; the dotnet builds and test runs it started may restore packages, as your own do.",
  "totals": {
    "changes": 112,                   // changes that ran tests to a judgement (caught + missed + didNotBreak + inconclusive)
    "broke": 103,                     // n: caught + missed
    "caught": 103, "missed": 0, "didNotBreak": 7, "inconclusive": 2, "notReached": 31, "stopped": 0,
    "brokeUncovered": 4,              // broke although no map said a test ran the member
    "upperBound95": 0.028666,         // MissRate.UpperBound(missed, broke); null with broke = 0
    "unjudged": 2,                    // inconclusive + stopped
    "worstCaseN": 105,                // broke + unjudged
    "worstCaseUpperBound95": 0.058745, // MissRate.UpperBound(missed + unjudged, broke + unjudged); null with worstCaseN = 0
    "targetBroken": 100, "targetReached": true
  },
  "graduation": {
    "met": false,
    "status": "no",                   // met | not-yet (only too few changes broke tests) | no
    "line": "no: worst case 5.9% > 3.0% because 2 inconclusive and 0 stopped are counted as misses",
    "reasons": ["worst case 5.9% > 3.0% because 2 inconclusive and 0 stopped are counted as misses"],   // empty when met
    "criterion": "at least 100 changes that broke tests, 0 missed, and the worst-case 95% upper bound (inconclusive and stopped changes counted as misses) at most 3.0% as shown, rounded up"
  },
  "run": {
    "wallSeconds": 9120.4, "baselineSeconds": 312.0, "parallelism": 1,
    "budgetSeconds": 14400,           // null: no limit
    "overBudgetSeconds": 0,           // how far the audit ended after the budget; null without a budget
    "finishedAfterBudget": [],        // indexes of the changes running when the budget ran out, which finished after it
    // "overBudget": "The audit ended 12 m 4 s after its 4 h budget: change #112 was running when …",   // only when 1 s or more over
    "maxMutants": null,
    "workFolder": "/tmp/td-audit-4242-1a2b3c4d", "workFolderRemoved": true,   // false in the report written before the deletion
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
    "note": "…",                      // why not reached, inconclusive or stopped; for a miss kept after a cancellation, that
    "mutatedMethods": 1, "seconds": 38.5,
    "selectedShare": 0.12, "caughtByCrash": false,   // these four only when a project was reached
    "reached": ["tests/Shop.Tests/Shop.Tests.csproj"],
    "seen": ["Shop.Discount.Apply(decimal) modified"],
    "broken": [{
      "project": "tests/Shop.Tests/Shop.Tests.csproj", "class": "Shop.Tests.DiscountTests", "selected": false,
      "chain": { "projectMode": "Filtered", "projectReason": "…", "decision": "Skipped", "evidence": "…", "mapCommit": "9f3c…" },
      "note": "fails alone with the change and passes alone without it"
    }, {
      "project": "tests/Wait.Tests/Wait.Tests.csproj", "class": null, "part": "left-out", "crash": true, "selected": false,
      "chain": { "projectMode": "Skipped", "projectReason": "…", "decision": "Skipped", "evidence": "…", "mapCommit": "9f3c…" },
      "note": "the run did not complete with the change (timed out after 2 min) and completes without it; the selection ran none of this project"
    }],
    "notCounted": [],                 // failed only next to other tests, fails alone anyway, a crash that did not come back
    "edits": [{ "file": "src/Shop/Calc.cs", "line": 3, "column": 58, "replaces": 0, "insert": "(" }],   // misses only
    "reproduce": "td audit --member Shop.Discount.Apply --kinds return-default --max-map-age 50 --drift-fallback all"
  }],
  "reproduction": {
    "testDettaVersion": "…", "testDettaSha256": "…", "head": "…",   // head: the commit recorded at the start
    "maps": [{ "testProject": "…", "commit": "…", "sha256": "…" }],
    "options": ["--seed 1", "--kinds all", "--max-map-age 50", "--drift-fallback all", "-- -c Release"],
    "command": "td audit --seed 1 --kinds all --max-map-age 50 --drift-fallback all -- -c Release"
  }
}
```

### For the owner

- **No license, no network call of its own** (as decided for shadow mode): the audit reveals decisions only for classes that ran
  and failed with a change, never a selection for a pull request. Someone could still learn from it which test
  classes cover which member; that is what a coverage map already says, on their own machine.
- **Cost**: a change runs every test project that loads the member's assembly, completely; on a large suite with
  one big test project that is the whole suite per change. 100 breaking changes with about 30% of changes not
  breaking anything means 130 or more full runs; the budget (4 h) and the target bound it. Teams with long suites
  will need `--parallel` (memory permitting) or a longer budget; the report says when the target was not reached.
  Open: combining several runs into one bound (a roll-up like `td shadow report`), which needs disjoint samples.
- **Criterion and the worst case** (decided 2026-10-07): graduation asks for n at least 100, 0 missed, and the
  worst-case bound at most 3.0% as shown; the report says per run whether it meets it and why not.
- **The evaluator's rules** (open, docs/backlog.md "Next"): the corpus evaluator keeps its earlier, asymmetric
  rules for crashes and catches (unconfirmed catches count, a crash left out can leave n); `td audit` does not
  inherit them. Changing the evaluator changes comparability with earlier rounds.
- **Budget overrun** (decided 2026-10-07: keep the rule, report it): a change that started finishes within its
  own limits, so an audit can run past `--budget` by one change's runs (with a hang, a few times five baseline
  runs). A hard stop would bring back the bias the prefix rule removes; a cancelled job counts the change as
  stopped instead. The report says by how much and why ("Past the budget").
