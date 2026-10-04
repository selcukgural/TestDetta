# Google for Startups Cloud Program: application draft

Status: draft 2026-10-05, not submitted. Two decisions are the owner's: whether TestDetta plans to seek
venture funding (the Start tier asks for it; claim it only if true) and whether to incorporate first.

## Findings (sources checked 2026-10-05)

- **Start:** up to $2,000 in Cloud and Firebase credits for 12 months. A working MVP, a business model,
  "plans to seek venture funding soon", founded within 24 months. https://cloud.google.com/startup/pre-funded
- **Scale:** up to $200,000; needs an institutional pre-seed/seed round (angels, grants and TÜBİTAK do not
  count). Not eligible today. https://cloud.google.com/startup/faq
- **AI-first:** for products with AI at their core. TestDetta is deterministic analysis: do not apply.
- **Domains must match:** website, applicant email and billing account email: all `testdetta.com`. Ready.
- **Billing account:** an 18-character Cloud Billing ID, created from `selcuk@testdetta.com`, country
  Türkiye, payments profile **Business** (country and profile type cannot change later).
- **Türkiye** is eligible; the terms are a contract with a "Company". Ask cloudstartupsupport@google.com
  whether an unincorporated founder can apply, and under what name.
- Review takes a few business days, sometimes 10 or more.

## Before applying

1. Email cloudstartupsupport@google.com about incorporation.
2. Decide on an entity (a şahıs şirketi is the cheapest way to a tax number and a Business payments profile;
   invoices and Polar may need it anyway).
3. Create the billing account as above and link it to `testdetta-prod` (the license service needs it too).
4. Have ready: the evaluation link (https://testdetta.com/docs/examples/#corpus), a one-page roadmap, a LinkedIn profile.

## Draft answers ([brackets] = to fill in)

**Short description.** TestDetta runs only the .NET tests a code change can affect, inside the customer's
own CI, so pull-request test time drops without missing a failing test.

**Long description.** TestDetta is test impact analysis for .NET. It reads a pull request's git diff and
works out which test projects and test classes can observe the change: it follows project references,
compares C# syntax trees, uses per-test-class coverage maps recorded on the default branch, and traces new
code by name. Then it runs only those tests. When it cannot be sure, it runs more, never less, and without
a valid license every test runs. It runs entirely in the customer's CI (GitHub Actions, GitLab CI, Azure
DevOps, Jenkins, Bitbucket); no source code leaves the customer's machines.

**Problem.** Large .NET test suites run in full on every pull request, even when a change touches one
method. Teams pay for CI minutes and wait for feedback. Existing approaches tie customers to one CI or
analytics vendor, or skip tests in ways that can miss failures.

**Solution.** Conservative test selection from the diff, coverage and source analysis, evaluated on 41
open-source .NET repositories (Polly, Newtonsoft.Json, Serilog, OpenTelemetry and others) by changing one
member at a time: of 2,941 changes that broke at least one test, 3 were missed, and each was fixed. In the
median repository a change ran 28% of the test classes.

**Market.** Professional .NET teams with private repositories and CI test suites. SlashData estimated about
10 million C# developers (https://www.developernation.net/blog/language-communities-an-update/). [Owner's
estimate of the share that are active committers on private, CI-tested code, and the resulting seats.]
Initial focus: small and mid-size teams on GitHub Actions; then enterprises on other CI systems.

**Business model.** A subscription per active committer: $15 a month, or $12 a month billed yearly. Free
for public repositories, 30-day trial without a card. Polar is the merchant of record. Annual offline
licenses for networks without internet access.

**Traction.** Pre-revenue. Product built and evaluated (above); licensing and email delivery implemented;
site live at https://testdetta.com in five languages. [Launch date, trial users, design partners.]

**Use of credits.** Cloud Run for the license service with Cloud SQL (PostgreSQL) and Secret Manager;
Firebase Hosting for the site; Compute Engine runners for the 41-repository evaluation corpus, rerun before
each release; later, a hosted coverage-map store (Cloud Storage) for customers outside GitHub Actions.

**Team.** [Name], solo founder and developer based in Türkiye. [.NET experience, prior roles, LinkedIn.]

**Funding.** [Bootstrapped / plans, only if true.]
