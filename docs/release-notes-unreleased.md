# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.4.0 (2026-10-24).

## Behaviour users must know about

- **Shadow mode shows every decision.** A project's mode, reason and estimate in the shadow report are always the
  selection's own; a class your filter left out is listed with its decision (selected or not, and why) but not
  compared, since none of its tests ran; a project none of whose classes ran says why in `notCompared`. Before, the
  report kept those decisions back (`not-shown`, `partial`). The JSON report moves to schema 5: `notRun` holds objects
  instead of names, and `not-shown` and `partial` are gone.

## Known gaps

## Changes
