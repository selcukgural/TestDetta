# Non-code files inside a project: run their readers, not the project

Status: design 2026-10-10. Approved by the owner the same day: the third-party read gap is closed
by instrumentation (option a below), and a CLI option lets the developer choose the rule, with a
default set by measurement. Analyzer-only package bumps are a separate proposal (last section).

## Problem

A changed file inside a project folder that is not C#, Visual Basic or F# code runs the owning
project and every project that depends on it in full (`TestSelector.FindProjectLevelChanges`: "is not
C#, Visual Basic or F# code"). The rule is right for a `.resx`, a copied `appsettings.json` or a
`.proto` file, which change what the build produces or what the tests read. It is wrong for most
`.md` files, a `CHANGELOG`, a `.gitignore` or an editor file, which change nothing a test can see.

Measurement 1 of incremental recording (docs/design/incremental-recording.md) showed the cost: no step
was partial, largely because `Src/Library/changelog.md` changes in almost every fastendpoints commit and
`docs/*.md` in meziantou-analyzer. The same rule makes pull requests that touch a project's README run
the project's tests in full.

A second, older gap sits next to it: a non-code file owned by project `P` that a test project `T`
reads, where `T` does not depend on `P` (integration tests that read `../../src/App/seed.json`), selects
nothing in `T` today. `FindFileReaders` only looks at files that no project owns.

## When can a non-code file change a test result?

1. **The build uses it.** It is compiled or embedded (`Compile`, `EmbeddedResource`, `Resource`), copied
   to the output (`Content`, `None` with `CopyToOutputDirectory`), given to a source generator
   (`AdditionalFiles`), turned into code by a tool (`Protobuf`, a custom item type a target processes),
   or read by a target (`ReadLinesFromFile`, `Exec`, a task that walks `@(None)`).
2. **A test reads it at run time**: repository code (a test, a fixture, the library) or a library the
   tests use (a snapshot library such as Verify reads `*.verified.*` next to the test source; a docs
   test library reads Markdown).
3. **Neither.** Then the change cannot reach a test.

## Signals

**The build (case 1)**, from the MSBuild evaluation TestDetta already runs (`MsBuildProjectEvaluator`):

- The evaluation target reports, besides today's items, which files are only `None` items without
  `CopyToOutputDirectory` (kind `Inert`), and which are items of a build type (kind `Built`): `Compile`,
  `EmbeddedResource`, `Content`, `AdditionalFiles`, `None` with a copy, and the item types the SDKs and
  common workloads process (`Resource`, `Page`, `ApplicationDefinition`, `Protobuf`, `TypeScriptCompile`,
  the `Maui*`, `Android*` and `Bundle*` items, `EditorConfigFiles`).
- The XML of the project file and every repository file it imports is scanned (the graph already reads
  it). An item element of a type that is not known to be inert (`None`, the reference and package items)
  whose `Include`/`Update` can match the file, any target that names the file, or any target that uses
  `@(None)` makes the file a build input of that project. A pattern with a property TestDetta cannot
  resolve counts as a match.

A file is **inert in project `P`** when `P` was evaluated, the file is an `Inert` item of `P` and of no
`Built` kind, and the scan finds no use. Anything else is a build input, as today.

**Run-time reads (case 2)**, from the coverage maps. Maps record the files repository code reads
(docs/design/runtime-file-reads.md). New: the recorder also wraps the read calls of the third-party
assemblies in the test output (option a):

- After the repository assemblies are instrumented, every other managed assembly in the test project's
  output directory (never a file outside it, so never the NuGet cache) that calls one of the wrapped
  read or directory methods is rewritten with the same `FileReadCalls` wrappers, and nothing else: no
  method probes, no ids, no process-start or assembly-load wrappers (a test framework loading the test
  assembly would otherwise look like a use of the test project's output).
- Skipped: the tracker, the startup hook and the test platform (`testhost`, `Microsoft.TestPlatform.*`,
  `Microsoft.Testing.*`, `xunit.runner.*`, `xunit.v3.runner.*`, `NUnit3.TestAdapter`,
  `MSTest.TestAdapter`, `Microsoft.VisualStudio.TestPlatform.*`), which read their own settings outside
  any test. Their reads would only add ambient reads, which run more.
- A read lands in the bucket of whatever is running, exactly like a read by repository code: the test
  class, a fixture, initialization, or ambient.
- An assembly that cannot be rewritten (mixed-mode, a write error) is restored and named in the map
  (`fileReadsUnseen`). The backups and `Restore` cover the new files as they cover the repository's.
- The map says that third-party reads were recorded: `fileReadsUnseen` is present, empty when every
  library's reads were seen. A map without it, or with any assembly in it, cannot vouch that a file was
  read by no one.

## Selection

New option `--non-code-files readers|project` on `td test`, `td affected`, `td record --incremental` and
shadow mode, and action input `non-code-files`:

- `project`: today's rule. A non-code file runs its project and the dependents in full.
- `readers`: a non-code file that is **inert** in its project is treated like a file no project owns:
  each test project whose map recorded file reads with `fileReadsScope: "all"` and no unseen assembly
  selects the classes that read the file or listed a folder above it; a read outside the tests, while
  initializing or in a fixture's setup runs that test project in full; a test project without such a
  map, among the owner and its dependents, runs in full. A file that is a build input keeps today's rule.

In both modes the readers of a project-owned non-code file in test projects that do not depend on the
owner are now added (the second gap above). That only selects more.

The run reports which mode decided and why: "README.md is not part of the build and no recorded test
read it (non-code files: readers)" in the project's reason, and "runs completely: changelog.md is not
C#, Visual Basic or F# code (non-code files: project)" otherwise.

**Default.** Chosen from the measurement below: `readers` if it misses nothing on the corpus,
otherwise `project` until the misses are understood. The developer's value always wins, from the command
line or the action input. The docs (CLI reference, "What runs everything") state the rule of each mode,
what `readers` relies on (the build items, the MSBuild files the repository holds, the recorded reads),
and what it cannot see (below), so a team can choose `project` when its build or tests read files in
ways TestDetta does not observe.

## Limits of `readers`

- A file read by native code, a child process that is not .NET, or a .NET API outside the wrapped list
  (`ZipFile.OpenRead`, `X509Certificate2(string)`, `XslCompiledTransform.Load(string)`) is not seen.
  These are the limits of file-read tracking today, which `readers` extends from unowned files to inert
  project files.
- A file read only by a test class that did not run while recording (failed, skipped on that platform)
  is unknown to the map; such classes already run whenever their project is affected.
- A build step in an SDK or package target that processes `None` items with a default glob of its own
  type is seen only if the type is in the known list. An unknown type defined outside the repository is
  the one residual case; the CLI docs name it and `project` avoids it.

## Measurement

- `DataFileSampler` also samples inert non-code files inside project folders (`EmptyFile` kind, with the
  `.md` exclusion lifted for them), so the eval's ground truth (the full run with the file emptied) checks
  `readers` directly: a failing test class that `readers` did not select is a miss.
- Corpus: the app tier and the incremental set (fastendpoints, polly, awesomeassertions, mapperly,
  NodaTime, meziantou) with `--data-files`, both modes; misses must be 0. Then the incremental
  measurement again: partial steps and seconds saved.
- Recording cost: the extra rewrite of third-party assemblies, per repository (seconds and number of
  assemblies).

## As built (2026-10-10)

- **Recorder** (`ThirdPartyAssemblies.Find`, `AssemblyInstrumenter.WrapThirdPartyReads`): every `.dll`
  under the test output, `runtimes/` included, except the repository's assemblies (every project's, so an
  executable copied in to run in another process stays as it is), satellite resource assemblies, the test
  frameworks, platforms, runners, adapters, their telemetry and coverage collectors (`xunit.*`, `nunit.*`,
  `MSTest.*`, `TUnit.*`, `Microsoft.Testing.*`, `Microsoft.TestPlatform*`, `Microsoft.ApplicationInsights*`,
  `coverlet.*`; first measurement: xunit.v3's testing-platform bridge listed the bUnit test output before any
  test, and the linked `xunit.runner.json` made that every file under `tests/`), and TestDetta's own. An assembly is opened in deferred mode
  and rewritten only when its member references name a wrapped read or directory method
  (`FileReadCalls.ReferencesReaders`); the rewrite adds the `<TestDettaFileReads>` wrappers, the tracker
  reference and the test host's core library reference (as for repository code), drops the ReadyToRun flag,
  and leaves the PDB alone. A mixed-mode image, or any write failure, is put back from its backup and listed
  in `InstrumentationResult.ReadsUnseen`. The map's `FileReadsUnseen` (`fileReadsUnseen` in the file, no
  format version change) is that list; `CoverageMap.KnowsAllFileReads` is true when the map has file reads
  and the list is empty. An incremental merge unites the two lists, and is null when either map is.
- **Evaluator** (`MsBuildProjectEvaluator`, version 6): kinds `Inert` (`None` without
  `CopyToOutputDirectory` or with `Never`) and `Built` (the copied `None` items and the build item types of
  the list above); `ProjectNode.InertItems` = inert under some framework and built under none. Cached
  evaluations of version 5 are evaluated again.
- **MSBuild scan** (`NonCodeFileUses`): every `.csproj`/`.fsproj`/`.vbproj`/`.props`/`.targets`/
  `.projitems`/`.proj` under the repository outside `bin`, `obj` and tool folders, read once per analysis
  and only when an inert candidate exists. Uses: the file's name anywhere in a file's XML after removing
  comments, `None` items and `Package*` elements (`PackageReadmeFile`, `PackageReleaseNotes`: pack
  metadata); an item element of a type that is neither a known non-file type (`PackageReference`,
  `ProjectReference`, `Using`, ...) nor one the evaluation reports, whose `Include` or `Update` can match
  the file (`$(MSBuildThisFileDirectory)` and `$(MSBuildProjectDirectory)` are resolved, any other
  property, item or metadata reference matches everything); a target that mentions `@(None`; an MSBuild
  file that is not valid XML.
- **Selection** (`TestSelector`): `FindProjectLevelChanges` takes a `ProjectLevelUses` (generator and
  output uses as before, plus the `NonCodeFileUses` under the readers rule). A non-code file inert in its
  owner becomes an `InertFile` instead of a fallback; `AddUnvouchedReaders` then runs completely every test
  project among the owner and its dependents whose map cannot vouch ("README.md is not part of the build,
  but it has no coverage map to tell which tests read it" / "its map was recorded before the reads of
  third-party libraries were" / "its map could not see the reads of X.dll"). `FindFileReaders` now takes
  the changed files no project owns plus every project-owned non-code file, under both rules, so readers in
  test projects that do not depend on the owner are selected too. A build use keeps the fallback, with the
  reason ("..., and src/Lib/Directory.Build.targets names it"). The drift bridge keeps the project rule.
- **Unchanged outputs** (`ProjectLevelUses.UnchangedOutputs`, found in the first measurement: bUnit's
  `src/bunit.generators.internal/README.md` ran every consumer of the generator completely): an affected
  project that only inert files reach, directly or through references, builds what it built before. Its
  generator consumers, the projects referencing it without compiling against it, and the tests that load its
  output are not run for it. Any other changed file or build-wide change makes its projects and their
  dependents changed.
- **Options**: `--non-code-files readers|project` for `affected`, `test` (and `--shadow`), `record`
  (`--incremental` plans with it) and `audit`, `AnalysisOptions.NonCodeFiles` with
  `AnalysisOptions.DefaultNonCodeFiles`, action input `non-code-files` (empty keeps the CLI default) in
  test, shadow and audit modes; shadow and audit reports repeat the rule in their reproduce commands.
- **Evaluation**: `--data-files n` empties n files outside every project, then n inert files inside
  project folders (`DataFileSampler.IsInert`, Markdown included), each sampled 80% from files a map read.

## Proposal, not decided: analyzer-only package bumps

A package version change in `Directory.Packages.props` runs every project that references the package.
For a package that has only analyzers (no `lib/` or `runtime/` assets, no source generator, no build
targets that change outputs), the bump changes warnings, not what the tests run; with warnings as
errors it can break the build, which `dotnet test` reports anyway. The signal is the package's asset
list in `obj/project.assets.json`. Narrowing here is a separate decision for the owner.
