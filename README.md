# TestDetta

Run only the .NET tests a change can affect.

TestDetta compares your branch with its base, works out which test projects and which test classes can
observe the change, and runs just those. When it cannot be sure, it runs more, never less.

```
$ td test --base origin/main
td: 1 changed file(s), 2 affected test project(s).
Tests to run:
  filtered tests/Shop.Tests/Shop.Tests.csproj  (3 of 41 test class(es) run or reference the changed code)
  skip     tests/Api.Tests/Api.Tests.csproj  (no test class runs or references the changed code)
...
td summary:
  passed  tests/Shop.Tests/Shop.Tests.csproj  (3 class(es), 4.1 s)
1 project(s) run, 0 failed, 1 skipped.
```

## How it decides

1. **Projects.** Changed files are mapped to their projects and followed through `ProjectReference`s.
   Build-wide files (`Directory.Build.props`, `global.json`, `.editorconfig`, ...) affect every
   project below them.
2. **Symbols.** For C# files, the old and new syntax trees are compared. Comment and whitespace
   edits select nothing; otherwise TestDetta knows which types and members changed.
3. **Coverage** (after `td record`). A map of which test class ran which method, recorded at the
   base commit, gives the test classes for changed methods, properties and constructors directly.
4. **Names.** What coverage cannot see (new code, fields, constants) is traced through the source by
   name until it reaches test classes or code the coverage map knows.
5. **Fallbacks.** A whole test project runs when a change cannot be traced: non-C# files in a
   project (`appsettings.json`, `.csproj`, `.resx`), global usings and assembly attributes, and types
   a framework discovers through reflection (controllers, message handlers, EF configurations).

A filter that matches no test is treated as a TestDetta bug: the project is run again without it.

## Commands

| Command | What it does |
|---|---|
| `td affected [--format text\|json\|paths\|commands] [--build]` | Print what a change affects and why. `--build` builds the test projects that use a changed source generator, to compare what it generates; without it, an existing build newer than the sources is read. |
| `td test [--dry-run] [--summary-file <path>] [-- <dotnet test args>]` | Run only the affected tests. Exit code 3 when tests fail. |
| `td record [--project <csproj>]... [-c <configuration>] [-f <tfm>] [--parallel <n>] [--no-build]` | Run all tests with coverage and write one map per test project to `.testdetta/coverage`. `--parallel` spreads the test classes over n test processes; `--no-build` records the output of an earlier build with the same configuration. A test project that does not build or run on the machine (a mobile app without its workload) gets no map and is decided by name; the command fails only when no map could be written. |
| `td license status` | Check the license and show what it covers. |
| `td license install <license\|file>` | Store a license in `~/.testdetta/license`, for machines where no CI secret can hold it. |

Shared options: `--base <ref>` (default `origin/main`), `--repo <path>`, `--maps-dir <dir>` (coverage
maps outside the repository, see [docs/ci.md](docs/ci.md)), `-v` for debug logs on stderr,
`--log-level <level>` for any other level. Exit codes: `0` success, `1` the analysis failed, `2`
invalid command line, `3` tests failed.

Coverage maps are best recorded at the merge base of your branch. A map recorded up to 50 commits
earlier (`--max-map-age`) is still used: code changed in between could have created calls the map
does not know, so TestDetta also selects the classes that ran any changed-since member that can reach
your change, and traces new code by name. When something in between cannot be described by members
(a build file, `appsettings.json`), the affected test projects run completely; `--drift-fallback name`
decides them by name instead, as without a map (faster, but blind to calls wired by configuration).
Add `.testdetta/` to `.gitignore`.

## GitHub Actions

Record a map for every commit on the default branch, and let pull requests run only the tests
their changes affect:

```yaml
on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read
  actions: read # lets pull requests find the newest coverage map in the Actions cache

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7 # pin to a commit SHA
        with:
          fetch-depth: 0 # TestDetta compares with the merge base
          persist-credentials: false

      - uses: actions/setup-dotnet@v6 # pin to a commit SHA
        with:
          global-json-file: global.json

      # Also the full test run of main. Leave out projects that cannot run here, such as
      # integration tests that need a database; they are then decided by name on pull requests.
      - if: github.event_name == 'push'
        uses: <owner>/testdetta@<sha>
        with:
          mode: record
          projects: |
            tests/Shop.Tests/Shop.Tests.csproj

      - if: github.event_name == 'pull_request'
        uses: <owner>/testdetta@<sha>
        with:
          mode: test
          dotnet-test-args: --logger trx
```

Maps are stored in the Actions cache under `testdetta-coverage-<commit>`. Pull requests can read caches
of their base branch. A pull request uses the map of its merge base, or the newest one recorded
up to `max-map-age` commits before it (finding that one lists the caches, which needs
`actions: read`). Without a map, `mode: test` still works, deciding by name only.

| Input | Default | |
|---|---|---|
| `mode` | | `test` or `record` |
| `base` | the PR's base branch | branch to compare with in test mode |
| `configuration` | `Debug` | build configuration |
| `projects` | all test projects | record mode: `.csproj` paths, one per line |
| `parallel` | `1` | record mode: test processes per project (the suite must tolerate several at once) |
| `no-build` | `false` | record mode: `true` to record the build an earlier step made with the same `configuration` |
| `dotnet-test-args` | | test mode: extra `dotnet test` arguments |
| `max-map-age` | `50` | test mode: commits a map may be behind the merge base (0: only an exact map) |
| `drift-fallback` | `all` | test mode: `all` runs test projects completely when the changes since an older map include a build file or configuration; `name` decides them by name |
| `github-token` | `github.token` | test mode: lists the Actions caches to find the newest map |
| `license` | | test mode: the TestDetta license, from a secret such as `TESTDETTA_LICENSE`; public repositories need none |
| `working-directory` | `.` | any directory inside the repository |
| `setup-dotnet` | `true` | install the .NET 10 SDK TestDetta runs on |

## License

Public repositories need no license on GitHub Actions and GitLab CI, which say whether a repository
is public; on other CI systems and on developer machines every repository counts as private. Private
ones pass the license string in `TESTDETTA_LICENSE` (or a file named by `TESTDETTA_LICENSE_FILE`); in GitHub
Actions, through the `license` input from a secret. Only `affected` and `test` check the license;
`record` runs every test anyway.
`td license status` shows what a license covers. Without a valid license every test runs and the
build never fails because of it. Machines that cannot reach the license service set
`TESTDETTA_LICENSE_REFRESH=off` (see [docs/ci.md](docs/ci.md)).

## Other CI systems

GitLab CI, Jenkins, Azure DevOps, Bitbucket and on-premises setups use the same two commands with a
folder of per-commit coverage maps (`--maps-dir`) kept on a shared disk or object storage. Examples
and the details are in [docs/ci.md](docs/ci.md).

## Limitations

- Both `dotnet test` runners are supported. The runner is read from `global.json`
  (`"test": { "runner": "Microsoft.Testing.Platform" }`, required for xUnit v3 on the .NET 10 SDK).
  Each test project is built and run in the directory of the nearest `global.json` at or above it,
  so a file kept next to the solution (`src/global.json`) counts as it does for a developer there;
  `--dry-run` prints such commands as `(cd src && dotnet test ...)`. Recording and selection are
  tested end to end with xUnit v2 (VSTest), xUnit v3 and TUnit (Microsoft.Testing.Platform), and
  MSTest and NUnit under both runners, including projects that turn on their framework's own
  parallelization.
- Test classes that fail while recording are always selected when their project is affected: a
  failing test stops early, so its coverage is incomplete. When tests fail and no TRX report says
  which (NUnit under Microsoft.Testing.Platform without `Microsoft.Testing.Extensions.TrxReport`),
  the map is not used and selection falls back to names.
- Code that runs once per process (caches, lazy singletons, EF model building) is credited to the
  first test class that triggers it. Changes to code that ran inside a static constructor or a
  cache-filling call (`ConcurrentDictionary.GetOrAdd`, `Lazy<T>.Value`, `LazyInitializer`,
  `ConditionalWeakTable`, `IMemoryCache.GetOrCreate`), and to constructors and static members that
  only one test class per recording process ran, are traced by name instead. A cache written by
  hand (a `Dictionary` checked with `TryGetValue`) can still hide its users. Keep running the full suite on the default branch; recording does that.
- Project files are evaluated by MSBuild (one `dotnet msbuild` call, cached in `.testdetta/cache`) for
  references, compiled and embedded items, assembly names and target frameworks, in the default
  configuration and without a restore. Items only a package's props add, or only a `Release`
  condition adds, are not seen. A project MSBuild cannot evaluate is read as XML, as are imports.
  A changed file inside a project folder that the project does not compile counts as a change to
  that project; a file outside every project folder (README, `.github/`, shared test data) selects
  only the test classes that read it by a path while the map was recorded (reads in the repository's
  own code; docs/design/runtime-file-reads.md), and nothing when no test read it. Test
  projects are also recognised by a `*.Tests`/`*.Test` name.
- C# files are parsed with each target framework's real preprocessor symbols, and also with no
  symbols and with every symbol their `#if`s name, so code under any active condition is seen.
- Tests skipped while a map was recorded have no coverage. A class whose tests were all skipped is
  selected on another platform (it may run there) and skipped on the recording platform. Tests
  skipped for other reasons, such as a missing Docker daemon, are covered only by recording where
  the tests run.
- Code written by source generators is not in the source tree; `td record` reads it from the
  PDBs of the recorded assemblies (portable or embedded) and keeps it in the map, where it is
  analysed like the project's own code. Assemblies built without a portable PDB give nothing.
- A change that reaches a source generator in the repository (a project used with
  `OutputItemType="Analyzer"`) is decided by what the generator writes: `td test` builds the test
  projects that use it and compares their generated code with the map, so only generated code that
  really changed counts, framework by framework. A test project without a map, without a portable
  PDB, or recorded with `-f` for one of several frameworks still runs completely; a build that fails
  stops `td test` with the compiler's errors.
  A project referenced with `ReferenceOutputAssembly="false"` is decided by how its output is used:
  the classes whose recording started it as a process, loaded it as an assembly or read a build output
  named after it run. When the consumer's build uses the output (an `OutputItemType`, a target that
  names the project, a compile input from it), `td test` builds the test project and compares what the
  build fed the consumer's compiler from outside git with the map; a difference, or a map recorded
  before such uses were, runs the consumer completely.
- F# and Visual Basic projects are part of the project graph, but their code is not analysed: when
  one is affected, everything that depends on it runs completely.

## Building

.NET 10 SDK. `dotnet build`, `dotnet test` (the `Category=Slow` test builds and records a sample
project). Coding guidelines: [CONTRIBUTING.md](CONTRIBUTING.md).
