# Precision round of 2026-10-05: where the extra selection comes from

Status: findings, for discussion. Groups A and the full-run rule are done; B has its own proposal
(docs/design/generator-output-changes.md); this note is group C, the coverage rules.

## How it was measured

`testdetta-eval --corpus … --explain` on the pinned round-2 commits, joined with round 2c's stored
outcomes by `eval/precision/attribute.py` (which rule selected each class that did not fail), and the
same runs with `-v`, counting the debug line `coverage cannot answer alone: <reason>` per reached
member (`~/sift-corpus/run-reasons.sh`). Counts are over 100 changes per repository, both traces; they
say which rule widens most, not how many classes it adds.

## Done today

| Change | Effect |
|---|---|
| Generated code indexed as source (A, 0766ad4) | false "removed" verdicts gone; no share change alone |
| A project that runs completely has no say in coverage answers | Mapperly 93.7% → 45.3%, Immediate.Handlers 82.2% → 67.1%, no miss on 9 repositories |

## Why coverage could not answer (C)

| Reason | Newtonsoft.Json | GraphQL | AutoMapper | NSubstitute | Lamar | Autofac | Meziantou |
|---|---|---|---|---|---|---|---|
| ran outside any test | **177k** | 31k | – | – | – | – | – |
| a map does not know it | 53k | 23k | 18k | 2k | 1k | 3.5k | 0.6k |
| constructor or static member in one bucket per process | **144k** | 56k | **82k** | 16k | 1k | 2k | 0.5k |
| ran in a static constructor or cache fill | 18k | 30k | 22k | **32k** | 4k | 3k | 1.4k |
| no affected test project with a map depends on it | – | 6.5k | **32k** | 2k | **27k** | 2.7k | – |
| a member of the type ran in a static constructor (type level) | 13k | 12k | 2k | 2k | 0.3k | 0.3k | 0.3k |

("ran outside any test" and "a map does not know it" were one reason until today; split for this
analysis, measured on the first three repositories.)

## Candidates, in the order I would take them

1. ~~**Code that runs outside any test (Newtonsoft.Json: 701 core methods)**~~ (examined, parked):
   the repository's tests use Microsoft.NET.Test.Sdk 16.3.0, whose test host talks to the runner
   through Newtonsoft.Json. It loads the repository's instrumented build from the test output, so the
   protocol traffic is recorded as code outside every test. No test-project method is in the ambient
   set, which fits: the caller is the test platform. It only happens when the library under test is a
   dependency of the test platform itself (current test hosts use System.Text.Json), so it is not
   worth a rule; the ambient rule stays, since code a repository runs before the first test (a DI
   singleton) is a real gap it covers.
2. **The single-bucket rule** (a constructor or static member that ran in one test class per
   process). The largest count in Newtonsoft.Json, AutoMapper and GraphQL. Measured on 2026-10-03/04:
   needed only for code run outside tests (NSec's theory data built during discovery) and for code a
   test framework calls back (AutoFixture's NUnit attributes). Narrowing it to those shapes, now that
   value probes link most run-once code, is the backlog item "narrow the run-once rules"; it needs the
   corpus with fresh recordings, which the warm cache now makes cheap.
3. **"No affected test project with a map depends on it"** (AutoMapper, Lamar). The member's project
   has no test project with a map below it (benchmarks, samples, widget projects loaded by scanning).
   The name trace from there can only reach that project and its dependents; to be checked whether it
   still widens map projects (through type spreads into shared support projects), and if so where.
4. **Per-call third-party callbacks** (Meziantou's analyzers, called by Roslyn; NSubstitute's
   interceptors): already a backlog item. Roslyn calls an analyzer per compilation; the callback rule
   treats it as a cached result.

5. **References that exist for another framework only** (LibGit2Sharp, 100% selected; found
   2026-10-05). LibGit2Sharp.Tests references NativeLibraryLoadTestApp (x86, x64) only when
   `TargetFramework` is `net472`, without compiling against it. The graph merges the references of all
   frameworks, so every change to LibGit2Sharp (which the test app references) affects the test app,
   and the opaque-reference rule runs the whole test project, also when the tests run for net8.0 only
   (the corpus passes `-f net8.0`; so may a team's `dotnet-test-args`). Candidate: keep the frameworks a
   reference holds for in the graph, and when the frameworks that run are known (`-f` in the test
   arguments, or every map recorded with `-f` for one framework), ignore references that hold only
   for others. Needs the owner's view, since without `-f` dotnet test runs every framework, net472
   included where it can run (Windows), and there the reference is real.

Each candidate ships only with a red test first and the stored-outcome miss check on the corpus.
