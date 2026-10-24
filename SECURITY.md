# Security policy

## Reporting a vulnerability

Please do not open a public issue. Report it privately, either way:

- [Report a vulnerability](https://github.com/selcukgural/TestDetta/security/advisories/new) through GitHub's private
  vulnerability reporting, or
- e-mail support@testdetta.com.

Say what is affected (the `td` tool, the GitHub Action `TestDetta/action`, the Claude Code plugin or the site), the
version, and how to reproduce it. You get an answer within a few days. Please give us time to release a fix before you
publish; we credit you in the advisory unless you ask us not to.

## Supported versions

Fixes go into the next release of the latest version. TestDetta is pre-1.0, so there are no maintained older lines:
update to the latest `TestDetta` package on nuget.org, or pin the Action to its latest release.

## What TestDetta touches

`td` reads your repository and its build output, runs `dotnet` and `git`, and makes no network call of its own. What it
writes, where and for how long is listed on the [security page](https://testdetta.com/docs/security/).
