# Build inputs per framework

Status: approved by the owner 2026-10-10 (high-share repositories, step 3: LibGit2Sharp).

## Problem

A test project whose build uses the output of a project it references without compiling against it
(LibGit2Sharp's `NativeLibraryLoadTestApp`, `OutputItemType="TestAppExe"`) runs only the classes that start or
load that output when a change reaches it, provided its build fed the compiler the same files from outside git
as when the map was recorded (`TestSelector.BuildInputsDiffer`, docs/design/runtime-file-reads.md). The
comparison was skipped for every multi-targeted test project and for every map recorded for one framework:
"the test project targets several frameworks, whose builds are not compared". The whole project then ran.
LibGit2Sharp selected 100 % of its classes on every sampled change for that reason alone.

The recorder reads one framework's build: the one it records (`-f`, or the first of a multi-targeted
project). `dotnet test` without `-f` runs every framework, and another framework's build may compile other
files (a polyfill, a conditional item), so one framework's inputs do not speak for the others.

## Design

**Which frameworks are compared.** The frameworks the tests will run:

- `td test … -- -f <framework>` (or `--framework`) runs one; only its build is compared. The other frameworks'
  tests do not run, so they cannot be missed. `TestSelectionRequest.Framework` carries it (from the
  `dotnet test` arguments in `td test`, from `--framework` in the evaluator).
- Without it, a project with one framework compares that one, as before, and a multi-targeted project compares
  every framework it targets.

**What the map holds.** A map recorded without `-f` already reads the generated code of every framework the
project targets (`ReadOtherFrameworks`, `GeneratedFrameworks`). It now reads their build inputs in the same pass:
`CoverageMap.FrameworkBuildInputs`, by framework, for the frameworks other than the recorded one
(`BuildInputs` keeps the recorded one). A map recorded with `-f` holds that framework only.

**Comparison.** For each framework to compare, the recorded inputs are the map's `BuildInputs` for the recorded
framework or `FrameworkBuildInputs` for another one; the current inputs come from a build of that framework
(`IGeneratedCodeReader.Read(…, framework)`). The output stays a file to run only when every compared framework
is equal. Anything else runs the whole project, with the reason:

- a framework the map holds no build for: "its map holds the build of net8.0 only (record it without -f, or
  run the tests with -f net8.0)";
- inputs that differ in any framework: "what the net48 build fed its compiler changed: …";
- a build that cannot be read, as before.

## Source generators (step 4, immediate-handlers)

A changed source generator already compares what it writes framework by framework
(`TestSelector.Generators`, `GeneratedFrameworks`), but only for maps recorded without `-f`; a map recorded with
`-f` ran its project completely whatever the tests ran. The same rule now applies there
(`GeneratorFrameworks`): a run of one framework compares that framework's generated code when the map holds it, and
a framework the map holds no code of runs the project ("its map holds no generated code of net10.0, the framework
the tests run for").

Measured on immediate-handlers (69 sampled changes, maps recorded for net10.0), full evaluations with the binary
before and after on the same sample and cached outcomes: **0 missed of 39 failing mutants** both times; the mean
share went from 61.6 % to 59.6 %. 22 changes selected less and 23 more. The ones that select more are generator
changes: the comparison now runs, and the generated members it finds changed are traced like code changes, which
likely selects more in the other test project (not verified per project: `--explain` cannot build the generator
copy, and the result file keeps no per-project share). The gain is small; the rule is kept because it is the same
rule as for build inputs and it is what a team running with `-f` needs.

## Safety

Selection narrows only where every framework that will run was compared and found equal; that is the rule a
single-framework project had already. Restricting the comparison to the `-f` framework is safe because no
other framework's tests run in that invocation. A team that records with `-f` and runs without it keeps the
whole-project run.

## Measured (2026-10-10)

LibGit2Sharp, the 100 sampled changes of the corpus commit, maps recorded for net8.0 (the evaluator's `-f`):

| | Before | After |
|---|---|---|
| `--explain`: mean share of the selected classes | 100 % (every change) | 16.2 %; 100 of 100 changes narrower, none wider |
| Full evaluation: mean share of the recorded classes | 100 % | 26 % |
| Missed | | **0 of 50 failing mutants** |

Most changes now run 1 to 5 of the 66 classes, and 20 run none. The only other repository with the old reason
in the 2026-10-10 scan was CliWrap, for 1 of its 88 changes: a change to `CliWrap.Signaler`, whose output
CliWrap embeds as a resource. The embedded resource changes with it, so that change still runs the project.
