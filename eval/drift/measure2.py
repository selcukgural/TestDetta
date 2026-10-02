"""Measurement 2 of docs/design/map-drift-bridge.md: per map age k and selector, over the failing
mutants every selector was judged on, the mutants with a missed class, the missed classes and the
mean selected share. Reads <root>/drift-k<k>/<repo>/synthetic.json written by
`sift-eval --corpus ... --map-age k`. Usage: measure2.py ~/sift-corpus [repo ...]"""
import json, os, sys, glob, re

root = os.path.expanduser(sys.argv[1])
only = set(sys.argv[2:])
SELECTORS = ["exact", "bridge", "noMap", "naive", "projectLevel"]
rows = {}  # (k, repo) -> {selector: [missed mutants, missed classes, share sum, n]}
for path in sorted(glob.glob(os.path.join(root, "drift-k*", "*", "synthetic.json"))):
    k = int(re.search(r"drift-k(\d+)", path).group(1))
    repo = os.path.basename(os.path.dirname(path))
    if only and repo not in only:
        continue
    run = json.load(open(path))
    if run.get("error"):
        print(f"k={k} {repo}: error {run['error'][:100]}", file=sys.stderr)
        continue
    stats = {s: [0, 0, 0.0, 0] for s in SELECTORS}
    for m in run["mutants"]:
        others = m.get("others") or {}
        if m["inconclusive"] or not m["killed"] or "bridge" not in others:
            continue
        results = {"exact": (m["missed"], m["selectedShare"])}
        for s in SELECTORS[1:]:
            if s in others:
                results[s] = (others[s]["missed"], others[s]["selectedShare"])
        for s, (missed, share) in results.items():
            st = stats[s]
            st[0] += 1 if missed else 0
            st[1] += len(missed)
            st[2] += share
            st[3] += 1
    rows[(k, repo)] = stats

def cell(st):
    if not st[3]:
        return "–"
    return f"{st[0]}/{st[3]} miss, {st[1]} cls, {100 * st[2] / st[3]:.0f}%"

print("| k | repository | " + " | ".join(SELECTORS) + " |")
print("|---|---|" + "---|" * len(SELECTORS))
for (k, repo), stats in sorted(rows.items()):
    print(f"| {k} | {repo} | " + " | ".join(cell(stats[s]) for s in SELECTORS) + " |")

print()
print("Totals per k (mutants with a miss / judged mutants, mean selected share):")
for k in sorted({k for k, _ in rows}):
    line = []
    for s in SELECTORS:
        tot = [0, 0, 0.0, 0]
        for (kk, _), stats in rows.items():
            if kk == k:
                for i in range(4):
                    tot[i] += stats[s][i]
        line.append(f"{s}: {cell(tot)}")
    print(f"k={k}: " + "; ".join(line))
