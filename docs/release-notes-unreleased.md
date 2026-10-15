# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.2.1 (2026-10-15).

## Behaviour users must know about

## Known gaps

## Changes

- **An item pattern with a property no longer matches every changed file.** In the scan of the repository's MSBuild
  files, a property, item or metadata TestDetta cannot resolve now stands for any part of a path, and the rest of the
  pattern must match: OpenTelemetry's `<SigstoreBundle Include="$(ArtifactsPath)\bin\…\$(MSBuildProjectName).dll.sigstore.json" />`
  made every pull request's `CHANGELOG.md` entry run every test project, even after 0.2.1. A pattern that is a property
  alone, such as `$(Readme)`, still matches every file.
