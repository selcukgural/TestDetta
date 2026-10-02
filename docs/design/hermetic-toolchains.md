# Hermetic toolchains for the evaluation

Status: implemented 2026-10-02 (`Toolchain`, `ToolchainLock`, `ToolchainInstaller`, `ToolchainScope`). Backlog item 0 ("Hermetic, reproducible runners").

## Problem

Every corpus run uses one shared .NET installation (`~/sift-corpus/dotnet-sdk`: SDK 10.0.401 with
runtimes 10.0.12 and 8.0.31 added by hand). The nightly run uses whatever `setup-dotnet` installs.
So:

- Adding a runtime means changing an installation that running evaluations use. On 2026-10-02
  `dotnet-install.sh` replaced the `dotnet` host in place, macOS killed every new `dotnet`, and a
  night of results had to be thrown away.
- A result does not say which SDK and runtimes produced it, and the machine and CI can differ.
- Repositories that need another runtime (net9.0 only, x64 only) cannot be added without touching
  the shared installation again.

## Goal

Each repository is evaluated with a toolchain named in the corpus (SDK version, extra runtimes,
architecture). The toolchain is installed once into its own directory, which is never changed
afterwards, from archives whose SHA-512 is pinned in the repository. Evaluations on the machine and
in CI with the same corpus entry run the same bits, and the report names them.

## Design

### Toolchain spec

```jsonc
"defaults": { "toolchain": { "sdk": "10.0.401", "runtimes": ["8.0.31"] } },
"repositories": [ { "name": "x", "toolchain": { "sdk": "10.0.401", "runtimes": ["9.0.12"], "architecture": "x64" } } ]
```

- `sdk`: exact SDK version; it brings its own runtime (10.0.401 → 10.0.12).
- `runtimes`: extra ASP.NET Core runtime versions (the archive holds `Microsoft.NETCore.App` and
  `Microsoft.AspNetCore.App` of that version).
- `architecture`: `arm64` or `x64`, default the machine's. `x64` on Apple silicon runs under
  Rosetta, for suites with x64-only native dependencies.
- An entry's toolchain replaces the default one as a whole. The repository's `global.json` must
  accept the pinned SDK; if it does not, the build fails with the SDK's own message, which says
  what to pin.
- Single-repository runs take `--sdk <version>`, `--runtime <version>` (repeatable) and
  `--architecture`; without them nothing changes (the ambient `dotnet`), so local use stays simple.

### Installation

- Root: `--toolchains <dir>` (corpus: `~/sift-corpus/toolchains`, CI: `$RUNNER_TEMP/toolchains`).
  A toolchain lives in `<root>/<rid>/sdk-<version>[+<runtime>...]`, for example
  `osx-arm64/sdk-10.0.401+8.0.31`.
- Built in `<root>/.partial-<guid>`, then renamed into place; the rename is atomic on one file
  system, so a reader sees a complete toolchain or none. When two evaluations install the same one
  at once, the second rename fails, the loser deletes its copy and uses the winner's. A finished
  toolchain directory is never written again; a new runtime is a new toolchain.
- Archives come from the URLs in Microsoft's release metadata
  (`builds.dotnet.microsoft.com/dotnet/release-metadata/<major>/releases.json`) and are verified
  against SHA-512 values in `eval/toolchains.lock.json`, committed like `packages.lock.json`:
  - A version missing from the lock is resolved from the metadata and added for every supported
    RID (`osx-arm64`, `osx-x64`, `linux-x64`), so a lock written on the Mac serves CI too.
  - With `CI=true` the lock is read-only: a missing entry fails the run (locked mode, as for NuGet).
  - A download whose hash differs from the lock fails the run; nothing is extracted.
- Extraction with `System.Formats.Tar` into a scratch directory per archive. The SDK archive is the
  base (its host is the newest); from a runtime archive only `shared/<framework>/<version>` folders
  are taken, so no host or `hostfxr` is ever replaced.

### Using a toolchain

While a repository is evaluated, the process environment points at its toolchain and every child
inherits it (builds, test runs, recording, setup and cleanup commands):
`DOTNET_ROOT` (and `DOTNET_ROOT_X64` for x64), `DOTNET_HOST_PATH`, `PATH` with the toolchain first,
`DOTNET_MULTILEVEL_LOOKUP=0`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1`,
`MSBUILDDISABLENODEREUSE=1` (no MSBuild node outlives a build and holds a pipe or a file). The
previous values are restored afterwards; repositories of a corpus run one after another.

`synthetic.json` and the summary record the toolchain (SDK, runtimes, RID) of each repository.

## Tests

- Installer against a fake release source serving small archives built in the test: installs,
  verifies hashes, rejects a wrong hash without leaving a directory, takes only `shared/` from
  runtime archives, keeps a finished toolchain untouched, handles two installs of the same
  toolchain at once, locked mode with a missing entry.
- Scope sets and restores the environment.
- Corpus parsing of `toolchain` with defaults and overrides.
- One manual run of a real install (10.0.401 + 8.0.31) and a corpus repository on it, compared with
  the shared installation's result.

## Verification (2026-10-02)

SDK 10.0.401 + 8.0.31 installed from the metadata into `~/sift-corpus/toolchains/osx-arm64/`:
host, SDK, 10.0.12 runtimes and ASP.NET Core 8.0.31 are byte for byte the files of the hand-made
corpus installation. `Microsoft.NETCore.App/8.0.31` differs in its native libraries only (same
version; the ASP.NET Core archive's copies are signed separately from the runtime archive's).

Caches: with a toolchain the recording key and the outcome cache file name include it, so results
of the shared installation are not mixed in. Moving the corpus to toolchains therefore records
again; outcome files may be carried over by renaming once a run shows the same results.

## Out of scope

Windows toolchains (`zip` archives), workloads (MAUI, WASM), and container isolation of whole runs.
