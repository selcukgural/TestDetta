# TestDetta for Codex and ChatGPT

The TestDetta skill packaged for the ChatGPT and Codex plugin directory: `plugin.json` (the portable manifest with
OpenAI's listing details), `assets/` (logo and composer icon) and, when packed, the `testdetta` skill from
[`dist/claude-plugin/skills`](../claude-plugin/skills), its only source. It has no hooks: the directory accepts no
plugin with lifecycle hooks, so the Stop hook stays in the Claude Code plugin.

```bash
bash dist/codex-plugin/pack.sh              # writes testdetta-codex-plugin-<version>.zip
```

Upload the ZIP at platform.openai.com/plugins. A new version raises `version` here and goes through review again.

Without the directory, copy `dist/claude-plugin/skills/testdetta` into `~/.codex/skills/testdetta` and restart Codex.
