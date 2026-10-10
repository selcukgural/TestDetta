# Licensing

Status: phase 1 implemented 2026-10-03 (see "As built"); enforced since launch (`LicenseKeys.Enforced`,
with the production key `td-2026-10` trusted). Decisions
marked **(decided)** were made with the user.

## Goals

- Any CI works the same way: GitHub Actions, GitLab, Jenkins, Bitbucket/Bamboo, Azure DevOps,
  TeamCity, and on-premises installations with no outbound network at all. Legacy setups at large
  companies are the expected case, not an edge case **(decided)**.
- A licensing problem never breaks a customer's build. Without a valid license, TestDetta runs every
  test and says why **(decided)**.
- Not tied to a payment provider. Polar.sh, Paddle and direct invoices (Turkish corporate customers
  paying in TL) all produce the same license (docs: payments research, 2026-10-02).
- No personal data leaves the customer's machines: committer emails are only counted locally.

Not a goal: stopping a determined attacker. TestDetta is a .NET tool and can be decompiled. Licensing
keeps honest customers honest and makes paying the easy path.

## Pricing unit and free use

- **Active committers (decided):** the price is per active committer per month.
- **Free (decided):**
  - public repositories;
  - a time-limited trial (30 days, full features, no card).

## The license

A license is a small JSON document signed with ECDSA P-256 (SHA-256; built into .NET, unlike Ed25519). It is passed to TestDetta as one string:
`testdetta1.<base64url payload>.<base64url signature>`.

```json
{
  "v": 1,
  "id": "L-2026-0042",
  "kid": "2026-1",
  "plan": "team",
  "kind": "subscription",
  "licensee": "Acme Corp",
  "seats": 5,
  "scopes": ["github.com/acme/*", "gitlab.acme.local/*", "bitbucket.acme.corp/scm/pay/*"],
  "issued": "2026-10-01T00:00:00Z",
  "validFrom": "2026-10-01T00:00:00Z",
  "validUntil": "2026-11-08T00:00:00Z",
  "refresh": "https://license.<domain>/v1/licenses/L-2026-0042"
}
```

- **Signature.** Only the license service holds the private key: a cloud KMS or secrets manager,
  never in a repository or in CI. TestDetta embeds the public keys by key id (`kid`), so a key can be
  rotated: new TestDetta versions trust the new key, and licenses are reissued under it.
- **Kinds:**
  - `subscription`: valid for the billing period plus 7 days, renewed by refresh, and never more than 40 days
    ahead of its signing (`LicenseIssuer.MaxAhead`, 2026-10-08), so a yearly plan's string cannot outlive a
    refund or revocation by months;
  - `annual`: offline customers, valid for the term plus 30 days;
  - `trial`: 30 days, one per scope.
- **Scopes** are patterns over normalised git remote URLs: host and path, without scheme, user,
  password or `.git`. An air-gapped company's license names its internal git server.

## Verification in TestDetta (every run, offline)

1. **Find the license.** In order:
   - `TESTDETTA_LICENSE` (the string);
   - `TESTDETTA_LICENSE_FILE` (a path);
   - the refreshed copy in `~/.testdetta/license` (see below);
   - `.testdetta/license` in the repository (allowed, though a secret variable is recommended).
   Every whitespace character is dropped, not only at the ends (`License.Normalize`): a license holds
   none, and e-mail clients, terminals and secret forms break or indent a long string when it is copied.
   Every CI has a way to pass a secret variable or file: GitHub/GitLab secrets, Jenkins
   credentials, Bitbucket and Azure DevOps secret variables, TeamCity parameters.
   TestDetta removes `TESTDETTA_LICENSE` and `TESTDETTA_LICENSE_FILE` from every process it starts
   (`dotnet test`, builds, recording and audit runs; `LicenseInputs.KeepOutOf`): the team's tests and its
   packages' build steps run there and have no use for the license.
2. **Check the signature** against the embedded key for `kid`.
3. **Check the scope.** The repository identity is the `origin` remote, normalised. Credentials
   embedded in the URL (common with Jenkins) are stripped before matching, and never logged.
   `TESTDETTA_LICENSE_SCOPE` overrides the identity when there is no usable remote. One of the license's
   scopes must match.
4. **Check the time.** The current time is the later of the system clock and the newest commit's
   committer date on HEAD. Moving the clock back does not bring an expired license back while
   commits keep arriving.
5. **Count active committers** (below) and compare with `seats`.
6. **Decide:**
   - valid, or within a tolerance or grace period: select tests normally;
   - otherwise: run every test.
   Either way, the reason goes to the log, the Markdown summary and the JSON output (`license`
   field): valid, expiring in N days, over seats, expired, wrong scope, invalid, missing.

A **public repository** needs no license. TestDetta knows that only where the CI says so: on GitHub
Actions, `repository.private` in the event payload (`GITHUB_EVENT_PATH`); on GitLab,
`CI_PROJECT_VISIBILITY`. Anywhere else, a repository counts as private.

## Active committers (decided)

An active committer is a distinct author with at least one non-bot commit whose **author date** is
within the last 30 days, among the commits reachable from the analysed HEAD.

- **Author date**, not committer date or merge date. Merging, rebasing and cherry-picking keep it.
  Old work brought into the pipeline now (a long-lived branch by people who have left) does not
  count. Its authors are not active, and whoever merges it is.
- **Squash merges** produce one new commit by the person who squashed (or the PR author).
  `Co-authored-by` trailers are not counted.
- **Identity:** the author email, folded through `.mailmap`, case-insensitive.
- **Bots** are excluded:
  - GitHub `*[bot]`, `dependabot`, `renovate`, `github-actions`;
  - GitLab `project_*_bot*` and `group_*_bot*`;
  - common CI service accounts (`jenkins`, `buildbot`).
  A customer can add patterns in `.testdetta/license.json` (`"bots": [...]`). The list is shown in
  `td license status`, so it cannot hide people quietly.
- **Per pipeline:** each run counts its own HEAD. Unmerged branches are counted by their own
  pipelines. Across an organisation's repositories, TestDetta's count is per repository, since no
  identity leaves the machines.
- **What a seat is (decided 2026-10-08, owner):** the seats cover the active committers of **all** the
  repositories a license covers together, not each repository on its own. A team of 50 spread over 10
  repositories of 5 committers each needs 50 seats, although every repository's own count shows 5. This
  is written into the terms, the pricing page and the license documentation; TestDetta enforces only the
  per-repository count it can see, and the rest is honest use, as for the rest of the license. Annual
  (invoiced) customers settle the organisation-wide total by a yearly declaration (true-up). Rejected:
  counting across repositories in the license service from hashed author identities, which would break
  the promise that no author data leaves the customer's machines and would not work offline.
- Author dates can be forged (`GIT_AUTHOR_DATE`), like any client-side check. That is accepted.

## Over the seat count (decided)

1. **Tolerance:** up to 20% over `seats`, at least one person, is fine. Holidays, an intern, an
   outside contributor.
2. **Warning period, 30 days:** above the tolerance, TestDetta keeps selecting and warns in every
   summary, for example: "6 active committers in the last 30 days; the license covers 5. Add seats:
   <link>". How long the count has been over is computed from the history on every run: for
   each of the last days, the 30-day window ending that day is counted (60 days of author dates).
   No state is stored, so every CI gets the same answer, ephemeral runners included. If the count
   drops back, the period resets by itself.
3. **After 30 days** still over: as without a license, every test runs, with the reason.

When the customer adds seats during the warning period, the next refresh (or a new key in the
secret) carries the new `seats`, and the warning disappears.

## Expiry

- **Warnings** start 14 days before `validUntil`.
- **After `validUntil`**, every test runs. The 7-day (subscription) or 30-day (annual) slack is
  already inside `validUntil`, so there is a single date to explain.

## Refresh (decided: automatic, optional)

A license carries its `refresh` URL. At most once a day, TestDetta asks for the current version of the
license:

- `GET <refresh>` with the license id in the path and `Authorization: TestDetta <current license string>`.
  The license string never goes into a URL or a query string.
- It runs next to the analysis with a 6-second timeout, so its wait mostly overlaps work done anyway. A
  failure that may pass (no answer, a timeout, 5xx or 429) is tried again after an hour; a refusal
  waits the day. A blocked network costs at most a few seconds an hour.
  `TESTDETTA_LICENSE_REFRESH=off` disables it, which offline customers set. `HTTPS_PROXY` is honoured.
- The response is a newer signed license. It is checked exactly like the original and stored in
  `~/.testdetta/license` together with the time of the attempt. Persistent agents (common on-premises)
  keep it between runs. Ephemeral runners refresh again on every run, which is why the service must answer
  within the timeout from idle (a cold start counts).
- A failed refresh changes nothing: TestDetta uses the best valid license it has. The service is an
  update channel, not a gate. That is why `subscription` licenses include 7 days of slack.

Offline customers get `annual` licenses. Renewals and seat changes are delivered as a new license
string or file, and their administrators replace the secret.

## The license service

A small web service, the only holder of the signing key.

- **Customer records:** licensee, scopes, seats, kind, period, source (`polar`, `paddle`, `manual`).
- **Webhooks:** Polar and Paddle subscription created, updated (quantity = seats), renewed,
  cancelled and refunded. Every webhook's signature is verified before anything is stored. Failures
  are counted and handlers are idempotent by event id. Unverified posts are never stored.
- **Manual issuance:** direct invoices (TL, e-Fatura), annual offline licenses and extensions, with
  an audit log.
- **Refresh endpoint:**
  - returns the current signed license for the id when the presented license is valid for it;
  - throttled per license and per IP;
  - returns the same 404 for unknown and revoked ids.
- **Customer portal:** the current license string, a download as a file, scopes, seats, and
  "add seats" (to the provider's checkout).
- **Cancellation and refunds:** no further renewal is issued, and the license runs out at
  `validUntil`, at most 40 days after its last refresh for a subscription. There is no instant revocation
  offline, by design. A full refund (`order.refunded`, status `refunded`) revokes the subscription's license
  for good; a partial one is flagged for an admin.
- **Trials:** sign-up with an email, verified before a key is issued. One trial per scope, and the
  service throttles per address and per scope. As built: "Trials (as built)" below.
- Requests to it never carry personal data from the customer's repositories.

## CLI

- `td license status`: the license found, its validity, scopes, the active committer count and
  the bot patterns. It shows no identities, since its output may end up in CI logs.
- `td license install <string|file>`: stores a license in `~/.testdetta/license` for machines without
  a secret mechanism.
- The GitHub Action gets a `license` input that maps to `TESTDETTA_LICENSE`. Other CIs set the variable.

## Tests (when implemented)

- Signature: valid, tampered payload, unknown `kid`, rotated key.
- Scopes: normalisation of https, ssh, scp-style, and credential-bearing URLs; wildcards; no
  remote with `TESTDETTA_LICENSE_SCOPE`.
- Committers:
  - the long-lived branch case (old authors merged now are not counted);
  - squash, rebase and cherry-pick;
  - `.mailmap`, bots and custom bots.
- Over seats: the tolerance, the 30-day period from HEAD dates, and its reset.
- Time: clock moved back, with newer commit dates.
- Refresh: offline, slow, proxy, newer license, invalid response.
- Degradation: every failure mode runs every test and reports why; none fails the build.

## As built (phase 1, 2026-10-03)

- `TestDetta.Core.Licensing`:
  - `License`: signing and verifying, ECDSA P-256, key ids;
  - `RepositoryIdentity`: remote normalisation and scope matching;
  - `ActiveCommitters`: author-date window, built-in and custom bots, days over;
  - `LicenseCheck` and `LicenseStatus`: every outcome above;
  - `LicenseInputs`: variables, files, CI public flags; git output for remotes and authors is never logged.
- `TestDetta.Cli.LicenseGate` applies the status to `td affected` and `td test`. It is recorded on the
  analysis and in the JSON output (`license`). When enforced and not covering, every test project of
  the repository runs completely, and the text, plan and Markdown summary say why. The analysis
  itself (`AnalysisPipeline`) knows nothing of licenses, so `testdetta-eval` and the corpus measurements
  are unaffected. `td test --shadow` needs no license (owner, 2026-10-07): it starts no refresh, reads no
  license variable or file, and TestDetta itself makes no network call (the Action still downloads the tool
  and uses the Actions cache, and the rerun on the base commit restores packages like any build); in return it shows decisions only for the tests the
  job ran (docs/design/shadow-mode.md, "Free, but no free selection").
- `td license status`, `td license install`, and the action's `license` input.
- **Enforced since launch:** `LicenseKeys.Trusted` holds the production key `td-2026-10` and
  `LicenseKeys.Enforced` is true. Before launch both were off: the status was checked and appeared only in
  the JSON output, the debug log and `td license status`. A test keeps them together: enforcement without
  the production key would run every test for every customer.
- Refresh is phase 2: it needs the service.
- Tests: `LicensingTests` and `LicenseGateTests`, sabotage-checked:
  - a tampered license, an unknown key;
  - remote URL forms with credentials; scopes;
  - the long-lived branch case; bots; days over;
  - each outcome; a clock moved back;
  - enforced and not enforced; JSON and Markdown.

## As built (phase 2, first part, 2026-10-03)

`src/TestDetta.LicenseService`: ASP.NET Core with Postgres through Npgsql and plain SQL, deployed as a
container (`Dockerfile`: images pinned by digest, the image's non-root user, the signing key mounted
as a file and never baked in or passed as a variable). Hosting: Cloud Run (see "Hosting" below).

- **Records** (`licenses`) are the source of truth. Every change also writes an `audit` row with its
  reason, in the same transaction.
- **Signing:** `SigningKey` loads the ECDSA P-256 key from a PEM file and never logs it. A cloud KMS
  can replace it behind the same members. `LicenseIssuer` adds the slack: 7 days for a subscription,
  30 for annual, none for a trial.
- **Refresh:** `GET /v1/licenses/{id}` with `Authorization: TestDetta <license>`. The presented license
  must be genuine (current or retired key) and for the same id; it may have expired. Unknown,
  revoked, mismatched and unproven requests get the same 404. The response is `no-store`. There is
  a rate limit per client address and per license, and the request body is limited to 16 KiB.
- **Admin:** `dotnet TestDetta.LicenseService.dll admin issue|update|revoke|restore|send|show|attention|list|public-key`.
  In production and the sandbox it runs as the Cloud Run job `<service>-admin` (deploy.sh deploys it from
  the same image and secrets), through `deploy/license-service/admin.sh production|sandbox <command> …`, which
  runs it, prints its output from the project's logs and deletes the execution. There is no admin web UI.
  - `issue` refuses an existing id; `update` on a provider's record only sets `--scope` (seats and the
    period come from the provider), and those scopes are kept over the checkout's on every later event
    (`scopes_override`);
  - `revoke` is kept over the provider's later events (`admin_revoked`), as a full refund is; `restore` lifts it;
  - `send --id --to --note` e-mails the current license; the address is not stored, but it stays in the
    project's Admin Activity audit log as the job's argument (Google keeps those 400 days), so to keep none,
    send the string from `show` by hand;
  - `attention [--days n]` lists what needs an admin (checkouts without a usable scope, refused e-mails,
    refunds), newest first, then paid licenses never e-mailed (an e-mail outage longer than Polar's
    retries; `LicenseStore.UndeliveredAsync`);
  - `trial-release --owner <host/owner> --note` lets an owner have a trial again, for an owner whose name
    someone else used in the trial form (which cannot check who owns a repository). `public-key` prints what goes into
  `LicenseKeys.Trusted`.
- **Tests:** `tests/TestDetta.LicenseService.Tests` against a real Postgres, one database per test,
  through the real HTTP pipeline; sabotage-checked. CI runs a Postgres service container
  (`TESTDETTA_TEST_POSTGRES`).

Client addresses behind the host's proxy: `Licenses:TrustedProxies` lists the proxies (IPs or CIDR
networks) whose `X-Forwarded-For` is believed, or `*` for one hop from anywhere when the proxy is the
only way in. With none configured, the header is ignored, so nobody can pick their own address. The
per-address limit counts an IPv6 client by its /64 network (`ClientPartition`). `Licenses:ForwardedHops` (default 1) is how many trusted proxies in a row append to the header: 2
behind Firebase Hosting in front of Cloud Run, where Hosting appends the client and Cloud Run's front end
appends Hosting; each hop is taken only from a trusted proxy.

Secrets as files (2026-10-05): with `TESTDETTA_SECRETS=<folder>`, every file in that folder and its
immediate subfolders is a setting named after the file (`Licenses__ConnectionString`), added last so it
wins over environment variables and appsettings.json, so the connection string, the Polar webhook secret
and the Resend key need not be environment variables. Subfolders exist because Cloud Run mounts one
secret per folder. Keep the signing key's own mount (`Licenses__KeyFile`) outside that folder.

TestDetta's refresh client (`TestDetta.Cli.LicenseRefresher`, same day): before each check, at most once a
day (`~/.testdetta/license-refresh`), started next to the analysis with a 6-second timeout (2 seconds
until 2026-10-05, too short for a service starting from idle); transient failures retry after an hour. It is `https` only and
honours `TESTDETTA_LICENSE_REFRESH=off` and `HTTPS_PROXY`. The license goes in the header only. A
response is stored in `~/.testdetta/license` only when it is a genuine, newer version (`issued`) of the
same license, and never over another license installed there (2026-10-08); every failure is ignored. The
license refreshed is the first that has not run out, else the first. Licenses carry `issued`. Each license
found counts in its newest genuine version (the CI secret, the refreshed or installed copy), and of several
licenses the first that covers the run counts, so an old license left in the CI secret cannot hide a current
one installed next to it; when none covers, the first one's reason is reported (`LicenseCheck.Candidates`,
`Check`, 2026-10-08). A seat reduction is a newer license with fewer seats. Before launch no key was trusted, so no request
was ever sent.

Still to do in phase 2:
- the production key and embedding its public key;
- the customer portal;
- Polar webhooks (phase 3).

## As built (phase 3, Polar, 2026-10-03)

`POST /webhooks/polar` (`PolarWebhook`) turns Polar subscription events into license records.

- **Signature.** Polar signs with the Standard Webhooks scheme (secrets made on or after
  2026-09-08; older ones are keyed by their own UTF-8 bytes, and both keys are tried, as Polar's SDKs do:
  the sandbox handed out such a secret on 2026-10-07). The check is HMAC-SHA256 with the base64 key after `whsec_` over
  `{webhook-id}.{webhook-timestamp}.{body}`, compared in constant time with every `v1,` signature,
  and the timestamp must be within 5 minutes. It is checked against the specification's published
  test vector. A failure returns 401, logs a warning, and nothing is stored. Without
  `Licenses:PolarWebhookSecret` the endpoint answers 404.
- **Products.** `Licenses:PolarProducts` (`POLAR_PRODUCTS="<id> <id>" deploy.sh`) lists the products whose
  subscriptions and refunds count; empty takes every product. `deploy.sh production` sets the two live products
  (TestDetta Team monthly and yearly) itself since 2026-10-08, so no deploy can leave it out; the sandbox takes
  every product. A signed event in another shape (a field missing or of another kind) is handled as ignored and
  logged, instead of failing until Polar gives up.
- **Delivery.** Each `webhook-id` is handled once. It is marked handled after it is applied, so a
  failure in between is left to Polar's retries (up to 10). The body limit is raised to 256 KiB for
  this endpoint only.
- **Trace.** `processed_webhooks` keeps each delivery's event type, license id and outcome (`Saved`,
  `Stale` for an older state than the stored one, `Unpaid`, `NeedsAttention`, `Ignored` for events other
  than `subscription.*`), and the same goes to the log at Information; a repeated delivery is logged
  too. This answers "Polar shows 200, what happened?" without the body, which carries the customer's
  name and address: neither is stored nor logged, not even masked, since the subscription id finds the
  customer in Polar.
- **State.** Every `subscription.*` event carries the whole subscription, so the record is rebuilt
  from its current state, keyed `P-<subscription id>`. The write is conditional on the state being
  newer than the stored one, in one statement, so late or simultaneous events never restore an older
  state. The state's time is the event's `timestamp` (Polar builds the payload when it emits the event,
  and a redelivery repeats both), else `modified_at`, else `created_at`. Not `modified_at` first: Polar
  leaves it null for a while, and the created, active and updated events of one sandbox checkout
  (2026-10-07) all carried none, so every event after the first counted as stale.
- **Mapping:**
  - seats come from the subscription's `seats`;
  - the period ends at `ends_at`, or else `current_period_end`; a subscription cancelled at period
    end keeps its license until then;
  - `ended_at`, `incomplete_expired` or `unpaid` revoke the record, which stops renewals;
  - `trialing` makes a trial license;
  - an incomplete checkout makes no license;
  - `past_due` and `paused` change nothing by themselves (owner's decision, 2026-10-07): the license
    runs to the end of the paid period and is not renewed beyond it; Polar's later `unpaid` or
    `revoked` stops it, `resumed` carries the new period. Not verified: if Polar moves
    `current_period_end` on before the renewal is paid, the license follows it until `unpaid` arrives;
    `LicenseIssuer.MaxAhead` bounds that to 40 days;
  - the licensee is the customer's name or billing name, never the email, which would end up in the
    license string;
  - the scopes are the checkout's `scope` field as customers type it, normalised by `ScopePatterns`
    (2026-10-08; the trial form uses the same rules): a URL or SSH remote becomes `host/path` without
    `.git`, a host and owner alone (`github.com/acme`) become `github.com/acme/*` (which also matches the
    bare owner), and a pattern must name a host with a dot and an owner without wildcards, so no checkout
    can license `*/*` or a whole host. Unreadable patterns are left out and flagged; the rest are kept
    (at most 50). Before this, `github.com/acme` was kept as typed and matched no repository at all;
  - once an admin set the scopes (`admin update --scope`), later events keep them;
  - `order.refunded`: a full refund revokes the subscription's license for good (as `admin revoke`
    does, so later subscription events cannot bring it back) and is flagged; a partial refund is only
    flagged. Polar's webhook must send `order.refunded` for this (launch checklist).
- **Needs attention** (a warning plus an `attention` audit entry, listed by `admin attention`): a
  subscription without a seat count (the product must be seat-based), without a usable scope or with
  unreadable ones, a refund.

### Delivery by e-mail (2026-10-04)

A buyer receives the license string by e-mail, sent by the service after the webhook has saved the
record (`LicenseDelivery`, through Resend's HTTP API in `ResendMailer`). The customer portal comes
later; e-mail is enough for the first sales.

- **When.** A license is sent when it is new and again whenever what it grants changes: licensee,
  plan, kind (a trial that becomes a subscription), seats or scopes. A renewal alone sends nothing,
  since the refresh endpoint hands those out. Revoked records and records without scopes (flagged
  for an admin) are not sent.
- **Once per version.** `license_deliveries` keeps the license id and a SHA-256 of what it grants,
  written with an audit entry after the provider accepted the mail. The same key, as Resend's
  `Idempotency-Key`, lets Resend drop a second send of one version within a day.
- **Failures.** Network errors, 429 and 5xx make the webhook answer 503 without marking it handled,
  so Polar delivers it again; the record is saved already and the retry sends the mail. Any other
  refusal (an invalid address, an unverified domain) is not retried: it is audited as `attention`
  and an admin sends the license with `admin send` (or by hand from `admin show`). An event without a usable address (one plain
  address) is audited the same way.
- **Personal data.** The address comes from the event (`customer.email`) and is neither stored nor
  logged; logs and audit name the license id only.
- **Format (2026-10-07).** Plain text and a designed HTML version with the same content, both built from
  one `LicenseMailParts` (`LicenseMail`, `LicenseMailHtml`): details, the key alone in a box (Apple Mail
  selects it whole with one tap; `td` drops any whitespace a copy adds), setup steps, a link to the setup
  guide. Tables and inline styles, no script, no web fonts, no tracking; the only image is the logo,
  `https://testdetta.com/assets/email-logo.png` (Gmail shows no SVG). Every checkout value (licensee,
  plan, scopes) is HTML-encoded where it is placed, so it cannot become markup or links. The seller line
  follows the record's source: a Polar purchase says it is sold and invoiced by Polar as merchant of
  record; any other source names no seller rather than a wrong one. Replies go to `support@`
  (`deploy.sh` sets `MAIL_REPLY_TO`).
- **Settings.** `Licenses:ResendApiKey` (a key with sending access to the sender's domain only,
  separate from any mailbox's key), `Licenses:MailFrom` (required with the key) and optionally
  `Licenses:MailReplyTo`. Without the key, delivery is off and logged at debug level.
- Licenses issued with `admin issue` are not e-mailed by themselves: the admin has no address from an
  event, and sends them with `admin send` or the string from `admin show` with the invoice.

**Polar production (2026-10-08):** organization `testdetta` set up like the sandbox: country Türkiye, logo,
social links, default tax behavior Exclusive, the `scope` custom field (text area, "Repositories"), and two
seat-based products without a trial, each with `scope` as a required checkout field: TestDetta Team (monthly)
$15 per seat per month and TestDetta Team (yearly) $144 per seat per year. Webhook made by the owner, its secret in
`license-polar-webhook-secret` (read access for `license-service` only); `deploy.sh production` and
`schedule-trials.sh production` run the same day (revision 00005, trials still off). Checkout link "Site pricing page
(monthly + yearly)", both products, success URL `/docs/license/`, return URL `/pricing/`; it goes into `site.json`
`checkoutUrl` at launch. Later the same day: Polar's account review approved (identity, payout account), the
`license-resend-api-key` secret added (revision 00006), and one end-to-end purchase with a one-use 100 % code
verified the whole chain (webhooks, license record, license e-mail, cancellation). Launch steps:
docs/launch-checklist.md.

**To go live in Polar** (no change needed before then):
1. Make the product seat-based.
2. Add a checkout custom field with the key `scope`, where customers enter their repositories
   (`github.com/acme/*`, `git.acme.corp/*`).
3. Add a webhook endpoint `https://<service>/webhooks/polar` for the `subscription.*` events.
4. Put its secret in `Licenses__PolarWebhookSecret`.
5. Verify the sending domain in Resend (DKIM and the return-path records in DNS), make an API key
   with sending access to that domain only, and set `Licenses__ResendApiKey`,
   `Licenses__MailFrom` and `Licenses__MailReplyTo`.

## Hosting (2026-10-07)

Google Cloud project `testdetta-510908`, region `europe-west1`, at `https://license.testdetta.com/` (a Cloud Run
domain mapping; Porkbun holds the CNAME). Scripts in `deploy/license-service/`:

- `setup.sh`, once: the service account `license-service` gets `cloudsql.client` and read access to its two
  secrets only; the database `licenses` is created. The Cloud SQL instance (`testdetta-licenses`, Postgres 17,
  smallest shared-core tier, backups and point-in-time recovery, deletion protection, encrypted connections
  only), the Artifact Registry repository and the service account were made by hand the same day.
- `deploy.sh`: Cloud Build builds the image as the account `license-builder` (new projects leave the
  default compute account without roles; it may read the uploaded source, write to the one repository and
  write logs), the upload limited to what the Dockerfile copies (`.gcloudignore`), tagged with the commit;
  Cloud Run runs it with at most 2 instances, none when idle. The organization allows IAM members of its
  own domain only, so the public endpoint comes from turning Cloud Run's invoker check off rather than
  granting `allUsers`; the service answers unproven requests with 404 and rate-limits them. Cloud Run's
  front end answers some paths ending in `z` itself, so the health route is `/v1/health`, not `/healthz`.
  TLS ends at that front end: forwarded headers are applied first, before HSTS (sent only on requests that
  count as HTTPS) and the rate limiter (partitioned by the client's address).
- Secrets are made by a person and never printed: the signing key is generated straight into Secret Manager
  (`license-signing-key-td-2026-10`, key id `td-2026-10`), and the connection string with a random password
  (`license-connection-string`). Cloud Run mounts both as files, the key outside `TESTDETTA_SECRETS`.
- Client addresses: Cloud Run's front end is the only way in and appends the client, so
  `TrustedProxies=*` with `ForwardedHops=1`.

`deploy.sh sandbox` deploys `license-service-sandbox` for Polar's sandbox: its own database
(`licenses_sandbox`), service account (`license-sandbox`) and signing key (`td-sandbox-1`), which no release
ever trusts, so a test purchase can never become a working license. Since 2026-10-08 it has its own Cloud SQL instance,
`testdetta-licenses-sandbox` (Postgres 17, smallest tier, encrypted connections only, no backups: test data only):
on the shared instance every user gcloud makes is a `cloudsqlsuperuser` member and could reach `licenses`, so
revoking `CONNECT` would not have been enough. The production instance now holds only `licenses` and its user;
the sandbox's earlier test records were not moved. The Polar webhook secret and the Resend key are mounted once their
secrets exist (`<prefix>-polar-webhook-secret`, `<prefix>-resend-api-key`, with `MAIL_FROM` set).

### Logs and alerts (2026-10-08)

On Cloud Run (`K_SERVICE` or `CLOUD_RUN_JOB` set) the service, the trial job and the admin job log one JSON line
per entry (`CloudLogFormatter`: `severity`, `message`, `logger`), so Cloud Logging keeps the level; the default
console formatter wrote the category and the message as two lines without one. Elsewhere logs stay single plain
lines. `alerts.sh <env> <address>` (re-runnable) makes an e-mail channel and two log-based alert policies, at
most one mail an hour each: every warning or error of the service and its two jobs (only stdout and stderr, not
the request log, where a refresh of an unknown license is a 404 warning), and every trial refused because the
address or an owner had one. Run it after the first deploy that has the formatter.

The key's public half went into `LicenseKeys.Trusted` at launch, together with the site's texts that had said
no license verifies before launch.

## Trials (as built, 2026-10-07)

The site's trial needs no card (owner's decision, 2026-10-07: Polar's trials always take a card, so its
products carry no trial; a purchase pays from day one).

- **Flow.** `/trial/` (five languages, `site/src/assets/trial.js`) fetches `GET /v1/trials/challenge`, solves
  the proof of work while the visitor types and posts `{email, repositories, challenge, nonce}` to
  `POST /v1/trials` (`TrialEndpoint`). The service checks the address (one plain address), the patterns
  (`TrialScopes`: host and owner first, no wildcard in either, at most 10 patterns naming at most 3 owners) and the proof, reserves the trial,
  saves a `T-<random>` record (kind trial, 50 seats, the patterns, 30 days, source `trial`, licensee
  "Trial for <first owner>") and e-mails it with the license mail. Sending the license to the address is
  the address check. A mail that does not go out revokes the record and releases the reservation, so a
  retry works.
- **Proof of work** (`TrialChallenge`, no third party): a token `<seconds>.<random>.<HMAC>` under a key
  derived from the signing key (HKDF, `SigningKey.DeriveKey`), good for 15 minutes and once
  (`trial_challenges`); the nonce must give a SHA-256 with 17 leading zero bits (`TrialProofBits`), a few
  seconds in a browser.
- **Limits.** One trial per owner key (`github.com/acme`) and per address in 12 months, checked and
  reserved in one transaction under an advisory lock (`TrialStore.ReserveAsync`); a daily ceiling over all
  trials (`TrialsPerDay`, 50), reserved before any mail is sent, answering 503 `paused`; 5 requests per
  client address (IPv6 by /64) per hour in memory. Since one request blocks every owner it names, a trial
  names at most 3 (2026-10-08, was 10 through the pattern cap); someone else's request can still block an owner,
  so the site's answer tells a visitor with no mail after ten minutes to write to support, the alert below
  reports refusals, and `admin trial-release --owner` frees the owner.
- **No enumeration.** Every well-formed request gets 202 `accepted` after at least 2 s
  (`TrialMinimumResponse`), whether a license went out or the address or owner had one; malformed
  requests get 400 with the field to fix, which says nothing about anyone else.
- **Data.** The address is kept until 7 days after the trial ends, for the one reminder; then only its
  HMAC (key derived from the signing key) and the owner keys stay, 12 months (rows go after 400 days).
  The IP address is used by the rate limiter only. The license carries the patterns, never the address.
  Logs name the trial id and the outcome. Privacy policy: "Starting a trial", five languages.
- **Daily job.** `dotnet TestDetta.LicenseService.dll trials daily` (`TrialDaily`), deployed by `deploy.sh`
  as the Cloud Run job `<service>-trials` and run at 07:00 Istanbul by Cloud Scheduler
  (`schedule-trials.sh`, account `license-scheduler` with `run.invoker` on the job only): one reminder 3
  days before the end (`TrialReminderMail`), never to an owner whose scopes a Polar license names, then
  `PurgeAsync`.
- **Switches.** `Licenses:TrialsEnabled` (needs the Resend key) and `Licenses:TrialOrigins` (CORS):
  sandbox on, from `https://testdetta.com` and `http://localhost:4321`; production off until launch
  (`TRIALS=true deploy.sh production`), with the site's `trialsOpen` in `site/site.json`, which shows
  "Trials open at launch" until then. The site's CSP allows `connect-src https://license.testdetta.com`.

## Phases

1. Format, verification, committer counting and degradation in TestDetta, with a locally signed test
   key. `td license status`.
2. The license service with manual issuance, the refresh endpoint and the portal.
3. Polar webhooks and checkout (primary), then Paddle.
4. Trials and the public-repository detection.
