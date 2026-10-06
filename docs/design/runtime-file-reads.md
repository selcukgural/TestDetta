# Files tests read at run time

Status: file reads implemented 2026-10-05, process starts and assembly loads 2026-10-06 (see the "As built" sections). In v1 (user decision, 2026-10-05).

## Problem

A changed file outside every project folder belongs to no project and selects nothing
(`ImpactAnalyzer`, unmapped files). That is right for a README or a workflow file, and wrong when tests
read the file:

- **Listed as an item** (`<None Include="..\..\testdata\**" />`, a `Content` link, an item a
  `Directory.Build.props` adds): already handled. MSBuild's evaluation reports the item's full path, so
  the file belongs to the project and a change runs the project (`MsBuildProjectEvaluator`, item owners in
  `ProjectGraph`). Deleting such a file was missed until 2026-10-05 (5ce31e1).
- **Read by a path built in code**, copied by a custom target or a script, or under a snapshot folder no
  item lists: nothing selects the tests. A change to such a file can be missed.

"Select more, never less" says these must select their readers; "selection must stay narrow" forbids
running everything on any unmapped change. The precise signal is which test classes read which files.

## Design: record file reads with the coverage

**Instrumenter.** The recorder already rewrites calls to framework methods it must observe
(`ConcurrentDictionary.GetOrAdd` and other cache fills, `AssemblyInstrumenter`). In the same way, calls in
the recorded assemblies to System.IO, System.Xml and System.Xml.Linq methods and constructors that take a
file path (`File.*`, `FileStream`, `StreamReader(string)`, `FileInfo`, `XDocument.Load`,
`XmlReader.Create`, `File.Exists`) go through a generated wrapper with the same signature that reports the
path, then calls the original. `Directory.Get*`/`Enumerate*` report a listed directory.

**Tracker.** `TestDettaTracker.FileRead(path)` resolves the path at call time (the current directory
matters) and adds it to the current bucket with the rules `Hit` uses: test class, fixture, data source or
ambient code.

**Map.** An optional `FileReads` section per class, fixture and ambient bucket: repository-relative paths
and listed directories. Paths outside the repository are dropped (no user names in cached maps), and so is
`.testdetta/`. A read under `bin/` or `obj/` is mapped back to the repository file with the same name and
content hash, which also catches custom copy steps. A map without the section predates tracking and can
answer nothing about reads.

**Selection.** An unmapped changed file selects exactly the classes that read it (or listed its
directory), with evidence such as "reads testdata/x.json"; a fixture read selects the fixture's users; an
ambient read runs the test project; a file nobody read stays ignored. In the drift bridge, classes whose
code changed since the map get a cheap fallback: a literal search of their source for the file's name.

**Output.** Unmapped files are split into "read by N test classes" and "read by no recorded test
(ignored)".

## Cost and limits

About 640 product lines and 300 test lines (instrumenter wrappers, tracker, recorder, map format,
selection, output), the design's docs page in five languages, and a corpus mode that changes data files
(empty or delete a few unmapped files per repository, one full run as ground truth).

Not seen: reads by third-party libraries (a later step could rewrite their IO calls too), child processes
and native code, and reads by a computed path from code the map does not cover. All of this is strictly
more than today, which selects nothing for these files.

## Also: outputs referenced without compiling against them

A `ReferenceOutputAssembly="false"` reference runs its consumer completely when it is affected: the
tests may run its output as another process (LibGit2Sharp's `NativeLibraryLoadTestApp`, 100% of classes
selected), or it may only order the build (protobuf-net: `protobuf-net.Core` references
`protobuf-net.BuildTools` this way, and since almost every test project depends on Core, 77.7% of classes
run). Recording which test classes start a process, and from which output, is the same instrumentation
as file reads (`Process.Start`, `ProcessStartInfo`): classes that started the output are selected, and a
reference no test started or read is build order only.

## Meanwhile

The docs ("What runs everything", "Files that are ignored") tell users to list such files as items of
the test project; a change or a deletion then runs the project.

## As built (file reads, 2026-10-05)

- **Instrumenter** (`FileReadCalls`): every call in an instrumented module to one of a fixed list of
  methods and constructors whose first parameter is a `string` path (`File.ReadAllText` and its
  kin, `File.Open*`, `File.Exists`, `File.Copy`, `FileStream`, `StreamReader`, `FileInfo`,
  `XDocument.Load`, `XElement.Load`, `XmlReader.Create`, `XmlDocument.Load`; directories:
  `Directory.Get*`, `Enumerate*`, `Exists`, `DirectoryInfo`) becomes a call of a generated static
  wrapper in `<TestDettaFileReads>` with the same arguments and result, which reports the path and
  makes the original call. No stack analysis, any argument count.
- **Tracker**: `FileRead`/`DirectoryListed` resolve the path against the current directory and add it
  to the bucket a hit would go to; inside a static constructor or cache fill, to a separate
  initialization set. Results gain `fileReads` (classes, fixtures, ambient, initialization).
- **Map**: optional `fileReads` section, no format change; a map without it knows no reads.
  `FileReadPaths` keeps repository paths only (outside, `.git/` and `.testdetta/` dropped); a copy under
  `bin`/`obj` stands for the tracked files with its name and content (its name alone when no content
  matches), a copy that does not exist for nothing, and a listed output folder for the files in it and
  the folders they came from.
- **Selection** (`TestSelector.FileReads`): a changed file no project owns selects the classes that read
  it or listed a folder above it (decision kind `FileRead`, evidence "read <file> (map <commit>)"), added
  to what the code changes select. A read outside the tests, while initializing, or **in a fixture's
  setup runs the whole project** (unlike the design above: the fixture's users need the source index of
  a project the changes may not otherwise reach). Output: "N changed file(s) outside any project: M read
  by recorded tests, K read by none (ignored)".
- **Evaluation**: `--data-files <n>` (eval and corpus runs) empties n tracked text files outside every
  project after the member changes, most of them files some map recorded a read of
  (`DataFileSampler`, mutant kind `empty-file`, never part of `all`). The full run with the file emptied
  is the ground truth, as for member mutants; `EvalDataFileTests` runs it end to end.
- **Not yet**: the drift bridge's literal search for classes changed since the map, and process starts. Reads by third-party code (snapshot libraries) stay unseen;
  the docs say so.

## Open question for the owner: process starts and build-time tools (2026-10-05)

Narrowing the `ReferenceOutputAssembly="false"` rule with runtime tracking (process starts, and
`Assembly.LoadFrom` as a file read) sees what tests do with the reference's output at run time, but not
what the build does with it: a tool project that a custom target runs during the build (`Exec`, an
MSBuild task) to write code or files into the consumer. A change to such a tool changes the consumer
without any test starting it, so "no test started or read it, so it is build order only" could miss.
Options:

1. Narrow only when the consumer's build runs nothing of the reference: no target of the consumer (or
   of an import it uses) names the reference's output or project, checked from the MSBuild evaluation.
   Precise for protobuf-net (BuildTools is listed for build order only), but the check reads targets
   and may need care for wildcards and imported targets.
2. Narrow, and compare what the build writes, as B does for generators: build the consumer before and
   after and compare its intermediate outputs (generated `.cs` files under `obj`). Safe, costs a build
   per affected consumer, like B.
3. Leave the rule as it is (whole consumer) and narrow only through the runtime reads of the
   consumer's tests where the reference is an executable they start (LibGit2Sharp's test app), which
   still needs 1 or 2 to rule out a build-time use.

Recommendation: 1 first (cheap, covers protobuf-net and LibGit2Sharp), with 2 as the fallback when the
check finds a target that uses the output.

What the two repositories do (checked 2026-10-05):

- protobuf-net.Core references BuildTools only to have it built for the package: the output is named
  by `None` items with `Pack="true"` (and BuildTools' props file, renamed into the package). No target
  runs it. Option 1 must therefore ignore items whose only use is packing (`Pack="true"`).
- LibGit2Sharp.Tests references NativeLibraryLoadTestApp (x86, x64) only when `TargetFramework` is
  `net472`, with `OutputItemType="TestAppExe"`; its own target `CopyTestAppExes` copies the exe next to
  the tests, which start it. On macOS net472 is not built, so the 100% selected there needs its own look
  (the condition may not reach the graph, or another rule runs the project).

## Decided: outputs referenced without compiling against them (owner, 2026-10-06)

The owner chose options 1 and 2 above. Checked against the corpus before building: 17 of the
repositories with a clone on disk have such a reference. Most are analyzers and source generators
(already compared by building, B); the rest are packing only (protobuf-net, Meziantou), a test app the
tests start (LibGit2Sharp), AOT and smoke projects (Refit, protobuf-net, YamlDotNet, FastEndpoints), and
one build-time generator target (Open XML SDK). The rule below names no repository.

**When the referenced project R is affected and a consumer C references it this way:**

1. **Does C's build use R's output?** (option 1, from the MSBuild evaluation, no build). It does when
   the reference has an `OutputItemType` other than `Analyzer` (the output becomes items that some target
   consumes), or when a `<Target>` in C's project file or a repository `.props`/`.targets` file that C
   imports names R (its project file name, its assembly name) or iterates `@(ProjectReference)` /
   `@(_ResolvedProjectReferencePaths)`. Items outside targets are classified by type: `Compile`,
   `EmbeddedResource`, `AdditionalFiles` and `Analyzer` naming R's output are a build use; `None` and
   `Content` (packing, copying) are not, since copying only makes the output available at run time.
2. **If the build uses it** (option 2): C is built, as for a changed generator, and the documents it
   compiled that git does not track (PDB document checksums) and its embedded resources are compared
   with the recording. Different: C changed as a whole (today's rule). Equal: the build only moved R's
   output around (LibGit2Sharp copies the test app next to the tests), so go on with 3.
3. **Run-time users.** In every test project with a map below C, the classes that started R's output as
   a process, loaded it as an assembly, or read a file the build put into the output that git does not
   track are selected. A reference no test used and no build step used is build order only and selects
   nothing. A use outside the tests, while initializing, or in a fixture's setup runs the whole project,
   as for file reads.

**Recording the run-time use.**

- In-repository call sites, like file reads: `Process.Start` (all overloads, and the instance `Start()`,
  which reads `StartInfo`), `Assembly.LoadFrom`/`LoadFile`/`Load(string)` and
  `AssemblyLoadContext.LoadFromAssemblyPath`. The tracker keeps the started file and every argument
  that looks like a path, by name.
- Starts made by third-party code (CliWrap, SimpleExec, Testcontainers) have no call site in the
  repository. The test host's environment gets a startup hook (`DOTNET_STARTUP_HOOKS`, appended to any
  the team set): every .NET process started from a test reports its entry assembly to the tracker's
  results with the bucket that was running when it started (the tracker keeps the current bucket in an
  environment variable the child inherits). Native and .NET Framework executables started by
  third-party code stay unseen; the docs will say so.
- Map: a new optional section `outputUses` (classes, fixtures, ambient, initialization: file names), so
  an older map knows no uses and keeps today's rule (whole consumer).
- Matching is by name: a recorded name whose last path segment, without `.dll`, `.exe` or `.csproj`,
  equals R's assembly name or project file name.

**Validation.** Red tests first on a sample repository (a helper exe started from a test, a packing-only
reference, a target that copies the output, a target that generates code from it), then the corpus
quick check on the repositories that have such references, with `--focus` on members of the referenced
projects.

## As built (outputs, 2026-10-06)

- **Instrumenter** (`FileReadCalls`): calls of `Process.Start` (every overload and the instance
  `Start()`), `Assembly.LoadFrom`/`UnsafeLoadFrom`/`LoadFile`/`Load`, `AssemblyLoadContext.LoadFrom*`
  and `AppDomain.ExecuteAssembly*` go through wrappers that pass their first two arguments to
  `TestDettaTracker.OutputUsed`. It keeps names: the last path segment of every path in a file name,
  argument list, working directory, `ProcessStartInfo`, `Process` or `AssemblyName`, before a comma,
  without `.dll`/`.exe`/project extensions.
- **Startup hook** (`TestDetta.Coverage.StartupHook`, netstandard2.0, so any .NET child can load it):
  `td record` passes its path in `TESTDETTA_STARTUP_HOOK`; the tracker appends it to
  `DOTNET_STARTUP_HOOKS` of the test host only (not of `dotnet test` or MSBuild) and keeps
  `TESTDETTA_BUCKET` at the bucket entered last (`a|` with tests in parallel). Each .NET child writes
  one line (bucket, entry assembly, command line) to `process-starts-<shard>.tsv`; failures are
  swallowed. Not seen: native and .NET Framework programs started by third-party code (documented in
  the limitations page), and starts inside a static constructor or cache fill through third-party code
  (they count for the bucket, not as initialization).
- **Map**: optional `outputUses` (the names, shaped like `fileReads`; reads of untracked files under
  `bin`/`obj` add their names too) and `buildInputs` (per project in the test output: untracked compiled
  documents by file name with their PDB checksums, embedded resources with SHA-256; the SDK's assembly
  info, assembly attributes and global usings files are left out).
- **Recording scope changed**: only the assemblies of projects the test project compiles against
  (transitively, without references that do not compile against) are instrumented. A copy of another
  project's executable in the test output used to be instrumented and broke when a test ran it.
- **Selection** (`TestSelector.AddOutputUsers`, `BuildOutputUse`, `BuildInputsDiffer`): as decided above.
  A use outside the tests, while initializing or in a fixture's setup runs the whole project; a map
  without `outputUses` runs it; a multi-targeted test project whose build uses the output runs it
  (builds are not compared per framework yet); map drift keeps today's rule.
- **Tests**: `OutputUseRecordingTests` (repository call, reflection standing in for a third-party
  starter, assembly load), `OutputUseSelectionTests` (12 cases of the rule), `OutputUseBuildTests` (a
  build that copies the output selects the starter; one that generates code from it runs everything).
