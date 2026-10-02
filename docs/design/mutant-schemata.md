# Mutant schemata and parallel mutants

Status: implemented 2026-10-02 (d7530a9 schema injector, 089a2e0 evaluator, 302abe6 parallel mutants). Backlog item 0 ("Fast, reliable evaluation environment").

## Problem

A synthetic evaluation runs one mutant at a time per repository. For each mutant it rewrites every
copy of the target assembly in the test outputs (Cecil, with the `.pdb`), runs the reached test
projects, confirms misses alone, lifts the mutant for control runs, and restores the files. More
than 90 % of the time is the test runs; the rewrite costs seconds. Two mutants cannot run at once
on one clone, because they would rewrite the same files.

Corpus round 1 took two days on one machine, mostly waiting for one mutant after another; a full
rerun of one large repository took 5 to 8 hours.

## Goal

Run several mutants of one repository at the same time, on the same binaries, with results that are
exactly those of today's one-at-a-time evaluation. Keep every safety property of the evaluation
(baseline, confirmation alone, control without the mutant, crash and hang handling, outcome cache).

## Design

### 1. Schemata: all mutants compiled in, one switched on per process

After the commit is recorded and built, the sampled targets (all of them) are injected into the
test outputs **once**. Each mutated method gets a guarded prologue instead of an unconditional one:

```
if (<SiftMutantSwitch>.Active == 17) { <mutation of kind K> }
<original body>
```

- `<SiftMutantSwitch>` is an internal static class added to every rewritten assembly, so the output
  folder needs no extra file. `Active` is a `static readonly int` read once from the environment
  variable `SIFT_MUTANT` (0 or missing: no mutant). A static readonly int compare per call is the
  whole cost when no mutant is active; tiered JIT folds it after warm-up.
- Every test process gets `SIFT_MUTANT=<id>` (or nothing) from the evaluation. Lifting a mutant for
  a control run is running without the variable; nothing on disk changes during the evaluation.
- Mutation kinds as guarded code:
  - **Throw**: `if (active) throw new InvalidOperationException("Sift mutant: T.M");`
  - **ReturnDefault**: `if (active) return default;` (void: `return;`; by-reference returns are not
    mutated, as today).
  - **FlipBranch**: the first two-way conditional branch is preceded by a switch: when the mutant is
    active the inverted branch is taken, otherwise the original. For `brtrue/brfalse` the value on
    the stack is normalised to a bool (`ldnull; cgt.un` for references) and combined with the switch;
    `beq/bne.un` become `ceq` + switch + `brtrue/brfalse`. Semantics match today's inversion exactly
    (`bne.un` is the negation of `beq`, unordered included).
  - **FieldValue**: the constant stored by the constructor is replaced by
    `active ? changed : original`, both constants of the field's type.
- Ids are the 1-based index of the target in the sample; the schema records, per id, which copies
  and how many methods it changed (today's `MutatedMethods` and reached outputs).
- The `.pdb` is rewritten with the assembly (as since ae65f0b); originals are backed up once and
  restored once at the end, or on any failure.

### 2. Baseline on the schema build

The baseline run happens **after** injection, with no mutant active, so the run every mutant is
compared with uses the very same binaries. The evaluation checks that the baseline with the schema
fails exactly the classes the baseline without it fails (one extra full run per commit); if not,
schemata are turned off for the commit and the evaluation falls back to one-at-a-time rewriting
(logged and reported), so a guard that changes behaviour can never produce false results.

### 3. Parallel mutants

- `--mutant-parallelism <n>` (corpus: `mutantParallelism`, default 1 until measured; repositories
  with a cleanup command or tagged with fixed ports stay at 1).
- Mutants are evaluated by n workers; each worker runs its mutant's projects, confirmation and
  control runs with its own `SIFT_MUTANT`. Results are collected and reported in sample order, so a
  report does not depend on scheduling.
- `TestExecutor` becomes safe for concurrent runs: run ids, log and TRX names are already unique;
  the per-project longest-time table and the cleanup command move behind a lock; test outputs are
  read-only during the evaluation.
- Shared state the test suites themselves create (files written next to the binaries, snapshot
  `.received` files, local ports) can make parallel runs interfere. Two guards: confirmation alone
  already rejects failures that do not repeat; and the evaluation records, per repository, whether a
  parallel run's baseline-equivalent check (below) passed, falling back to 1 if it did not.

### 3b. Finding: parallel runs must not share the clone (2026-10-02)

Measuring Scriban at 2 mutants at once gave one result different from 1 at a time: its `TestAot`
runs `dotnet publish` into a fixed folder inside the repository, two mutants' runs removed and wrote
it at the same moment, and one failed ("Unable to remove directory"). The interference check of
section 3 passed: two baseline runs at once did not happen to collide, since a race shows up only
sometimes. A check cannot prove the absence of a race; isolation can.

Design change: each parallel worker runs in its own copy of the built clone (the repository with
its build outputs and the schema written in), made once per commit after the schema check:
copy-on-write where the file system has it (`cp -c` on APFS, `cp --reflink=auto` on Linux), a plain
copy otherwise. Test runs of worker k use copy k as working directory and output folders; project
paths are mapped from the clone to the copy. What tests write into the repository (publish folders,
snapshot `.received` files, local databases) then stays per worker. The interference check stays as a
second guard for what copies cannot isolate (fixed ports, global temp files, machine-wide state).
Copies are removed at the end of the commit.

### 4. Equivalence, before switching the default

- Unit tests: for each kind, a small compiled assembly is rewritten with schemata; loading it in a
  collectible load context with `SIFT_MUTANT` unset behaves like the original, and with the id set
  behaves like today's rewrite mutant (same exception, return value, branch, field value).
- The existing slow evaluation tests (mutant kinds, confirmation, unreported projects, crashes,
  outcome cache) run unchanged on schemata and must pass.
- Corpus check: three repositories of different shapes (Scriban, Fluid, Mediator) are evaluated with
  rewrite and with schemata (parallelism 1, then 4); killed and missed classes per mutant must be
  identical. Run time is recorded for both.
- `OutcomeCache.Version` is raised, since injection changes.

### 5. Afterwards

The rewrite injector (the same code without the guard) stays as the fallback of section 2 and for
`--no-schema`.

Merge-mode evaluation keeps writing its mutants in one at a time, deliberately: it evaluates 2 or 3
mutants per merge, each against the full suite, and a rewrite costs seconds. A schema would add a
full-suite check run per merge (and parallel mutants n more) to save those seconds, so it would make
merge mode slower, not faster. Revisit if merge mode ever samples many mutants per merge.

## Measurements to report

Per repository: wall-clock time of a full synthetic evaluation with rewrite, schemata at 1, and
schemata at 2/4/8; identical results; CPU and memory peak. The default parallelism is set from
these numbers, not guessed.
