# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.2.0 (2026-10-15).

## Behaviour users must know about

## Known gaps

## Changes

- **A pull request that also changes a test no longer runs that test project completely just because the test host read
  its assembly.** NUnit's adapter and the dependency context read the test project's own output (`UnitTests.dll`, its
  `deps.json`) outside every test, and the loaded-output rule took that as a use to inspect: on MimeKit's own recent
  commits, 8 of 10 ran all 197 classes. That read is now left out for the test project itself; its changes are answered
  by its own coverage, and a class that loads its own output still runs.
- **A `CHANGELOG.md` entry no longer runs every test project when an `EmbeddedFiles` item uses a property.** The scan
  of the repository's MSBuild files counted `<EmbeddedFiles Include="$(GeneratedAssemblyInfoFile)" />` (OpenTelemetry's
  `src/Directory.Build.targets`) as matching every file. MSBuild's evaluation now reports the files embedded in the PDB,
  which stay build inputs, and the scan leaves that item type to it. Cached project evaluations are made again once.
