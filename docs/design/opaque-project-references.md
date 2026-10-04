# Project references a project does not compile against

Status: implemented 2026-10-03. Found in the precision round (docs/design/reflection-types-with-coverage.md).

## Problem

Some `ProjectReference` items do not give the consumer an assembly to compile against and call:

- `OutputItemType="Analyzer"`: the referenced project is an analyzer or source generator. It
  runs inside the consumer's compiler, and what it generates becomes part of the consumer's
  assembly. protobuf-net's AotConformanceTests uses `protobuf-net.BuildTools` this way, and
  BuildTools compiles in all of protobuf-net.Core's and protobuf-net.Reflection's sources.
- `ReferenceOutputAssembly="false"`: build order only (protobuf-net.Core → BuildTools, to pack
  it), or an output the consumer copies and runs as another process (a CLI under test).

The graph treated both as ordinary runtime dependencies. A change to such a project then went
through name tracing and coverage. Neither can see it: no test names generator code, and no map
records code that ran in the compiler or in a child process. A generator change could leave the
consumer's tests unselected (a miss), and the reflection fallback hid this in protobuf-net.

The pattern is common: 20 of the corpus's 41 repositories have such references, among them every
source generator (Mapperly, Mediator, Vogen, Immediate.Handlers, CommunityToolkit).

## Design

- **Reading.** MSBuild evaluation reports the `ProjectReference` items with an `OutputItemType`
  or `ReferenceOutputAssembly="false"` as `OpaqueReference` lines. Evaluation sees metadata from
  `Update` items, item definitions and property functions, in every target framework (evaluator
  version 3; the disk cache keeps them). The XML reading, used when MSBuild cannot evaluate,
  reads the same metadata as an attribute or child element on the project's own references and
  on those its imports declare. A value only MSBuild can compute counts as opaque. An opaque
  reference that cannot be resolved makes every project its pattern may name opaque.
- **Graph.** `ProjectNode.OpaqueReferences` is a subset of `References`, so project-level impact
  is unchanged. `ProjectGraph.OpaqueConsumersOf` answers "who uses this project opaquely?".
- **Selection.** When a project is affected (changed, or downstream of a change, including sources
  it compiles in), each of its opaque consumers is a project-level change: the consumer and
  everything downstream of it run completely. The reason says
  `it uses X.csproj, which is affected, as an analyzer or source generator or without compiling against it`.
  This holds with or without coverage maps, and for drift: a drifted map whose drift reaches a
  generator is dropped like one whose drift changed a build file.

`ReferenceOutputAssembly="false"` without an analyzer is treated the same way. This is the safest
choice: build-order-only references cost precision, but a copied and executed output cannot be told
apart from one that is only built (user decision, 2026-10-03).

## Results

Round-2 maps, 100 mutants each, `testdetta-eval --explain` and `attribute.py`:

| Repository | Selected share before | After | Missed |
|---|---|---|---|
| CommunityToolkit | 17.1% | 17.1% | 0 |
| protobuf-net | 89.5% | 89.5% | 0 |

On these two repositories the safety costs no precision. The projects it runs completely were
already running completely for other reasons, and the reason now names the real one.

## Not covered

- Analyzers and generators from packages (`PackageReference`): a version change is a package
  change and handled as one. Their code is not in the repository.
- A consumer that reads another project's output by path without any reference (a test that runs
  `../../src/Tool/bin/...`). The graph cannot see it.
