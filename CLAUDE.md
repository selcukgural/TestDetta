# Sift

.NET test impact analysis: given a git diff, select only the projects and tests it can affect.

- Build: `dotnet build` (warnings are errors) · Test: `dotnet test`
- Try the CLI: `dotnet run --project src/Sift.Cli -- affected --base HEAD --verbose`
- Run only the affected tests: `dotnet run --project src/Sift.Cli -- test --base HEAD` (`--dry-run` to print the commands)
- Record coverage maps (per test class, at HEAD, into `.sift/coverage/`): `dotnet run --project src/Sift.Cli -- record`.
  Maps are only used when recorded exactly at the merge base. `CoverageRecorderTests` is slow (`Category=Slow`).
- The SDK is pinned to 10.0 in `global.json`; the machine's 11.0 preview SDK is broken.

Follow the coding guidelines below for every change, and keep `CONTRIBUTING.md` up to date when a
rule changes.

@CONTRIBUTING.md
