# TestDetta for Claude Code

A Claude Code plugin that lets the agent run only the .NET tests its changes can affect.

- **Skill `testdetta`:** the agent runs `td test` where it would run `dotnet test`, and reads `td affected` to find
  the tests that can observe a change.
- **Stop hook:** when the agent finishes a turn with a changed working tree, the affected tests run. A failing test
  or a broken build sends the agent back with a short excerpt (each failure's message, each compiler error once)
  and the path of the full log, so it fixes the problem before handing back. Passing tests show one line to the
  user.

It needs the `td` tool (`dotnet tool install -g TestDetta`).

## Install

In Claude Code (2.1.275 or later), one step:

```
/plugin install testdetta --marketplace selcukgural/TestDetta
```

On earlier versions, add the marketplace first:

```
/plugin marketplace add selcukgural/TestDetta
/plugin install testdetta@testdetta
```

`/plugin update testdetta@testdetta` takes a newer version. For a whole team, add the marketplace and the plugin to
the repository's `.claude/settings.json` (`extraKnownMarketplaces` and `enabledPlugins`), so everyone who opens it
is offered the plugin.

### Other agents

The skill follows the open Agent Skills format, so agents that read `SKILL.md` folders can use it; the Stop hook is
Claude Code's own. For Codex, copy [`skills/testdetta`](skills/testdetta) into `~/.codex/skills/testdetta` (or
`$CODEX_HOME/skills`) and restart Codex.

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
bash tests/claude-plugin-stop-hook.test.sh     # hook tests, against a fake td (also run in CI)
```

The icon is `brand/social/github-avatar.svg` rendered at 1024 px (`rsvg-convert -w 1024 -h 1024`).

## Releasing

The marketplace is this repository: [`.claude-plugin/marketplace.json`](../../.claude-plugin/marketplace.json) at its
root lists the plugin from `dist/claude-plugin`, so what is on `main` is what users install. For a new version, raise
`version` in both `plugin.json` and the marketplace entry (`claude plugin validate .` checks they agree), merge, and
tag it with `claude plugin tag dist/claude-plugin` (`testdetta--v<version>`). Users get it with `/plugin update`.
