# Evaluation on real repositories

## Corpus round 1 (2026-09-30 → 2026-10-02)

`sift-eval --corpus eval/corpus.json`: per repository, coverage is recorded at the default branch's
head, then up to 100 sampled members are changed one at a time (source edit for Sift, a `throw` in
the compiled member for the tests) and every test class that fails because of the mutant must be in
Sift's selection. A missed class is confirmed by running it alone with and without the mutant.
macOS arm64, .NET SDK 10.0.401 (+ ASP.NET Core 8.0.31 for net8.0 suites). Durations were measured
with several repositories running at once and are noisy; selection shares and misses are not.

**Headline: 41 repositories, 3 missed of 2941 failing mutants (95% upper bound of the miss rate
0.26%); all three are first-day results from builds that predate the fixes, and all three are
verified fixed on fresh recordings (see below).** Every miss found later was fixed with a test that fails without the
fix, and its repository rerun until clean. Refit was added after round 1.

| Repository | Failing mutants | Missed | Selected (mean share) | Precision (median classes run per failing class) |
|---|---|---|---|---|
| roslynator | 80 | 0 | 8% | 1.0 |
| polly | 65 | 0 | 7% | 1.0 |
| nodatime | 80 | 1 | 43% | 1.0 |
| mapperly | 79 | 0 | 89% | 5.7 |
| spectre | 80 | 0 | 9% | 2.0 |
| communitytoolkit | 76 | 1 | 18% | 1.5 |
| argu | 0 | 0 | – | – |
| automapper | 80 | 0 | 67% | 1.7 |
| autofixture | 80 | 0 | 16% | 1.0 |
| fluentvalidation | 80 | 1 | 32% | 1.0 |
| awesomeassertions | 81 | 0 | 27% | 2.0 |
| autofac | 77 | 0 | 49% | 1.0 |
| scrutor | 80 | 0 | 57% | 1.0 |
| lamar | 80 | 0 | 26% | 1.0 |
| libgit2sharp | 57 | 0 | 19% | 1.0 |
| nsec | 79 | 0 | 13% | 1.0 |
| newtonsoft-json | 80 | 0 | 47% | 4.5 |
| protobuf-net | 80 | 0 | 78% | 31.1 |
| yamldotnet | 80 | 0 | 78% | 3.4 |
| castle-core | 80 | 0 | 19% | 1.3 |
| nsubstitute | 3 | 0 | 29% | – |
| nlog | 65 | 0 | 82% | 3.0 |
| unitsnet | 80 | 0 | 34% | 2.0 |
| serilog | 79 | 0 | 20% | 1.5 |
| open-xml-sdk | 80 | 0 | 30% | 1.0 |
| scriban | 71 | 0 | 100% | 5.6 |
| markdig | 80 | 0 | 98% | 6.0 |
| mimekit | 79 | 0 | 14% | 5.0 |
| vogen | 77 | 0 | 36% | 1.0 |
| mediator | 80 | 0 | 21% | 1.2 |
| immediate-handlers | 43 | 0 | 29% | 1.0 |
| meziantou-analyzer | 80 | 0 | 63% | 186.6 |
| swashbuckle | 80 | 0 | 12% | 1.0 |
| fastendpoints | 80 | 0 | 23% | 1.8 |
| carter | 80 | 0 | 24% | 1.0 |
| aspnetcore-odata | 79 | 0 | 13% | 2.1 |
| opentelemetry-dotnet | 79 | 0 | 6% | 1.3 |
| graphql-dotnet | 80 | 0 | 45% | 4.8 |
| guardclauses | 24 | 0 | 13% | 1.0 |
| cliwrap | 69 | 0 | 81% | 5.0 |
| fluid | 79 | 0 | 23% | 1.0 |

Not informative: Argu (F#: no C# member to change), NSubstitute (3 failing mutants: NUnit under the
testing platform wrote no report; fixed for round 2), Guard Clauses (small library, 24). Refit was
added after round 1 (first TUnit suite).

### Misses

- First day (2026-09-30), from builds before the run-once rules, generated-code reading and the
  alone-confirmation:
  - NodaTime `TextCursor..ctor` (7 classes that use patterns parsed once in static initialisers);
  - CommunityToolkit `Dictionary2<,>.Initialize` (3 classes);
  - FluentValidation `RuleBase<,,>.Current` (4 classes).

  The round-1 maps of these runs lack initialization and field-read data, and with them the NodaTime
  miss still reproduces. So each change was checked again on a fresh recording at the same commit
  with the current Sift (2026-10-02):
  - NodaTime: all 7 classes are selected (the test project runs whole).
  - FluentValidation: all 4 are selected.
  - CommunityToolkit: all 3 projects select the class, by coverage. It was a real dependency: with
    a `throw` in `Initialize`, the class fails alone too (`WeakReferenceMessenger.Default`'s static
    initializer builds a `Dictionary2`). The field-read probes now credit that building code to
    every reader of `Default`. Checked on round 2's recording at the same commit.
- Found and fixed later, repository rerun clean: MimeKit (NUnit `[SetUpFixture]`, static
  constructor), Open-XML-SDK (hand-rolled lazy init), Meziantou.Analyzer (test projects sharing a
  directory, object pool create delegate), FastEndpoints (fixture declared by a base class in a
  library), OpenTelemetry (test files linked into other test projects, a static helper of another
  test class), plus evaluation artefacts (stale `.pdb`, package copies of the library, crashed
  baselines) that are now detected.

### Precision

Safety comes first; the cost is selection size. Worst: Meziantou.Analyzer (186 classes per failing
class: a change spreads through common names such as `Create` across its Roslyn-version projects),
protobuf-net (31), Markdig (6), Mapperly (5.7), Scriban (5.6), MimeKit, CliWrap (5). These are the
precision round's starting point, measured again on round 2's data.

## Pilot: history mode (2026-09-30)

State on 2026-09-30, Sift at `2a657c3`. Every run used `sift-eval`: for each of the latest
first-parent commits it records coverage at the parent, lets Sift select, runs the selection and
the full suite, checks the selection against coverage recorded after the commit, and injects
mutants (a `throw` in a changed member) to see whether a failing test class was left out.
macOS arm64, .NET SDK 10.0.401, `-f net10.0`, `--parallel 4`, 2 mutants per commit.

**Headline: 0 missed test classes in 70 evaluated commits across 9 repositories.** Two classes of
real misses were found while evaluating and are fixed (see below). Polly, NodaTime, Roslynator and
CommunityToolkit were run with both fixes; AfterApply, Spectre.Console, Mapperly, Argu and
FSharp.Data ran before them and should be rerun.

## Results

| Repository | Commits | Full suite | With Sift | Saved | Mutants caught / missed | Notes |
|---|---|---|---|---|---|---|
| AfterApply (own, xUnit v2, Testcontainers) | 8 | 8 m 5 s | 3 m 22 s | 58% | 11 / 0 | mutants from the first run, times from the second |
| Polly (MTP, xUnit v3) | 20 | 3 m 13 s | 1 m 19 s | 59% | 0 / 0 | few commits touch library code |
| NodaTime (NUnit) | 6 | 2 m 14 s | 1 m 18 s | 41% | 0 / 0 | code commits narrowed to 47–782 of 26k tests |
| Roslynator (xUnit v2) | 6 | 1 m 50 s | 1 m 12 s | 34% | 3 / 0 | 6 of 7 mutants injected |
| Mapperly (xUnit v3, source generator) | 6 | 5 m 53 s | 3 m 57 s | 32% | 3 / 0 | the generator runs in most tests |
| Spectre.Console | 6 | 15.1 s | 10.4 s | 31% | 2 / 0 | package bumps run everything (Polyfill) |
| CommunityToolkit (MSTest, generators) | 6 | 20 m 54 s | 15 m 14 s | 27% | 3 / 0 | was 40% before the `#if` fix, which was a miss |
| Argu (F#) | 6 | 5.2 s | 4.0 s | 21% | – | F# is not analysed; runs whole projects |
| FSharp.Data (F#, Paket) | 6 | 52.2 s | 0 s | – | – | only workflow/doc commits; not informative |

"Mutants caught" counts mutants that made at least one test class fail in the full suite; only
those can prove or disprove a selection. 22 such mutants, 0 missed. The coverage check found 0
missed classes everywhere.

## Candidates

Evaluated: the nine above. Rejected: Humanizer and Verify (pin an SDK 11 preview); microsoft/testfx,
akka.net and BenchmarkDotNet were dropped during screening. Roslynator's 48 projects make it the
largest graph so far. Still worth adding:
a Windows-only repository (WPF/WinForms) once a Windows machine is available, and a large
ASP.NET Core app with integration tests like AfterApply.

## Fixed while evaluating

- **Code inside `#if` was invisible** (`2a657c3`, a real miss). Files were parsed with no
  preprocessor symbols, so a whole file inside `#if ROSLYN_4_12_0_OR_GREATER` (CommunityToolkit)
  or `#if NET8_0_OR_GREATER` produced no changed symbols, and test classes inside such regions were
  not indexed. Files are now parsed a second time with every symbol their conditions name; a change
  that still maps to no declaration counts as a whole-project change.
- **`AssemblyName` from imports or property functions** (`58c0f5f`). CommunityToolkit
  (`$(MSBuildProjectName.Substring(...))`) and Roslynator (`$(RoslynatorDllPrefix)Roslynator.Core`)
  compiled assemblies under names the loader did not know, so those libraries were not instrumented
  (selection fell back to names, safely) and mutants could not be injected. Such names are now asked
  from MSBuild, once per affected project.
- Earlier in the evaluation: central package bumps narrowed past test packages added from
  `eng/$(ProjectType).targets` (Polly, a real miss, `a42419b`); a directory-wide
  `Directory.Packages.props` wrongly treated as a hidden project input; projects named `*Test` that
  run no tests; several `sift-eval` false positives (symlinked temp roots, same type names in
  unrelated assemblies, reused report directories, artifacts output layout, crashed test hosts).

## Known limitations

- **Windows is untested.** Everything above ran on macOS.
- Code that runs once per process (caches, lazy singletons) is credited to the first test class that
  triggers it; static members and constructors seen by only one class are traced by name instead.
- Code only active under a mix of symbols (`#if A && !B`) is still invisible; such changes run the
  whole project.
- Name tracing is coarse for common override names: a change to one analyzer's `Initialize` reaches
  every type named by an `Initialize`. Scoping overrides to their type hierarchy would narrow this.
- F# and VB code is not analysed; affected F#/VB projects and their dependents run completely.
- Test projects are also recognised by a `*.Tests`/`*.Test` name; false positives are skipped at
  run time.
- Repositories that run Microsoft's code coverage in every test run (Polly: `--coverage` in
  `eng/Test.targets`) abort at test host exit while Sift records (exit 134, `BadImageFormatException`).
  The maps are complete (each shard writes its results before the abort), but `sift record` reports
  the run as failed.
