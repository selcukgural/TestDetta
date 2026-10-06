# Visual Basic and F#: the same support as C# (proposal, 2026-10-06)

Status: proposal for the owner. Goal (owner, 2026-10-06): Visual Basic (much used in legacy systems) and
F# (in many projects in recent years) get the same support as C#.

## Where the languages stand today

| Step | C# | Visual Basic | F# |
|---|---|---|---|
| Project graph (references, items, MSBuild evaluation) | yes | yes | yes |
| Recording (Cecil rewrites the compiled IL; the tracker counts hits) | yes | yes, IL is the same | yes, IL is the same |
| Test entry points (xUnit/NUnit/MSTest attributes on test classes) | yes | yes, same attributes | mostly: F# modules with `[<Fact>]` functions compile to static classes; to check |
| What changed (`CodeChangeDetector`, Roslyn C# syntax) | members | no: the project changed as a whole | no: the project changed as a whole |
| Who names it (`SourceIndexBuilder`, name tracing) | yes | no | no |
| Selection | class level | an affected VB project runs every test project below it | the same for F# |

So a VB or F# library under C# tests is recorded precisely, but a change to it cannot be described, and
everything below it runs.

## Two ways to close the gap

**A. One analyser per language.** Visual Basic through Roslyn's VB syntax (`Microsoft.CodeAnalysis.VisualBasic`):
a `DeclarationWalker` and change detector like the C# ones, and name tracing over VB identifiers
(case-insensitive). F# through FSharp.Compiler.Service's untyped syntax tree: modules, let-bound
functions, members, active patterns; its compiled names differ from the source (modules become static
classes, curried functions, closures), so a mapping from source names to IL names is needed. Two more
analysers to keep in step with every C# rule (generated code, `#if`, metadata changes, reflection types).

**B. Compare compiled code (language-independent).** At recording, the map keeps a hash per method of
its IL body, signature and attributes (and per type of its declaration). In `td test` the test projects
are built first (as for a changed generator today; `dotnet test` then runs with `--no-build`, so no
extra build), and the same hashes are computed: every method whose hash differs is a changed member,
new ones are additions, missing ones removals. Callers come from the IL too (call instructions), which
gives the name tracing a precise call graph for code the map never saw run. It works the same for C#,
Visual Basic, F#, generated code and any other .NET language, and it covers the drift between the map's
commit and the merge base without a bridge. Points to solve: compiler-generated names that shift (lambdas,
closures, state machines: compare them under their user method, as the recorder already credits them),
inlined constants (a caller's IL changes, which is right), debug-only differences (build in the same
configuration as the map, deterministic builds), and members with no IL body (abstract, extern: compare
signatures).

**Recommendation: B as the core, with the C# source analysis kept where it adds something** (explaining a
decision with source lines, changes inside `#if` branches of other frameworks, non-compiled C# files).
B gives VB and F# full support at once, removes the per-language rule copies, and makes generated code
and drift the normal case instead of special ones. A is faster for VB alone but leaves F# expensive and
doubles the rules to maintain.

## Validation

- Corpus: add Visual Basic and F# repositories with real test suites (F#: FsCheck, Fantomas, Paket or
  FSharp.Data tests; VB: few open-source VB libraries exist, so a VB library under C# tests and a sample
  legacy VB solution are needed).
- The evaluation's mutants are already IL-level, so they apply to every language unchanged.

## Cost

B is the larger change: recorder (hashes per method), map format, a build-first `td test`, the IL change
detector, an IL call graph, and the selection rules moved onto IL identities. A first step that is useful
alone: B's change detector for VB and F# projects only, beside today's C# analysis.
