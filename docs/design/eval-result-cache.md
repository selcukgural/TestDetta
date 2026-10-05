# Reusing test outcomes and maps across corpus rounds

Status: accepted 2026-10-05. Backlog item 0 ("Fast, reliable evaluation environment"), after
[mutant schemata](mutant-schemata.md).

## Problem

Checking one TestDetta fix on the corpus takes a night or more. A round reruns every repository from
scratch although most of the work does not depend on TestDetta:

- **Which classes a mutant fails** depends on the repository commit, the mutant, the toolchain and
  how the evaluation injects and runs it, not on TestDetta's selection. `OutcomeCache` already
  stores it, but in the round's own output folder (`runs-r2c/outcome-cache`), and every round
  evaluates the default branch's new head, so the commit changes and nothing is found.
- **The coverage maps** depend on the recorder. `RecordingCache` already stores them, keyed by a
  hash that includes the whole `TestDetta.Core` assembly. Selection lives in Core, so every
  selection fix changes the key and the maps are recorded again, a full one-test-at-a-time run.

In round 2b, 43% of the mutants failed no test, and OpenTelemetry alone took two hours.

## Design

### 1. Pinned commits and a shared cache

- A corpus entry gets an optional `commit`. When it is set, the synthetic evaluation (and the history
  mode's walk) starts there instead of at the branch head. Repositories are moved to a newer commit
  on purpose, in a refresh round, not by every run.
- `testdetta-eval --corpus … --cache <dir>` puts both caches outside the round:
  `<dir>/outcomes/<repository>/` and `<dir>/maps/<repository>/`. Without `--cache` they stay in the
  output folder, as today. The corpus pool passes `~/sift-corpus/cache`.
- The mutant sample is already deterministic (seed per entry, default 1), and outcomes are stored per
  member and kind, so a sample that shifts because the maps changed only computes the new mutants.
- Nothing about a stored outcome changes: crashed, hung or unreported runs are never stored, and
  `OutcomeCache.Version` is still raised with any change to injection, runs or confirmation.
- Each outcome stores the test projects the evaluation ran (`Evaluated`). A project whose run without
  mutants crashed is left out of that evaluation, so its failures are not in the outcome; when a later
  round evaluates it, the outcome is not reused (found in round 2d: Mapperly's main test project crashed
  under load in 2c, and 2d reused 2c's outcomes, counting 1 failing mutant instead of 74). Outcomes
  written before the field existed are not reused; the warm round-2 cache was stamped once from the
  round that wrote it (`~/sift-corpus/stamp-outcomes.py`).

After a TestDetta fix, a round then records maps only where the recorder changed, computes the
selection for every mutant (seconds) and compares it with the stored failures. Classes that look
missed are still confirmed alone, from the cache when known.

### 2. A recording key that follows the recorder's code, not all of Core

The recording key keeps everything it has today (the recording version, framework, parallelism,
setup, projects, toolchain, environment) and the full code of `TestDetta.Coverage` and
`TestDetta.Coverage.Runtime`: every type's members and IL and every embedded resource, not the file
bytes, which carry the commit id (`AssemblyInformationalVersion` is `1.0.0+<sha>`; until 2026-10-05
every TestDetta commit recorded every repository again). In place of the hash of the whole `TestDetta.Core` assembly it takes
a fingerprint of **the Core code the recorder can reach**:

- Start from every type in the two coverage assemblies. Follow every reference into Core from their
  signatures, base types, interfaces, attributes, fields and method bodies (calls, field accesses,
  `newobj`, `ldtoken`, generic arguments), then the same from each Core type reached, until nothing
  new is found. Nested and compiler-generated types (lambdas, iterators) come with their outer type.
- An interface or abstract type in the set brings every Core type that implements or derives from
  it, because the evaluation can hand the recorder any implementation (`IProjectEvaluator`).
- Hash, in a stable order, each reached type's members: signatures and IL (opcodes and resolved
  operands, not metadata tokens, which shift when unrelated code changes).

A change to selection code that the recorder never reaches (test selection, name tracing, the
drift bridge) keeps the key; a change to the project graph, the MSBuild evaluation, TRX reading or
test filters changes it. The fingerprint is computed from the built assemblies, so it needs no list
kept by hand, and it errs towards recording again: any reachable change counts, even a cosmetic one.

`RecordingVersion` still covers the evaluation's own recording code (`EvalSteps.RecordAt` and the
options it passes), which is outside the fingerprint.

### 3. A round whose outcomes are stored builds and runs nothing

Measured on round 2c (2026-10-05): 1768 machine minutes, 81 % in mutant runs and 19 % in setup.
With every outcome stored, the mutant runs go, but the setup still built every test project, ran the
full suite without mutants, ran it again with the schema written in, and ran it once per worker for
the parallelism check: four to six full runs per repository, about 25 minutes for OpenTelemetry alone.

- **Selection first, runs after.** The evaluation records (or reuses) the maps, samples the mutants,
  lets TestDetta select for each, and looks every mutant up in the cache. Only the mutants without a
  usable outcome are built, written into the schema and run; the parallelism check starts no more
  workers than there are such mutants. With none, the round ends after the selection.
- **The run without mutants is stored too**, next to the outcomes (`Baseline` in the cache file), with
  the test projects it considered, those that reported, their failing classes and its duration. It is
  used when today's test projects are the same; otherwise the baseline runs as before. Only a complete
  run is stored: a project that crashed under load once would otherwise stay left out of every round.
  Outcomes are compared with the baseline they were measured against, so reusing it is also more
  consistent than a fresh run.
- When some mutant has to run on a stored baseline, its test projects are built then; one that no
  longer builds is left out of those runs.
- **A full run that crashed or hung is stored as such** (`FullRunIncomplete`). Such a mutant is judged
  by running what TestDetta left out, the only place a miss can be; later rounds skip the full run,
  which mostly waits for its timeout (Polly: 8 such mutants took 11 of 16 minutes on a warm cache).
  When that run missed nothing, the selection's fingerprint is stored with it (`NothingMissedWith`):
  the same selection leaves out the same classes and is not run again, any other selection is.

## Memory guard

A test run's process tree is killed, and counted as hung, when it holds more than half the machine's
memory, or more than a tenth while less than a tenth of the machine's memory is free
(`MemoryLimit`, `--memory-limit <GB>`). Round 2e reset the machine: Mapperly's four mutant workers and
NLog together held 69 GB of 48, and macOS swapped until its watchdog restarted it, ending both
evaluations and everything else on the machine. A killed tree costs one inconclusive run instead; in
the parallelism check it makes the repository run its mutants one at a time.

## Not done here

- Skipping mutants that no test reaches (a reachability run with the schema) and scheduling the
  slowest repositories first: separate steps, measured after this one.
- Sharing clones between rounds: a clone is modified during an evaluation, so it stays per round.

## Tests

- The corpus reads `commit`, and the batch evaluates that commit instead of the branch head.
- `--cache` places outcomes and maps under the given folder; without it they stay in the output.
- The fingerprint is stable across builds of the same code; it changes when a method the recorder
  reaches changes, and stays when an unreached Core method changes (two small generated assemblies).
- With every outcome and the baseline stored, a second evaluation writes no build or test-run log; with
  one outcome lost, only that mutant is built and run, on the stored baseline, with the same result.
- A process tree over its memory limit is stopped within seconds; one within it runs to its end.
