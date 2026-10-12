# Loaded outputs: who loaded an assembly

Status: approved by the owner 2026-10-10 (high-share repositories, step 5: NLog; options "a" and "i"); compile
references whose types nothing names approved 2026-10-11 (step 5b).

## Problem

Two rules narrow a test project to the classes that loaded a project's output:

- **Opaque outputs** (docs/design/runtime-file-reads.md): a changed project referenced without compiling against it
  (`ReferenceOutputAssembly="false"`) runs only the classes whose map recorded starting or loading its output.
- **Reflection-found types in a loaded output** (new, step 5): NLog's `ManuallyLoadedExtension.ManuallyLoadedTarget` is
  a type nothing names, so a change that reaches it ran all 178 classes of `NLog.UnitTests` on 16 of 100 sampled
  changes. `NLog.UnitTests` references the extension only for build order; `ExtensionTests` loads it by path through
  NLog's configuration (`<add assemblyFile='…'/>`). The type can reach a test process only when that output is loaded.

Both rest on `CoverageMap.OutputUses`, which the recorder fills from the calls it wraps in repository code
(`Assembly.Load*`, `LoadFile`, `AssemblyLoadContext.LoadFromAssemblyPath`, `Process.Start`, …). A load it does not
wrap is invisible: by third-party code (a plugin host, a DI scanner; third-party assemblies have only their file reads
wrapped), by the runtime (a dependency, `Assembly.Load(bytes)` has no name to record), or before the tracker started.
A map would then say that no class loaded the output, and the rules would run none.

## Design

**Recording.** The tracker listens to `AppDomain.AssemblyLoad` and looks at every assembly already loaded when it
starts. An assembly loaded with no wrapped call naming it before (any class, the whole process) is listed in
`outputLoadsUnseen` (simple and file name). The recorder merges the lists of every test process into
`CoverageMap.OutputLoadsUnseen`; a process that wrote none makes it null. `AppDomain.GetAssemblies` is wrapped too: the
class that enumerates the loaded assemblies is a user of each one loaded at that moment, recorded as a scan
(`scan:<name>`, `CoverageMap.OutputScanPrefix`, 2026-10-11). Only the rules for outputs found at run time count scans
(opaque outputs and `AnswerByLoaders`), as a plugin host finds a plugin's types among the loaded assemblies. The rule for
a compiled-against output loaded by its path (`AddLoadedOutputUsers`, a public API test reading the declarations) does
not: every class that names a project loads it, and repository code scans for its own converters while initializing.
Loads by name (`Assembly.Load(name)`, `AssemblyLoadContext.LoadFromAssemblyName`, `ExecuteAssemblyByName`) in code
that also runs outside the tests are recorded the same way (`name:<name>`, `CoverageMap.OutputNameLoadPrefix`, through
`TestDettaTracker.OutputLoadedByName`, 2026-10-11): Carter's module catalog loads every library of the dependency
context by name to find its modules, and the compiled-against rule ran 8 of Carter.Tests' 12 classes on every change
(share 47.2 %; 17.4 % recorded apart, no miss in 64 failing mutants; CliWrap and Autofac unchanged). The same call in a
test, or in a helper only tests use, stays a load like one by path: AfterApply's architecture tests load each layer by
name (`Assembly.Load("AfterApply.Application")`) and check with NetArchTest which assemblies its method bodies depend
on, so any change to the layer can fail them though none of its code runs there. Which code runs outside the tests is
read from the project graph (`ProjectGraph.RunsOutsideTests`): a project nothing references (an application, a
sample), or one a non-test project that runs outside them references. A library only its tests reference serves the
tests alone, and its loads by name keep selecting: the analysis cannot tell a catalog from an inspection there. A load
by name still counts as a wrapped load for `outputLoadsUnseen`, since the call loads the assembly; a scan does not.
Left open: product code that loads an output by name and hands the assembly to a test that inspects it is counted
as a catalog; an inspection through `typeof(T).Assembly` loads nothing a call can see ([backlog](../backlog.md)).
Counted there, Newtonsoft.Json's scan while initializing ran every class on every change of a fresh recording: 100 %, 40.5 % once scans were apart (0 of 69 missed); Autofac 42.3 % either way (its compilation test reads Autofac.dll by path, a file read the rule still counts; 0 of 73).

`CoverageMap.KnowsLoadsOf(names)` is true when the map tracked loads and none of the output's names was loaded unseen.

**Opaque outputs (option i).** A map without the list (recorded before loads were tracked) or with the output in it
runs the test project completely: "its map was recorded before TestDetta tracked assembly loads (record it again to
narrow)", or "a test process loaded it without a call the recording saw". Recording again brings the narrowing back.

**Reflection-found types (`TestSelector.AnswerByLoaders`).** For a reflection-found type the maps cannot answer, when
every test project that depends on its project references it directly (not as an analyzer) and through no other
project, either without compiling against it or compiling against a project whose types no code outside it names
(`SourceIndex.IsAnyTypeNamedOutside`, step 5b), each such test project runs the classes that loaded the output
(`AddUsersOf`, as for opaque outputs: a load outside the tests, while initializing or in a fixture's setup runs the
project). A test project whose map is untrusted, lacks output uses, did not track loads or saw the output loaded unseen
runs completely. Any other shape (a test project compiles against a project whose types are named somewhere, or reaches
it through another project) keeps the whole downstream running, as before.

**Compile references nothing names (step 5b).** `NLog.UnitTests` compiles against `NLogAutoLoadExtension`, but only so
the build copies it next to the tests: NLog finds it by scanning that folder and loads it by path, through a call the
recording wraps (NLog is repository code). A change reaching `NLogAutoLoadExtension.AutoLoadTarget` ran all 178 classes
on 16 of 100 sampled changes after step 5. When no code outside the project names one of its types, the compile
reference adds no way in: code that named a type would make the runtime load the output, a load no wrapped call saw,
which runs the project completely. A shared simple name counts as a use, which only keeps more tests.

## Remaining risk (owner, 2026-10-10: to discuss later)

An assembly loads once per process. When a class loads it through a wrapped call and a later class uses it without
loading it, through third-party code that hands out the loaded assembly or through a third-party scan of the loaded
assemblies, the later class is not recorded. That class depends on the earlier one having run first in the same
process, the order-dependent shape of docs/design/order-dependent-state.md. Scans in repository code are covered by
the `GetAssemblies` wrapper; `AssemblyLoadContext.Assemblies` and `AssemblyLoadContext.All` are not wrapped yet.

**Reproduced (2026-10-12, `OrderDependentLoadTests`, red on research/order-dependent-loads).** `ALoaderTests` loads a
plugin referenced only for build order (`Assembly.LoadFrom`, wrapped); `BShapeTests` runs after it, finds the plugin
through `AssemblyLoadContext.Default.Assemblies` and checks that its exported types are sealed, running none of their
code. Adding an unsealed type selects only `ALoaderTests` ("read a changed file"), and a full run fails `BShapeTests`.
The miss needs a test that depends on the output's shape (its types, attributes), not on its code: a class that runs
the output's code is in its coverage. The mutants of the evaluation change behaviour only, so the corpus cannot show
this kind of miss; its 0 misses say nothing here.

Census of the corpus (headroom run of 2026-10-12, 46 of 59 repositories so far): the rules that narrow to loaders
act with at least one class in 34 test projects of 16 repositories. Most are Roslyn analyzer and generator tests and
public API tests that read the output as a file (`MetadataReference`, Cecil), which every class does anew, so the
once-per-process shape does not apply. Plugins loaded into the process: NLog's extensions (every class that ran
their code is a recorded loader), Autofac's `LoadContextScopeTests` (one class), Serilog's `TestDummies` (one
recorded loader, four more classes ran its code; to look at).

**Signal (owner, 2026-10-12; branch feat/enumeration-scans, being measured).** The calls that enumerate loaded
assemblies or one assembly's types (`AppDomain.GetAssemblies`, `AssemblyLoadContext.Assemblies`, `Assembly.GetTypes`,
`GetExportedTypes`, `DefinedTypes`, `ExportedTypes`; `FileReadCalls.Scanners`) go to their own probe,
`TestDettaTracker.OutputScanned`, with the object they are called on, in repository and in third-party code. Every
assembly it stands for is recorded for the bucket as a scan (`scan:<name>`), which the rules for outputs found at run
time count. The call happens in each class that inspects, so the order no longer decides; it only adds users.
`AssemblyLoadContext.All` lists contexts, not assemblies: code that goes on to their `Assemblies` is seen there.
A separate probe because the first argument of a load (`AssemblyLoadContext.LoadFromAssemblyPath`) is a context too,
and a load is no scan of everything the context holds. `OrderDependentLoadTests` covers both: the inspection in the
test's own code and through a third-party plugin host (a library compiled in the test); with the third-party calls
left unwrapped the second fails. Still open: `Module.GetTypes`, a `Type` kept from an earlier class (a static cache of
plugin types), and reflection over a type found by name (`Assembly.GetType(name)`).

## Measurement (2026-10-10/11, fresh recordings, one repository at a time on an idle machine)

| Repository | Before | Step 5 | Step 5b | Missed |
|---|---|---|---|---|
| NLog (100 changes) | 31.0 % | 31.3 % | 31.2 % | 1 of 55, also with main's binary |
| LibGit2Sharp | 26 % | 26 % (once the recording's shards had their own temporary directories) | – | 0 of 50 |
| CliWrap | 62 % | 62 %, every change equal | – | 0 of 43 |

No change widened. Step 5 moved the 16 NLog changes that ran all of `NLog.UnitTests` through
`ManuallyLoadedExtension` onto `NLogAutoLoadExtension`; step 5b answers those too, but the changes are central code
(`LogFactory.Flush`, `ConfigurationItemFactory`) that 176 of the 178 classes run, so the share hardly moves. The
reflection fallback had hidden a real, wide impact there. The NLog miss (`AppEnvironmentWrapper.EntryAssemblyLocation`,
`FileTargetArchiveFileNameTest` failing) is the order-dependent shape of docs/design/order-dependent-state.md: NLog's
configuration factory and auto-loading run once per process, tests such as `ExtensionTests` reset them, and the class
that comes first is the one recorded. It happens with main's binary on the same commit, so it is not this change's.
