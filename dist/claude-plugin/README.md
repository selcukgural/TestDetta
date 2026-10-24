# TestDetta for Claude Code

A Claude Code plugin that lets the agent run only the .NET tests its changes can affect.

- **Skill `testdetta`:** the agent runs `td test` where it would run `dotnet test`, and reads `td affected` to find
  the tests that can observe a change.
- **Stop hook:** when the agent finishes a turn with a changed working tree, the affected tests run. A failing test
  or a broken build sends the agent back with a short excerpt (each failure's message, each compiler error once)
  and the path of the full log, so it fixes the problem before handing back. Passing tests show one line to the
  user.

It needs the `td` tool (`dotnet tool install -g TestDetta`).

## Settings

Environment variables, e.g. under `env` in `.claude/settings.json`:

| Variable | Effect |
|---|---|
| `TESTDETTA_AGENT_HOOK=off` | Turn the Stop hook off; the skill stays. |
| `TESTDETTA_AGENT_BASE=<ref>` | Compare with this ref instead of `origin/main`. |
| `TESTDETTA_AGENT_MAX_BLOCKS=<n>` | How often one turn may be sent back for failing tests (default 3); after that the agent hands back and the user is told. |

## How the hook decides

- Only in a git repository with a `.csproj`, `.fsproj` or `.vbproj`; anywhere else it does nothing.
- It fingerprints the working tree (tracked and untracked files, through a copy of the index) and runs nothing when
  the tree is the same as at its last test run, so a turn that changed no code costs a few milliseconds.
- A td error, a missing td or a timeout never blocks the agent; the user sees one line.
- State lives in `.git/testdetta-agent/` (last fingerprint, block count, last log and summary).

The hook is a bash script (git, awk, grep, sed, tr); it is tested on macOS and Linux, not yet on Windows.

## Developing

```bash
claude --plugin-dir dist/claude-plugin          # try the plugin in a session
claude plugin validate dist/claude-plugin       # check the manifest
bash dist/claude-plugin/tests/stop-hook.test.sh # hook tests, against a fake td (also run in CI)
```

The icon is `brand/social/github-avatar.svg` rendered at 1024 px (`rsvg-convert -w 1024 -h 1024`).

Not published yet. Customers will install it from a public marketplace repository, like the action is copied to
`TestDetta/action` (docs/release.md); that repository and its `marketplace.json` come with the first release.
