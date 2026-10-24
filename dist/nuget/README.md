# TestDetta

Run only the .NET tests a change can affect. TestDetta reads your git diff, follows it through
projects, changed members and per-test-class coverage maps recorded on your default branch, and
runs just the test classes that can observe it. When it cannot be sure, it runs more, never less.

```sh
dotnet tool install --global TestDetta

td record                 # on the default branch: record coverage maps
td test --base origin/main  # on a pull request: run only the affected tests
td affected -v            # what would run, and why
```

- Runs inside your CI; no source, coverage or test results leave your machines.
- xUnit v2 and v3, NUnit, MSTest and TUnit, on VSTest and Microsoft.Testing.Platform.
- GitHub Actions, GitLab CI, Azure DevOps, Jenkins and Bitbucket.
- Free and open source (Apache-2.0) for every repository, public or private: https://github.com/selcukgural/TestDetta

Documentation: https://testdetta.com/docs/
Sponsor its development: https://github.com/sponsors/selcukgural
