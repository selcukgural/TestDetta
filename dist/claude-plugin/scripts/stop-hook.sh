#!/usr/bin/env bash
# Claude Code Stop hook: before the agent hands back, run the tests the working tree's changes can affect
# (`td test`, which compares with the base branch and includes uncommitted and untracked files).
#
# Exit 0 lets the agent stop; exit 2 keeps it working and shows stderr to it, so a failing test or a broken
# build reaches the agent as a short excerpt instead of the whole dotnet test output.
#
# The hook never blocks for its own problems: no td, a td error or a repository that is not .NET lets the agent
# stop, with at most a one-line message to the user. Settings, from the environment:
#   TESTDETTA_AGENT_HOOK=off    turn the hook off
#   TESTDETTA_AGENT_BASE=<ref>  compare with this ref instead of td's default (origin/main)
#   TESTDETTA_AGENT_MAX_BLOCKS  how often the agent may be sent back in one turn (default 3)
set -u

input="$(cat)"

if [ "${TESTDETTA_AGENT_HOOK:-on}" = "off" ]; then
  exit 0
fi

# A one-line message for the user (not the agent), as the hook's JSON output; the text is flattened to one line
# and escaped by hand because the hook must not depend on jq.
notify_user() {
  local text
  text="$(printf '%s' "$1" | tr '\n\r\t' '   ' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')"
  printf '{"systemMessage":"%s"}\n' "$text"
}

project_dir="${CLAUDE_PROJECT_DIR:-$PWD}"
top="$(git -C "$project_dir" rev-parse --show-toplevel 2>/dev/null)" || exit 0
cd "$top" || exit 0

# Only .NET repositories: a project file anywhere in the tree, tracked or new.
if [ -z "$(git ls-files --cached --others --exclude-standard -- '*.csproj' '*.fsproj' '*.vbproj' | head -n 1)" ]; then
  exit 0
fi

state="$(git rev-parse --absolute-git-dir)/testdetta-agent"
mkdir -p "$state" || exit 0

if ! command -v td >/dev/null 2>&1; then
  # Said once per repository, not at every stop.
  if [ ! -e "$state/td-missing" ]; then
    : > "$state/td-missing"
    notify_user "TestDetta: td is not installed, so the affected tests were not run. Install it with: dotnet tool install -g TestDetta"
  fi
  exit 0
fi
rm -f "$state/td-missing"

# The working tree's fingerprint: the tree hash of every tracked and untracked (not ignored) file, written through
# a copy of the index so the real one is never touched. The copy keeps git's stat cache, so only changed files are
# hashed again. The same fingerprint as the last run means nothing changed since: the result is already known
# (and was already shown to the agent when it failed), so nothing runs.
scratch_index="$state/index.tmp"
cp "$(git rev-parse --git-path index)" "$scratch_index" 2>/dev/null || rm -f "$scratch_index"
fingerprint="$(GIT_INDEX_FILE="$scratch_index" git add -A . 2>/dev/null && GIT_INDEX_FILE="$scratch_index" git write-tree 2>/dev/null)"
rm -f "$scratch_index"
if [ -z "$fingerprint" ]; then
  exit 0
fi
if [ "$(cat "$state/last-run" 2>/dev/null)" = "$fingerprint" ]; then
  exit 0
fi

# stop_hook_active is true when the agent is still working because this hook sent it back. A new turn starts the
# count again; within a turn the count caps the loop, so an agent that cannot fix a test hands back to the user.
blocks=0
if printf '%s' "$input" | grep -Eq '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  blocks="$(cat "$state/blocks" 2>/dev/null || echo 0)"
fi
max_blocks="${TESTDETTA_AGENT_MAX_BLOCKS:-3}"

log="$state/last-test.log"
summary="$state/last-summary.md"
rm -f "$summary"
args=(test --color never --summary-file "$summary")
if [ -n "${TESTDETTA_AGENT_BASE:-}" ]; then
  args+=(--base "$TESTDETTA_AGENT_BASE")
fi

td "${args[@]}" >"$log" 2>&1
status=$?

# Only a test result is remembered for the fingerprint; after a td error the next stop tries again.
case "$status" in
  0)
    printf '%s\n' "$fingerprint" > "$state/last-run"
    echo 0 > "$state/blocks"
    totals="$(grep -E ' run · [0-9]+ failed' "$log" | tail -n 1)"
    notify_user "TestDetta: affected tests passed${totals:+ ($totals)}"
    exit 0
    ;;
  3)
    printf '%s\n' "$fingerprint" > "$state/last-run"
    blocks=$((blocks + 1))
    echo "$blocks" > "$state/blocks"
    if [ "$blocks" -gt "$max_blocks" ]; then
      notify_user "TestDetta: affected tests still fail after $max_blocks attempts; full output in $log"
      exit 0
    fi
    {
      echo "TestDetta ran the tests your changes can affect, and some failed or did not build."
      echo "Fix them, or tell the user why they fail if the failure is not caused by your changes."
      echo
      if [ -s "$summary" ]; then
        cat "$summary"
        echo
      fi
      # Each failed test from its name to its stack trace (the message, expected and actual values), and each
      # compiler error once: MSBuild repeats them in its closing summary.
      echo "Failures (first 60 lines):"
      awk '
        /^[[:space:]]*[Ff]ailed [^ ]/ { inFailure = 1 }
        /^[[:space:]]*Stack Trace:/ { inFailure = 0 }
        inFailure { print; next }
        /: error [A-Z]+[0-9]+/ && !seen[$0]++ { print }
      ' "$log" | head -n 60
      echo
      echo "Full output: $log"
    } >&2
    exit 2
    ;;
  *)
    notify_user "TestDetta could not choose the tests (exit code $status): $(grep -v '^[[:space:]]*$' "$log" | tail -n 1). Full output in $log"
    exit 0
    ;;
esac
