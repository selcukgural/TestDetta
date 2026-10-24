# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.3.0 (2026-10-20).

## Behaviour users must know about

- **TestDetta is free and open source.** Apache-2.0, for every repository, public or private, on any CI and on
  developer machines: no license, trial or seats, and selection always runs. The source is public at
  https://github.com/selcukgural/TestDetta; donations through [GitHub Sponsors](https://github.com/sponsors/selcukgural).
- **License settings are ignored, and `td license` is gone.** `td` reads no license: `TESTDETTA_LICENSE`,
  `TESTDETTA_LICENSE_FILE` and the other `TESTDETTA_LICENSE*` variables and `~/.testdetta/license` are ignored and can
  be removed, and so can the CI secret that held the license. `td license status` and `td license install` no longer
  exist; remove calls to them from scripts (they end with exit code 2 and say why).
- **The Action's `license` input is deprecated and ignored.** Existing workflows keep working; the log says the input
  is ignored. Remove it from your workflow.
- **`td affected --format json` has no `license` field any more,** and Markdown summaries and text reports no longer
  have a license line. A script that reads the field must stop expecting it. The audit report
  (`testdetta-audit.json`) moves to schema 4: its `license` field is replaced by `network`, the note that TestDetta
  itself made no network call.
- **`td` makes no network calls.** The daily license refresh is gone; `td` itself never uses the network. The builds
  and test runs it starts restore packages as your own do.
- **Map format 5: maps recorded by 0.3.0 are ignored until recorded again.** They count the setup of an xUnit v3 assembly
  fixture declared in a shared test library as code outside every test, so a change to that setup could miss every class
  that names nothing of it. Until the first recording on the default branch after upgrading, those test projects are
  selected by names, which selects more (Limitations, "Maps recorded by an earlier version").

## Known gaps

## Changes

- **`td record --incremental` no longer depends on a license.** It plans partial recordings on every repository;
  before, a machine without a covering license recorded every project in full.
- **An xUnit v3 assembly fixture from a shared library serves every class.** `[assembly: AssemblyFixture(typeof(X))]`
  sets `X` up for every class of the test assembly. When `X` was declared in the test assembly, a change to its setup, or
  to code only the setup runs, already selected every class; when `X` (or the base class with its setup) lives in a test
  library the test project references, it now does too. Before, no class was selected unless it named that code.
