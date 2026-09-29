# Sift

Run only the .NET tests a change can affect.

Sift compares your branch with its base, works out which test projects and which test classes can
observe the change, and runs just those. When it cannot be sure, it runs more, never less.

```
$ sift test --base origin/main
sift: 1 changed file(s), 2 affected test project(s).
Tests to run:
  filtered tests/Shop.Tests/Shop.Tests.csproj  (3 of 41 test class(es) run or reference the changed code)
  skip     tests/Api.Tests/Api.Tests.csproj  (no test class runs or references the changed code)
...
sift summary:
  passed  tests/Shop.Tests/Shop.Tests.csproj  (3 class(es), 4.1 s)
1 project(s) run, 0 failed, 1 skipped.
```

## How it decides

1. **Projects.** Changed files are mapped to their projects and followed through `ProjectReference`s.
   Build-wide files (`Directory.Build.props`, `global.json`, `.editorconfig`, ...) affect every
   project below them.
2. **Symbols.** For C# files, the old and new syntax trees are compared. Comment and whitespace
   edits select nothing; otherwise Sift knows which types and members changed.
3. **Coverage** (after `sift record`). A map of which test class ran which method, recorded at the
   base commit, gives the test classes for changed methods, properties and constructors directly.
4. **Names.** What coverage cannot see (new code, fields, constants) is traced through the source by
   name until it reaches test classes or code the coverage map knows.
5. **Fallbacks.** A whole test project runs when a change cannot be traced: non-C# files in a
   project (`appsettings.json`, `.csproj`, `.resx`), global usings and assembly attributes, and types
   a framework discovers through reflection (controllers, message handlers, EF configurations).

A filter that matches no test is treated as a Sift bug: the project is run again without it.

## Commands

| Command | What it does |
|---|---|
| `sift affected [--format text\|json\|paths\|commands]` | Print what a change affects and why. |
| `sift test [--dry-run] [--summary-file <path>] [-- <dotnet test args>]` | Run only the affected tests. Exit code 3 when tests fail. |
| `sift record [--project <csproj>]... [-c <configuration>] [--parallel <n>]` | Run all tests with coverage and write one map per test project to `.sift/coverage`. `--parallel` spreads the test classes over n test processes. |

Shared options: `--base <ref>` (default `origin/main`), `--repo <path>`, `-v` for debug logs on stderr.

Coverage maps are used only when they were recorded exactly at the merge base of your branch;
code that changed in between could have created calls the map does not know. Add `.sift/` to
`.gitignore`.

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

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7 # pin to a commit SHA
        with:
          fetch-depth: 0 # Sift compares with the merge base
          persist-credentials: false

      - uses: actions/setup-dotnet@v6 # pin to a commit SHA
        with:
          global-json-file: global.json

      # Also the full test run of main. Leave out projects that cannot run here, such as
      # integration tests that need a database; they are then decided by name on pull requests.
      - if: github.event_name == 'push'
        uses: <owner>/sift@<sha>
        with:
          mode: record
          projects: |
            tests/Shop.Tests/Shop.Tests.csproj

      - if: github.event_name == 'pull_request'
        uses: <owner>/sift@<sha>
        with:
          mode: test
          dotnet-test-args: --logger trx
```

Maps are stored in the Actions cache under `sift-coverage-<commit>`. Pull requests can read caches
of their base branch. Without a map, `mode: test` still works, deciding by name only.

| Input | Default | |
|---|---|---|
| `mode` | | `test` or `record` |
| `base` | the PR's base branch | branch to compare with in test mode |
| `configuration` | `Debug` | build configuration |
| `projects` | all test projects | record mode: `.csproj` paths, one per line |
| `parallel` | `1` | record mode: test processes per project (the suite must tolerate several at once) |
| `dotnet-test-args` | | test mode: extra `dotnet test` arguments |
| `working-directory` | `.` | any directory inside the repository |
| `setup-dotnet` | `true` | install the .NET 10 SDK Sift runs on |

## Limitations

- Recording coverage supports VSTest runs (xUnit v2, NUnit, MSTest through `Microsoft.NET.Test.Sdk`),
  not yet Microsoft.Testing.Platform or xUnit v3.
- Code that runs once per process (caches, lazy singletons, EF model building) is credited to the
  first test class that triggers it. Keep running the full suite on the default branch; recording does that.
- Project files are read as XML, not evaluated: conditions, imports and linked source files outside
  a project directory are not followed. Test projects are also recognised by a `*.Tests`/`*.Test` name.

## Building

.NET 10 SDK. `dotnet build`, `dotnet test` (the `Category=Slow` test builds and records a sample
project). Coding guidelines: [CONTRIBUTING.md](CONTRIBUTING.md).
