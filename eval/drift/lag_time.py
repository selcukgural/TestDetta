"""Time-weighted map lag: CI runs at any moment of an active day (every 15 minutes); the PR's
merge base is main's tip at that moment (GitHub tests refs/pull/N/merge against the current base).
The newest usable map is the newest recorded commit, at or before the tip, whose recording
finished before that moment."""
import subprocess, os, json, statistics, sys
from datetime import datetime, timezone, timedelta
RUNS = os.path.expanduser(os.environ.get("SIFT_CORPUS_RUNS", "~/sift-corpus/runs")); DAYS = 365
def git(repo, *a): return subprocess.run(["git", "-C", repo, *a], capture_output=True, text=True, check=True).stdout
def pct(xs, p): xs = sorted(xs); return xs[min(len(xs)-1, int(round(p/100*(len(xs)-1))))]
POL = {"every-commit+30m": (None, 30), "every-commit+120m": (None, 120), "hourly+30m": (1, 30), "nightly+60m": (24, 60), "weekly+60m": (168, 60)}
out = {}
for name in sorted(os.listdir(RUNS)):
    repo = os.path.join(RUNS, name, "repo")
    if "." in name or not os.path.isdir(os.path.join(repo, ".git")): continue
    since = (datetime.now(timezone.utc) - timedelta(days=DAYS)).isoformat()
    log = [l.split() for l in git(repo, "log", "--first-parent", "--reverse", f"--since={since}", "--format=%H %ct", "HEAD").split("\n") if l.strip()]
    if len(log) < 50: continue
    ts = [int(t) for _, t in log]
    days = sorted({t // 86400 for t in ts})
    samples = [d * 86400 + q * 900 for d in days for q in range(96)]
    row = {"commits": len(ts)}
    for pol, (hours, rec) in POL.items():
        # ready[k] = time the map of commit k becomes usable (None = never recorded).
        ready = [None] * len(ts)
        if hours is None:
            ready = [t + rec * 60 for t in ts]
        else:
            slot = hours * 3600; j = 0
            start = (ts[0] // slot + 1) * slot
            while start <= ts[-1] + slot:
                while j + 1 < len(ts) and ts[j + 1] < start: j += 1
                if ts[j] < start and (ready[j] is None): ready[j] = start + rec * 60
                start += slot
        lags = []; tip = -1
        for s in samples:
            while tip + 1 < len(ts) and ts[tip + 1] <= s: tip += 1
            if tip < 0: continue
            k = tip
            while k >= 0 and (ready[k] is None or ready[k] > s): k -= 1
            if k < 0: continue
            lags.append(tip - k)
        row[pol] = {"exact": round(sum(1 for l in lags if l == 0) / len(lags), 3), "p50": pct(lags, 50), "p90": pct(lags, 90), "max": max(lags)}
    out[name] = row
json.dump(out, open(sys.argv[1], "w"), indent=1)
for p in POL:
    ex = [r[p]["exact"] for r in out.values()]; p90 = [r[p]["p90"] for r in out.values()]
    print(f"{p:18} repos={len(ex)} exact median {100*statistics.median(ex):.0f}% (min {100*min(ex):.0f}%) | p90 lag median {statistics.median(p90)}, max {max(p90)}")
for n in ["opentelemetry-dotnet", "meziantou-analyzer", "fastendpoints", "polly"]:
    print(n, {p: (out[n][p]["exact"], out[n][p]["p90"]) for p in POL})
