# Test classes the map never saw running

Status: built 2026-10-04 (backlog: "A test class the map never saw running is skipped").

## Problem

A coverage map lists the test classes whose tests ran while it was recorded (`Classes`) and those
with a failing test (`FailedClasses`). `TestSelector.DecideClass` skips any other candidate class
with no name path to the change as "ran none of the changed code". That answer is only right when
the class did run and touched nothing. Two real cases break it:

- **The build renames a class.** Markdig regenerates its committed spec classes at build time with a
  culture-sensitive `ToUpper`. A map recorded on a Turkish-locale machine names a class
  `TestBlocksAndİnlinesPrecedence`; on an en-US CI runner the same class is
  `TestBlocksAndInlinesPrecedence`, the map has no record of it, and it is never selected (26 misses
  in round 2b).
- **The class was skipped where the map was recorded.** A test skipped on macOS (a platform check,
  `[Platform(Exclude = "MacOsX")]`) runs on Windows CI, but the map cannot say what it runs.

## Rejected: select every class the map does not record

Most classes missing from a map are not like either case. Some have only `[Explicit]` tests, some
are excluded by the repository's own filter or run settings, and some are classes the index takes
for test classes that hold no test at all. A normal full run does not run them either. Selecting
them all makes the selection wider with every such class, and taken far enough it runs everything.
The rules below add a class only where the map shows that it does not know it.

## Design

### What the map records

Three new fields, all null in older maps:

- `SkippedClasses`: test classes the test reports list but none of whose tests executed (TRX
  `NotExecuted`/`Inconclusive`/`Pending`, NUnit `Skipped`/`Inconclusive`). The recorder already reads
  these reports for failures and dropped the skipped ones. The field is null when any test process
  left no report: then the map cannot say which classes it saw.
- `Platform`: the runtime identifier of the recording machine (`osx-arm64`, `linux-x64`).
- `ClassLocations`: for every class in the map's universe (below), one anchor per source file:
  the file relative to the repository root and the first line of code in it, read from the test
  assembly's portable PDB before instrumentation. Code in compiler-generated nested types (lambdas,
  async state machines) counts for the class that contains it. Deterministic paths (`/_/...`) are
  taken as relative to the repository root.

The map's **universe** is `Classes ∪ FailedClasses ∪ SkippedClasses`: every class the test runner
reported. It is built from what ran, not from the source tree: Markdig's build rewrites the
committed files, so the source the recorder could scan says `Inlines` while the runner says
`İnlines`.

### Rule 1: skipped on another platform

A candidate class in `SkippedClasses` is selected when the selecting machine's platform differs from
the map's (`ClassDecisionKind.SkippedWhileRecording`). On the recording platform the class would be
skipped again, so it stays skipped and the decision says why. This keeps a class whose tests always
skip (`[Fact(Skip = "...")]`) from running with every change on the platform the map came from.

### Rule 2: the map and the build disagree

A candidate class outside the universe of a map that has one is either new since the map (traced by
name, as before, through the drift bridge or the change itself) or a class the map knows under
another name. The second case leaves a trace: an **orphan**, a class of the universe that the build
no longer has. Orphans are matched by place, not by name:

- For each orphan anchor, find the innermost indexed type of the same project whose declaration
  span in that file contains the anchor line. If it is a candidate class outside the universe, the
  two are paired.
- A candidate paired with exactly one orphan, which is paired with nothing else, **is recorded as
  that orphan**: it is decided by the orphan's coverage, failure and skip, and the decision names the
  recorded class. No extra test runs: Markdig's 26 classes use what their İ-named records ran.
- A candidate whose pairing is not one to one, or that shares a file with an orphan that paired with
  nothing, is selected (`ClassDecisionKind.MapMismatch`) and a warning says to record the map again.
- A candidate outside the universe with no orphan in its files is skipped as before: nothing shows
  that the map should have known it.

Paired orphans are no longer added as candidates of their own (`AddRecordedClassesTheIndexMisses`),
since their name is not in the build and a filter on it matches nothing. Unpaired orphans still are:
a class hidden from the index by preprocessor conditions (Newtonsoft.Json's `DateOnlyTests`) is an
orphan too, and the map still decides it.

No name folding (`İ`/`I`): matching by place covers any generator that names classes differently
from one build to the next, not only a Turkish `ToUpper`.

### Classes the runner reported but the index does not take for tests (2026-10-04)

Round 2c missed a failing MSpec context in AwesomeAssertions' `MSpec.Specs`. Two gaps met:

- MSpec's adapter writes the namespace as the TRX `className` and `Context::Specification` as the
  test name, so the map recorded the class as `MSpec.Specs`, which matches no class in the source.
  `TrxReport` now puts the class back together for that adapter (`MSpec.Specs.When_mspec_is_used`).
- An MSpec context has no test attributes and the tracker leaves it no coverage, so the recorder
  lists it as failed (untrusted), and only classes with coverage counted as candidates beyond what
  the index recognises. Every class the runner reported (`Classes`, `FailedClasses`,
  `SkippedClasses`) is now a candidate, inside the index or not, and a reported class the index
  cannot see is decided by the map alone: coverage, a failure while recording, or tests skipped on
  another platform (Rule 1).

Maps recorded before the fix still name the namespace; it is selected as a class the index misses,
and its filter (`MSpec.Specs.`) runs the whole project, which is safe.

### Recordings that failed on TestDetta's side (2026-10-07)

A class the reports say ran, with no trace in the map, is kept as untrusted (it runs whenever its project is
affected). When that is every class, the recording itself failed, and the map would list every class as failed:
meziantou-analyzer's maps in round `runs-full-1006` (its five test projects share one assembly name, and the test
assembly was not among the instrumented ones). Such a project is now **not recorded**, with the reason
(`RecordingFailedException`; `td record` prints "not recorded" and keeps its exit codes):

- its test assembly is not among the repository assemblies the recorder instruments (checked before the tests run);
- its tests ran and passed, but the tracker wrote no results in their test process (it did not load there; this is
  no crash, so the crash reruns in one process and with the suite's parallelism are not tried);
- its tests ran, but no results were written even after those reruns (the message says the host crashed before
  writing them or never loaded the tracker, instead of "no test ran");
- its tests ran and passed, the tracker wrote results, yet no test class was recorded.

The map also lists the projects whose assemblies the recording instrumented (`instrumentedProjects`). Selection reads
a changed member whose project the test project compiles against, but which the map did not instrument, as one the
map does not know, and traces it by name: a linked file compiled into two assemblies has the same names in both, and
the map's knowledge of one must not answer for the other (meziantou-analyzer, mutant 17 of `runs-full-1006`).

### Old maps

Maps without `instrumentedProjects` answer as before: a member the map names counts as known.

Without `SkippedClasses` neither rule applies and selection is unchanged. Without `ClassLocations`
rule 2 finds no orphan anchors and skips as before.

## Limits

- A test skipped by its environment rather than its platform (a Docker check while recording
  locally, then Docker on CI) is skipped by rule 1 on the same platform. Record where the tests run
  (the weekly CI recording) to cover it.
- A class compiled only for another platform (`#if WINDOWS`, a `-windows` target) is outside the
  universe with no orphan, so it is skipped as before.
- A preprocessor-hidden orphan sharing a file with an `[Explicit]`-only class makes rule 2 select
  that class. This is rare, costs one class, and comes with the warning.

## Tests

- `TrxReport`: skipped classes from TRX and NUnit XML.
- `CoverageMapStore`: the new fields round-trip; old maps read as null.
- PDB anchors: a compiled class, a nested class, an async test method.
- Selection: skipped class on the same and on another platform; renamed class matched to its
  orphan (coverage taken from the orphan, no stale name in the filter); ambiguous pairing selects
  with a warning; an unknown class with no orphan in its file stays skipped; an old map behaves as
  before.
