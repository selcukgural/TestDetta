# Go-to-market plan (draft)

Status: draft 2026-10-05 for the owner's review on 2026-10-06. Numbers marked [to verify] are not checked
yet; nothing here is published.

## Positioning

**One line:** TestDetta runs only the .NET tests a pull request can affect, proven safe on 40+ open-source
repositories, inside your own CI, and it explains every decision.

Three claims, each backed by something a buyer can check:

| Claim | Evidence |
|---|---|
| Safe | The public corpus evaluation: thousands of real mutants, misses counted and fixed in the open (docs/evaluation.md). "When unsure, run more"; no license, run everything. |
| Private | Runs in the customer's CI; no source, coverage or test names leave the machine. Only the license check calls home. |
| Explainable | Every selected (and skipped) project and class comes with its reason in the job summary. |

Who it is not for: teams whose whole suite runs in under ~5 minutes (nothing to save), and teams
without CI tests.

## Competition

| Alternative | What they do | Our answer |
|---|---|---|
| Doing nothing / sharding / bigger runners | The real default. Costs money, keeps latency. | Selection stacks with sharding; the shadow report shows the minutes. |
| dotnet-affected (OSS) | Project-level selection from the graph. | Same graph, then class level: corpus median ~35% of classes vs ~91% for whole affected projects. |
| Datadog Test Impact Analysis | Per-test coverage, part of Datadog CI Visibility [to verify: .NET support level, pricing per committer]. | No Datadog account, no data leaves, published miss rate. |
| Build-system TIA (Bazel, Nx) | Needs a build-system migration. | Works on plain MSBuild solutions. |

## Phase 1: design partners (October to November 2026)

Goal: 3 to 5 .NET teams with slow suites (15 minutes or more per pull request) using it on real pull
requests, and permission to quote numbers.

1. **Shadow mode first.** The partner adds the Action with `mode: shadow`: TestDetta selects, the full
   suite still runs, and the job summary shows what the selection would have run, the minutes saved, and
   any test that failed outside the selection (a miss). No risk to the partner, no license needed, and real
   proof for us. Built (2026-10-07): the Action's `mode: shadow` and `td test --shadow`, which run the
   partner's own `dotnet test` command (docs/design/shadow-mode.md).
2. **Weekly report** per partner: pull requests, minutes saved, misses (expected 0), slowest projects.
3. **Switch on** selection once the graduation criteria hold (within 30 days, 40 pull requests with a
   shadow result and no unexplained miss, plus one `td audit` run on their code with at least 100 breaking
   changes, 0 missed, and the worst-case 95% upper bound, unjudged changes counted as misses, at most 3.0%;
   docs/design/shadow-mode.md); the full suite keeps running nightly.
4. In return: free licenses for a year, a 30-minute call every two weeks, a quote or case study.

Where to find them:
- Personal network and LinkedIn: .NET leads at companies with large monorepos (Türkiye and the EU first).
- Repositories in the corpus: maintainers of the slowest suites (OpenTelemetry, NodaTime, protobuf-net,
  Roslynator) get a pull request comment offering a shadow run; open source stays free.
- Communities: .NET Foundation Slack/Discord, r/dotnet, dotnet conference meetups; one technical post
  ("How we proved test selection safe on 42 repositories") rather than ads.

## Phase 2: launch (December 2026)

- Public launch post with the evaluation, the shadow-mode numbers of the partners, and a 30-day trial
  without a card.
- GitHub Marketplace listing for the Action; NuGet tool package `TestDetta`.
- Comparison pages: vs dotnet-affected, vs Datadog TIA, vs sharding.

## Pricing

Today: $15 per active committer a month, $12 billed yearly; free for public repositories.

To validate with partners before launch:
- What one saved CI minute is worth to them (runner cost and developer waiting time).
- A worked example on the pricing page: a team of 20 committers, 30 pull requests a day, a 25-minute
  suite at 35% selected saves ~490 runner minutes a day; $300 a month against [runner cost + waiting time].
- Whether a usage floor (minimum 5 seats) or a per-repository plan sells better to small teams.

## Measures

| Stage | Measure | Target by launch |
|---|---|---|
| Interest | Site visits to the evaluation page, Action installs | [set after first month] |
| Proof | Partners in shadow mode, pull requests analysed | 3 partners, 500 pull requests |
| Safety | Misses in shadow mode | 0 (each one fixed and published) |
| Value | Median CI minutes saved per pull request | reported per partner |
| Revenue | Paying seats | first paying team |

## Owner decisions needed

1. Shadow mode as the entry point (needs a small build).
2. Which 10 teams to approach first, and who writes to them.
3. Pricing experiment: keep $15 or test a team plan.
4. Incorporation timing (also needed for Polar payouts and Google for Startups).
