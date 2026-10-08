# TestDetta for GitHub Actions

Run only the .NET tests a change can affect on pull requests, and record coverage maps on your default
branch. TestDetta reads the diff, follows it through projects, changed members and per-test-class
coverage, and runs just the test classes that can observe it. When it cannot be sure, it runs more,
never less.

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
      - uses: actions/checkout@<sha> # v4 or later
        with:
          fetch-depth: 0 # TestDetta compares with the merge base

      - if: github.event_name == 'push'
        uses: TestDetta/action@<sha> # v0.1.0
        with:
          mode: record

      - if: github.event_name == 'pull_request'
        uses: TestDetta/action@<sha> # v0.1.0
        with:
          mode: test
          license: ${{ secrets.TESTDETTA_LICENSE }}
```

Pin the action to a commit SHA. Each release pins the `TestDetta` package from nuget.org in `tool.lock`
by version and SHA-512, and the action checks it before running anything.

- Every input, the permissions and the job summary: https://testdetta.com/docs/github-actions/
- GitLab CI, Jenkins, Azure DevOps, Bitbucket: https://testdetta.com/docs/other-ci/
- Free for public repositories; private repositories get a 30-day trial: https://testdetta.com/pricing/

TestDetta runs inside your CI. No source, coverage or test results leave your machines.
See [LICENSE.txt](LICENSE.txt).
