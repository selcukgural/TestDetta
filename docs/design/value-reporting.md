# Value reporting: what TestDetta saved, and why it was safe

Status: first slice implemented 2026-10-02 (backlog "Next" item 4). This document covers the first slice, decisions
per test class. Measured savings and reports over time follow in later slices.

## Problem

Today a run reports one line per test project: All, Filtered (n of m classes) or Skipped, with
one reason and a time estimate. Nobody can check *why* a class was left out, and nobody can see
which change pulled a class in. That makes the savings hard to trust and the over-selection hard to
fix. The precision round needs the same data: which rule selected the extra classes.

## First slice: a decision per test class

Every test class of an affected test project gets a decision and its evidence.

| Decision | Evidence |
|---|---|
| selected by coverage | the changed member (or bridged drift member) and the map commit: "ran `Order.Total`, map `abc123`" |
| selected by name | the chain that reached it, shortest first: "names `Total` ← `Checkout` ← `OrderServiceTests`" |
| selected because it failed while recording | the map commit |
| run with the whole project | the project-level reason (build file, non-C# file, drift fallback, ...) |
| skipped | "ran none of the changed members (map `abc123`)" or "names none of them" |

### Model

- `ClassDecision(TestClass, Selected, Kind, Evidence)`, where `Kind` is coverage, name, failed
  while recording, whole project or skipped.
- `TestProjectSelection` gets `Decisions` (all candidate classes, not only the selected ones).

### Collection

- `CoverageSelection.SelectAll` records, per class, the first change that selected it, together
  with the map commit.
- `ImpactPropagation` records, per reached member, the member and name it was reached from
  (today only logged at debug level). The chain for a class is walked back from its first affected
  member to a seeded change, capped at 8 steps.
- `Decide` fills the evidence. A skipped class gets the "ran none" or "names none" evidence of its
  project's mode.

### Output

- JSON (`td affected --format json`): `tests[].decisions[]`.
- Markdown summary: per project, a collapsed `<details>` list of selected classes with their
  evidence, and the count of skipped classes. The summary already carries the time estimate.
- Text: `-v` prints the same lines; the normal text output stays as it is.

### Cost and limits

The extra bookkeeping is one dictionary entry per reached member and per selected class. That is
small next to the source index.

Name chains name the first path found, not every path: enough to explain the decision, not a
proof. The chain is shortest because the spread is a breadth-first search.

## Later slices (from the backlog item)

- Measured, not estimated: main runs the full suite when it records. The real duration of what a
  pull request skipped is known from that run's report, so the estimate's error can be reported.
- Over time and across repositories: per day/week/month, per team and project, cost from runner
  prices; a dashboard and a weekly digest; an API for the hosted tier.
- Evidence of safety: failures on main that a pull request's selection skipped, and how often and
  why a fallback ran everything.

## Tests

- A selection test asserts the evidence of each kind:
  - a coverage-selected class names the change and map commit;
  - a name-selected class gives its chain;
  - a skipped class says why.
- JSON and Markdown output tests for the new fields.

## As built (first slice)

- `TestProjectSelection.Decisions`: `ClassDecision(TestClass, Selected, Kind, Evidence)` for every
  candidate class, ordered by class; `Kind` is Coverage, Name, FailedWhileRecording, WholeProject
  or Skipped.
- Evidence:
  - coverage: "ran `Type.Member`, which changed (map `<commit>`)", or for the constructor and
    static-constructor rules "ran a member of `Type`, whose constructor changed"; a class the map credits with the
    member only through a value it read (`builtValues`: a field another class filled while the member ran) gets
    "read a value built by `Type.Member`, which changed" instead (2026-10-12; the selection is the same);
  - name: the chain from `ImpactPropagation.Explain`, e.g. "`OrderServiceTests.Adds_tax` names
    Checkout ← `OrderService.Checkout` names Total ← … ← changed `PriceCalculator.Tax(decimal)`";
  - skipped: whether a map was asked.
- The decision rules are unchanged; evidence is recorded alongside them.
- Output:
  - JSON `tests[].decisions[]` (`class`, `selected`, `kind`, `evidence`);
  - the Markdown summary lists each selected class with its evidence in a code span (repository
    text never renders) and counts the skipped classes.
- `-v` text output is not done yet.
- Tests: `TestSelectorTests` (coverage and name evidence, skipped), `MarkdownSummaryTests`
  (evidence and hostile evidence stay in code spans), `MapDriftTests` (JSON).
