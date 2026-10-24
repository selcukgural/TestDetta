# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.5.1 (2026-10-25).

## Behaviour users must know about

## Known gaps

## Changes

- Recording no longer crashes on .NET Framework test projects whose output holds facades (`System.Runtime.dll`,
  `netstandard.dll`), such as Castle.Core's and log4net's: their references were resolved against the .NET runtime
  `td` runs on, whose `mscorlib` forwards back to those facades, and the loop overflowed the stack (exit code
  -1073741571 on Windows). A .NET Framework output now resolves from .NET Framework (Mono outside Windows), and a
  forwarding loop of any origin ends as an unresolved type.
