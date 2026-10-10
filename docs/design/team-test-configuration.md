# The team's test configuration

Status: approved by the owner 2026-10-10 ("code, tests and documentation equally"). Implementation in steps,
each with tests; see "Steps".

## Problem

A team configures what its test runs include and how they run, in several places: `VSTestTestCaseFilter`
and `RunSettingsFilePath` (or `VSTestSetting`) in its project files, a run settings file with a
`TestCaseFilter`, environment variables and `TestRunParameters`, `TestingPlatformCommandLineArguments` for
Microsoft.Testing.Platform, and the arguments its CI passes to `dotnet test` (`--filter Category!=Integration`,
`--settings ci.runsettings`). TestDetta adds its own arguments to the same runs: class filters to run a
selection or a recording shard, run settings that make recording sequential and load the collector, and
`--ignore-exit-code 8` for a recording's catch-all shard.

Measured on 2026-10-10 (SDK 10.0.105 VSTest, SDK 11 rc Microsoft.Testing.Platform with xUnit v3):

| Combination | What `dotnet test` does |
|---|---|
| `--filter` on the command line, `VSTestTestCaseFilter` in the project | the command line **replaces** the project's filter |
| `--filter` on the command line, `TestCaseFilter` in the run settings | both apply (and) |
| `--settings` on the command line, `RunSettingsFilePath` in the project | the command line **replaces** the file: its filter, environment variables and parameters are gone |
| testing platform: a class filter on the command line, `TestingPlatformCommandLineArguments` in the project | both apply |

What follows for a team:

1. **TestDetta runs tests the team excludes.** A selection's `--filter FullyQualifiedName~…` replaces
   `VSTestTestCaseFilter`: an integration test the team never runs on pull requests runs, and fails without a
   database. The team sees TestDetta break its CI.
2. **Recording runs under other settings.** Recording passes its own run settings, which replace the team's:
   tests that need the team's environment variables or parameters fail while recording (their classes then
   run on every change), or take other paths than in the team's runs, which the map then records (a selection
   built on paths the real runs do not take can miss).
3. **Recording does not know the team's command.** `td record` takes no `dotnet test` arguments, and the
   action passes `dotnet-test-args` in test, shadow and audit modes only: a filter or settings file the CI
   passes on the command line is not applied while recording.
4. **Options that may appear once collide.** The testing platform rejects a second `--ignore-exit-code`
   (EF Core sets one in `TestingPlatformCommandLineArguments`). EF Core's catch-all shards end with exit code 5
   ("Handshake failures"), which makes 13 of its 15 maps untrusted; the cause is not confirmed yet.

## Principle

TestDetta narrows the team's runs; it never widens or changes them. Every run TestDetta starts (`td test`,
recording shards, shadow and audit reruns, the evaluator's runs) runs under the team's configuration, with
TestDetta's own parts added where the team's leave room:

- a filter TestDetta adds is combined with the team's filter, never in place of it;
- run settings TestDetta needs are added to a copy of the team's run settings, never in place of them;
- an option the team already set once is not set again.

## Design

**Discovery.** The MSBuild evaluation TestDetta already runs reports, per test project, `VSTestTestCaseFilter`,
`RunSettingsFilePath`, `VSTestSetting` and `TestingPlatformCommandLineArguments` (`ProjectNode.TestConfiguration`;
no extra MSBuild call). The command line adds the team's `--filter` and `--settings` (`-s`), which take
precedence over the project's, as they do in `dotnet test`.

**Filters (VSTest syntax).** The team's filter is the command line's `--filter` if given, else the project's
`VSTestTestCaseFilter`. TestDetta's filter becomes `(team)&(TestDetta)`. A filter in the team's run settings
needs nothing: `dotnet test` applies it alongside. The testing platform's own filters
(`--filter-class`, `--treenode-filter`) combine with the team's already; where a combination is impossible
(xUnit v3's query filter with simple filters, TUnit's tree-node filter), the project runs with the team's
filter only, as `TestFilter.WithinUserFilter` does today for the command line.

**Run settings.** Where TestDetta needs run settings (recording), it starts from the team's file (command line,
else `RunSettingsFilePath`/`VSTestSetting`) and adds or overrides only its own elements: `DisableParallelization`,
`DisableAppDomain`, the xUnit, NUnit and MSTest parallelism elements, the in-process collector and the NUnit
result folder. The team's `TestCaseFilter`, `EnvironmentVariables`, `TestRunParameters`, data collectors,
timeouts and target platform stay. A file that cannot be read stops the recording of that project with a
reason, rather than recording under other settings.

**Recording takes the team's command.** `td record [-- <dotnet test arguments>]`; the action's
`dotnet-test-args` applies in record mode too. Their filter and settings are used as above; arguments that
change what recording controls (`--no-build`, `--logger`, `--results-directory`, `-c`) keep TestDetta's.

**Single-value options.** TestDetta adds `--ignore-exit-code` only when neither the command line nor the
project's `TestingPlatformCommandLineArguments` has it; an empty catch-all shard's exit code 8 is accepted
either way.

**Visible.** `td test`, `td affected` and `td record` print the team configuration they kept, per test
project, in debug logs and once in the console summary ("your filter Category!=Integration
(VSTestTestCaseFilter) and run settings ci.runsettings are kept"); the job summary says the same.

**Documented.** A docs page, "TestDetta and your test configuration", in five languages: what TestDetta adds,
what it keeps, the table above, and what to do when a filter cannot be combined.

## Steps

1. Evaluator reports the test configuration (version 8); `ProjectNode.TestConfiguration`.
2. One function that combines a team filter with TestDetta's, used by `td test`, recording, shadow and audit
   reruns and the evaluator; tests with VSTest and the testing platform.
3. Run settings merged into a copy of the team's, in recording; tests that fail without it (a test that needs
   the team's environment variable, a test the team's run settings filter out).
4. `td record -- <args>` and the action input in record mode.
5. Output and docs (site in five languages, README, docs/ci.md, atlas).
6. Corpus: how many repositories set any of this; failed-while-recording classes before and after; EF Core's
   exit code 5.
