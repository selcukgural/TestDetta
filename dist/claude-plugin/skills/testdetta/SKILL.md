---
name: testdetta
description: Run only the .NET tests a change can affect with TestDetta's td tool instead of a full dotnet test. Use in a C#, F# or Visual Basic repository whenever you are about to run tests after changing code, need to know which tests can observe a change, or want to find the tests that cover a type or member before editing it. Do not use outside .NET repositories or when the user asks for the complete test suite.
---

# TestDetta

`td` compares the working tree with the base branch (default `origin/main`; committed, uncommitted and untracked
changes all count), works out which test projects and test classes can observe the change, and runs only those.
When it cannot be sure, it runs more, never less, so a passing `td test` is as trustworthy as a full run for the
change at hand.

Check that it is installed with `td --version`. If it is not, run the full `dotnet test` as usual and tell the
user once that `dotnet tool install -g TestDetta` would let you run only the affected tests.

## Running tests

Run `td test` where you would run `dotnet test`:

```bash
td test --color never
td test --color never --base origin/develop   # the branch the work will merge into, when it is not origin/main
td test --color never -- -c Release           # anything after -- goes to every dotnet test
```

- Exit code 0: every selected test passed. 3: a test failed or a test project did not build. Anything else is a
  TestDetta error: read its message, then fall back to `dotnet test`.
- The table at the top says, per test project, whether it ran `filtered` (only the listed classes), `all`, or was
  `skipped`, and why. Read the *Why* column before assuming a test was left out by mistake.
- To fix one failure, rerun just that class with `dotnet test <project> --filter FullyQualifiedName~<Class>`;
  run `td test` again before you finish.

## Seeing what a change affects

```bash
td affected --color never                 # projects and test classes, with the reason for each
td affected --format json                 # the same, for reading by a program
td affected --format commands             # the dotnet test commands td test would run
```

In the JSON, `tests[]` holds one entry per test project: `mode` (`All`, `Filtered`, `Skipped`), `reason`,
`classes`, and `decisions[]` with each class's `selected`, `kind` and `evidence`. Use it to find the tests to read
or extend before changing a member: make the edit, run `td affected`, and the classes that can observe it are the
ones to look at.

## What changes the precision

- **Coverage maps.** With a map recorded at the base commit (`td record`, stored in `.testdetta/coverage`),
  selection follows which test class really ran which method. Without one, td traces names through the source,
  which selects more but stays safe. `td record` runs every test with coverage and can take as long as a full CI
  run: suggest it to the user, do not start it yourself.
- **Whole projects.** A change to a `.csproj`, `Directory.Build.props`, `appsettings.json`, a global using or a type
  found through reflection (controllers, message handlers) runs whole test projects. That is the safe answer,
  not a bug.

## The Stop hook

Installed as the Claude Code plugin, TestDetta also runs `td test` when you finish a turn, if the working tree changed since the last run. A failure
comes back to you as a short excerpt with the path of the full log; fix it, or tell the user why it is not caused
by your change. The user can turn the hook off with `TESTDETTA_AGENT_HOOK=off`.
