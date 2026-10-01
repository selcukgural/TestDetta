# Backlog

Work agreed on but not started, in the order we plan to take it. Reliability and correctness
come first: the evaluation corpus and every finding there before anything below, and features
that report numbers (value reporting) only once the numbers are proven right.

## Now: evaluation corpus, round 1

- Finish the queue in `eval/corpus.json` (sequential, one repository at a time), fix what each
  repository reveals, with a test for every fix.
- Rerun the repositories evaluated before later fixes: Roslynator, Polly, NodaTime, Mapperly,
  Spectre.Console, CommunityToolkit, AutoMapper (round 2), and write the consolidated results into
  `docs/evaluation.md`.
- Track new findings per repository; maturity shows when several repositories in a row find none.
- Round 2 turns on every mutant kind (`--mutant-kinds all`; return-default, flip-branch and
  field-value exist since 1d54474).

## Known gaps behind shipped fixes

Interim fixes that are safe for the common case; come back for the complete solution.

- **Test data credited to both buckets** (interim: 7c33ec6). A data member enters the class that
  uses it and the marker is not undone on return, so lazily built rows stay with that class. A
  test that calls another class's data member directly therefore credits the rest of its own code
  to that class: a possible miss. Complete fix: while data code runs, and until the next class or
  fixture is entered, credit hits to both the data's class and the bucket that was current
  before. Measure the cost on the hit path first (one extra field read per hit) and add a test
  where a test calls another class's data member.
- **Startup time vs. discovery-time data** (interim: 7c33ec6). Theory data enumerated during
  discovery now ends the recorded startup time early and adds discovery time to the first data
  class. Complete fix: count time spent in data members during discovery as startup (it is paid
  by every run), so per-class times and the value report stay right.
- **Files rewritten for other tools match the SDK byte for byte** (interim: 911e519). The patched
  `deps.json` broke xUnit 2.3's own JSON reader through `+` escaping. Complete fix: a test
  that patches real SDK-written `deps.json` files and compares them with the original except for
  the added entries, and the same check for any other file Sift rewrites.

## Next

1. **Precision round**: safety fixes made selection broader (NodaTime 16% → 34%); measure where
   the extra classes come from and narrow without losing safety (override names scoped to their
   hierarchy, run-once rules, name spreading through common member names like `Initialize`).
2. **Value reporting: show exactly what Sift saved, and why it was safe.** Not "you saved five
   minutes" but the breakdown behind it, in formats people and tools use:
   - Per run: every test project and class that ran or was skipped, the reason for each decision
     (the changed member, the coverage map and commit it came from, or the fallback rule), and
     time per project measured against the recorded full-suite time.
   - Measured, not only estimated: main runs the full suite when recording, so the real duration of
     what was skipped is known; report both and the error of the estimate.
   - Over time: per day, week and month; per repository, team and test project; trends of saved
     time, CI minutes and their cost (runner price per minute), and of selection size.
   - Evidence of safety: failures found on main that a PR's selection skipped (should be none), how
     often a fallback ran everything and why, so the numbers can be trusted.
   - Formats: GitHub job summary and PR comment (Markdown), JSON and CSV export, an HTML dashboard,
     a badge, a weekly digest (Slack/e-mail), and an API for the hosted tier.
3. **Method-level selection, measured first**: record buckets per test method as an option and
   compute how much a method-level filter would save over class-level before building it.

## Later

- Windows: build, record and select on Windows; path and process differences.
- CI in practice: map cache hit rates and end-to-end times in a real pipeline.
- Product name before any public launch (Sift is taken on the Marketplace and NuGet).
- Paid tier: hosted map store and ROI history.
