# Evaluation on real repositories

## How mutants are judged (from 2026-10-07)

Every doubt counts against TestDetta. The corpus evaluator (`tools/TestDetta.Eval`, `MutantJudge`) judges each
mutant by the same rules as `td audit` (docs/design/audit.md, "Judging"); the summaries name them as
`judging: strict (2026-10-08)` (the final round `runs-final-1007` was judged `strict (2026-10-07)`).

**Rule change.** Up to and including round `runs-full-1006`, the evaluator was lenient in three ways: a crash or
hang outside the selection made the mutant inconclusive and never a miss; a crash of the full run counted as caught
when the part the selection left out passed, without the selected part being run again; and a class the selection
ran counted as caught from the full run alone, unconfirmed. The numbers published above (rounds 1 to 3, the F#
round) were measured under those rules. They will be replaced by the next round's, measured under the strict rules
below; until then they are marked as measured with the lenient judging.

**Rule change 2026-10-08** (`strict (2026-10-08)`): a part the selection left out that did not complete with the
mutant counted as a miss after one run; it now has to fail to complete a second time. In the final round
(`runs-final-1007`) a mutant of `ConcurrentHashSet<T>.IsEmpty` in Meziantou.Analyzer, a member nothing calls, was
a miss because one test host died after 1,880 of 5,398 tests while the machine ran two repositories; the same run
without the mutant passed. A crash that does not come back proves no miss and rules none out, so the mutant is
inconclusive: it counts in the worst-case bound. The outcome cache keeps its version (3): the stored runs are
judged again, and only the repeats are new runs.

**Judging.**

- A **run that does not complete** is one that hits its time limit, whose test host crashed (the log or the report
  says so), whose report says the run was aborted, ended as an error or timed out, that fails (a non-zero exit code)
  without a failed test, or, for a run of a whole project, whose report lists fewer test classes than the run without
  mutants did (a host that died without a recognised message). The runs are made in English
  (`DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033`) so the crash lines are recognised; the report's outcome, the exit code
  and the class count catch what a message does not. A test project whose run without mutants does not complete is
  left out and named.
- A class the selection **left out** that fails with the mutant (and not without it) runs alone with the mutant,
  every such class of a project in one run. Passing there, it failed only next to other tests and is not counted.
  Failing there, it runs alone without the mutant, the failing ones in one run: failing there too, it fails alone
  anyway and is not counted; passing there, or when that run cannot tell, it is a **miss**.
- A class the selection **ran** that fails is confirmed the same way, in one run with and one without the mutant per
  project, and counts as **caught** only when it fails alone with the mutant and passes alone without it. When the run
  without it cannot tell, the catch is unconfirmed. Once the verdict is decided (a miss, a doubt, or a confirmed
  catch), the remaining selected classes are not confirmed, unless another selector compared on the same mutants
  (project level, drift modes, dotnet-tia) left them out; they stay among the failed classes for the precision figure.
- A class whose run alone **ran none of its tests** makes the mutant inconclusive; it never counts as passed alone.
- A full run that **does not complete** runs again without the mutant, by parts:
  - the selection ran nothing in that project: completing without the mutant, the run is repeated with it; not
    completing again is a **miss** (the part left out breaks with it), a repeat that completes is judged by its
    failures and leaves the mutant **inconclusive** at least; not completing without the mutant, **inconclusive**;
  - it ran the whole project: the run is repeated with the mutant; not completing again and completing without it
    is **caught by a crash**; a repeat that completes is judged by its failures;
  - it ran some classes: the classes it left out run apart with the mutant (not completing there, completing
    without it and not completing with it a second time is a miss; a repeat that completes leaves the mutant
    inconclusive; their failures are judged as above); then the classes it ran (not completing there and
    completing without it is caught); when neither part fails apart, **inconclusive**.
- A mutant with a miss is **missed**; else with a doubt that could hide a miss, **inconclusive**; else with a
  confirmed catch, **caught**; else with an unconfirmed catch, **inconclusive**; else it did not break a test.

**Reported.** n is the number of failing mutants (caught and missed); the 95% upper bound of the miss rate is the
exact one-sided Clopper-Pearson bound. Next to it, every report states the inconclusive mutants and a **worst-case
bound** that counts each of them as a failing mutant that was missed. Inconclusive mutants are listed with the reason.

**Cost.** A caught mutant now costs, besides its full run, one run of its failing selected classes with the mutant and
one without (the second is shared by every mutant that fails the same classes of a project, and stored in the
outcome cache). A mutant whose full run crashes or hangs costs one more run of the part the selection ran, which waits
for its time limit again when it hangs.

**Leftovers in the output folder (2026-10-08).** Every run is made without a build, and the files it leaves in
the test project's output folder are deleted after it (`OutputLeftovers`, `TestExecutor`; `td audit` does the
same). In the final round NLog's `LogFactoryTests.SecondaryLogFactoryDoesNotTakePrimaryLogFactoryLock` wrote
`NLog.config` there, the mutant of `XmlParser.IsValidXmlNameChar` crashed the host before the test deleted it, and
every later run auto-loaded that file: three classes failed alone with the mutant and passed alone without it, a
miss made by the polluted folder. Runs stored before this change may carry such leftovers.

**Outcome cache.** From cache version 3 every run is stored raw, under what it ran (project, whole / only some classes /
all but some, the mutant on or off), with its completeness; a later round judges again from the stored runs and makes
only the runs a changed selection asks for (docs/design/eval-result-cache.md). Nothing of a version 2 cache is reused:
its full runs were judged complete without the report outcome, the exit code and the class count, it holds no
confirmation of selected classes, and its runs alone do not say whether a class ran at all.

## Incidents

The per-repository report (`synthetic.md`, `synthetic.json`) and the round summary (`summary.md`, `summary.json`)
list what a reader of the numbers must know, because each one makes the selection of a project fall back to names:

- a test project **not recorded although its tests passed** (or not recorded on TestDetta's side at all: its test
  assembly not instrumented, the tracker silent; `td record` says "not recorded" with the reason);
- a map that recorded **0 test classes**, one whose **every class failed** while recording, and one whose failures no
  report names (it is not used);
- with `--compare <previous round>`, **fewer recorded classes** than in the previous round of the same repository, in
  total or in one map (read from the previous round's own `synthetic.json` when its summary has no counts).

The round comparison (`comparison.md`) also says when two rounds were judged by different rules.

## Findings (from 2026-10-10)

`eval/findings.json` lists every class of problem the corpus (or reading code) revealed, once, where it first
showed: a **miss**, a **narrowing** that could have left a failing class out and was found before it did, a
**recording** failure, an **analysis** hang, a **precision** problem (a selection far wider than needed), or an
**evaluation** artefact. A class is a rule TestDetta got wrong or lacked, not a repository: the same class found
again elsewhere goes into `seenAgain`. `--summarize` writes `findings.md` from it.

The number to watch is **product classes per evaluated new repository, batch by batch** (batches are the dates
repositories joined `eval/corpus.json`; repositories that could not be evaluated yet do not count). If new
repositories keep revealing new classes at the same rate, working repository by repository does not converge; if
the rate falls, it does. Classes that a new TestDetta feature's first measurement exposed (`feature`) are counted
apart: they rate the feature's design, not the corpus's reach. On 2026-10-10: 0.49 per repository in the first 41,
0.18 in the 11 application repositories added that day (plus 5 from the readers rule's first measurement).

When a fix closes a class, add it to the ledger in the same change; when repositories join the corpus, add a batch.

## Precision triage and new repositories: the fast path

Mutant runs answer "does it miss?" and take hours; why a repository selects widely needs no test run:

1. **Precision triage** (`--explain --any-recording`): every sampled change's selection and evidence, from the
   cached maps at the commit (the newest recording there, also from an older recorder: maps without newer data only
   select more). Minutes per repository. `--summarize` then writes `precision.md`: mean and median share, how many
   changes run a whole test project, and the rules (evidence with names, paths and numbers taken out) that select
   the most classes, per repository and across them. `~/sift-corpus/run-triage.sh` runs it on the repositories whose
   last full run selected 50% or more.
2. **A new repository**: measure build and test time first (`measure.py`), record once (the recording cache keeps
   it), run the precision triage, and only then mutants (`--max-mutants 40`, `--focus` for the feature under test,
   two repositories at a time). The app tier (`--tag app`) is the quick regression set; the whole corpus is for
   changes to recording or selection rules.

Narrowing a rule the triage points at is a design decision for the owner first (it can cost misses); the triage
says where the selection goes, not that it may go less far.

## F#: Argu (2026-10-06)

*Judged with the lenient rules used up to round `runs-full-1006` (see "How mutants are judged").*

First measurement of F# analysis (docs/design/vb-fsharp-support.md), Argu at the corpus commit, 100
synthetic changes sampled from F# members (`|> id` source edits, IL mutants of every kind):

| Binary | Failing mutants | Missed | Selected | Project level |
|---|---|---|---|---|
| 5311fbb | 64 | 4 | 56 % | 100 % |
| a9a11cd | 64 | **0** | 56 % | 100 % |

All four misses were one class, `EnvVarTests+Prefixed`: its tests call `envOverride` of the enclosing
module, and the F# compiler's `CompilationArgumentCounts` attribute made that helper a test entry point,
so the recorder credited the rest of the tests' code to `EnvVarTests`. Fixed in a9a11cd (F# compiler
attributes are compiler attributes; a running class nested in the entry point's class keeps its frames).
Argu was "not informative" before (F# not analysed); it is now sampled like every other repository.

## Final round (2026-10-07 → 2026-10-08)

*Judged strictly (see "How mutants are judged"): `runs-final-1007` under the rules of 2026-10-07, then
Meziantou.Analyzer judged again under those of 2026-10-08 (`runs-final-1007-rejudge`) and NLog run again on a
clean machine (`runs-final-1007-nlog-clean`). These are the numbers on the site since 2026-10-08
(`eval/site/corpus_site.py`).*

**Headline: 1 missed of 2561 failing mutants on 43 repositories (95% upper bound 0.19%), 2 inconclusive.
Median share per repository 31%, mean 38%; whole affected test projects (as dotnet-affected selects) 91%.**

- **The miss (NLog, `LogManagerTests`):** the class reads a setting an earlier class left behind, so the map
  records a path the class does not take alone; it came back in the clean rerun. Accepted for v1 and on the
  site's limitations page (docs/design/order-dependent-state.md: 67 % of 8765 classes read another class's
  leftover, too broad a signal to select on).
- **Meziantou.Analyzer's miss in the round** was a test host that died once under load in a part the selection
  left out; under the 2026-10-08 rule a left-out crash is a miss only when it repeats, and it did not (0 of 74).
- **Not run:** Orleans, Akka.NET, Lucene.NET, ABP and MSBuild (5 of the 48 entries).
- **Incidents:** 20 (in the round's summary.md); each makes a selection fall back to names, which can only select
  more.
- Precision is the median number of classes run per class that failed (1.0 is exact).

| Repository | Failing mutants | Missed | Inconclusive | Selected | Project level | Precision |
|---|---|---|---|---|---|---|
| polly | 64 | 0 | 0 | 3% | 65% | 1.0 |
| spectre | 61 | 0 | 0 | 6% | 76% | 2.0 |
| opentelemetry-dotnet | 56 | 0 | 0 | 8% | 50% | 2.4 |
| meziantou-analyzer | 74 | 0 | 1 | 9% | 100% | 1.0 |
| swashbuckle | 66 | 0 | 0 | 10% | 62% | 1.0 |
| unitsnet | 50 | 0 | 0 | 10% | 83% | 2.0 |
| nsec | 63 | 0 | 0 | 12% | 100% | 1.0 |
| guardclauses | 23 | 0 | 0 | 13% | 100% | 1.0 |
| mimekit | 56 | 0 | 1 | 13% | 100% | 6.0 |
| fluid | 63 | 0 | 0 | 15% | 100% | 1.0 |
| nodatime | 60 | 0 | 0 | 15% | 93% | 1.9 |
| aspnetcore-odata | 60 | 0 | 0 | 16% | 98% | 9.1 |
| roslynator | 69 | 0 | 0 | 17% | 72% | 1.0 |
| awesomeassertions | 63 | 0 | 0 | 18% | 100% | 1.0 |
| communitytoolkit | 67 | 0 | 0 | 18% | 41% | 1.0 |
| autofixture | 71 | 0 | 0 | 20% | 68% | 1.6 |
| fastendpoints | 67 | 0 | 0 | 20% | 75% | 3.0 |
| fluentvalidation | 64 | 0 | 0 | 20% | 100% | 1.0 |
| vogen | 57 | 0 | 0 | 20% | 96% | 1.0 |
| serilog | 61 | 0 | 0 | 21% | 100% | 2.0 |
| castle-core | 56 | 0 | 0 | 30% | 100% | 2.9 |
| nlog | 55 | 1 | 0 | 31% | 98% | 6.0 |
| open-xml-sdk | 67 | 0 | 0 | 35% | 95% | 1.1 |
| markdig | 61 | 0 | 0 | 39% | 100% | 1.0 |
| yamldotnet | 65 | 0 | 0 | 40% | 100% | 1.1 |
| refit | 74 | 0 | 0 | 41% | 100% | 1.5 |
| lamar | 64 | 0 | 0 | 42% | 98% | 2.4 |
| carter | 64 | 0 | 0 | 46% | 67% | 2.3 |
| newtonsoft-json | 69 | 0 | 0 | 48% | 100% | 6.0 |
| scriban | 59 | 0 | 0 | 48% | 100% | 1.8 |
| autofac | 73 | 0 | 0 | 53% | 100% | 4.0 |
| protobuf-net | 60 | 0 | 0 | 55% | 98% | 6.6 |
| scrutor | 62 | 0 | 0 | 55% | 100% | 1.0 |
| argu | 64 | 0 | 0 | 56% | 100% | 1.0 |
| graphql-dotnet | 53 | 0 | 0 | 59% | 99% | 13.0 |
| immediate-handlers | 39 | 0 | 0 | 63% | 95% | 2.8 |
| nsubstitute | 63 | 0 | 0 | 77% | 100% | 5.9 |
| cliwrap | 43 | 0 | 0 | 78% | 100% | 4.5 |
| automapper | 70 | 0 | 0 | 85% | 100% | 14.6 |
| mediator | 59 | 0 | 0 | 85% | 100% | 4.9 |
| efcore | 21 | 0 | 0 | 89% | 90% | 11.4 |
| mapperly | 55 | 0 | 0 | 97% | 98% | 61.3 |
| libgit2sharp | 50 | 0 | 0 | 100% | 100% | 32.0 |

## Corpus round 3 (2026-10-06)

*Judged with the lenient rules used up to round `runs-full-1006` (see "How mutants are judged").*

The whole corpus at the pinned commits, built from `592c15b` plus the generator-copy fix, run twice:
once with the single-bucket rule off and once with it on, nothing else different (outcomes cached, so
the second run only re-selects). The rule was removed after this round (see
`docs/design/precision-round-2026-10-05.md`, item 2); the "rule off" column is what TestDetta does now.

**Headline: 0 missed of 2422 failing mutants on 40 informative repositories (95% upper bound 0.12%).
Median share per repository 30% (rule on: 35%), mean 40% (rule on: 44%); whole affected test projects
(as dotnet-affected selects) 90%.**

- Mapperly's row comes from a run of its own (`runs-nosingle-mapperly`): in the round the memory guard
  killed its schema check while another Mapperly run shared the machine, and the eval then hit a file
  lock writing the assembly (fixed in `a34eb37`; a memory kill still counts as "schema differs", open).
- **Not informative:** Argu (F#), and AspNetCoreOData: 0 failing mutants in rounds 2 and 3 (79 in round
  1), so its suite does not fail under the eval any more; to be looked into.
- Precision is the median number of classes run per class that failed (1.0 is exact).

| Repository | Failing mutants | Missed | Selected | Rule on | Project level | Precision |
|---|---|---|---|---|---|---|
| polly | 64 | 0 | 2.9% | 3.1% | 64.8% | 1.0 |
| spectre | 61 | 0 | 5.9% | 6.3% | 76.0% | 2.0 |
| opentelemetry-dotnet | 56 | 0 | 8.1% | 13.5% | 50.5% | 1.9 |
| unitsnet | 49 | 0 | 9.7% | 34.8% | 83.5% | 2.0 |
| swashbuckle | 66 | 0 | 10.1% | 10.6% | 61.9% | 1.0 |
| nsec | 63 | 0 | 12.2% | 12.3% | 100.0% | 1.0 |
| guardclauses | 23 | 0 | 13.5% | 13.5% | 100.0% | 1.0 |
| mimekit | 58 | 0 | 13.7% | 14.8% | 100.0% | 6.0 |
| fluid | 60 | 0 | 14.4% | 22.3% | 100.0% | 1.0 |
| nodatime | 60 | 0 | 14.7% | 22.9% | 93.4% | 1.0 |
| communitytoolkit | 64 | 0 | 15.6% | 17.1% | 40.3% | 1.0 |
| roslynator | 69 | 0 | 16.9% | 34.9% | 72.0% | 1.0 |
| awesomeassertions | 63 | 0 | 19.6% | 24.4% | 99.9% | 3.0 |
| autofixture | 71 | 0 | 20.3% | 23.8% | 67.5% | 1.5 |
| fluentvalidation | 64 | 0 | 20.4% | 23.5% | 100.0% | 1.0 |
| fastendpoints | 66 | 0 | 20.6% | 25.9% | 75.3% | 4.0 |
| serilog | 61 | 0 | 20.8% | 25.7% | 99.9% | 2.0 |
| carter | 64 | 0 | 22.1% | 23.5% | 67.4% | 1.0 |
| open-xml-sdk | 62 | 0 | 29.7% | 32.5% | 94.5% | 1.0 |
| castle-core | 56 | 0 | 29.9% | 31.7% | 100.0% | 2.9 |
| vogen | 57 | 0 | 30.3% | 34.2% | 95.7% | 1.1 |
| markdig | 61 | 0 | 39.2% | 39.3% | 100.0% | 1.0 |
| yamldotnet | 64 | 0 | 41.8% | 42.2% | 100.0% | 1.7 |
| lamar | 64 | 0 | 42.0% | 43.6% | 98.4% | 2.4 |
| refit | 68 | 0 | 43.2% | 45.2% | 100.0% | 1.5 |
| scriban | 59 | 0 | 44.7% | 58.9% | 100.0% | 1.0 |
| meziantou-analyzer | 74 | 0 | 45.1% | 65.1% | 100.0% | 3.0 |
| newtonsoft-json | 68 | 0 | 47.1% | 48.5% | 100.0% | 6.0 |
| scrutor | 62 | 0 | 54.8% | 55.2% | 100.0% | 1.0 |
| autofac | 69 | 0 | 57.0% | 61.8% | 100.0% | 8.2 |
| graphql-dotnet | 53 | 0 | 58.7% | 61.3% | 98.9% | 13.0 |
| immediate-handlers | 39 | 0 | 62.6% | 70.0% | 94.7% | 3.3 |
| nsubstitute | 63 | 0 | 76.9% | 78.5% | 100.0% | 4.0 |
| cliwrap | 43 | 0 | 78.5% | 80.8% | 100.0% | 4.2 |
| mediator | 55 | 0 | 84.0% | 86.4% | 100.0% | 7.0 |
| protobuf-net | 63 | 0 | 84.2% | 84.4% | 97.8% | 46.3 |
| automapper | 70 | 0 | 84.6% | 84.7% | 100.0% | 14.4 |
| nlog | 56 | 0 | 96.3% | 96.4% | 97.8% | 133.0 |
| mapperly | 69 | 0 | 96.8% | 96.8% | 96.8% | 24.4 |
| libgit2sharp | 65 | 0 | 100.0% | 100.0% | 100.0% | 16.0 |
| argu | 0 | 0 | – | – | – | – |
| aspnetcore-odata | 0 | 0 | – | – | – | – |


## Corpus round 2 (2026-10-03 → 2026-10-04)

*Judged with the lenient rules used up to round `runs-full-1006` (see "How mutants are judged").*

The same method as round 1 on 42 repositories (Refit added), with every fix up to `e55241c`: MSBuild
evaluation of each project, the real preprocessor symbols per target framework, initialization and
field-read data in the maps. Recorded fresh at each repository's head
(the commits are now pinned in `eval/corpus.json`).

**Headline (round 2c): 2 missed of 2313 failing mutants (95% upper bound 0.27%), both fixed since.
TestDetta ran 44% of the test classes on average, whole affected test projects (as dotnet-affected
selects) 91%. Median share per repository 35% (round 1: 27%).**

- **Misses:** AwesomeAssertions `NumericAssertions'1..ctor` and `FailureMessageFormatter.Format`, both
  failing an MSpec context in `MSpec.Specs`. MSpec's adapter reports the namespace as the class, so the
  map named a class that does not exist and the context was never a candidate. Fixed in `82ff30a`;
  the rerun is round 2d.
- **Round 2b's 26 misses** (all Markdig) were an evaluation artefact: under the machine's Turkish locale
  Markdig regenerates its spec classes with culture-dependent names, so failing classes matched nothing
  in the map. Builds and tests now run under en-US (`environment` in `eval/corpus.json`); 2c has 0
  Markdig misses at the same commit.
- **Wider than round 1** on some repositories (Roslynator 8% → 35%, OpenTelemetry 6% → 15%) and
  narrower on others (Markdig 98% → 39%, NodaTime 43% → 23%). The fixes since
  round 1 (run-once code credited to every reader, the MSBuild evaluation) widen the selection where
  shared code runs everywhere and narrow it where names used to over-select. Precision work comes
  after v1.
- **Not informative:** Argu (F#), Mapperly (its main test host crashed in both baselines while the
  machine was loaded, so only 1 mutant counted; passes when run alone, rerun in round 2d).
- Durations are not comparable with round 1: 2c ran two repositories at once, and a locale
  change invalidated the outcome cache, so every mutant ran again.
- **Round 2d** (2026-10-05, build `150b13b`, shared cache): AwesomeAssertions 0 missed of 63 (the MSpec
  fix confirmed, 4.5 minutes instead of 17 with the cached outcomes), Spectre.Console 0 of 61,
  AutoMapper 0 of 70. Mapperly again counted 1 failing mutant: its main test project ran this time,
  but the outcomes were reused from 2c, recorded while that project was left out. Outcomes now store
  the test projects they were recorded with and are not reused when one more is evaluated.
- **NLog's main test project was never evaluated** (rounds 1 to 2d): its test host dies on macOS
  when `LogEvent_OutOfMemoryException_AsyncNotCrash` throws `OutOfMemoryException` on a background
  thread, so the baseline was incomplete and the project left out. NLog's numbers so far come from its
  three small test projects. The corpus entry now skips that one test; round 2e reruns NLog and
  Mapperly.

## Corpus round 1 (2026-09-30 → 2026-10-02)

*Judged with the lenient rules used up to round `runs-full-1006` (see "How mutants are judged").*

`testdetta-eval --corpus eval/corpus.json`: per repository, coverage is recorded at the default branch's
head, then up to 100 sampled members are changed one at a time (source edit for TestDetta, a `throw` in
the compiled member for the tests) and every test class that fails because of the mutant must be in
TestDetta's selection. A missed class is confirmed by running it alone with and without the mutant.
macOS arm64, .NET SDK 10.0.401 (+ ASP.NET Core 8.0.31 for net8.0 suites). Durations were measured
with several repositories running at once and are noisy; selection shares and misses are not.

**Headline: 41 repositories, 3 missed of 2941 failing mutants (95% upper bound of the miss rate
0.26%); all three are first-day results from builds that predate the fixes, and all three are
verified fixed on fresh recordings (see below).** Every miss found later was fixed with a test that fails without the
fix, and its repository rerun until clean. Refit was added after round 1.

| Repository | Failing mutants | Missed | Selected (mean share) | Precision (median classes run per failing class) |
|---|---|---|---|---|
| roslynator | 80 | 0 | 8% | 1.0 |
| polly | 65 | 0 | 7% | 1.0 |
| nodatime | 80 | 1 | 43% | 1.0 |
| mapperly | 79 | 0 | 89% | 5.7 |
| spectre | 80 | 0 | 9% | 2.0 |
| communitytoolkit | 76 | 1 | 18% | 1.5 |
| argu | 0 | 0 | – | – |
| automapper | 80 | 0 | 67% | 1.7 |
| autofixture | 80 | 0 | 16% | 1.0 |
| fluentvalidation | 80 | 1 | 32% | 1.0 |
| awesomeassertions | 81 | 0 | 27% | 2.0 |
| autofac | 77 | 0 | 49% | 1.0 |
| scrutor | 80 | 0 | 57% | 1.0 |
| lamar | 80 | 0 | 26% | 1.0 |
| libgit2sharp | 57 | 0 | 19% | 1.0 |
| nsec | 79 | 0 | 13% | 1.0 |
| newtonsoft-json | 80 | 0 | 47% | 4.5 |
| protobuf-net | 80 | 0 | 78% | 31.1 |
| yamldotnet | 80 | 0 | 78% | 3.4 |
| castle-core | 80 | 0 | 19% | 1.3 |
| nsubstitute | 3 | 0 | 29% | – |
| nlog | 65 | 0 | 82% | 3.0 |
| unitsnet | 80 | 0 | 34% | 2.0 |
| serilog | 79 | 0 | 20% | 1.5 |
| open-xml-sdk | 80 | 0 | 30% | 1.0 |
| scriban | 71 | 0 | 100% | 5.6 |
| markdig | 80 | 0 | 98% | 6.0 |
| mimekit | 79 | 0 | 14% | 5.0 |
| vogen | 77 | 0 | 36% | 1.0 |
| mediator | 80 | 0 | 21% | 1.2 |
| immediate-handlers | 43 | 0 | 29% | 1.0 |
| meziantou-analyzer | 80 | 0 | 63% | 186.6 |
| swashbuckle | 80 | 0 | 12% | 1.0 |
| fastendpoints | 80 | 0 | 23% | 1.8 |
| carter | 80 | 0 | 24% | 1.0 |
| aspnetcore-odata | 79 | 0 | 13% | 2.1 |
| opentelemetry-dotnet | 79 | 0 | 6% | 1.3 |
| graphql-dotnet | 80 | 0 | 45% | 4.8 |
| guardclauses | 24 | 0 | 13% | 1.0 |
| cliwrap | 69 | 0 | 81% | 5.0 |
| fluid | 79 | 0 | 23% | 1.0 |

Not informative: Argu (F#: no C# member to change), NSubstitute (3 failing mutants: NUnit under the
testing platform wrote no report; fixed for round 2), Guard Clauses (small library, 24). Refit was
added after round 1 (first TUnit suite).

### Misses

- First day (2026-09-30), from builds before the run-once rules, generated-code reading and the
  alone-confirmation:
  - NodaTime `TextCursor..ctor` (7 classes that use patterns parsed once in static initialisers);
  - CommunityToolkit `Dictionary2<,>.Initialize` (3 classes);
  - FluentValidation `RuleBase<,,>.Current` (4 classes).

  The round-1 maps of these runs lack initialization and field-read data, and with them the NodaTime
  miss still reproduces. So each change was checked again on a fresh recording at the same commit
  with the current TestDetta (2026-10-02):
  - NodaTime: all 7 classes are selected (the test project runs whole).
  - FluentValidation: all 4 are selected.
  - CommunityToolkit: all 3 projects select the class, by coverage. It was a real dependency: with
    a `throw` in `Initialize`, the class fails alone too (`WeakReferenceMessenger.Default`'s static
    initializer builds a `Dictionary2`). The field-read probes now credit that building code to
    every reader of `Default`. Checked on round 2's recording at the same commit.
- Found and fixed later, repository rerun clean: MimeKit (NUnit `[SetUpFixture]`, static
  constructor), Open-XML-SDK (hand-rolled lazy init), Meziantou.Analyzer (test projects sharing a
  directory, object pool create delegate), FastEndpoints (fixture declared by a base class in a
  library), OpenTelemetry (test files linked into other test projects, a static helper of another
  test class), plus evaluation artefacts (stale `.pdb`, package copies of the library, crashed
  baselines) that are now detected.

### Precision

Safety comes first; the cost is selection size. Worst: Meziantou.Analyzer (186 classes per failing
class: a change spreads through common names such as `Create` across its Roslyn-version projects),
protobuf-net (31), Markdig (6), Mapperly (5.7), Scriban (5.6), MimeKit, CliWrap (5). These are the
precision round's starting point, measured again on round 2's data.

## Pilot: history mode (2026-09-30)

State on 2026-09-30, TestDetta at `2a657c3`. Every run used `testdetta-eval`: for each of the latest
first-parent commits it records coverage at the parent, lets TestDetta select, runs the selection and
the full suite, checks the selection against coverage recorded after the commit, and injects
mutants (a `throw` in a changed member) to see whether a failing test class was left out.
macOS arm64, .NET SDK 10.0.401, `-f net10.0`, `--parallel 4`, 2 mutants per commit.

**Headline: 0 missed test classes in 70 evaluated commits across 9 repositories.** Two classes of
real misses were found while evaluating and are fixed (see below). Polly, NodaTime, Roslynator and
CommunityToolkit were run with both fixes; AfterApply, Spectre.Console, Mapperly, Argu and
FSharp.Data ran before them and should be rerun.

## Results

| Repository | Commits | Full suite | With TestDetta | Saved | Mutants caught / missed | Notes |
|---|---|---|---|---|---|---|
| AfterApply (own, xUnit v2, Testcontainers) | 8 | 8 m 5 s | 3 m 22 s | 58% | 11 / 0 | mutants from the first run, times from the second |
| Polly (MTP, xUnit v3) | 20 | 3 m 13 s | 1 m 19 s | 59% | 0 / 0 | few commits touch library code |
| NodaTime (NUnit) | 6 | 2 m 14 s | 1 m 18 s | 41% | 0 / 0 | code commits narrowed to 47–782 of 26k tests |
| Roslynator (xUnit v2) | 6 | 1 m 50 s | 1 m 12 s | 34% | 3 / 0 | 6 of 7 mutants injected |
| Mapperly (xUnit v3, source generator) | 6 | 5 m 53 s | 3 m 57 s | 32% | 3 / 0 | the generator runs in most tests |
| Spectre.Console | 6 | 15.1 s | 10.4 s | 31% | 2 / 0 | package bumps run everything (Polyfill) |
| CommunityToolkit (MSTest, generators) | 6 | 20 m 54 s | 15 m 14 s | 27% | 3 / 0 | was 40% before the `#if` fix, which was a miss |
| Argu (F#) | 6 | 5.2 s | 4.0 s | 21% | – | F# is not analysed; runs whole projects |
| FSharp.Data (F#, Paket) | 6 | 52.2 s | 0 s | – | – | only workflow/doc commits; not informative |

"Mutants caught" counts mutants that made at least one test class fail in the full suite; only
those can prove or disprove a selection. 22 such mutants, 0 missed. The coverage check found 0
missed classes everywhere.

## Candidates

Evaluated: the nine above. Rejected: Humanizer and Verify (pin an SDK 11 preview); microsoft/testfx,
akka.net and BenchmarkDotNet were dropped during screening. Roslynator's 48 projects make it the
largest graph so far. Still worth adding:
a Windows-only repository (WPF/WinForms) once a Windows machine is available, and a large
ASP.NET Core app with integration tests like AfterApply.

## Fixed while evaluating

- **Code inside `#if` was invisible** (`2a657c3`, a real miss). Files were parsed with no
  preprocessor symbols, so a whole file inside `#if ROSLYN_4_12_0_OR_GREATER` (CommunityToolkit)
  or `#if NET8_0_OR_GREATER` produced no changed symbols, and test classes inside such regions were
  not indexed. Files are now parsed a second time with every symbol their conditions name; a change
  that still maps to no declaration counts as a whole-project change.
- **`AssemblyName` from imports or property functions** (`58c0f5f`). CommunityToolkit
  (`$(MSBuildProjectName.Substring(...))`) and Roslynator (`$(RoslynatorDllPrefix)Roslynator.Core`)
  compiled assemblies under names the loader did not know, so those libraries were not instrumented
  (selection fell back to names, safely) and mutants could not be injected. Such names are now asked
  from MSBuild, once per affected project.
- Earlier in the evaluation: central package bumps narrowed past test packages added from
  `eng/$(ProjectType).targets` (Polly, a real miss, `a42419b`); a directory-wide
  `Directory.Packages.props` wrongly treated as a hidden project input; projects named `*Test` that
  run no tests; several `testdetta-eval` false positives (symlinked temp roots, same type names in
  unrelated assemblies, reused report directories, artifacts output layout, crashed test hosts).

## Known limitations

- **Windows is untested.** Everything above ran on macOS.
- Code that runs once per process (caches, lazy singletons) is credited to the first test class that
  triggers it; static members and constructors seen by only one class are traced by name instead.
- Code under a mix of symbols (`#if A && !B`) is parsed with the symbols each target framework
  really defines, read from MSBuild's evaluation (2026-10-03, after round 2 found Newtonsoft.Json's
  DateOnlyTests missed). Without an evaluation (MSBuild unavailable) it is still invisible to the
  name trace; changes inside it then run the whole project, and test classes inside it are decided
  by coverage.
- Name tracing is coarse for common override names: a change to one analyzer's `Initialize` reaches
  every type named by an `Initialize`. Scoping overrides to their type hierarchy would narrow this.
- F# and VB code is not analysed; affected F#/VB projects and their dependents run completely. (Since 2026-10-06 both are analysed like C#: docs/design/vb-fsharp-support.md.)
- Test projects are also recognised by a `*.Tests`/`*.Test` name; false positives are skipped at
  run time.
- **State one test class leaves for the next** (NLog, 2026-10-08): a class that reads what an earlier class set
  (NLog's global configuration) takes another path alone; the map records the path of the recorded order, so a
  change to the other path can miss it (1 of 2,562 in the final round, `LogManagerTests`). Accepted for v1
  (owner): the naive signal (`leakedReads`) flags 67 % of the corpus's classes. docs/design/order-dependent-state.md.
- **A catch-all shard that ends with an unexpected exit code makes the map unusable** (EF Core, 2026-10-08,
  also in runs-full-1006). `td record --parallel` ends with a shard for the classes the source index did not
  find; it normally matches no test, and the testing platform's "zero tests" exit code 8 is accepted. Under
  EF Core's .NET 11 preview SDK `dotnet test` ends that shard with exit code 5 ("Zero tests ran", a handshake
  failure) and no report. A failed shard without a report makes `Untrusted` return null, so the map is not used
  and selection falls back to names: all 13 EF Core test projects, 89 % of the classes selected. Safe (it only
  costs time), and the general fix (check with `--list-tests` that the shard's filter matches no test, for every
  runner) waits until a user meets it outside the preview (owner, 2026-10-08). Recording without `--parallel`
  has no catch-all shard.
- Repositories that run Microsoft's code coverage in every test run (Polly: `--coverage` in
  `eng/Test.targets`) abort at test host exit while TestDetta records (exit 134, `BadImageFormatException`).
  The maps are complete (each shard writes its results before the abort), but `td record` reports
  the run as failed.
