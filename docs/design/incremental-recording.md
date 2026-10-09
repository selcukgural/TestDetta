# Incremental recording: re-record only what a merge can change

Status: design 2026-10-09 (owner asked for it after the first AfterApply record run on main); CLI and
evaluator built the same day (see "As built"). Open: the corpus measurement, the thresholds and the
action's default.

## Problem

The action's `record` mode runs every test of every test project, instrumented, on each push to main.
On AfterApply's first v0.1.3 record run (37967498626) that took 5 min 38 s, against 5 min 6 s for the
plain backend test job: every merge costs one full instrumented suite. TestDetta's purpose is to cut
the CI minutes customers pay for, and this cost grows with the merge rate, exactly where the savings
on pull requests should be largest.

Recording less often does not fix it. Measurement 1 in docs/design/map-drift-bridge.md: with nightly
recording, half of the CI runs find no exact map, and right after merges the newest map is up to 38
commits behind (p90, worst repository). The bridge keeps such runs safe by widening the selection,
so a team that merges many times a day would trade record minutes for broader PR runs. Neither is
what we promise.

## Idea

After a merge, run only the test classes the merge can affect, instrumented, and carry every other
class's entries over from the previous map. The selection that decides this is the one `td test`
already makes, with the previous map as an exact map at its own commit.

Let `A` be the commit of the newest map on main (an ancestor of the new head `H`), and treat `A..H`
as if it were a pull request based on `A`:

1. Select with the map at `A` and the changes `A..H`, exactly as `td test --base A` would at `H`.
2. Record the selected classes at `H`, with the recorder's existing class filters.
3. Merge: selected classes get their new entries, the rest keep theirs from `A`; process-wide data
   is rebuilt as described under "Merging a map". The result is the map at `H`.

The cost per merge drops from the whole suite to the classes the merge touches, plus the build,
which recording pays anyway. On AfterApply PR #205 that would have been 4 of 97 integration classes.

### Why an unchanged class keeps a valid entry

Take a test class `T` that the selection leaves out. Selection leaves `T` out only when nothing `T`
ran at `A` (its methods, its fixtures' methods, the run-once and built-value credits, the files it
read, the ambient code every class depends on) changed in `A..H`, and when the name trace from the
changes does not reach it. A deterministic test that runs no changed code and reads no changed input
follows the same path at `H` as at `A`, so it runs the same methods and its entry is still right.

This is the argument that makes selection safe, with one difference that matters: a selection miss
costs one pull request, while a stale entry stays in every later map until something re-records the
class. A miss in selection would thus turn into a miss that persists. The design therefore adds three
safeguards that selection alone does not need: members no fresh run has seen count as unknown, a
full recording runs regularly, and that full recording checks the incremental map it replaces.

### Members no fresh run has seen are unknown

A map lists every instrumented method; a listed method that no class ran is known to be untested.
That claim is only as fresh as the run that made it. A carried-over entry describes `A`, where a
member added in `A..H` did not exist, so it cannot say whether `T` reaches the new member at `H`.

The obvious case is already covered by selection: `T` runs unchanged code that finds plugins by
assembly scanning, and the merge adds a plugin class. A type that nothing names but that has a base
list or attributes may be created by a framework, so selection runs its test projects completely
(docs/design/reflection-types-with-coverage.md), and the step records them in full. What remains are
paths the step's selection did not follow for some other reason: a gap nobody knows yet. With a full
recording at `H`, `T`'s entry would show such a path. With a carried-over entry it does not, and the
map would answer "nobody runs this member" to every later pull request that changes it.

Rule: these members are stored as **unobserved**:

- every member added in `A..H`;
- every member of a type whose declaration was added or changed in `A..H` (attributes, base types,
  interfaces: what scanning and dispatch look at);
- every member whose metadata changed (attributes, parameter defaults), which reflection reads
  without running it.

Selection treats an unobserved member like code that ran outside every test: the map cannot name its
users, so it is traced by name, which is what happens today for code the map cannot answer for. A
member stays unobserved until the next full recording, even when a re-recorded class ran it: that
class is then known to use it, but a carried-over class may reach it as well, and only a run of that
class at `H` could say. A member whose body alone changed does not need this: who calls it did not
change, and every class that ran it at `A` was selected and re-recorded.

What this buys is the same bar the drift bridge sets: a later change to an unobserved member is
decided by name, never by an entry that could not have seen it. It does not make a path visible that
the name trace cannot follow either (reflection by a string); that is the no-map path's blind spot,
shared by the bridge, and the regular full recording with its divergence check is what bounds it.

### Merging a map

Per test project, from the previous map `M(A)` and the partial recording `P(H)` of the selected classes:

| Part | Merged value |
|---|---|
| `Methods` | the list at `H`, from `P(H)`'s instrumentation; carried-over indexes are re-mapped by (type, member) |
| `Hits.Classes`, `FileReads`, `OutputUses`, `BuiltValues`, `LeakedReads`, timings per class | from `P(H)` for selected classes, from `M(A)` for the rest |
| `Hits.Fixtures`, fixture reads and timings | from `P(H)` for every fixture that ran in it, from `M(A)` for the rest |
| `Ambient`, `Initialization`, `LinkedInitialization`, `ExternalCallbacks`, `NestedMethods`, ambient and initialization reads | union of both, after re-mapping; a larger set only selects more |
| `FailedClasses`, `SkippedClasses` | selected classes take their new status, the rest keep theirs |
| `Generated`, `BuildInputs`, `ClassLocations`, `InstrumentedProjects`, `Platform`, `Framework` | from `P(H)`: they describe the build at `H`, which the partial recording builds in full |
| Unobserved members | `M(A)`'s set, re-mapped, plus the members `A..H` makes unobserved (see above) |
| Startup seconds | from `P(H)` |

A carried-over class that no longer exists at `H` (its test class is gone from the test assembly's
PDB) is dropped. A carried-over entry that names a method missing at `H` cannot occur for a class that
was left out, since removing a method it ran is a change that selects it. If it does occur, the class
is kept as failed while recording (`FailedClasses`), which makes it run whenever its project is
affected, until the next recording of it.

The header records the lineage: the commit of the last full recording and the number of incremental
steps since then.

Run-once code is credited to the first class that needs it in a process. A partial run has a
different first class, so the credit moves to a selected class and the carried-over class keeps its
old credit. The union keeps both, which can only select more.

### When to record in full instead

- **The selection says "run all"** for a test project (a build input, a package, a non-code file, a
  global using, an uncomparable source file, a non-C# project): that project is recorded in full.
  Its map is then fresh, as today.
- **The selection covers most of a project** (above a share set by the measurement, starting at 60%):
  record it in full; the saving would be small and a full map resets the lineage.
- **The lineage is long**: after `full-record-every` incremental steps (start at 20) or when the last
  full recording is older than `full-record-days` (start at 7), record in full. This bounds how long a
  wrong entry can survive and clears the unobserved set. A team can also schedule a weekly full run.
- **No usable previous map**: none within `max-map-age` on main's first-parent line, or a map recorded
  by a different TestDetta map format, platform or framework. The map must also come from main itself;
  see "Trust".
- **No covering license**: without a license, selection runs every project (docs/design/licensing.md),
  and the recording follows it, so it is a full recording, as today.

### The full recording checks the map it replaces

When a full recording replaces a map that came from incremental steps, compare them per class. A
class whose fresh entry contains a method its carried-over entry lacked, or reads a file the
carried-over entry did not have, is a **divergence**: the incremental map was missing something the
selection should have caught. Report each one in the job summary with the class, the members and the
step that last carried the class over, and count them in the JSON output. A divergence is either
nondeterminism in the test (the same data shows up when two full recordings of one commit are
compared) or a selection gap, which then gets a complete fix and a red test, like every miss.

This gives every customer repository a continuous check of the selection itself, at no extra cost.

## Trust

The previous map decides which classes are re-recorded, so a map with entries taken out would make
later maps skip those classes. Today maps reach main's runs only through the Actions cache, and caches
created by `pull_request` runs are scoped to the pull request's ref and are not visible to runs on
main. Incremental recording on main therefore builds only on maps main itself recorded, and adds no
new way to get a map into main's lineage. It must stay that way: the action restores the previous map
only in a `push` run on the default branch, and never from an artifact or a cache a pull request could
write. Reusing coverage from pull request runs (a later idea, below) would change that and needs its
own trust design.

## Concurrency

Two merges in quick succession: the record run for the first may still be running when the second
lands. The record job uses a concurrency group that cancels the older run; the newer run builds on
the newest map that exists (`A` is then further back, and `A..H` covers both merges). Nothing is ever
merged into a map that is not an ancestor of `H`.

## Interface

- `td record --incremental`: read the newest map within `--max-map-age` from `--maps-dir`, select
  against it, record the selection, merge, and write the map at `HEAD`. Without a usable map it
  records in full and says why. `--full-record-every <n>`, `--full-record-days <d>` and
  `--full-record-share <percent>` set the thresholds above.
- Output: per test project, `full` or `incremental` with the reason, the classes recorded against
  the total, and the time; on a full recording after incremental steps, the divergences.
- Action, `mode: record`: restore the newest ancestor map with the step that already exists for
  `test` mode (needs `actions: read`), run `td record --incremental`, save under
  `testdetta-coverage-<sha>` as today. A new input `record: incremental | full`; the default is
  decided after the measurement. The README and docs/ci.md describe the choice: per-merge
  incremental recording (narrowest selection, small cost per merge) and full recording on a schedule.

## Measurement before implementation

The evaluator records at `A = N−k` and then incrementally at `N`, in one step `A→N` and in a chain
`A→A+1→…→N` (the chain shows how errors would pile up). Then, for `k ∈ {1, 3, 10, 30}` on the
high-commit-rate repositories of measurement 2 (opentelemetry-dotnet, fastendpoints,
meziantou-analyzer, polly, awesomeassertions, mapperly, NodaTime) and the app repositories as they join
the corpus:

1. **Map comparison**: the incremental map at `N` against a full recording at `N`, per class, as in
   the divergence check, with two full recordings at `N` compared the same way to separate
   nondeterminism from gaps.
2. **Selection**: every synthetic mutant at `N` selected with the incremental map and with the exact
   full map. Misses come from the outcome cache, as today.
3. **Cost**: time of the incremental recording against the full one, split into build, instrumented
   test run and merge.

Pass criteria:

- the incremental map causes no miss that the full map catches;
- every divergence is explained as nondeterminism or fixed;
- the time is close to the selected share of the full recording time plus the build.

These measurements set the starting values of `full-record-share`, `full-record-every` and
`full-record-days`.

## Later: reuse the pull request's coverage

A pull request's `test` run already executes the classes its change affects. If it ran them with
coverage, the record run after merging that pull request could merge those entries without running
any test, at least when the merged tree equals the tree that was tested. It saves the remaining cost,
but maps would then come from pull request runs, which forks and untrusted branches can influence
(see "Trust"), and the instrumented run is slower than a plain one. Not part of this design.

## Order of work

1. Evaluator: incremental recording with `--map-age` (single step and chain), map comparison,
   selection with the incremental map, cost split.
2. Run the measurement; decide thresholds and the action's default with the owner.
3. `td record --incremental`: map merge, unobserved members in the map and in selection, divergence
   check, thresholds, output. Unit tests on small repositories built in the tests: an unaffected class
   carried over; a changed method re-recorded; an added plugin found by scanning (unobserved, so traced
   by name); a project-level change recorded in full; a removed test class dropped; a fixture that ran
   in the partial run replaced, another kept; the lineage threshold; a divergence reported.
4. Action input and steps, README and docs/ci.md, atlas.
5. AfterApply: switch its record job to incremental and compare per-merge time and PR selection for a
   week.

## As built (2026-10-09)

- Map: `CoverageMap.Unobserved` (method ids) and `CoverageMap.Lineage` (`MapLineage(FullCommit,
  FullRecordedAt, Steps)`), both optional in the file (`unobserved`, `lineage`); the format version is
  unchanged, since a map without them is a full recording. `CoverageIndex` adds unobserved members to
  the ambient set, so `ClassesRunning` returns null and selection traces them by name; the reason reads
  "the incremental map has not seen its users since it changed".
- `CoverageMapMerge.Merge(previous, partial, MapMergeScope(RecordedClasses, AssemblyClasses,
  NewlyUnobserved))` as in "Merging a map"; `CoverageMapMerge.UnobservedBy(changes, methods)` for the
  rule above. `MapDivergence.Find(incremental, full)` for the check.
- Recorder: `RecordOptions.Classes` and `CoverageRecorder.RecordClasses`, returning
  `PartialRecording(Map, TestsPassed, AssemblyClasses, Complete)`. The selected classes are dealt into at
  most `--parallel` filtered shards, plus the catch-all shard for tests outside every class the source
  index knows. A crashed shard falls back to an unfiltered run, as before, and the result is then a
  full recording (`Complete`).
- CLI: `td record --incremental` with `--max-map-age`, `--full-record-every`, `--full-record-days`,
  `--full-record-share` (and `--maps-dir`, restored before planning). `IncrementalRecording.Plan` decides
  `Full`, `Partial`, `Carry` or `Current` per project with the reasons above; selection runs once per
  distinct map commit through `AnalysisPipeline.Run(root, A)` with exact maps only. The license check
  is the one `td test` uses; without a covering license every project is recorded in full. The summary
  names each project's step and reason; a full recording after incremental steps lists divergences.
- Tests: `CoverageMapMergeTests` (merge rules, unobserved members, lineage, divergence),
  `UnobservedMemberTests` (selection traces an unobserved member by name; without the mark the carried
  entries would select nothing), `IncrementalRecordingTests` (end to end through the CLI: partial step,
  carried map, reflection-found plugin recorded in full with the divergence check, step threshold),
  `CliOptionsTests`. Sabotage-checked: no re-indexing, and a recorder that ignores the class filter.
- Evaluator: with `--map-age k`, after recording both commits, `EvalSteps.RecordIncremental` runs the
  step from the maps at `N−k` to `N` as the CLI would (cached under `<N>-incremental-<A>`) and every
  mutant is also selected with those maps (`OtherSelector.Incremental`, JSON `others.incremental`). The
  report's drift table gets a row for it, and an "Incremental recording step" table lists per project
  the step, classes recorded, seconds against the full recording (now kept as `<project>.record-seconds`
  next to each recorded map) and divergent classes (JSON `incrementalSteps`). `EvalMapAgeTests` covers
  it: the drift adds a call, the naive maps miss the class, the incremental maps do not.
  Still to do: the chain `A→A+1→…→N`.
