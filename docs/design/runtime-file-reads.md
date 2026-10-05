# Files tests read at run time

Status: proposed 2026-10-05; in v1 (user decision, 2026-10-05), after the switch-based evaluation of generator changes.

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
