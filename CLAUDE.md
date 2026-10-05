# TestDetta

.NET test impact analysis: given a git diff, select only the projects and tests it can affect.

- Build: `dotnet build` (warnings are errors) · Test: `dotnet test`
- Try the CLI: `dotnet run --project src/TestDetta.Cli -- affected --base HEAD --verbose`
- Run only the affected tests: `dotnet run --project src/TestDetta.Cli -- test --base HEAD` (`--dry-run` to print the commands)
- Record coverage maps (per test class, at HEAD, into `.testdetta/coverage/`): `dotnet run --project src/TestDetta.Cli -- record`.
  Maps recorded at the merge base are used as they are; older ones (an ancestor within `--max-map-age`) through the drift bridge (docs/design/map-drift-bridge.md). `CoverageRecorderTests` is slow (`Category=Slow`).
- The SDK is pinned to 10.0 in `global.json`; the machine's 11.0 preview SDK is broken.
- The atlas (`docs/atlas/`, Turkish, interactive) documents every flow at code level; a change to a flow,
  class, signature or rule it describes updates it in the same change (`node docs/atlas/build.mjs`, see its README).

Follow the coding guidelines below for every change, and keep `CONTRIBUTING.md` up to date when a
rule changes.

@CONTRIBUTING.md
