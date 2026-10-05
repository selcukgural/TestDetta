# What a generator change does to its consumers

Status: generators built 2026-10-05 (decisions with the user, same day); outputs run as another process stay as they are. Precision round, group B.

## Problem

A project used as an analyzer or source generator (`OutputItemType="Analyzer"`), or referenced without
compiling against it (`ReferenceOutputAssembly="false"`), is opaque (docs/design/opaque-project-references.md):
when it is affected, every consumer runs completely. That was the safe answer: nothing TestDetta
sees tells what the change does inside the compiler or in another process.

The precision round of 2026-10-05 (`--explain`, `eval/precision/attribute.py`, against round 2c's
outcomes; round 2e for Mapperly) shows it is now the largest cost left:

| Repository | Mean selected | Classes selected that did not fail, from this rule | Failing classes it was needed for |
|---|---|---|---|
| Fluid | 98% | 100% | 309 |
| LibGit2Sharp | 100% | 100% (`ReferenceOutputAssembly="false"` test app) | 219 |
| Mediator | 86% | 92% | 0 |
| protobuf-net | 87% | 86% | 1329 |
| Mapperly | 97% | 24% | 0 |
| GraphQL | 62% | 10% | 0 |

"Needed" counts failing classes this rule selected; most of them ran the changed code and coverage
would have selected them too. The rule is not wrong in principle: Fluid's generator runs the Fluid
library itself at compile time, and protobuf-net.BuildTools compiles in protobuf-net.Core's sources,
so a library change can change what they generate. It is too coarse: almost always the generated
code does not change.

## Two different cases

1. **A generator** (Fluid, Mediator, protobuf-net, Mapperly, GraphQL's analyzers): what reaches the
   consumer's tests is the code it generates into the consumer's assembly. If that code is the same,
   the consumer's assembly is the same apart from diagnostics, and diagnostics that matter break the
   build, which CI shows anyway.
2. **An output run as another process** (LibGit2Sharp's `NativeLibraryLoadTestApp`): what reaches the
   tests is the program, through the test classes that start it.

## Proposal for generators: compare what they generated

The precise signal is the generated code itself. Every assembly embeds its generated sources in its
PDB; the recorder already reads them (`GeneratedSources`, used for name tracing of generated code).

- **Recording**: for every consumer of an opaque reference, the map stores a fingerprint per generated
  declaration (type and member, as `DeclarationWalker` names them): a hash of its text. A few bytes
  per member, not the text.
- **Selection**: when an opaque generator is affected, TestDetta reads the consumer's generated sources
  from the PDB of the current build and compares the fingerprints. Each declaration that differs, was
  added or was removed becomes a change of the consumer's code, like an edit to a source file: coverage
  answers it, name tracing traces it. Identical output means no change through the generator.
- **The fallback stays** when there is no current build of the consumer (no PDB, or one older than the
  sources), when the map has no fingerprints (recorded before this), or when a generated file cannot be
  read. Then the consumer runs completely, as today.

### What it needs: a build before the selection

This is the one real change to the product's flow. Today TestDetta selects from sources, then
`dotnet test` builds. With generators affected, the selection needs the consumer built from the
current sources:

- `td test` builds first (`dotnet build`), selects, then runs `dotnet test --no-build`. CI builds anyway,
  so the build moves, it is not added. The GitHub Action does the same.
- `td affected` without a build keeps today's answer, and says so in the reason ("build first for a
  narrower answer").
- Only when an opaque generator is affected; otherwise nothing changes.

### Cost in the evaluation

The evaluation selects from sources too, before it builds. A mutant that affects a generator would need
the consumer built with the mutant's source edit before selecting: about 20 s for Fluid, 1 to 2 minutes
for protobuf-net, per such mutant. With stored outcomes that is the only cost of those rounds, so a
round of protobuf-net goes from about a minute to one or two hours. Acceptable for the precision rounds;
a cheaper path (running only the compiler on the consumer) can come later if it hurts.

## LibGit2Sharp: an output run as another process (needs your decision)

On 2026-10-03 you chose to treat every `ReferenceOutputAssembly="false"` reference like a generator,
because a copied and executed output cannot be told apart from a build-order-only one. Two narrower
answers, both precise only with recording:

- **Record which test classes start a process** (the tracker hooks `Process.Start` and notes the
  executable). Classes that started the referenced project's output are selected; with no class
  starting it, the reference is build order only. Close to the run-time file reads design
  (docs/design/runtime-file-reads.md), which also waits for your decision (v1 or later).
- **Name the output in test code**: select the classes whose source names the output's assembly name.
  Cheaper, but a path built another way (a constant from MSBuild, a config file) would be a miss. Not
  recommended.

## As decided and built (2026-10-05)

- **Only for generators in the repository.** The graph keeps, besides `OpaqueReferences`, the
  `GeneratorReferences` (`OutputItemType="Analyzer"`) from the MSBuild evaluation (evaluator version 4)
  and from a literal attribute in the project file. A package generator changes only with its version.
  `ReferenceOutputAssembly="false"` alone stays opaque: the consumer runs completely, as before.
- **One framework only, for now.** `dotnet test` runs every framework of a test project, and a
  generator may write other code for another one (a polyfill the framework lacks), while the map holds
  the generated code of the framework it was recorded for. A test project with several frameworks
  (its map names one, or MSBuild gives several symbol sets) still runs completely when a generator it
  sees is affected. Next: record the generated code of every framework and compare each.
- **Built only when it decides something.** For every test project downstream of a consumer of an
  affected generator: one that runs completely anyway is not built; one without a map, or without a
  reader (map drift, evaluation drift modes), runs completely as before. Otherwise it is built (or an
  up-to-date build is used) for the framework its map was recorded for (`CoverageMap.Framework`, new),
  and the generated files of its output are read as the recorder reads them (`GeneratedSourceReader`,
  shared). Files are matched by generator assembly, generator type and hint name, so another
  configuration or machine matches too.
- **The result.** Generated members that differ are changes of the consumer, traced like source
  changes (coverage first); the current generated files replace the recorded ones in the index. The
  same output changes nothing. Every step is logged: why a project is built, what differed, why a
  project was not built.
- **A build that fails ends the analysis** with the compiler's errors: `dotnet test` would fail the same
  way, and CI should not go on with code that does not compile. A build without a portable PDB, or no
  current build for `td affected`, runs the project completely and says why and how to fix it.
- **`td test`** builds with the configuration it passes to `dotnet test` (`-- -c Release`). Compared with
  before it never costs more: before, the same consumer was built and all its tests ran.
- **`td affected`** reads an existing build newer than the sources; `--build` builds.
- **Evaluation.** The selection of a change that reaches a generator builds the change in a copy of the
  clone (`GeneratorWorkspace`), so the clone's binaries the mutants run never hold it, and stores the
  read with the outcomes. The user's idea for a cheaper path: write the mutants into the generator's
  assembly behind switches, as for the tests, and rerun only the consumer's compile per mutant with the
  switch set (no shared compiler server, forced compile). To do once the simple path is measured.

## What the evaluation can and cannot show (2026-10-05)

Fluid with stored outcomes: 98.0% → 22.5% of classes selected, no missed or inconclusive mutant; 98
of 100 changes built in the generator copy, all with the same generated output (about 6 s each, once).

The synthetic change TestDetta sees is a source edit that keeps behaviour (so a change exists); the
mutant itself is written into the test outputs' assemblies. A generator never runs the mutant, so in
the evaluation its output never changes: "no missed mutant" shows that the rest of the selection still
catches every failure once the generator rule is gone, not that a changed output is traced well. That
path is covered by `GeneratorOutputTests` only. The evaluation has never produced a failure caused by a
generator either: the old rule ran those projects completely, so it could not miss one, and nothing
measured it. Measuring it needs the user's switch approach: the mutant also written into the
generator's assembly, the consumer compiled with the switch on, and the tests run against that build.

## Measurement before shipping

- Generators: `--explain` and normal synthetic rounds on Fluid, Mediator, protobuf-net, Mapperly,
  GraphQL and the other generator repositories (Vogen, Immediate.Handlers, CommunityToolkit), with no
  missed mutant. Stored outcomes make the miss check cheap.
- Expected: Fluid and Mediator from about 90% to their coverage answer (Mediator's coverage-only
  extra is about 5%), protobuf-net from 87% to about 15%, Mapperly and GraphQL smaller gains on top of
  the generated-type fix.
