# Project graph from MSBuild evaluation

Status: implemented 2026-10-02 (`MsBuildProjectEvaluator`, `ProjectGraphLoader.ApplyEvaluation`, `ProjectGraph.ResolveFile`). Replaces three interim fixes in "Known gaps behind shipped fixes":
project graph from XML (24df010), linked files whose path needs evaluation (ea5acb7), projects
sharing a directory (232ee1a, over-selection through `Compile Remove`).

## Problem

`ProjectGraphLoader` reads project files as XML. It cannot evaluate conditions, property
functions, item functions or `Remove`, so it guesses:

- References only MSBuild can compute (`$(RoslynVersion)` from a property function) become path
  patterns that match many projects; a test SDK without "Test" in its name is missed.
- Linked files with computed paths become patterns, or are claimed by nobody.
- Every project in a directory owns every file in it, so projects that split a directory with
  `Compile Remove`/`Include` (Meziantou's Roslyn-version projects) are all selected together.

Each guess was made safe by widening; each widening costs precision, and a wrong guess can still
miss a project.

## Measurements (2026-10-02, SDK 10.0.401, M-series Mac)

One `dotnet msbuild` process that evaluates every project through an injected target returning
its items (no restore, no build):

| Repository | Projects | Evaluations (inner builds) | Time |
|---|---|---|---|
| Meziantou.Analyzer | 19 | 19 | 0.7 s |
| OpenTelemetry | 82 | 314 | 3.1 s |

The outer build of a multi-targeting project has an incomplete `Compile` list (OpenTelemetry's
core project: 14 of 176 files), so every target framework's inner build is evaluated and the
items are united. Meziantou's property-function references and its company test SDK come out
right.

## Design

### Evaluation

- `IProjectEvaluator` in Sift.Core (`Evaluate(repoRoot, projects) → evaluated projects or null`),
  implemented in Sift.Coverage with `DotnetProcess`, so Core stays free of process code and tests
  can fake it.
- One process: `dotnet msbuild <driver.proj> -nologo -v:q -m -nodeReuse:false -restore:false`,
  where a generated driver calls the `MSBuild` task on every project with
  `ContinueOnError=true` and the global properties `CustomAfterMicrosoftCommonTargets` and
  `CustomAfterMicrosoftCommonCrossTargetingTargets` pointing to a generated `sift-graph.targets`.
  Driver, targets and output live in a temporary directory, never in the repository.
- The target returns, per project and per target framework (inner builds for `TargetFrameworks`):
  `IsTestProject`, `AssemblyName`, `TargetFramework(s)`, the full paths of `ProjectReference`,
  `PackageReference` names, and the full paths of the build items that affect the output
  (`Compile`, `EmbeddedResource`, `Content`, `None`, `AdditionalFiles`). Output is one tab-separated
  line per item, written with `WriteLinesToFile`, parsed by Sift.
- Global properties: none besides the hooks (the default `Configuration`). Items conditioned on
  `Release` are a known limit (see below).

### Merging into the graph

Evaluation is authoritative for what it answers; the XML reading stays for what it does not and
as the fallback:

- **References**: the evaluated project references (union over frameworks) replace the XML ones
  and the unknown-reference patterns.
- **Test projects**: a project is a test project when evaluation says `IsTestProject=true`, when
  an evaluated package reference is a test package, or when the XML reading marked it (package
  props that set `IsTestProject` are not imported without a restore, so the XML and package rules
  stay). Selecting a non-test project as a test project costs a no-op run; missing one loses tests.
- **File ownership** (`ResolveFile`): a file belongs to every evaluated project whose items include
  it. A file under a project directory that no evaluated project includes keeps today's rule
  (every project of the innermost directory), since targets can read files that are no item.
  Unknown item patterns and external items of an evaluated project are not used.
- **Assembly names** come from evaluation; `AssemblyNameUnresolved` only stays for projects that
  could not be evaluated.
- Imports (build inputs) keep the XML rule; evaluation does not report them reliably.

A project whose evaluation failed (missing SDK, an error in its files) keeps its XML node, and the
analysis logs it. When the process fails as a whole, or the SDK is too old for the hooks, the whole
graph is the XML graph, as today, with a warning.

### Cache

Evaluations are cached in `.sift/cache/graph/<key>.json` (and in memory for the process). The key
hashes: the evaluator version, the SDK version (`dotnet --version` in the repository root), the
contents of every MSBuild input Sift knows (project files, their XML imports, `Directory.Build.*`,
`Directory.Packages.props`, `global.json`, `NuGet.config`), and the sorted list of all file paths
in the repository outside `bin`/`obj`/`.git`, since globs depend on which files exist. Editing a
source file keeps the key (an evaluation reads no source file); adding, removing or renaming one,
or changing any project or build file, changes it. The synthetic evaluation, which analyses a
hundred edits per commit, evaluates once.

### Known limits

- No restore: items or `IsTestProject` that only package-provided props/targets add are missing;
  the XML and package rules cover the test flag, and package-provided items lie outside the
  repository.
- One configuration (the default one, normally `Debug`).
- Each evaluation runs the repository's MSBuild logic (property functions, SDK resolvers), as
  `dotnet build` does; Sift runs where the build runs.

## Corpus check (2026-10-02)

XML graph against evaluated graph on the 41 round-1 clones (`dotnet msbuild`, SDK 10.0.401): 0.6 to
2.2 s per repository. Every dropped dependency or file owner traced to a condition the XML reading
ignored: CommunityToolkit and Meziantou code fixers reference only their own Roslyn version,
GraphQL references its analyzer package only during restore (`'$(ExcludeRestorePackageImports)' != ''`),
Open-XML imports its source generator only with `IncludeSourceGenerator`, Polly and UnitsNet compile
polyfills only for `netstandard2.0`/.NET Framework. Gains the XML reading missed: OpenTelemetry 13
dependents and 28 linked files, Open-XML 83 linked test files, Swashbuckle 30 and protobuf-net 36
dependents, Meziantou its pack project. Not evaluated, so read as XML: legacy and mobile samples
(protobuf-net `assorted/`, NLog and MimeKit samples) and NSubstitute.Specs, which imports a file
that no longer exists. The first evaluation in a driver fails as a whole when one project fails, so
the driver evaluates one by one after such a failure.

## Tests

- Fake evaluator: merging rules (references replaced, union of test flags, ownership by items,
  innermost-directory fallback, failed project keeps the XML node, whole failure falls back).
- Real evaluation (Slow): property-function references (the Meziantou shape), `Compile Remove`
  split of one directory into two projects (only the including project owns a file), a linked file
  with a computed path, a multi-targeting project with a framework-conditioned compile item, a
  project that fails to evaluate next to one that works.
- Cache key: content edit keeps it, file added or project edited changes it.
- Corpus: Meziantou, OpenTelemetry and every repository of round 1 with no new miss; precision on
  Meziantou is expected to improve.

## Preprocessor symbols (2026-10-03)

The injected target also reports `DefineConstants` per target framework. In an inner build of an
SDK project it first depends on `AddImplicitDefineConstants`, so the SDK's framework symbols
(`NET8_0_OR_GREATER`, `NETSTANDARD2_0`) are included. Distinct sets land in
`EvaluatedProject.SymbolSets` and `ProjectNode.SymbolSets` (evaluator version 2).
`DeclarationWalker.ParseVariants` and `CodeChangeDetector` add one parse per set, reduced to the
symbols a file's conditions name, next to the no-symbol and all-symbols parses. That way code under
a mix of symbols (`#if A && !B`) is indexed and its changes are attributed.

## Opaque references (2026-10-03)

The target also reports `ProjectReference` items with an `OutputItemType` or
`ReferenceOutputAssembly="false"` as `OpaqueReference` lines (evaluator version 3):
`EvaluatedProject.OpaqueReferences`, `ProjectNode.OpaqueReferences`. See
docs/design/opaque-project-references.md.
