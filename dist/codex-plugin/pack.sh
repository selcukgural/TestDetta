#!/usr/bin/env bash
# Builds the ZIP that is uploaded to the ChatGPT and Codex plugin directory (platform.openai.com/plugins).
# The skill has one source, the Claude Code plugin's (dist/claude-plugin/skills); this package adds the portable
# manifest and the icons, and no hooks: the directory accepts no plugin with lifecycle hooks.
# Usage: bash dist/codex-plugin/pack.sh [output.zip]   (default: testdetta-codex-plugin-<version>.zip here)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
version="$(sed -n 's/^  "version": "\(.*\)",$/\1/p' "$here/plugin.json")"
out="${1:-$PWD/testdetta-codex-plugin-$version.zip}"
case "$out" in /*) ;; *) out="$PWD/$out" ;; esac

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp "$here/plugin.json" "$stage/"
cp -R "$here/assets" "$stage/assets"
mkdir -p "$stage/skills"
cp -R "$root/dist/claude-plugin/skills/testdetta" "$stage/skills/testdetta"
cp "$root/LICENSE" "$stage/LICENSE"

rm -f "$out"
(cd "$stage" && zip -q -r -X "$out" plugin.json assets skills LICENSE)
echo "$out"
