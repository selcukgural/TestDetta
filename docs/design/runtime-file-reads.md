# Files tests read at run time

Status: file reads implemented 2026-10-05 (see "As built"); process starts not yet. In v1 (user decision, 2026-10-05).

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
- **Not yet**: the drift bridge's literal search for classes changed since the map, process starts, and
  the corpus mode that changes data files. Reads by third-party code (snapshot libraries) stay unseen;
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
