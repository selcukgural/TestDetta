# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.3.0 (2026-10-20).

## Behaviour users must know about

- **Map format 5: maps recorded by 0.3.0 are ignored until recorded again.** They count the setup of an xUnit v3 assembly
  fixture declared in a shared test library as code outside every test, so a change to that setup could miss every class
  that names nothing of it. Until the first recording on the default branch after upgrading, those test projects are
  selected by names, which selects more (Limitations, "Maps recorded by an earlier version").

## Known gaps

## Changes

- **An xUnit v3 assembly fixture from a shared library serves every class.** `[assembly: AssemblyFixture(typeof(X))]`
  sets `X` up for every class of the test assembly. When `X` was declared in the test assembly, a change to its setup, or
  to code only the setup runs, already selected every class; when `X` (or the base class with its setup) lives in a test
  library the test project references, it now does too. Before, no class was selected unless it named that code.
