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

### Old maps

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
