# TestDetta

[![NuGet](https://img.shields.io/nuget/v/TestDetta?label=NuGet)](https://www.nuget.org/packages/TestDetta)
[![NuGet downloads](https://img.shields.io/nuget/dt/TestDetta?label=downloads)](https://www.nuget.org/packages/TestDetta)
[![CI](https://github.com/selcukgural/TestDetta/actions/workflows/ci.yml/badge.svg)](https://github.com/selcukgural/TestDetta/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Run only the .NET tests a change can affect.

TestDetta compares your branch with its base, works out which test projects and which test classes can
observe the change, and runs just those. When it cannot be sure, it runs more, never less. It runs inside
your CI and on your machine; your source, coverage and test results never leave them.

**Documentation: [testdetta.com/docs](https://testdetta.com/docs/)**

```
$ td test --base origin/main
TestDetta 0.5.0 · 1 changed file · 2 affected test projects
Tests to run
┌──────────────────┬──────────┬─────────┬───────┬──────────────────────────────────────────────────────────┐
│ Test project     │ Run      │ Classes │ Saved │ Why                                                      │
├──────────────────┼──────────┼─────────┼───────┼──────────────────────────────────────────────────────────┤
│ tests/Shop.Tests │ filtered │  3 / 41 │     - │ 3 of 41 test class(es) run or reference the changed code │
│ tests/Api.Tests  │ skipped  │  0 / 12 │     - │ no test class runs or references the changed code        │
└──────────────────┴──────────┴─────────┴───────┴──────────────────────────────────────────────────────────┘
```

## Install

TestDetta is the [`TestDetta` package on nuget.org](https://www.nuget.org/packages/TestDetta), a .NET tool with the
command `td`. The .NET 10 SDK runs `td` itself; your tests keep the SDK and runner your repository already uses.
Tested on Linux and macOS; Windows is not tested yet.

```sh
dotnet tool install --global TestDetta      # or: dotnet tool install TestDetta (local tool manifest)
dotnet tool update --global TestDetta       # to the latest version
```

xUnit v2 and v3, NUnit, MSTest and TUnit, on VSTest and Microsoft.Testing.Platform, in C#, Visual Basic and F#
([compatibility](https://testdetta.com/docs/compatibility/)).

## Use

```sh
td record                    # on the default branch: run every test once and record coverage maps
td test --base origin/main   # on a branch: run only the affected tests
td affected -v               # what would run, and why, without running anything
```

Add `.testdetta/` to `.gitignore`. Without a coverage map TestDetta still works, deciding by name, which
selects more. Every command and option: [CLI reference](https://testdetta.com/docs/cli/).

### GitHub Actions

```yaml
- uses: actions/checkout@v7          # pin to a commit SHA
  with:
    fetch-depth: 0                   # TestDetta compares with the merge base

- if: github.event_name == 'push'    # default branch: record the maps
  uses: TestDetta/action@6afa8c0f324f1f3626d14d71a6f37899533441f0 # v0.5.0
  with:
    mode: record

- if: github.event_name == 'pull_request'   # pull requests: run the affected tests
  uses: TestDetta/action@6afa8c0f324f1f3626d14d71a6f37899533441f0 # v0.5.0
  with:
    mode: test
```

The whole workflow, permissions and inputs: [GitHub Actions](https://testdetta.com/docs/github-actions/). GitLab CI,
Jenkins, Azure DevOps and Bitbucket: [Other CI systems](https://testdetta.com/docs/other-ci/).

### Claude Code

A Claude Code plugin ([`dist/claude-plugin`](dist/claude-plugin)): agents run `td test` instead of a full
`dotnet test`, and the affected tests run before the agent hands back.

```
/plugin install testdetta --marketplace selcukgural/TestDetta
```

## How it decides

1. **Projects.** Changed files are followed through project references; build-wide files affect every project below them.
2. **Symbols.** Old and new syntax trees are compared, so a comment or whitespace edit selects nothing.
3. **Coverage.** A map of which test class ran which method gives the classes for changed code directly.
4. **Names.** New code, fields and constants are traced through the source by name.
5. **Fallbacks.** A change that cannot be traced (`appsettings.json`, a `.csproj`, reflection-found types) runs the whole test project.

Details: [How it decides](https://testdetta.com/docs/how-it-works/) ·
[Limitations](https://testdetta.com/docs/limitations/) ·
[Validate on your repository](https://testdetta.com/docs/validate/) (shadow mode and `td audit`) ·
[Your test configuration](https://testdetta.com/docs/test-configuration/) ·
[Troubleshooting](https://testdetta.com/docs/troubleshooting/) ·
[Security and data](https://testdetta.com/docs/security/)

## Building

.NET 10 SDK: `dotnet build`, `dotnet test` (`Category=Slow` tests build and record sample projects). Coding
guidelines: [CONTRIBUTING.md](CONTRIBUTING.md). Design notes: [docs/design](docs/design). Security reports:
[SECURITY.md](SECURITY.md).

## Sponsors

TestDetta is free and open source for every repository, public or private. It is built by one person; if it
saves your team test time, you can help it go on through [GitHub Sponsors](https://github.com/sponsors/selcukgural):
monthly or once.

## License

[Apache-2.0](LICENSE)
