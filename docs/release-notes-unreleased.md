# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.2.1 (2026-10-15).

## Behaviour users must know about

- **Documentation files count as build inputs only on an exact sign.** A changed `README`, `CHANGELOG`, `LICENSE`,
  `CONTRIBUTING`, `SECURITY`, `CODE_OF_CONDUCT`, `CLAUDE.md`, `AGENTS.md` and the like no longer runs its project
  completely because an item pattern in your MSBuild files could include it; it does when MSBuild builds, embeds or
  copies it, or a target names it, and a test that reads it still runs. Name more such files with the new
  `--not-build-inputs` option (action input `not-build-inputs`). The output lists the changed files this covers. If a
  target of yours takes a documentation file in through a pattern without naming it, name the file there or use
  `--non-code-files project` ([What runs everything](https://testdetta.com/docs/what-runs-everything/#documentation)).

## Known gaps

## Changes

- **An item pattern with a property no longer matches every changed file.** In the scan of the repository's MSBuild
  files, a property, item or metadata TestDetta cannot resolve now stands for any part of a path, and the rest of the
  pattern must match: OpenTelemetry's `<SigstoreBundle Include="$(ArtifactsPath)\bin\…\$(MSBuildProjectName).dll.sigstore.json" />`
  made every pull request's `CHANGELOG.md` entry run every test project, even after 0.2.1. A pattern that is a property
  alone, such as `$(Readme)`, still matches every file.
