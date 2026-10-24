# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied. Last emptied for 0.5.0 (2026-10-24).

## Behaviour users must know about

- **Windows is tested, and .NET Framework test projects record there.** The whole suite now runs on a Windows runner
  for every pull request, .NET Framework 4.6.2 and 4.8 test projects (xUnit v2, MSTest 1 and 3) and legacy (non-SDK)
  solutions included. Before, on Windows: a .NET Framework test project was not recorded ("no test class marked
  itself", the VSTest collector and the tests used two copies of the tracker), and one whose output holds a signed
  assembly such as Newtonsoft.Json ran no test while recording (the tracker had no strong name). Both projects were
  selected by name until they had a map.
- **On Windows, a file that only an item pattern starting with an unknown property can include is a build input
  again.** `<Schemas Include="$(SchemaDirectory)/*.yaml" />` matched no Windows path, so a change to such a file
  skipped the project's tests there; it now runs them, as on Linux and macOS.

## Known gaps

## Changes

- `td audit` works on Windows: it compared the working copy's path with `\` to resolved paths with `/`, and refused
  every repository as writing outside the copy.
- TestDetta's assemblies have a strong name (a public key in the repository).
