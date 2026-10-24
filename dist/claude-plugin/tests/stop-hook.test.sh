#!/usr/bin/env bash
# Tests of scripts/stop-hook.sh against a fake td in a throwaway git repository.
# Run: bash dist/claude-plugin/tests/stop-hook.test.sh
set -u

here="$(cd "$(dirname "$0")" && pwd)"
hook="$here/../scripts/stop-hook.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() {
  echo "FAIL: $1"
  failures=$((failures + 1))
}

# The fake td logs its arguments, prints what the real one prints at the end of a run, writes the summary file
# and exits with FAKE_TD_EXIT.
mkdir -p "$work/bin"
cat > "$work/bin/td" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_TD_CALLS"
summary=""
while [ $# -gt 0 ]; do
  if [ "$1" = "--summary-file" ]; then summary="$2"; shift; fi
  shift
done
if [ -n "$summary" ]; then echo "| tests/Shop.Tests | failed | 3 classes |" >> "$summary"; fi
case "${FAKE_TD_EXIT:-0}" in
  0) echo "1 project run · 0 failed · 0 skipped" ;;
  3) echo "  Failed Shop.Tests.CartTests.Adds [12 ms]"
     echo "  Error Message:"
     echo "   Assert.Equal() Failure: Values differ"
     echo "Expected: 5"
     echo "Actual:   6"
     echo "  Stack Trace:"
     echo "     at Shop.Tests.CartTests.Adds() in CartTests.cs:line 2"
     echo "src/Shop/Cart.cs(4,1): error CS1002: ; expected"
     echo "src/Shop/Cart.cs(4,1): error CS1002: ; expected"
     echo "1 project run · 1 failed · 0 skipped" ;;
  *) echo "td: origin/main is not a valid ref" ;;
esac
exit "${FAKE_TD_EXIT:-0}"
EOF
chmod +x "$work/bin/td"

# A PATH without any td, for the "not installed" case on machines where td is installed.
path_without_td=""
IFS=':' read -r -a dirs <<< "$PATH"
for dir in "${dirs[@]}"; do
  if [ -x "$dir/td" ]; then continue; fi
  path_without_td="${path_without_td:+$path_without_td:}$dir"
done

repo="$work/repo"
mkdir -p "$repo/src/Shop"
git -C "$repo" init -q
git -C "$repo" config user.email test@example.com
git -C "$repo" config user.name test
echo "<Project />" > "$repo/src/Shop/Shop.csproj"
echo "class Cart {}" > "$repo/src/Shop/Cart.cs"
git -C "$repo" add -A
git -C "$repo" commit -qm initial

export FAKE_TD_CALLS="$work/calls"

# Runs the hook in $1 with stop_hook_active $2; sets out, err, code and calls (how often td ran).
run_hook() {
  : > "$FAKE_TD_CALLS"
  out="$(printf '{"session_id":"s","stop_hook_active":%s}' "$2" \
    | CLAUDE_PROJECT_DIR="$1" PATH="${HOOK_PATH:-$work/bin:$PATH}" bash "$hook" 2>"$work/err")"
  code=$?
  # bash warns at start-up when LC_ALL names a locale the machine lacks (the quick CI job sets tr_TR.UTF-8 on a
  # runner without it); that is the environment talking, not the hook.
  err="$(grep -v 'warning: setlocale: ' "$work/err" || true)"
  calls="$(grep -c . "$FAKE_TD_CALLS")"
}

expect() {
  local name="$1" want_code="$2" want_calls="$3"
  if [ "$code" != "$want_code" ]; then fail "$name: exit $code, expected $want_code"; fi
  if [ "$calls" != "$want_calls" ]; then fail "$name: td ran $calls time(s), expected $want_calls"; fi
}

# 1. A repository without project files is left alone.
plain="$work/plain"
mkdir -p "$plain" && git -C "$plain" init -q && echo x > "$plain/a.txt"
run_hook "$plain" false
expect "not .NET" 0 0
[ -z "$out$err" ] || fail "not .NET: printed output"

# 2. The hook can be turned off.
TESTDETTA_AGENT_HOOK=off run_hook "$repo" false
expect "off" 0 0

# 3. Without td the user is told once, and the agent may stop.
HOOK_PATH="$path_without_td" run_hook "$repo" false
expect "no td" 0 0
[[ "$out" == *'"systemMessage":"TestDetta: td is not installed'* ]] || fail "no td: no message ($out)"
HOOK_PATH="$path_without_td" run_hook "$repo" false
[ -z "$out" ] || fail "no td: told twice"

# 4. Passing tests let the agent stop and tell the user the totals.
FAKE_TD_EXIT=0 run_hook "$repo" false
expect "pass" 0 1
[[ "$out" == *'affected tests passed (1 project run · 0 failed · 0 skipped)'* ]] || fail "pass: message ($out)"
grep -q -- "^test --color never --summary-file " "$FAKE_TD_CALLS" || fail "pass: td arguments ($(cat "$FAKE_TD_CALLS"))"

# 5. An unchanged working tree does not run again.
FAKE_TD_EXIT=0 run_hook "$repo" false
expect "unchanged" 0 0

# 6. A failure after a change sends the agent back with the excerpt and the summary.
echo "class Cart { int x }" > "$repo/src/Shop/Cart.cs"
FAKE_TD_EXIT=3 run_hook "$repo" false
expect "fail" 2 1
[[ "$err" == *"Failed Shop.Tests.CartTests.Adds"* ]] || fail "fail: no failed test in excerpt"
[[ "$err" == *"Expected: 5"*"Actual:   6"* ]] || fail "fail: no expected and actual values in excerpt"
[[ "$err" != *"at Shop.Tests.CartTests.Adds()"* ]] || fail "fail: stack trace in excerpt"
[ "$(grep -c "error CS1002" <<< "$err")" = 1 ] || fail "fail: build error missing or repeated in excerpt"
[[ "$err" == *"| tests/Shop.Tests | failed |"* ]] || fail "fail: no summary"
[[ "$err" == *"Full output: "*"/testdetta-agent/last-test.log"* ]] || fail "fail: no log path"

# 7. Stopping again without changing anything is not sent back a second time.
FAKE_TD_EXIT=3 run_hook "$repo" true
expect "fail unchanged" 0 0

# 8. Within one turn the agent is sent back at most TESTDETTA_AGENT_MAX_BLOCKS times.
echo "class Cart { int y }" > "$repo/src/Shop/Cart.cs"
FAKE_TD_EXIT=3 TESTDETTA_AGENT_MAX_BLOCKS=1 run_hook "$repo" false
expect "cap first" 2 1
echo "class Cart { int z }" > "$repo/src/Shop/Cart.cs"
FAKE_TD_EXIT=3 TESTDETTA_AGENT_MAX_BLOCKS=1 run_hook "$repo" true
expect "cap reached" 0 1
[[ "$out" == *"still fail after 1 attempts"* ]] || fail "cap reached: message ($out)"

# 9. A td error never blocks, and the same tree is tried again at the next stop.
echo "class Cart { }" > "$repo/src/Shop/Cart.cs"
FAKE_TD_EXIT=1 run_hook "$repo" false
expect "td error" 0 1
[[ "$out" == *"could not choose the tests (exit code 1): td: origin/main is not a valid ref"* ]] || fail "td error: message ($out)"
FAKE_TD_EXIT=0 run_hook "$repo" false
expect "after td error" 0 1

# 10. A new untracked file counts as a change.
echo "class Order {}" > "$repo/src/Shop/Order.cs"
FAKE_TD_EXIT=0 run_hook "$repo" false
expect "untracked" 0 1

# 11. TESTDETTA_AGENT_BASE is passed on as --base.
echo "class Order { }" > "$repo/src/Shop/Order.cs"
FAKE_TD_EXIT=0 TESTDETTA_AGENT_BASE=origin/develop run_hook "$repo" false
grep -q -- "--base origin/develop" "$FAKE_TD_CALLS" || fail "base: not passed ($(cat "$FAKE_TD_CALLS"))"

# The hook works on a copy of the index; the real index is untouched.
[ -z "$(git -C "$repo" diff --cached --name-only)" ] || fail "index: the hook staged files"

if [ "$failures" -gt 0 ]; then
  echo "$failures failure(s)"
  exit 1
fi
echo "stop-hook: all tests passed"
