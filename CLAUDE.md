# Sift

.NET test impact analysis: given a git diff, select only the projects and tests it can affect.

- Build: `dotnet build` (warnings are errors) · Test: `dotnet test`
- Try the CLI: `dotnet run --project src/Sift.Cli -- affected --base HEAD --verbose`
- Run only the affected tests: `dotnet run --project src/Sift.Cli -- test --base HEAD` (`--dry-run` to print the commands)
- The SDK is pinned to 10.0 in `global.json`; the machine's 11.0 preview SDK is broken.

Follow the coding guidelines below for every change, and keep `CONTRIBUTING.md` up to date when a
rule changes.

@CONTRIBUTING.md
