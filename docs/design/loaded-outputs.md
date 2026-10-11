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
class that enumerates the loaded assemblies is a user of each one loaded at that moment.

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
