# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.2.2 (2026-10-15).

## Behaviour users must know about

- **Map format 4: maps recorded by 0.2.0 to 0.2.2 are ignored until recorded again.** They have no record of the
  classes of an xUnit collection, so a change to a collection fixture's setup could miss a class that never asks for the
  fixture. Until the first recording on the default branch after upgrading, those test projects are selected by names,
  which selects more (Limitations, "Maps recorded by an earlier version").

## Known gaps

## Changes

- **Every class of an xUnit collection uses its collection fixture.** A class in a `[Collection]` whose definition has an
  `ICollectionFixture<T>` counts as a user of that fixture although it never asks for it in its constructor: it may
  depend on what the setup did (a container it started, a connection string it set). A change to the setup, or to code
  only the setup runs, now selects it; before, only the classes naming the fixture were selected.
