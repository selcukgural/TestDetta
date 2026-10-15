"""Puts an evaluation round's numbers on the site (site/README.md, "Evaluation numbers").

    python3 eval/site/corpus_site.py --round ~/sift-corpus/runs-final-1007 \
        --override ~/sift-corpus/runs-final-1007-rejudge --override ~/sift-corpus/runs-final-1007-nlog-clean

Reads the round's summary.md (and each repository's synthetic.md for the classes run per failure); an --override
folder's results replace the round's for the repositories it holds (a rejudged or clean rerun). Writes, in every
language:
  - site.json: corpusRepositories, corpusFailingChanges, corpusMissed, corpusUpperBound, corpusMedianShare,
    corpusQuarterOrLess, corpusInconclusive, and the localized forms of the formatted ones in i18n/strings;
  - the home page's chart (between <!-- corpus:chart --> markers): one bar per repository, sorted by share;
  - the examples page's table (between <!-- corpus:table --> markers) and the hard-case counts.
Text around them uses the site.json values; what the round's numbers say in prose is edited by hand.
Repositories with no failing change are left out, as they have no share to show.
"""

import argparse
import json
import math
import re
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LANGUAGES = ["", "tr"]

# Corpus tags as the table shows them; tags about size are not shown.
TAG_LABELS = {
    "mtp": "Testing Platform", "xunit-v3": "xUnit v3", "xunit": "xUnit", "nunit": "NUnit", "mstest": "MSTest",
    "tunit": "TUnit", "async": "async", "packages": "packages", "snapshots": "snapshots", "polyfill": "polyfills",
    "reflection": "reflection", "dynamic-code": "dynamic code", "static-state": "static state",
    "multi-target": "multi-target", "source-generator": "source generators", "analyzers": "analyzers",
    "conditional-compilation": "#if", "di-scanning": "DI scanning", "pinvoke": "P/Invoke", "native": "native code",
    "serialization": "serialization", "non-code-inputs": "non-code inputs", "cpm": "central packages",
    "linked-files": "linked files", "aspnet": "ASP.NET Core", "integration": "integration", "fsharp": "F#",
    "arcade": "Arcade", "expression-trees": "expression trees",
}

# How each language writes a count, a percentage and a decimal, for the values that need it (i18n/strings/<lang>.json
# overrides site.json there); Japanese writes them as English does.
LOCAL_FORMATS = {
    "tr": {"thousands": ".", "decimal": ",", "percent": "%{}"},
}

# The hard-case tiles of the examples page, by position in the English page, and the tag each one counts.
HARD_TAGS = ["reflection", "multi-target", "snapshots", "static-state", "dynamic-code", "non-code-inputs",
             "source-generator", "analyzers"]


def read_corpus(path):
    """The corpus entries by name; corpus.json allows // comments."""
    text = "\n".join(line for line in path.read_text().splitlines() if not line.lstrip().startswith("//"))
    text = re.sub(r",(\s*//[^\n]*)", ",", text)
    return {entry["name"]: entry for entry in json.loads(text)["repositories"]}


def read_summary(summary_md):
    """
    Per repository from a round's summary.md table (what the round reports, so the site says the same): failing
    mutants, missed, inconclusive and the share selected for failing mutants, in percent.
    """
    results = {}
    for line in summary_md.read_text().splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 7 or cells[2] != "done":
            continue
        results[cells[0]] = {"failing": int(cells[3]), "missed": int(cells[4]),
                             "inconclusive": int(cells[5]) if cells[5].isdigit() else 0,
                             "share": int(cells[6].split()[0])}
    return results


def read_precision(synthetic_md):
    """The median number of classes run per class that failed, from a repository's synthetic.md."""
    return float(re.search(r"ran ([\d.]+) test classes per class that failed", synthetic_md.read_text()).group(1))


def upper_bound(misses, trials, confidence=0.95):
    """One-sided Clopper-Pearson upper bound, as TestDetta.Mutants.MissRate computes it."""
    alpha = 1 - confidence
    if misses == 0:
        return 1 - alpha ** (1 / trials)

    def cdf(p):
        return sum(math.comb(trials, i) * p ** i * (1 - p) ** (trials - i) for i in range(misses + 1))

    low, high = misses / trials, 1.0
    for _ in range(60):
        middle = (low + high) / 2
        low, high = (middle, high) if cdf(middle) > alpha else (low, middle)
    return high


def page(language, relative):
    return ROOT / "site" / ("src" if language == "" else f"i18n/{language}") / relative


def replace_block(text, name, block, where):
    pattern = re.compile(rf"(<!-- corpus:{name} -->\n).*?(<!-- /corpus:{name} -->)", re.S)
    if not pattern.search(text):
        raise SystemExit(f"{where}: no <!-- corpus:{name} --> block")
    return pattern.sub(lambda m: m.group(1) + block + m.group(2), text, count=1)


def chart(rows):
    """The bars of the home page's chart: x from 36 to 720, y from 150 (0%) to 10 (100%)."""
    step = 680 / len(rows)
    width = round(step * 0.62, 1)
    lines = ['<g class="bars">']
    for i, row in enumerate(rows):
        height = round(row["share"] * 1.4, 1)
        x = round(36 + (step - width) / 2 + 4 + i * step, 1)
        lines.append(f'<g class="row"><title>{row["name"]}: {row["share"]}%</title>'
                     f'<rect class="bar" x="{x}" y="{round(150 - height, 1)}" width="{width}" height="{height}" rx="2"/></g>')
    lines.append("</g>")
    median = statistics.median(row["share"] for row in rows)
    y = round(150 - median * 1.4, 1)
    lines.append(f'<line class="median" x1="36" x2="720" y1="{y}" y2="{y}"/>'
                 f'<text class="median-label" x="44" y="{round(y - 6, 1)}">median {{{{corpusMedianShare}}}}</text>')
    lines.append(f'<text class="axis" x="40" y="172">{rows[0]["name"]}</text>'
                 f'<text class="axis" x="720" y="172" text-anchor="end">{rows[-1]["name"]}</text>')
    return "\n".join(lines) + "\n"


def table(rows):
    lines = []
    for row in rows:
        slug = row["url"].removeprefix("https://github.com/").removesuffix(".git")
        tags = "".join(f'<span class="tag">{TAG_LABELS[t]}</span>' for t in row["tags"] if t in TAG_LABELS)
        share = row["share"]
        lines.append(
            f'<tr><td data-value="{slug.lower()}"><a href="https://github.com/{slug}">{slug}</a><span class="tags">{tags}</span></td>'
            f'<td class="num" data-value="{row["failing"]}">{row["failing"]}</td>'
            f'<td class="share" data-value="{share}"><span class="share-cell"><svg viewBox="0 0 100 8" aria-hidden="true">'
            f'<rect class="track" width="100" height="8" rx="4"/><rect class="fill" width="{max(share, 1)}" height="8" rx="4"/></svg>'
            f'<span>{share}%</span></span></td>'
            f'<td class="num" data-value="{row["precision"]:.1f}">{row["precision"]:.1f}</td>'
            f'<td class="num" data-value="{row["missed"]}">{row["missed"]}</td></tr>')
    return "\n".join(lines) + "\n"


def hard_cases(text, counts, where):
    """
    Sets the hard-case tiles' counts and sorts them by count, keeping each language's labels. Each tile names the
    tag it counts (data-tag); a page without them yet takes HARD_TAGS in order, which is how the tiles were written.
    """
    match = re.search(r'(<div class="hard" data-reveal>)(.*?)(</div>)\n', text)
    tiles = re.findall(r'<div(?: data-tag="([^"]+)")?><strong>\d+</strong><span>(.*?)</span></div>', match.group(2)) if match else []
    if len(tiles) != len(HARD_TAGS):
        raise SystemExit(f"{where}: expected {len(HARD_TAGS)} hard-case tiles")
    tiles = [(tag or HARD_TAGS[i], label) for i, (tag, label) in enumerate(tiles)]
    tiles.sort(key=lambda t: -counts[t[0]])
    body = "".join(f'<div data-tag="{tag}"><strong>{counts[tag]}</strong><span>{label}</span></div>' for tag, label in tiles)
    return text[:match.start()] + match.group(1) + body + "</div>\n" + text[match.end():]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--round", required=True, type=Path)
    parser.add_argument("--override", action="append", default=[], type=Path)
    parser.add_argument("--corpus", default=ROOT / "eval" / "corpus.json", type=Path)
    args = parser.parse_args()

    corpus = read_corpus(args.corpus)
    results = {}
    for folder in [args.round, *args.override]:
        folder = folder.expanduser()
        for name, result in read_summary(folder / "summary.md").items():
            results[name] = {**result, "precision": read_precision(folder / name / "synthetic.md")}

    rows = []
    for name, result in results.items():
        if result["failing"] == 0:
            continue
        rows.append({"name": name, "url": corpus[name]["url"], "tags": corpus[name].get("tags", []), **result})
    rows.sort(key=lambda r: (r["share"], r["name"]))

    failing = sum(r["failing"] for r in rows)
    missed = sum(r["missed"] for r in rows)
    bound = upper_bound(missed, failing) * 100
    values = {
        "corpusRepositories": str(len(rows)),
        "corpusFailingChanges": f"{failing:,}",
        "corpusMissed": str(missed),
        "corpusUpperBound": f"{math.ceil(bound * 100) / 100:.2f}%",
        "corpusMedianShare": f"{statistics.median(r['share'] for r in rows):g}%",
        "corpusQuarterOrLess": str(sum(1 for r in rows if r["share"] <= 25)),
        "corpusInconclusive": str(sum(r["inconclusive"] for r in rows)),
    }

    site_json = ROOT / "site" / "site.json"
    settings = json.loads(site_json.read_text())
    settings.update(values)
    site_json.write_text(json.dumps(settings, indent=2, ensure_ascii=False) + "\n")

    median = f"{statistics.median(r['share'] for r in rows):g}"
    bound_text = f"{math.ceil(bound * 100) / 100:.2f}"
    for language, form in LOCAL_FORMATS.items():
        strings_json = ROOT / "site" / "i18n" / "strings" / f"{language}.json"
        strings = json.loads(strings_json.read_text())
        strings["corpusFailingChanges"] = f"{failing:,}".replace(",", form["thousands"])
        strings["corpusMedianShare"] = form["percent"].format(median.replace(".", form["decimal"]))
        strings["corpusUpperBound"] = form["percent"].format(bound_text.replace(".", form["decimal"]))
        strings_json.write_text(json.dumps(strings, indent=2, ensure_ascii=False) + "\n")

    counts = {tag: sum(1 for r in rows if tag in r["tags"]) for tag in HARD_TAGS}
    for language in LANGUAGES:
        home = page(language, "index.html")
        home.write_text(replace_block(home.read_text(), "chart", chart(rows), home))
        examples = page(language, "docs/examples.html")
        text = replace_block(examples.read_text(), "table", table(rows), examples)
        examples.write_text(hard_cases(text, counts, examples))

    print(json.dumps(values, indent=2))


if __name__ == "__main__":
    main()
