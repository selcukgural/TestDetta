# Releasing TestDetta

Users get TestDetta two ways: the `td` tool as the `TestDetta` package on nuget.org, and the public
GitHub Action `TestDetta/action`, which downloads that package. Both are free and open source under Apache-2.0; the
source is public at https://github.com/selcukgural/TestDetta.

## One-time setup (owner)

1. **nuget.org:** sign in with the company account, create the organization `TestDetta`, and ask
   nuget.org to reserve the `TestDetta` ID prefix (the verified badge). Under the account's
   *Trusted Publishing*, add a policy: owner `selcukgural`, repository `TestDetta`, workflow
   `release.yml`, environment `nuget`.
2. **This repository:** the environment `nuget`, limited to tags `v*`, and the variable `NUGET_USER`
   (the nuget.org user name in the policy). Until `NUGET_USER` is set, a release only builds the package.
3. **GitHub:** the organization `testdetta` and the public repository `TestDetta/action` (created 2026-10-08). For the
   Marketplace listing, the action name must be unique: `TestDetta` in `action.yml`.

## A release

1. Tag the commit: `git tag v0.1.0 && git push origin v0.1.0`.
2. `.github/workflows/release.yml` runs the quick tests, packs the tool with the tag's version and, with
   the setup done, pushes it to nuget.org through trusted publishing (a short-lived key, no secret).
3. The `action-bundle` job waits until nuget.org serves the package, pins its SHA-512 in `tool.lock`
   and uploads the bundle: `action.yml`, `tool.lock`, `README.md` (its `{{version}}` replaced with the tag's
   version, so the example names the release), `LICENSE` (Apache-2.0).
4. Copy the bundle into `testdetta/action`, commit, tag `v0.1.0`, and move the major tag to it (`v0` while the version is 0.x, `v1` from 1.0.0).
5. Check every README before announcing: the bundled action README (its example names the new version), the NuGet
   README (`dist/nuget/README.md`: frameworks and CI systems as the site states them; a fix
   there needs the next package version), this README and `site/site.json` (`actionRef` pinned to the new action
   commit, `toolVersion` set to the new version for the install lines), and the release notes on `TestDetta/action`, which take the items of `docs/release-notes-unreleased.md`
   (behaviour users must know about first; empty the file after the release). Before copying them, read
   `git log --no-merges <previous tag>..<new tag>` against the file: 0.2.0's notes missed a week of changes made before
   the file existed, among them a new action input. A README fix after tagging is a README-only commit in
   `TestDetta/action` with the version and major tags moved to it.

`action.yml` is the same file in both places. Here it builds TestDetta from source (this repository's
own workflows use the action from the checkout); in the public repository, where there is no source, it
downloads the version in `tool.lock` and refuses a package whose SHA-512 differs.
