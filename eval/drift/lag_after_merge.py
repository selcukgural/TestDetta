"""Map lag right after main moves: for each first-parent main commit m (a PR whose CI runs 10 minutes later),
how many first-parent commits lie between m and the newest recorded ancestor under a recording
policy, and how many .cs files those commits change (the diff a static bridge must cover)."""
import subprocess, sys, os, statistics, json, bisect
from datetime import datetime, timezone, timedelta

RUNS = os.path.expanduser(os.environ.get("TESTDETTA_CORPUS_RUNS", "~/sift-corpus/runs"))
DAYS = 365
RECORDING = timedelta(minutes=30)
HOURS = {"hourly": 1, "6-hourly": 6, "nightly": 24, "weekly": 168}

def git(repo, *args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, check=True).stdout

def pct(xs, p):
    if not xs: return None
    xs = sorted(xs); i = min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1))))
    return xs[i]

def policies(times):
    """Recording points as indexes into the commit list (oldest first) per policy."""
    out = {}
    out["every-commit+30m"] = None
    out["every-commit+120m"] = None
    for name, hours in (("hourly", 1), ("6-hourly", 6), ("nightly", 24), ("weekly", 168)):
        pts, last_slot = [], None
        for i, t in enumerate(times):
            slot = int(t.timestamp() // (hours * 3600))
            # A scheduled recording at the start of each slot records the newest commit before it.
            if last_slot is not None and slot != last_slot:
                pts.append(i - 1)
            last_slot = slot
        out[name] = pts
    return out

results = {}
for name in sorted(os.listdir(RUNS)):
    repo = os.path.join(RUNS, name, "repo")
    if "." in name or not os.path.isdir(os.path.join(repo, ".git")):
        continue
    head = git(repo, "rev-parse", "HEAD").strip()
    since = (datetime.now(timezone.utc) - timedelta(days=DAYS)).isoformat()
    log = git(repo, "log", "--first-parent", "--reverse", f"--since={since}", "--format=%H %ct", head).split("\n")
    commits = [(l.split()[0], datetime.fromtimestamp(int(l.split()[1]), timezone.utc)) for l in log if l.strip()]
    if len(commits) < 10:
        results[name] = {"commits": len(commits)}; continue
    shas = [c[0] for c in commits]; times = [c[1] for c in commits]
    days_active = len({t.date() for t in times})
    per_day = {}
    for t in times: per_day[t.date()] = per_day.get(t.date(), 0) + 1
    # .cs files changed by each first-parent commit (vs its first parent).
    cs = []
    for s in shas:
        files = git(repo, "diff", "--name-only", f"{s}^1", s).split("\n")
        cs.append({f for f in files if f.endswith(".cs")})
    row = {"commits": len(commits), "activeDays": days_active,
           "perActiveDayMedian": statistics.median(per_day.values()), "perActiveDayMax": max(per_day.values())}
    for policy, pts in policies(times).items():
        lags, files = [], []
        for i in range(len(shas)):
            if policy.startswith("every-commit+"):
                # Every commit is recorded, but its map exists only `delay` after the commit; a
                # PR whose CI runs right after its merge base uses the newest finished one.
                delay = timedelta(minutes=int(policy.split("+")[1].rstrip("m")))
                r = i
                while r >= 0 and times[r] + delay > times[i] + timedelta(minutes=10):
                    r -= 1
                if r < 0: continue
            else:
                # A scheduled recording starts at a slot boundary, records the newest commit before
                # it and finishes RECORDING later; CI runs 10 minutes after the commit.
                hours = HOURS[policy]
                ci = times[i] + timedelta(minutes=10)
                ready = [p for p in pts if (int(times[p + 1].timestamp() // (hours * 3600)) * hours * 3600) + RECORDING.total_seconds() <= ci.timestamp()] if pts else []
                if not ready: continue
                r = min(ready[-1], i)
            lags.append(i - r)
            changed = set().union(*cs[r + 1:i + 1]) if i > r else set()
            files.append(len(changed))
        if not lags: continue
        row[policy] = {"lagMedian": pct(lags, 50), "lagP90": pct(lags, 90), "lagMax": max(lags),
                       "exactShare": round(sum(1 for l in lags if l == 0) / len(lags), 3),
                       "csFilesP50": pct(files, 50), "csFilesP90": pct(files, 90)}
    results[name] = row
    print(name, file=sys.stderr, flush=True)
json.dump(results, open(sys.argv[1], "w"), indent=1)
