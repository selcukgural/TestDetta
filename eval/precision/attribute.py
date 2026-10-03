"""Precision round: which rule selected the classes that did not need to run. Joins the evaluator's
explain.json (Sift's decisions and evidence per sampled change, `sift-eval --explain`) with the
corpus run's synthetic.json (which classes really failed per mutant), and attributes every selected
class that did not fail to the kind of evidence that selected it.
Usage: attribute.py <explain.json> <synthetic.json> [--top N]"""
import json, sys, re, collections

explain = json.load(open(sys.argv[1]))
synthetic = json.load(open(sys.argv[2]))
top = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 12
killed = {}
for m in synthetic["mutants"]:
    if m["killed"] and not m["inconclusive"]:
        killed[(m["target"], m["kind"])] = {k.split(": ", 1)[1] for k in m["killed"]}

def category(kind, evidence):
    if kind == "WholeProject":
        return "whole project: " + re.sub(r"^depends on [^:]+: ", "", evidence)[:90]
    if kind == "Coverage":
        if "whose static constructor changed" in evidence: return "coverage: ran a member of the type (static ctor rule)"
        if "whose constructor changed" in evidence: return "coverage: ran a member of the type (ctor rule)"
        if "names the changed code" in evidence: return "coverage: ran a member the change reaches by name"
        return "coverage: ran the changed member"
    if kind == "Name": return "name: names the change (directly or through others)"
    return kind

extra = collections.Counter(); needed = collections.Counter(); reached = collections.Counter(); names = collections.Counter()
judged = 0; selected_total = 0; extra_total = 0; missed = []; shares = []
for change in explain["changes"]:
    key = (change["target"], change["kind"])
    if key not in killed:
        continue
    judged += 1
    failing = killed[key]
    shares.append(change["selectedShare"])
    chosen = {d["class"] for project in change["projects"] for d in project["selected"]}
    if failing - chosen:
        missed.append((change["target"], change["kind"], sorted(failing - chosen)))
    for project in change["projects"]:
        for d in project["selected"]:
            selected_total += 1
            cat = category(d["kind"], d["evidence"])
            if d["class"] in failing:
                needed[cat] += 1
                continue
            extra_total += 1
            extra[cat] += 1
            if "names the changed code" in d["evidence"]:
                reached[re.sub(r"^ran (.*?), which names.*$", r"\1", d["evidence"])] += 1
            if d["kind"] == "Name":
                hops = re.findall(r" names ([^ ←,]+)", d["evidence"])
                if hops: names[hops[-1]] += 1  # the name nearest to the change

print(f"judged failing mutants: {judged}; selected classes: {selected_total}; of them did not fail: {extra_total} ({100*extra_total/max(1,selected_total):.0f}%)")
print(f"mean selected share: {100*sum(shares)/max(1,len(shares)):.1f}%; missed mutants: {len(missed)}")
for target, kind, classes in missed:
    print(f"  MISSED {kind} {target}: {', '.join(classes)}")
print("\nclasses that did not fail, by what selected them:")
for cat, n in extra.most_common():
    print(f"  {n:6}  {100*n/max(1,extra_total):5.1f}%  {cat}   (needed by the same rule: {needed[cat]})")
if reached:
    print(f"\ntop reached members (coverage through a name hop):")
    for m, n in reached.most_common(top): print(f"  {n:6}  {m}")
if names:
    print(f"\ntop names nearest to the change (name trace):")
    for m, n in names.most_common(top): print(f"  {n:6}  {m}")
