# Framework and test framework support (2026-10-06)

Status: owner's goal (2026-10-06): TestDetta must work for legacy systems as well as current ones: code
under test from .NET Framework 3.5 and .NET Standard 1.0 on, every .NET Core and .NET 5+ version, and
every test framework, legacy ones especially. Step 1 (test hosts) is done, see "Done"; steps 3 and 4
(legacy project files and runners, Windows verification) are open.

## Three different things "support" means

1. **Code under test** (the libraries the tests exercise). TestDetta reads their C# (Roslyn, any
   language version), evaluates their projects with MSBuild, and at recording rewrites their compiled
   assemblies with Mono.Cecil. None of that depends on the target framework: a `net35` or
   `netstandard1.0` assembly is rewritten like a `net10.0` one. What matters is the process the code
   runs in during the tests (2).
2. **Test projects and their test host** (the process that runs the tests: `testhost.exe`,
   `testhost.dll`, or the test project's own executable under Microsoft.Testing.Platform). The tracker
   assembly (`TestDetta.Coverage.Runtime`) and every rewritten assembly run inside it. This is today's
   real limit.
3. **Test frameworks and runners** (xUnit, NUnit, MSTest, MSpec, TUnit; `dotnet test`,
   `vstest.console.exe`, framework consoles). TestDetta must know their test-class entry points (to
   credit hits to a class), run a filtered selection, and read their reports.

## What the code allows today

| Part | Today | Limit it sets |
|---|---|---|
| Tracker (`TestDetta.Coverage.Runtime`) | `net8.0` | Test hosts on .NET 8 or later only. .NET Framework and .NET Core 3.1-7 hosts cannot load it. |
| Startup hook (`TestDetta.Coverage.StartupHook`) | `netstandard2.0` | Any .NET Core 3.0+ child process; .NET Framework ignores startup hooks. |
| Loading the tracker | VSTest in-proc collector (`AssemblyLoadContext`), or a `deps.json` entry (`RuntimeDeployment`) | No `deps.json` in a .NET Framework output: recording fails with "deps.json does not exist" (seen on NodaTime's `net48`). |
| Recording framework without `-f` | First framework that runs here (.NET Framework only on Windows), since 2026-10-06 | Before, `net48` was taken on macOS/Linux and the project was not recorded. |
| Builds and test runs | `dotnet build`, `dotnet test`, `dotnet msbuild` | SDK-style projects only in practice. Legacy (non-SDK) `.csproj` with `packages.config` need `msbuild.exe` and `vstest.console.exe`. |
| Test entry points | xUnit (`Fact`, `Theory`, Skippable), NUnit (`Test`, `TestCase`, `TestCaseSource`, setup fixtures), MSTest (`TestMethod`, `DataTestMethod`, assembly/class init), TUnit (hooks), MSpec (contexts; adapter reports the namespace, handled) | Other attribute names (xUnit v1 `[Fact]` is the same name; NUnit 2 `[Test]` the same; MSTest v1 `[TestMethod]` the same) are covered by name, but each needs its runner and report format checked. |
| Filters | VSTest `--filter FullyQualifiedName~`, xUnit v3 `--filter-class`, TUnit `--treenode-filter` | `vstest.console.exe /TestCaseFilter:` is the same syntax; framework consoles (nunit-console, xunit.console) differ. |
| Reports | TRX (VSTest and MTP), NUnit XML when needed | Legacy consoles write their own XML formats. |
| The `td` tool itself | `net10.0` global/local tool | The build agent needs a .NET 10 runtime to run `td`, whatever the repository targets. That is fine on Windows and Linux agents. |

## Changes the goal needs

1. **Tracker for every test host (planned first).** Retarget `TestDetta.Coverage.Runtime` to
   `netstandard2.0`, one binary for .NET Framework 4.6.2+ and .NET Core 2.0+ hosts:
   - replace `System.Text.Json` (`Utf8JsonWriter`) with a small writer of its own (objects, arrays,
     strings, numbers), so nothing new is loaded into the test host;
   - `Environment.ProcessId` -> `Process.GetCurrentProcess().Id` (once); ranges -> `Substring`;
     `ProcessStartInfo.ArgumentList` read through reflection where it exists;
   - the in-proc collector resolves through `AppDomain.AssemblyResolve` where `AssemblyLoadContext`
     does not exist;
   - `RuntimeDeployment`: a .NET Framework output has no `deps.json`; copying the tracker next to the
     test assembly is enough there (probing of the application base).
2. **Instrumenter for .NET Framework modules.** It already builds probe references from each module's
   own core library (`mscorlib` for .NET Framework). To check: the `System.Private.CoreLib` reference
   it adds for Microsoft's coverage profiler must be skipped for .NET Framework modules; `net35`
   assemblies (CLR 2 metadata) run in a CLR 4 test host and may reference a `netstandard2.0` tracker.
3. **Legacy projects and runners (Windows).** Non-SDK `.csproj`: evaluate and build with `msbuild.exe`
   (Visual Studio Build Tools), run tests with `vstest.console.exe`, whose filter syntax and TRX match
   `dotnet test`. Needs a runner abstraction where `dotnet test` is assumed today (recorder, `td test`,
   eval). Framework consoles (nunit-console, xunit.console) only if the research shows customers rely
   on them without VSTest adapters.
4. **Verification.** .NET Framework cannot run on the macOS machine. A Windows CI job (manual or
   weekly, to keep Actions cost in check) with sample repositories per framework and test framework,
   and a corpus entry on Windows later. .NET Core 3.1-7 hosts can be checked on macOS with old runtimes
   installed in a separate toolchain directory.

## Done (2026-10-06)

- The tracker targets `net462` and `netstandard2.0` with one identity. `RuntimeDeployment.TrackerFor`
  picks the net462 build (shipped under `runtime/net462` next to TestDetta) for a .NET Framework test
  assembly, which needs no netstandard facades, and the netstandard2.0 build for every other host. It no
  longer uses System.Text.Json (`ResultsJsonWriter`), `Interlocked.Or` (compare-exchange), ranges or
  .NET 5+ APIs. A .NET Framework output without `deps.json` gets the tracker copied next to the tests.
- Recording turns test AppDomains off in its run settings (`RunConfiguration/DisableAppDomain`, xUnit
  `AppDomain=denied`, `ShadowCopy=false`); before, xUnit v2 on .NET Framework recorded no class. A tracker
  in a non-default domain still writes its own results file (process id and domain id) at unload and
  after each test.
- Without `-f`, a multi-targeted test project is recorded for the first framework that runs on the
  machine (.NET Framework only on Windows).
- `LegacyHostRecordingTests` (category `Legacy`, run with a dotnet that has the .NET 6 runtime, and
  Mono for .NET Framework): xUnit 2, NUnit 3, MSTest 3 on net6.0; xUnit 2 on net462 and on net48 with
  net35 code under test; MSTest 1.4 and 3.6 on net48. All pass on macOS (Mono for .NET Framework).

## Support matrix (research 2026-10-06, sources in the session's research notes)

| Test platform | .NET Framework | .NET Core / .NET |
|---|---|---|
| Microsoft.NET.Test.Sdk 17.4-17.13 | net462+ | netcoreapp3.1+ |
| Microsoft.NET.Test.Sdk 17.14+ / 18 | net462+ | net8+ |
| Microsoft.Testing.Platform | net462+ | net8+ |
| xunit.runner.visualstudio 2.5-2.8 / 3.x | net462+ / net472+ | net6+ / net8+ |
| NUnit3TestAdapter 4.x / 5.x | net35+ (4.3) / net462+ | netcoreapp3.1+ |
| MSTest 3 / 4 | net462+ | netcoreapp3.1+ / net8+ |
| Test projects for net35-net461 | only Test SDK <= 17.3 or vstest.console with old adapters | - |

Support status in October 2026: .NET 8 and 9 end 2026-11-10, .NET 10 (LTS) runs to 2028-11-14; .NET
Core 3.1-7 are out of support; .NET Framework 4.6.2 ends 2027-01-12, 4.7-4.8.1 follow Windows, 3.5 SP1
runs to 2029-01-09. What TestDetta can promise: test projects on .NET Framework 4.6.2+ and .NET 6+ (6
and 7 with Test SDK 17.13), code under test on anything.

## Open

- NUnit 2 (NUnitTestAdapter 2.3.0): records no class; its test AppDomain is not unloaded before the
  host exits on Mono, and the results it writes after each test did not reach the map in the
  macOS/Mono check. To look at again on Windows. Until then its classes are new to the map, which the
  selection treats as unknown (they run).
- Windows: a CI job (manual, to keep Actions cost down; Windows minutes cost about 1.7x Linux) running
  the Legacy category on .NET Framework without Mono, and `vstest.console.exe`.
- Legacy (non-SDK) projects with `packages.config`: done 2026-10-06, docs/design/legacy-projects.md (full
  MSBuild to restore and build, tests from the compiled assembly through VSTest); Windows end to end open.
- Visual Basic and F#: docs/design/vb-fsharp-support.md.
