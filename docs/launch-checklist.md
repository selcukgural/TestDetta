# Launch checklist

The steps that put TestDetta in users' hands, and the releases since. TestDetta is free and open source under
Apache-2.0 since 2026-10-24, for every repository, public or private, on any CI and on developer machines; the
earlier steps that turned selling on (license service, checkout, trials) were dropped and remain in git history.
How a release is made: docs/release.md.

## Open source (owner, 2026-10-24)

- [x] `LICENSE` (Apache-2.0) at the repository root.
- [x] The next release without licensing (0.4.0): `td` reads no license and makes no network call, `td license` is
      gone, the Action's `license` input is deprecated and ignored (docs/release-notes-unreleased.md).
- [x] The source repository public at https://github.com/selcukgural/TestDetta (2026-10-24), with rulesets on `main` and
      `v*` tags, secret scanning, push protection, CodeQL and private vulnerability reporting; `TestDetta/action` the same.
- [x] Donations through GitHub Sponsors (https://github.com/sponsors/selcukgural), linked from the README.

## Distribution

- [ ] **nuget.org:** organization `TestDetta` (owns the package since 0.1.0), Trusted Publishing policy (owner
      `TestDetta`, repository `selcukgural/TestDetta`, `release.yml`, environment `nuget`), prefix reservation asked
      of `account@nuget.org`.
- [x] GitHub: environment `nuget` (tags `v*`), variable `NUGET_USER`, public repository `TestDetta/action`
      (2026-10-08). Any pushed `v*.*.*` tag now publishes.

## Releases

0.1.0 (2026-10-08, a quiet release): `TestDetta` 0.1.0 on nuget.org (owned by the `TestDetta` organization),
`TestDetta/action` 0.1.0 at `35e57ee` with tags `v0.1.0` and `v0`, the site pinned to it. The Trusted Publishing
policy needed the scope "Push new packages and package versions" for the first package.
0.1.1 (2026-10-08, the package's authors, and two fixes to the since removed licensing): `TestDetta/action`
at `7c3cda6` with tags `v0.1.1` and `v0`, the site and README pinned to it. The tags moved there from `cb38ce9` on
2026-10-09: the Marketplace refused the description (125 characters or more), so `7c3cda6` changes only that line.
Listed on the Marketplace the same day: https://github.com/marketplace/actions/testdetta (Testing, Continuous integration).
0.1.2 (2026-10-09, dogfooding fixes from AfterApply: uses while initializing a field belong to its readers, the test
framework's lifecycle is not an external callback, interface members answered through their implementations; corpus
check of 43 repositories, 100 failing mutants, 0 missed): `TestDetta/action` at `afc6fb3` with tags `v0.1.2` and `v0`,
GitHub release created, the site and README pinned to it.
0.1.3 (2026-10-09, a name used only inside endpoint handlers is answered by the handlers' coverage; maps record
`nestedMethods`; AfterApply PR #205 integration selection 97 -> 4 of 100 classes; corpus check of 43 repositories, 101
failing mutants, 0 missed): `TestDetta/action` at `6ceba36` with tags `v0.1.3` and `v0`, GitHub release created, the
site and README pinned to it. The tags moved there from `a46fb19` the same day: the action README's example still named
v0.1.0, so `6ceba36` changes only that, and the release workflow now writes the tag's version into it.
0.2.0 (2026-10-15, map format 3 with assembly loads, fixture-built tests and shape reads; non-code files run their
readers; the team's test configuration kept in every run; console tables; release check of 53 repositories, 3,071
failing changes, 5 missed in three known gaps, listed on Limitations): `TestDetta/action` at `c7ada8b` with tags
`v0.2.0` and `v0`, GitHub release created, the site and README pinned to it. The weekly recording on this repository's
CI is off until the tests that record inside a recording pass (#71).
0.2.1 (2026-10-15, two precision fixes found by replaying recent commits of MimeKit and OpenTelemetry in shadow mode
on forks: the test host's read of a test project's own output, #77, and `EmbeddedFiles` patterns with a property, #78;
no map format change): `TestDetta/action` at `79251cd` with tags `v0.2.1` and `v0`, GitHub release created, the site
and README pinned to it.
0.2.2 (2026-10-15, documentation files count as build inputs only on an exact sign, the new `--not-build-inputs`
option and `not-build-inputs` input, #83; an unresolved reference in an item pattern stands for a part of a path,
#81; no map format change): `TestDetta/action` at `a2f55a8` with tags `v0.2.2` and `v0`, GitHub release created, the
site and README pinned to it.
0.3.0 (2026-10-20, every class of an xUnit collection uses its collection fixture, #85; map format 4, so maps recorded
by 0.2.x are ignored until recorded again): `TestDetta/action` at `e596523` with tags `v0.3.0` and `v0`, GitHub release
created, the site and README pinned to it.
v0.3.1 (tagged 2026-10-21) was never published: its release run failed while Actions could not run on the private
repository; 0.4.0 carries its fix.
0.4.0 (2026-10-24, free and open source under Apache-2.0: licensing removed, `td` makes no network call; an xUnit v3
assembly fixture from a shared library serves every class, #89; map format 5, so maps recorded by 0.3.0 are ignored until
recorded again; a Claude Code plugin, #90): `TestDetta/action` at `da0e18e` with tags `v0.4.0` and `v0`, GitHub release
created, the site and README pinned to it.
0.5.0 (2026-10-24, shadow mode shows every decision and its JSON report moves to schema 5, #92; a Claude Code plugin
marketplace and a skill for Codex and ChatGPT, #94-#96; no map format change): `TestDetta/action` at `6afa8c0` with tags
`v0.5.0` and `v0`, GitHub release created, the site and README pinned to it.
0.5.1 (2026-10-25, Windows supported: .NET Framework test projects record there and signed outputs load, the whole
suite runs on Windows for every pull request, #100; no map format change): `TestDetta/action` at `2f1a6da` with tags
`v0.5.1` and `v0`, GitHub release created, the site and README pinned to it.
0.5.2 (2026-10-25, recording .NET Framework outputs with facades no longer overflows the stack, #103; a PowerShell
script for Windows agents with Azure DevOps and Jenkins examples, #104; no map format change): `TestDetta/action` at
`8750db1` with tags `v0.5.2` and `v0`, GitHub release created, the site, README and dist/ci examples pinned to it.

## Later (known, not blocking)

- An imprint page with a postal address (expected by German-speaking visitors), once there is a company.
