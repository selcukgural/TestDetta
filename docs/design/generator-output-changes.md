# What a generator change does to its consumers

Status: proposed 2026-10-05, for discussion. Precision round, group B.

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

## Measurement before shipping

- Generators: `--explain` and normal synthetic rounds on Fluid, Mediator, protobuf-net, Mapperly,
  GraphQL and the other generator repositories (Vogen, Immediate.Handlers, CommunityToolkit), with no
  missed mutant. Stored outcomes make the miss check cheap.
- Expected: Fluid and Mediator from about 90% to their coverage answer (Mediator's coverage-only
  extra is about 5%), protobuf-net from 87% to about 15%, Mapperly and GraphQL smaller gains on top of
  the generated-type fix.
