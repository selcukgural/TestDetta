# Licensing

Status: phase 1 implemented 2026-10-03 (see "As built"); enforcement off until launch. Decisions
marked **(decided)** were made with the user.

## Goals

- Any CI works the same way: GitHub Actions, GitLab, Jenkins, Bitbucket/Bamboo, Azure DevOps,
  TeamCity, and on-premises installations with no outbound network at all. Legacy setups at large
  companies are the expected case, not an edge case **(decided)**.
- A licensing problem never breaks a customer's build. Without a valid license, Sift runs every
  test and says why **(decided)**.
- Not tied to a payment provider. Polar.sh, Paddle and direct invoices (Turkish corporate customers
  paying in TL) all produce the same license (docs: payments research, 2026-10-02).
- No personal data leaves the customer's machines: committer emails are only counted locally.

Not a goal: stopping a determined attacker. Sift is a .NET tool and can be decompiled. Licensing
keeps honest customers honest and makes paying the easy path.

## Pricing unit and free use

- **Active committers (decided):** the price is per active committer per month.
- **Free (decided):**
  - public repositories;
  - a time-limited trial (30 days, full features, no card).

## The license

A license is a small JSON document signed with ECDSA P-256 (SHA-256; built into .NET, unlike Ed25519). It is passed to Sift as one string:
`sift1.<base64url payload>.<base64url signature>`.

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
  never in a repository or in CI. Sift embeds the public keys by key id (`kid`), so a key can be
  rotated: new Sift versions trust the new key, and licenses are reissued under it.
- **Kinds:**
  - `subscription`: valid for the billing period plus 7 days, renewed by refresh;
  - `annual`: offline customers, valid for the term plus 30 days;
  - `trial`: 30 days, one per scope.
- **Scopes** are patterns over normalised git remote URLs: host and path, without scheme, user,
  password or `.git`. An air-gapped company's license names its internal git server.

## Verification in Sift (every run, offline)

1. **Find the license.** In order:
   - `SIFT_LICENSE` (the string);
   - `SIFT_LICENSE_FILE` (a path);
   - the refreshed copy in `~/.sift/license` (see below);
   - `.sift/license` in the repository (allowed, though a secret variable is recommended).
   Every CI has a way to pass a secret variable or file: GitHub/GitLab secrets, Jenkins
   credentials, Bitbucket and Azure DevOps secret variables, TeamCity parameters.
2. **Check the signature** against the embedded key for `kid`.
3. **Check the scope.** The repository identity is the `origin` remote, normalised. Credentials
   embedded in the URL (common with Jenkins) are stripped before matching, and never logged.
   `SIFT_LICENSE_SCOPE` overrides the identity when there is no usable remote. One of the license's
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

A **public repository** needs no license. Sift knows that only where the CI says so: on GitHub
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
  A customer can add patterns in `.sift/license.json` (`"bots": [...]`). The list is shown in
  `sift license status`, so it cannot hide people quietly.
- **Per pipeline:** each run counts its own HEAD. Unmerged branches are counted by their own
  pipelines. Across an organisation's repositories, the count is per repository. The
  organisation-wide total is settled by a yearly declaration (true-up), which enterprise customers
  are used to.
- Author dates can be forged (`GIT_AUTHOR_DATE`), like any client-side check. That is accepted.

## Over the seat count (decided)

1. **Tolerance:** up to 20% over `seats`, at least one person, is fine. Holidays, an intern, an
   outside contributor.
2. **Warning period, 30 days:** above the tolerance, Sift keeps selecting and warns in every
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

A license carries its `refresh` URL. At most once a day, Sift asks for the current version of the
license:

- `GET <refresh>` with the license id in the path and `Authorization: Sift <current license string>`.
  The license string never goes into a URL or a query string.
- A 2-second timeout and no retries: an offline or blocked network costs at most 2 seconds a day.
  `SIFT_LICENSE_REFRESH=off` disables it, which offline customers set. `HTTPS_PROXY` is honoured.
- The response is a newer signed license. It is checked exactly like the original and stored in
  `~/.sift/license` together with the time of the attempt. Persistent agents (common on-premises)
  keep it between runs. Ephemeral runners refresh again, within the 2-second budget.
- A failed refresh changes nothing: Sift uses the best valid license it has. The service is an
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
  `validUntil`. There is no instant revocation offline, by design.
- **Trials:** sign-up with an email, verified before a key is issued. One trial per scope, and the
  service throttles per address and per scope.
- Requests to it never carry personal data from the customer's repositories.

## CLI

- `sift license status`: the license found, its validity, scopes, the active committer count and
  the bot patterns. It shows no identities, since its output may end up in CI logs.
- `sift license install <string|file>`: stores a license in `~/.sift/license` for machines without
  a secret mechanism.
- The GitHub Action gets a `license` input that maps to `SIFT_LICENSE`. Other CIs set the variable.

## Tests (when implemented)

- Signature: valid, tampered payload, unknown `kid`, rotated key.
- Scopes: normalisation of https, ssh, scp-style, and credential-bearing URLs; wildcards; no
  remote with `SIFT_LICENSE_SCOPE`.
- Committers:
  - the long-lived branch case (old authors merged now are not counted);
  - squash, rebase and cherry-pick;
  - `.mailmap`, bots and custom bots.
- Over seats: the tolerance, the 30-day period from HEAD dates, and its reset.
- Time: clock moved back, with newer commit dates.
- Refresh: offline, slow, proxy, newer license, invalid response.
- Degradation: every failure mode runs every test and reports why; none fails the build.

## As built (phase 1, 2026-10-03)

- `Sift.Core.Licensing`:
  - `License`: signing and verifying, ECDSA P-256, key ids;
  - `RepositoryIdentity`: remote normalisation and scope matching;
  - `ActiveCommitters`: author-date window, built-in and custom bots, days over;
  - `LicenseCheck` and `LicenseStatus`: every outcome above;
  - `LicenseInputs`: variables, files, CI public flags; git output for remotes and authors is never logged.
- `Sift.Cli.LicenseGate` applies the status to `sift affected` and `sift test`. It is recorded on the
  analysis and in the JSON output (`license`). When enforced and not covering, every test project of
  the repository runs completely, and the text, plan and Markdown summary say why. The analysis
  itself (`AnalysisPipeline`) knows nothing of licenses, so `sift-eval` and the corpus measurements
  are unaffected.
- `sift license status`, `sift license install`, and the action's `license` input.
- **Not enforced yet:** `LicenseKeys.Trusted` is empty until the license service makes the first
  production key, and `LicenseKeys.Enforced` is false. Until then the status is checked and appears
  only in the JSON output, the debug log and `sift license status`, so nobody's runs change or get
  noisy before launch.
- Refresh is phase 2: it needs the service.
- Tests: `LicensingTests` and `LicenseGateTests`, sabotage-checked:
  - a tampered license, an unknown key;
  - remote URL forms with credentials; scopes;
  - the long-lived branch case; bots; days over;
  - each outcome; a clock moved back;
  - enforced and not enforced; JSON and Markdown.

## As built (phase 2, first part, 2026-10-03)

`src/Sift.LicenseService`: ASP.NET Core with Postgres through Npgsql and plain SQL, deployed as a
container (`Dockerfile`: images pinned by digest, the image's non-root user, the signing key mounted
as a file and never baked in or passed as a variable). The host is not chosen yet (user decision:
container, decided later).

- **Records** (`licenses`) are the source of truth. Every change also writes an `audit` row with its
  reason, in the same transaction.
- **Signing:** `SigningKey` loads the ECDSA P-256 key from a PEM file and never logs it. A cloud KMS
  can replace it behind the same members. `LicenseIssuer` adds the slack: 7 days for a subscription,
  30 for annual, none for a trial.
- **Refresh:** `GET /v1/licenses/{id}` with `Authorization: Sift <license>`. The presented license
  must be genuine (current or retired key) and for the same id; it may have expired. Unknown,
  revoked, mismatched and unproven requests get the same 404. The response is `no-store`. There is
  a rate limit per client address and per license, and the request body is limited to 16 KiB.
- **Admin:** `dotnet Sift.LicenseService.dll admin issue|update|revoke|show|list|public-key`, run
  from a shell on the host. There is no admin web UI. `public-key` prints what goes into
  `LicenseKeys.Trusted`.
- **Tests:** `tests/Sift.LicenseService.Tests` against a real Postgres, one database per test,
  through the real HTTP pipeline; sabotage-checked. CI runs a Postgres service container
  (`SIFT_TEST_POSTGRES`).

Client addresses behind the host's proxy: `Licenses:TrustedProxies` lists the proxies (IPs or CIDR
networks) whose `X-Forwarded-For` is believed, or `*` for one hop from anywhere when the proxy is the
only way in. With none configured, the header is ignored, so nobody can pick their own address. The
per-address limit counts an IPv6 client by its /64 network (`ClientPartition`).

Sift's refresh client (`Sift.Cli.LicenseRefresher`, same day): before each check, at most once a
day (`~/.sift/license-refresh`), with a 2-second timeout and no retries. It is `https` only and
honours `SIFT_LICENSE_REFRESH=off` and `HTTPS_PROXY`. The license goes in the header only. A
response is stored in `~/.sift/license` only when it is a genuine, newer version (`issued`) of the
same license; every failure is ignored. Licenses carry `issued`. Of the genuine versions found (the
CI secret, the refreshed copy), the newest of the first license counts (`LicenseCheck.Best`). A
seat reduction is a newer license with fewer seats. Before launch no key is trusted, so no request
is ever sent.

Still to do in phase 2:
- the production key and embedding its public key;
- the customer portal;
- Polar webhooks (phase 3).

## As built (phase 3, Polar, 2026-10-03)

`POST /webhooks/polar` (`PolarWebhook`) turns Polar subscription events into license records.

- **Signature.** Polar signs with the Standard Webhooks scheme (secrets made on or after
  2026-09-08). The check is HMAC-SHA256 with the base64 key after `whsec_` over
  `{webhook-id}.{webhook-timestamp}.{body}`, compared in constant time with every `v1,` signature,
  and the timestamp must be within 5 minutes. It is checked against the specification's published
  test vector. A failure returns 401, logs a warning, and nothing is stored. Without
  `Licenses:PolarWebhookSecret` the endpoint answers 404.
- **Delivery.** Each `webhook-id` is handled once. It is marked handled after it is applied, so a
  failure in between is left to Polar's retries (up to 10). The body limit is raised to 256 KiB for
  this endpoint only.
- **State.** Every `subscription.*` event carries the whole subscription, so the record is rebuilt
  from its current state, keyed `P-<subscription id>`. The write is conditional on Polar's
  `modified_at` being newer, in one statement, so late or simultaneous events never restore an older
  state.
- **Mapping:**
  - seats come from the subscription's `seats`;
  - the period ends at `ends_at`, or else `current_period_end`; a subscription cancelled at period
    end keeps its license until then;
  - `ended_at`, `incomplete_expired` or `unpaid` revoke the record, which stops renewals;
  - `trialing` makes a trial license;
  - an incomplete checkout makes no license;
  - the licensee is the customer's name or billing name, never the email, which would end up in the
    license string.
- **Needs attention** (a warning plus an `attention` audit entry): a subscription without a seat
  count (the product must be seat-based), or without a scope.

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
  and an admin sends the license from `admin show`. An event without a usable address (one plain
  address) is audited the same way.
- **Personal data.** The address comes from the event (`customer.email`) and is neither stored nor
  logged; logs and audit name the license id only. The mail is plain text, so the licensee name from
  the checkout cannot become markup or links.
- **Settings.** `Licenses:ResendApiKey` (a key with sending access to the sender's domain only,
  separate from any mailbox's key), `Licenses:MailFrom` (required with the key) and optionally
  `Licenses:MailReplyTo`. Without the key, delivery is off and logged at debug level.
- Licenses issued with `admin issue` are not e-mailed: the admin has no address from an event and
  sends the string from `admin show` with the invoice.

**To go live in Polar** (no change needed before then):
1. Make the product seat-based.
2. Add a checkout custom field with the key `scope`, where customers enter their repositories
   (`github.com/acme/*`, `git.acme.corp/*`).
3. Add a webhook endpoint `https://<service>/webhooks/polar` for the `subscription.*` events.
4. Put its secret in `Licenses__PolarWebhookSecret`.
5. Verify the sending domain in Resend (DKIM and the return-path records in DNS), make an API key
   with sending access to that domain only, and set `Licenses__ResendApiKey`,
   `Licenses__MailFrom` and `Licenses__MailReplyTo`.

## Phases

1. Format, verification, committer counting and degradation in Sift, with a locally signed test
   key. `sift license status`.
2. The license service with manual issuance, the refresh endpoint and the portal.
3. Polar webhooks and checkout (primary), then Paddle.
4. Trials and the public-repository detection.
