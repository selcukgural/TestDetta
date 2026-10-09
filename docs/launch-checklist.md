# Launch day checklist

The steps that turn selling on, in order. Each one changes something live, so each is done with the owner's go
the same day. State before launch (2026-10-08): Polar production approved (account review, identity, payout
account), products and checkout link made, webhook and Resend key in Secret Manager, production service deployed
(trials off), and one end-to-end purchase verified (100 % code: checkout, webhooks, license record, license e-mail,
cancellation webhook). The pre-launch review of the same day (licensed and unlicensed use end to end) added the
"Before launch day" steps and the distribution steps below.

## Before launch day (owner)

- [ ] **Deploy the license service** with the review's fixes (commit 759c064 and later: checkout scopes normalised
      and checked, admin scopes and revocations kept over Polar's events, full refunds revoke, subscription licenses
      at most 40 days ahead, the admin job): `deploy/license-service/deploy.sh production`. Verified on the sandbox
      the same day. Then `deploy/license-service/admin.sh production attention` must answer, and
      `admin.sh production show --id P-90ec174e-…` must show the test license's scopes as `github.com/selcukgural/*`
      (fix them with `admin.sh production update --id … --scope github.com/selcukgural --note …` if not).
- [x] **Polar webhook:** add the `order.refunded` event to the production webhook, next to `subscription.*`.
- [x] **Polar checkout field `scope`:** its help text shows the format, for example "github.com/your-org for all its
      repositories, or github.com/your-org/app".
- [ ] **Production Resend key:** `admin.sh production send --id P-90ec174e-… --to <an address you read> --note "key check"`
      delivers the license mail. The sandbox key was found invalid on 2026-10-08 (Resend: "API key is invalid");
      replace it before testing on the sandbox again.
- [ ] **nuget.org:** organization `TestDetta`, prefix reservation asked of `account@nuget.org`, Trusted Publishing
      policy (owner `TestDetta`, repository `selcukgural/TestDetta`, `release.yml`, environment `nuget`). The policy
      was made on 2026-10-08 and is only temporarily active (private repository) until about 2026-10-15; if launch
      is later, reactivate it first, or the release cannot log in to nuget.org (nothing is published then).
- [x] GitHub: environment `nuget` (tags `v*`), variable `NUGET_USER`, public repository `TestDetta/action`
      (2026-10-08). Any pushed `v*.*.*` tag now publishes.

## Launch day

Done 2026-10-08 (stage 1, quiet release; checkout, trials and the Marketplace listing still closed): steps 1 to 3.
Production key trusted and enforcement on (bb1a0a4), `TestDetta` 0.1.0 on nuget.org (owned by the `TestDetta`
organization), `TestDetta/action` 0.1.0 at `35e57ee` with tags `v0.1.0` and `v0`, the site pinned to it, and the
license tested in CI with `.github/workflows/license-check.yml` (run 37835407956: valid with refresh, missing without).
The Trusted Publishing policy needed the scope "Push new packages and package versions" for the first package.
0.1.1 (2026-10-08, the first covering license counts, refresh never replaces another license, authors): `TestDetta/action`
at `7c3cda6` with tags `v0.1.1` and `v0`, the site and README pinned to it. The tags moved there from `cb38ce9` on
2026-10-09: the Marketplace refused the description (125 characters or more), so `7c3cda6` changes only that line.
Listed on the Marketplace the same day: https://github.com/marketplace/actions/testdetta (Testing, Continuous integration).
0.1.2 (2026-10-09, dogfooding fixes from AfterApply: uses while initializing a field belong to its readers, the test
framework's lifecycle is not an external callback, interface members answered through their implementations; corpus
check of 43 repositories, 100 failing mutants, 0 missed): `TestDetta/action` at `afc6fb3` with tags `v0.1.2` and `v0`,
GitHub release created, the site and README pinned to it.
0.1.3 (2026-10-09, a name used only inside endpoint handlers is answered by the handlers' coverage; maps record
`nestedMethods`; AfterApply PR #205 integration selection 97 -> 4 of 100 classes; corpus check of 43 repositories, 101
failing mutants, 0 missed): `TestDetta/action` at `a46fb19` with tags `v0.1.3` and `v0`, GitHub release created, the
site and README pinned to it.

1. **Trust the production key and enforce.** Add the public half of `td-2026-10` to `LicenseKeys.Trusted`
   (`admin.sh production public-key`, or `openssl pkey -pubout` from the secret; the private key is never
   printed), set `LicenseKeys.Enforced = true`, and merge the `launch` branch: every text that says no license
   verifies before launch, in five languages, the CLI help, `action.yml`, README and docs. CI green.
2. **Release 0.1.0.** `git tag v0.1.0 && git push origin v0.1.0`; `release.yml` tests, publishes `TestDetta` to
   nuget.org and uploads the action bundle. Copy the bundle into `TestDetta/action`, commit, tag `v0.1.0` and
   `v0`, and publish the release to the Marketplace from GitHub's release page (owner: it asks for the
   Marketplace agreement). Then replace `<owner>/testdetta@<sha>` with `TestDetta/action@<that commit> # v0.1.0`
   and `<package>` with `TestDetta` on the site (five languages), in README and docs/ci.md.
3. **Test the license in CI (owner, 2026-10-08: must not be skipped).** With the released `td`, put a real license
   (the owner's test license `P-90ec174e-…` runs until 2026-11-08) in a repository's `TESTDETTA_LICENSE` secret
   and run the Action: `td license status` must show the signature verified, the scope (`github.com/selcukgural/*`)
   matching the repository, the seat count, and a refresh through `license.testdetta.com`. Also run it locally with
   `td license install <file>`. Without the secret, the same job must run every test and say why.
4. **Open the checkout.** Done 2026-10-09 (owner confirmed the `order.refunded` webhook event and the `scope` help text first). The products are pinned: `deploy.sh production` passes the monthly and yearly ids itself
   (2026-10-08); check that the deployed revision has `Licenses__PolarProducts`. Then `site/site.json` `checkoutOpen: "true"` (the link is in `checkoutUrl` already),
   deploy the site, and click through from the pricing page in every language.
5. **Open trials.** Done 2026-10-09 (rev 00010, ten a day; a trial from the live site reached the owner by mail). `TRIALS=true TRIALS_PER_DAY=10 deploy/license-service/deploy.sh production` (a low daily ceiling
   while trials are new; `admin.sh production trial-release` frees an owner whose name someone else used), `site.json` `trialsOpen: "true"`, then
   one trial from the live site to an address the owner reads, with the repositories typed as a customer would
   (`github.com/your-org`).
6. **Watch the first days.** Alerts by mail first: after the deploy that logs with severities,
   `deploy/license-service/alerts.sh production <address>` (warnings and errors, refused trials). Then `admin.sh production attention` every morning (a checkout whose scopes could not be
   read waits for the owner to write to the buyer and fix them), the service's logs (webhooks "Saved", mails sent,
   no warnings) and Resend's delivery page.

## After launch (known, not blocking)

- An imprint page with a postal address (expected by German-speaking visitors), once there is a company.
- Signing key rotation: releases trust keys by id, so a rotation needs a release that trusts the new key well before
  the service signs with it; trial hashes derive from the signing key and reset with it.

Done before launch: the sandbox has its own Cloud SQL instance (2026-10-08), so no sandbox credential reaches the
production database. Also 2026-10-08: one copyright holder (the owner) across the footer, `LICENSE.txt` and the terms;
the Polar product allowlist, set by `deploy.sh production`; trials name at most three owners, `admin trial-release
--owner` frees one, and `alerts.sh` mails warnings (the daily ceiling, `attention` entries, undelivered licenses) and
refused trials; the terms link Polar's buyer terms.
