# Legacy project files: packages.config, msbuild, vstest

Status: implemented 2026-10-06 (owner: "add msbuild.exe and vstest.console.exe support"). Windows end to
end is in the backlog ("Later: Windows"); macOS is tested with Mono.

## Problem

Legacy .NET Framework solutions use non-SDK project files (`<Project ToolsVersion="15.0" ...>` that import
`Microsoft.CSharp.targets`) with `packages.config`. `dotnet build` cannot build them: `dotnet restore`
does not restore `packages.config` (neither does `dotnet msbuild -t:restore -p:RestorePackagesConfig=true`,
checked), and the .NET Framework reference assemblies come with Visual Studio, its Build Tools or Mono.
TestDetta recorded nothing for such a solution: "Building ... failed: MSB3644 The reference assemblies for
.NETFramework,Version=v4.8 were not found".

What does work with the .NET SDK: evaluating the project (`dotnet msbuild -getProperty:TargetPath`), so the
project graph, the source index and the selection need nothing new; and running the tests from the
compiled assembly (`dotnet test Tests.dll`), which is VSTest, the engine of `vstest.console.exe`, with the
same filter, run settings and TRX report. The adapters (xUnit's, NUnit's, MSTest's) are copied next to the
assembly by their packages, where VSTest finds them.

## Design

- `ProjectNode.SdkStyle`: false when `<Project>` has no `Sdk` attribute, no `<Sdk>` element and no
  `<Import Sdk=...>`. `ProjectGraph.NeedsFullMsBuild(project)`: the project or one it references is legacy
  (an SDK test project over a legacy library cannot be built by `dotnet build` either).
- `FullMsBuild` (Coverage): finds the MSBuild executable (`TESTDETTA_MSBUILD`; on Windows the newest Visual
  Studio or Build Tools MSBuild through `vswhere`; else `msbuild` on the PATH, which is Mono's on macOS and
  Linux), restores with `-t:restore -p:RestorePackagesConfig=true` on the solution that lists the project
  (NuGet needs one to place `packages/`; without one, the project with `SolutionDir` set to the repository),
  then builds the project. The .NET SDK's `MSBuild*` variables are removed from its environment: a dotnet
  that started TestDetta passes them on, and Mono's msbuild then loads the SDK's NuGet tasks and fails.
- Recording (`CoverageRecorder`): such a test project builds through `FullMsBuild`, runs under VSTest
  whatever `global.json` says, and its tests run as `dotnet test <TargetPath>` with the usual run settings,
  filters and TRX (no `-c`, `-f` or `--no-build`, which an assembly run rejects).
- `td test` (`TestInvocationBuilder`, `TestRunner`): the invocation carries the configuration (`-c`, default
  Debug); the runner builds with `FullMsBuild` unless `--no-build` is given, asks MSBuild for the
  `TargetPath`, and runs `dotnet test <assembly>` with the class filter and the user's arguments minus the
  build-only ones. Outside Windows, where the .NET Framework test host runs on Mono and xUnit v2's
  adapter fails across its own AppDomain, inline run settings turn test AppDomains off.

## Tests

`LegacyProjectTests`: recognition, `NeedsFullMsBuild` through a reference, the argument filter (fast), and
end to end (category `Legacy`: needs the full MSBuild, and Mono outside Windows): `td record` on a
packages.config solution with xUnit 2 on .NET Framework 4.8 records both classes; a change to
`Calculator.Add` makes `td test` build with msbuild and run `AddTests` alone.

## Not covered yet

- Windows end to end (MSBuild.exe from Visual Studio, the .NET Framework test host without Mono): backlog.
- Runners other than VSTest (`nunit3-console`, `xunit.console`, `mstest.exe`, which VSTest 18 removed):
  every framework they serve also has a VSTest adapter, which `dotnet test <assembly>` uses.
- The evaluation harness builds with `dotnet build`; a legacy corpus repository needs the same switch there.
- A changed source generator in a legacy test project is not compared by building (no generators in
  packages.config projects in practice); it runs the project, as before.
