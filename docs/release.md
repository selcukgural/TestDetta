# Releasing TestDetta

Customers get TestDetta two ways: the `td` tool as the `TestDetta` package on nuget.org, and the public
GitHub Action `testdetta/action`, which downloads that package. This repository stays private.

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
   version, so the example names the release), `LICENSE.txt`.
4. Copy the bundle into `testdetta/action`, commit, tag `v0.1.0`, and move the major tag to it (`v0` while the version is 0.x, `v1` from 1.0.0).

`action.yml` is the same file in both places. Here it builds TestDetta from source (this repository's
own workflows use the action from the checkout); in the public repository, where there is no source, it
downloads the version in `tool.lock` and refuses a package whose SHA-512 differs.
