# Backlog

Work agreed on but not started, in the order we plan to take it. The evaluation corpus comes
first: every finding there is fixed before anything below.

## Now: evaluation corpus, round 1

- Finish the queue in `eval/corpus.json` (sequential, one repository at a time), fix what each
  repository reveals, with a test for every fix.
- Rerun the repositories evaluated before later fixes: Roslynator, Polly, NodaTime, Mapperly,
  Spectre.Console, CommunityToolkit, AutoMapper (round 2), and write the consolidated results into
  `docs/evaluation.md`.
- Track new findings per repository; maturity shows when several repositories in a row find none.

## Next

1. **More mutant kinds** in `sift-eval`: besides "throw at method start", change return values,
   constants and fields, attribute arguments, and single branches (Stryker.NET's operators are a
   good reference).
2. **Precision round**: safety fixes made selection broader (NodaTime 16% → 34%); measure where
   the extra classes come from and narrow without losing safety (override names scoped to their
   hierarchy, run-once rules, name spreading through common member names like `Initialize`).
3. **Hung mutants**: find which test class hung (VSTest blame-hang, testing-platform hang dump)
   so hangs count as data instead of inconclusive (Polly: 15 of 100).
4. **`sift record --no-build`**: reuse the pipeline's build output instead of building again; the
   GitHub Action records after the build step. Needs the same configuration and PDBs.
5. **Method-level selection, measured first**: record buckets per test method as an option and
   compute how much a method-level filter would save over class-level before building it.

## Later

- Windows: build, record and select on Windows; path and process differences.
- CI in practice: map cache hit rates and end-to-end times in a real pipeline.
- Product name before any public launch (Sift is taken on the Marketplace and NuGet).
- Paid tier: hosted map store and ROI history.
