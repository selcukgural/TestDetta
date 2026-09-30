# Evaluation on real repositories

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
