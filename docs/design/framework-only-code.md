# Code compiled only for other frameworks

Status: built 2026-10-11 (high-share repositories, coverage group: Newtonsoft.Json).

## Problem

Newtonsoft.Json targets `net10.0;net8.0;net6.0;net45;net40;net35;net20;netstandard2.0`, and its tests target
`net46;net40;net35;net20;net8.0;net10.0`. `Utilities/LinqBridge.cs` is wrapped in `#if !HAVE_LINQ`: only the net20 build
compiles it. A change in it is detected (the bare parse and the net20 symbols both see it), but a map recorded for
net10.0 does not know the member, so coverage cannot answer and the change is traced by name. Its names are LINQ's
(`SelectMany`, `Max`, `GetHashCode`), which half the library names, and the trace selected every test class: 10 of 11
sampled LinqBridge changes ran all 313 classes, 10 points of Newtonsoft.Json's 51.5 % mean share.

For a run of all frameworks that is right: the net20 test build loads the net20 library build, which compiles the change,
and the map knows nothing of net20. But a run for one framework (`td test -f net10.0`, a CI matrix leg, the evaluation)
loads only that framework's builds, and none of them compiles the change.

## Rule

- **Symbols by framework.** The MSBuild evaluation already read each target framework's `DefineConstants`, implicit
  framework symbols included (`AddImplicitDefineConstants`), but kept only the distinct sets. They are now kept by
  framework as well: `EvaluatedProject.FrameworkSymbols` and `ProjectNode.FrameworkSymbols` (evaluator version 9).
- **What the run's builds see** (`CodeChangeDetector.SeenUnder`). With `-f`, when every project compiling a changed C#
  file with an `#if` targets that framework exactly, the reader parses both versions with that framework's symbols
  (one set per project) and keeps the changes those builds compile differently: `CodeChange.SeenByRunFramework`. The
  builds compile the tokens; code under a false `#if` is trivia, so equal token streams mean an unchanged build. A view
  that affects the whole project, or differs in a way no declaration explains, gives null, and every change counts.
- **Selection** (`TestSelector`, the change seeding of both traces). A symbol left out of `SeenByRunFramework` is not
  seeded when every test project below its owner that targets the framework reaches the owner only through projects
  that target it too (`LoadsOnlyRunBuild`). A project between them built for another framework (a `netstandard2.0`
  library) references whatever build of the owner is nearest to it, which may compile the change; a project whose
  frameworks are unknown (read from its XML alone) is not taken for one that lacks the framework. A test project that
  does not target the framework does not run in a `-f` run.
- Without `-f` nothing changes, and neither does a framework the owner does not target exactly: which of its builds
  the run loads would need NuGet's nearest-framework rules, which this does not model.

## Measured

Newtonsoft.Json, 100 sampled changes, `--explain` on the same recording, `-f net10.0` (the corpus default): mean share
51.5 % → 40.7 %; 12 changes narrower (11 in LinqBridge, `ThreadSafeStore.AddValue` under
`#if !HAVE_CONCURRENT_DICTIONARY`), none wider. In the full run (fresh recording, 0 of 69 missed) none of the 12 narrowed mutants failed a test.
