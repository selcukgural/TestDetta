# Comparing with dotnet-tia on the corpus

Status: design 2026-10-02 (backlog "Next" item 2). Nothing installed or run yet.

## Why

dotnet-tia (github.com/SebHenn/dotnet-tia, MIT; read at `5ad7c6b`, 2026-08-21) does the same job
by static analysis only:
- Roslyn `MSBuildWorkspace`;
- a reverse reference graph (callee→caller, interface↔implementation, base↔override, fixture→test);
- method-level selection;
- widening rules for reflection, source generators, `const` and non-`.cs` files.

It reports zero misses on its own mutation gate for NodaTime (20 samples) and FluentValidation
(16), and mean selections of 35.5% (NodaTime), 51% (FluentValidation) and 30% (MediatR) over
replayed history. Sift's claim is that recorded coverage is both safer (dispatch the static
graph cannot see: DI, reflection, serializers, run-once code) and narrower (type-insensitive
interface hops select whole hierarchies). Only the same mutants on the same repositories can
settle that.

## Method

Use the evaluator's existing machinery: each synthetic change is already selected by several
selectors and judged against the same confirmed failures (the drift modes). dotnet-tia becomes one
more selector:

- After the change's source edit is applied, and before the files are restored, run
  `dotnet tia analyze --base <sha> --json --path <clone> [--solution <sln>]`. Its diff is against
  the working tree, as Sift's is.
- Read the report into a class-level selection that can be compared with Sift's:
  - `mode: full` selects everything.
  - Each project in `projects[]` is matched to an evaluated test project by path. When `filtered`
    is false, the whole project runs. Otherwise its `tests[]` (fully qualified test names) are
    reduced to their classes: the name up to the method, with parameters and generic arguments
    stripped.
  - A project the report does not list ran nothing.
- Keep dotnet-tia's own precision too (`impactedTests` vs `selectedTests`, method level), but
  compare at class level, which is what the evaluator can prove a miss at.
- A failing class that any selector left out is confirmed alone, as for the drift modes. Misses
  and selected share are reported per selector on the same mutants.
- Time: dotnet-tia's `elapsedSeconds` per analysis, next to Sift's. The first analysis of a
  repository builds dotnet-tia's graph cold. It is reported separately, and the warm cache
  (`.tia/`) is kept in the clone between mutants, since only the edited project is rebuilt.

### Fairness

- The same mutant kinds (all four since round 2), the same sample and the same toolchain. Both
  selectors get the same diff.
- dotnet-tia requires a restored solution that compiles and a solution file. Repositories with
  several solutions get a corpus field naming the one to use. When dotnet-tia falls back to a full
  run (a load failure, a compile error), that is counted as its result (safe, maximal selection)
  and reported with its reason, never dropped.
- Its widening rules stay on and `--type-flow` stays off (its default; documented as not changing
  selection).
- Mutants Sift cannot see (`Detected = false`) still count for dotnet-tia.

## Implementation

- Generalise the drift modes into "other selectors" keyed by name (`bridge`, `noMap`, `naive`,
  `tia`). Each yields test-project decisions; confirmation and reporting stay shared.
- `--compare-tia <path to Tia.Cli.dll>` on single and corpus runs, plus an optional corpus field
  `tiaSolution`.
- Report: one more row in the synthetic.md table. In synthetic.json, `alternatives.tia.{missed,
  selectedShare, fullRun, seconds}`.

## Running it

- **Source and build:** build dotnet-tia from source at a pinned commit (`5ad7c6b`) into
  `~/sift-corpus/tools/dotnet-tia-<commit>`, rather than `dotnet tool install`, so the code that
  runs on the corpus machine is the code that was read. Its own NuGet dependencies
  (System.CommandLine, Microsoft.Build.Locator, Roslyn workspaces) come from nuget.org. Decision
  for the user: running a third-party tool on this machine.
- **Repositories:** NodaTime and FluentValidation first, since dotnet-tia has published numbers for
  both. Then MediatR-like DI suites (Mediator, Immediate.Handlers), reflection-heavy ones
  (Newtonsoft, AutoMapper, protobuf-net) and the ASP.NET Core ones (Carter, FastEndpoints).
- **Cost:** our mutant outcomes are cached, so this is analysis time only. That is a cold graph
  plus up to 100 warm analyses (seconds each), plus confirmation runs for classes only dotnet-tia
  misses.
