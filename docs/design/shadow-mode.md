# Shadow mode: select, run everything, compare

Status: proposed 2026-10-05, for the owner's decision (docs/business/go-to-market.md, phase 1).

## Problem

A team will not let a new tool skip its tests before it has seen the tool be right on its own pull
requests. The corpus evaluation shows that TestDetta is safe on open-source repositories, but a buyer
wants to see it on their own code, with no risk while they look.

## Design

`td test --shadow` (Action input `shadow: 'true'`):

1. Selects as `td test` does and writes the selection (the job summary's decisions, as today).
2. Runs **every** test project completely, as the team does without TestDetta, with one TRX report
   per project. The job's pass or fail is that full run's, so nothing changes for the team.
3. Compares: every failed test class the selection would have left out is a **miss**, listed with the
   change and the decision evidence that left it out. Failures the selection kept are "caught".
4. Reports, in the job summary and as `testdetta-shadow.json` (an artifact):
   - classes and projects selected, of all;
   - time: the full run's real duration per project and class (from the TRX), and the part the
     selection would have run, so the saving is measured, not estimated;
   - misses (expected none) and failures caught.

A miss in shadow mode is the best bug report TestDetta can get: the diff, the decision and the failure.
The summary asks the team to send it (no data leaves on its own).

## Cost

About 250 product lines (option, full run with reports, comparison, summary and JSON), the docs page
and the Action input in five languages, and tests on the sample repository (one change with a
caught failure, one with a miss forced through a stale map).

## Later

A weekly roll-up across pull requests (needs the reports of many runs: an Actions artifact per run
for now, the hosted tier later).
