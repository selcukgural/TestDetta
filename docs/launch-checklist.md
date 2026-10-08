# Launch day checklist

The steps that turn selling on, in order. Each one changes something live, so each is done with the owner's go
the same day. State before launch (2026-10-08): Polar production approved (account review, identity, payout
account), products and checkout link made, webhook and Resend key in Secret Manager, production service deployed
(trials off), and one end-to-end purchase verified (100 % code: checkout, webhooks, license record, license e-mail,
cancellation webhook).

1. **Trust the production key.** Add the public half of `td-2026-10` to `LicenseKeys.Trusted` (derived from the
   secret with `openssl pkey -pubout`; the private key is never printed), set `LicenseKeys.Enforced` as decided,
   update the site's text that says no license verifies before launch (five languages), release.
2. **Test the license in CI (owner, 2026-10-08: must not be skipped).** With the released `td`, put a real license
   (the owner's test license `P-90ec174e-…` runs until 2026-11-08) in a repository's `TESTDETTA_LICENSE` secret
   and run the Action: `td license status` must show the signature verified, the scope (`github.com/selcukgural/*`)
   matching the repository, the seat count, and a refresh through `license.testdetta.com`. Also run it locally with
   `td license install <file>`.
3. **Open the checkout.** Put the checkout link
   (`https://buy.polar.sh/polar_cl_7qgdSlOlLdrZJ1PE6WY3OnEADd2Z8cCkdw5yT0ED0uR`) into `site/site.json`
   `checkoutUrl`, deploy the site, and click through from the pricing page.
4. **Open trials.** `TRIALS=true deploy/license-service/deploy.sh production`, `site.json` `trialsOpen: true`, then
   one trial from the live site to an address the owner reads.
5. **Watch the first day.** The service's logs (webhooks "Saved", mails sent, no warnings) and Resend's delivery
   page.

Done before launch: the sandbox has its own Cloud SQL instance (2026-10-08), so no sandbox credential reaches the
production database.
