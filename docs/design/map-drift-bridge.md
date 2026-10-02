# Map drift: using an older map through a static bridge

Status: design 2026-10-02; selection side and the evaluator's drift modes implemented the same day
(see "As built"), the action's restore of an ancestor map too. Still open: running measurement 2
and the size cap.

## Problem

A coverage map is used only when it was recorded exactly at the merge base
(`AnalysisPipeline.LoadCoverageMaps`; in CI the action restores only the cache key
`sift-coverage-<merge-base>`). When the map was recorded at an earlier commit, Sift ignores it.
For that test project it then falls back to the name-only trace, not to the full suite (the backlog
text said "full suite", which was wrong). The name trace follows simple names through the
working tree's syntax, so it is broad. Its blind spots (reflection, strings, DI by convention) are
the same ones the map covers.

On GitHub, a `pull_request` run tests `refs/pull/N/merge`, so the merge base is main's tip at the
moment CI runs. A map exists there only if main has not moved since the last recording finished.

## Measurement 1: how far behind is the newest map?

Scripts: `eval/drift/lag_time.py`, `eval/drift/lag_after_merge.py`. Data: the 21 corpus
repositories with at least 50 first-parent commits in the last 365 days (open-source cadence: a
median of 1 to 4 commits per active day, at most 61).

Each policy records the newest main commit on a schedule. The recording takes the stated time
before its map can be used. "Exact" is the share of CI runs that find a map at the merge base.
"Lag" counts the first-parent commits between the newest usable map and the merge base.

**CI at any moment of an active day** (sampled every 15 minutes):

| Recording policy | Exact (median / worst repo) | Lag p90 (median / worst repo) |
|---|---|---|
| every commit, 30 min recording | 96% / 92% | 0 / 0 |
| every commit, 2 h recording | 88% / 78% | 1 / 2 |
| hourly, 30 min | 94% / 88% | 0 / 1 |
| nightly, 1 h | 52% / 29% | 3 / 9 |
| weekly, 1 h | 29% / 9% | 9 / 32 |

**CI 10 minutes after main moved** (the busy case: every merge triggers rebuilds of open PRs):

| Recording policy | Exact | Lag p90 (median / worst) | .cs files changed in the gap, p90 (median / worst) |
|---|---|---|---|
| every commit, 30 min | 0% | 3 / 7 | 10 / 129 |
| every commit, 2 h | 0% | 6 / 22 | 16 / 277 |
| nightly, 1 h | 0% | 7 / 38 | 22 / 879 |

Conclusions:

- Even at open-source cadence, the most affordable policy (nightly) leaves half of the runs without
  an exact map. Recording every commit costs a full instrumented run per merge and still misses
  every run that starts within the recording time after a merge.
- The gap is short: p90 is at most 9 commits for nightly recording when CI runs at a random moment,
  and at most 38 commits right after merges. A bridge has to cover tens of commits, not hundreds.
  A cap of 50 commits covers every corpus repository at p90 under nightly recording.
- The gap's code changes are usually small (median p90 of 10 to 22 `.cs` files). The outliers are
  bulk edits: generated units (UnitsNet), repository-wide refactors (AutoFixture). The bridge
  must handle large gaps by widening, never by trusting the map.
- AI-assisted teams that merge dozens of commits a day sit at the "after a merge" end. There the
  exact-map rule almost never holds.

## Idea

Take the newest map recorded at an ancestor `A` of the merge base `B`. The drift is the set of
commits `A..B`. Within the drift, the map still answers "which test classes ran member m at
`A`". The bridge adds the classes that can reach a changed member at `B` through code the drift
changed.

### Why that is enough

Take a test class `T` and a member `m` changed by the PR. Suppose `T` reaches `m` at `B` along a
call path `T = p0 → p1 → … → pn = m`. Every edge on the path either already existed at `A` or is
new. A new edge `pi → pi+1` needs one of three things in the drift:

- a change to `pi`'s body;
- a new `pi`;
- a new `pi+1` that dispatch now picks (an override, an interface implementation), or a non-code
  input that changes dispatch (configuration, DI by convention, data files).

If no edge is new, the map at `A` already credits `T` to `m`.

Otherwise, take the first new edge `pj → pj+1`. All edges before it are old, so the map at `A`
credits `T` to `pj`, provided `T` itself existed at `A`. `pj` is a member the drift changed, and
it reaches `m` at `B`. So the following rule finds `T`:

> **Bridge rule.** For every member `x` that the drift added or modified, and that can reach a
> PR-changed member at `B`: select the classes the map credits to `x`. When the map cannot answer
> for `x` (it was added, or it falls under one of today's unanswerable cases), trace `x` by name
> as today.

The other cases are covered as follows:

- A test class `T` that is new or changed in the drift: its test methods are drift members, so
  they are traced by name.
- A new override or implementation `pj+1`: it is an added member, so it is traced by name. The
  name trace follows the virtual call site's simple name.
- A non-code input to the drift has no member to anchor the rule to. It is handled below,
  under "project-level drift".

"Can reach `m` at `B`" is decided by the existing name-based propagation (`ImpactPropagation`), run
from the PR's changed symbols with no coverage cut-off. It over-approximates real calls, as it
does today. Its blind spots (reflection by string, conventions) are the same as the name trace
that runs today when there is no map. The bridge is therefore never less safe than the no-map
path. Where the bridge needs the name trace, it falls back to exactly that path.

### Inputs

- **Drift symbols**: `CodeChangeDetector.Detect(path, textAt(A), textAt(B))` for every `.cs` file
  changed in `A..B`. This is the same syntactic detection as for the PR, already able to compare
  any two texts. Contents come from one `git cat-file --batch` per revision (`GitClient.ReadFiles`).
  Rename handling stays delete+add, as for PRs.
- **PR symbols**: today's `CodeChanges` (merge base `B` against the working tree).
- **Source index**: today's index of the working tree. Reachability is decided at the PR's head,
  which contains `B` plus the PR. A path that only the PR creates is the PR's own change and is
  traced as today.

### Project-level drift

The PR path sends a project to "run all" when the PR changes one of these:

- a build input;
- a non-`.cs` file owned by the project;
- an uncomparable `.cs` file;
- `AffectsWholeProject` (global usings, assembly attributes);
- a non-C# project.

When the drift contains such a change, the map's view of every test project that depends on that
project may be wrong in ways no member explains. Those test projects do not use the map: they
take today's no-map path (name trace). The PR's own project-level changes still mean "run all",
unchanged.

Open question for review: a configuration-only change in the drift that wires a new DI
registration would be missed by the name trace, as it would be today without a map. The stricter
option is "run all" for those test projects. That would make some PRs slower than today in
exchange for closing a gap that exists today too. Default in this design: name trace, since it
equals today's behaviour. Revisit with measurement 2.

### Limits

- **Age cap**: the bridge applies when `A` is an ancestor of `B` (`git merge-base --is-ancestor`)
  and at most `--max-map-age` first-parent commits behind it (default 50, from measurement 1).
  Beyond that, or when `A` is not an ancestor, the map is not used (today's behaviour).
- **Size cap**: when the drift changes more than a set number of members, propagating each one
  costs more than the name trace saves. The test projects then take the no-map path. The number
  is set from measurement 2; a bulk edit should not be bridged member by member.
- **One map commit per run**: maps are restored together, so all maps in `.sift/coverage` normally
  come from the same `A`. Maps with different commits each get their own drift (computed once per
  distinct commit); bridged members are resolved against every map, which only adds classes.
- **Timings**: map timings at `A` stay valid for estimates; new classes have none, as today.
- **Failed classes**: classes that failed while recording at `A` keep running when their project is
  affected, as today.

## Pipeline changes

1. `AnalysisPipeline.LoadCoverageMaps`: a map at `A ≠ B` is accepted when `A` is an ancestor of `B`
   within the cap. It is returned with its `A`. A map at `B` works exactly as today, with an empty
   drift.
2. New `DriftReader`: changed files `A..B` (`git diff --name-only --no-renames A B`), project-level
   classification through the same `ImpactAnalyzer` rules, and drift symbols through
   `CodeChangeDetector`.
3. `TestSelector` / `CoverageSelection`: for each PR-changed symbol answered by coverage, run the
   uncut name propagation once over all PR symbols, intersect the reached members with the drift
   symbols, and apply the bridge rule. Drift members the map cannot answer are seeded into the
   name trace. When there is no drift, nothing changes, which keeps the current tests valid.
4. Reporting: JSON and Markdown name the map commit, its distance, and the drift members that
   added classes. This also starts the backlog item on reporting which map a decision came from.
5. Action (`action.yml`): instead of the exact cache key, restore the newest cache whose commit is
   a first-parent ancestor of the merge base within the cap.
   - Run `gh cache list --key sift-coverage- --json key` (needs `actions: read`) and walk
     `git rev-list --first-parent -n <cap> <merge-base>` in order, picking the first key that
     exists.
   - Plain `restore-keys` prefixes cannot express "an ancestor of the merge base".
   - Document the needed permission, and say that the checkout must fetch enough history
     (`fetch-depth: 0`, already required).

## Measurement 2 (before the implementation is trusted)

The evaluator gets a `mapAge` setting `k` (corpus entry, `--map-age`), used with synthetic changes:

- record at `A` = the `k`-th first-parent ancestor of the sampled head `N`;
- place those maps, and evaluate each mutant at `N` exactly as today. Outcomes depend only on `N`
  and the mutant, so the outcome cache is reused; only recording at `A` and selection cost time.

Each mutant is evaluated under three selections:

| Mode | Map | Drift handling |
|---|---|---|
| exact | at `N` | none (today, `k = 0`) |
| no map | none | name trace (today's behaviour when the map is older) |
| bridge | at `A` | bridge rule |

There is also a fourth, diagnostic-only mode, **naive**: the map at `A` used as if it were exact. It
shows how many misses the drift would cause without a bridge, which is the size of the problem
the bridge solves. It is never offered in the CLI.

Report the misses and selected share per mode for `k ∈ {1, 3, 10, 30}`, on the repositories with
the highest commit rates (opentelemetry-dotnet, fastendpoints, meziantou-analyzer, polly,
awesomeassertions, mapperly) plus NodaTime.

Pass criteria:

- the bridge misses nothing the exact map catches;
- the bridge's selected share lies between exact and no-map, and closer to exact for small `k`.

A bridge miss gets a complete fix and a red test, as with every other miss.

## Order of work

1. Evaluator: `--map-age` and the naive/no-map/bridge modes (the Sift.Core side gets a map policy
   that only the evaluator sets for "naive").
2. `DriftReader` + bridge rule in the selector, with unit tests on small repositories built in the
   tests:
   - a new call added in the drift;
   - a new test class;
   - a new override;
   - a configuration-only drift;
   - beyond the cap;
   - not an ancestor.
3. Measurement 2 on the corpus; set the size cap.
4. Action restore of the newest ancestor map; README "Coverage maps are used only when…"
   rewritten; backlog text corrected.
5. dotnet-tia comparison (backlog item 2) uses the same synthetic samples.

## As built (2026-10-02)

- `GitClient.FirstParentDistance` (ancestry via `merge-base`, distance via `rev-list --count
  --first-parent`; null for a commit outside the clone) and `GitClient.ChangedBetween`;
  `CodeChangeReader.ReadBetween` compares two commits.
- `AnalysisPipeline.Run(repo, base, AnalysisOptions)`: `MaxMapAge` (default 50, CLI
  `--max-map-age`, 0 = exact maps only) and an internal `MapUse` (`Bridge`; `None` and
  `AsIfCurrent` for the evaluator). One `MapDrift` per older map commit: changed files, code changes
  and project-level impact of `A..B`, package changes included. `AnalysisResult.Drifts` is reported
  in text, JSON (`olderCoverageMaps`) and the Markdown summary.
- `TestSelector`: `WithoutDriftedMaps` drops the map of every test project downstream of a
  project-level drift change (the same rules that make a PR change "run all"); `Bridge` takes the
  drift's added and modified members that the uncut name spread of the PR's changes reached
  (`ImpactPropagation.Reached`; for a type-level change, any member of the type) and resolves each
  like a change: coverage where the map answers, name trace otherwise.
- Tests: `MapDriftTests` (new call, new test class, new implementation, project-level drift,
  unrelated drift keeps the map's answer, age limit and foreign commit, output, git distance), each
  of the first three also showing that `AsIfCurrent` misses; sabotage-checked (no bridge → 3 red,
  no project-level rule → 1 red).
- Evaluator: `--map-age <n>` (single run and corpus override). The commit `n` first parents back is
  recorded first, then the evaluated commit; every synthetic change is selected four times (exact
  maps, and the older maps bridged / none / as if current). A failing class that any selection left
  out is confirmed alone once, and every selection is judged on the same confirmed failures; the
  outcome cache needs answers for all of them, otherwise the mutant runs again. `synthetic.json`
  has `mapAge`, `mapCommit` and per mutant `drift.{bridge,noMap,naive}.{missed,selectedShare}`;
  `synthetic.md` a "Map drift" table (missed mutants, missed classes, mean share, precision).
  With `--map-age 0` nothing changes. `EvalMapAgeTests` (end to end, sabotage-checked).
- Action: a "Find newest coverage map" step lists `sift-coverage-*` caches with `gh cache list`
  (input `github-token`, default `github.token`; the job needs `actions: read`) and walks
  `git rev-list --first-parent -n <max-map-age + 1> <merge-base>` for the first commit that has
  one; without the permission it notices and falls back to the exact key. Input `max-map-age`
  (default 50) is passed to `sift test`. Checked locally with a stub `gh` (ancestor found, 403
  fallback, age limit).
