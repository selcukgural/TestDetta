# Visual Basic and F#: the same support as C# (2026-10-06)

Status: Visual Basic and F# done 2026-10-06 (approach A, see "Decision"). Goal (owner, 2026-10-06):
Visual Basic (much used in legacy systems) and F# (in many projects in recent years) get the same
support as C#.

## Where the languages stood before (2026-10-06 morning)

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

## Decision (2026-10-06)

The owner accepted B "unless A is healthier and faster". It is faster: B needs a build before every
analysis, so `td affected` (seconds today, no build) and every selection of the evaluation (100 per
repository, none built today) would pay a compile; A parses source in milliseconds. A was taken, with
the duplication contained: only syntax is per language (`SourceLanguage`: parse, declarations, project-
and file-level nodes, metadata nodes, significant trivia); identities, the change comparison, the index
records, name tracing, coverage and the selection rules stay shared.

## As built: Visual Basic (2026-10-06)

- `VisualBasicLanguage` (Roslyn's VB syntax): declarations with C#'s identities (`Add(Integer,Integer)`,
  `.ctor`/`.cctor`, `` `n `` arity, properties by name and with parameters when indexed, comma-joined
  fields, operators by their compiled names `op_Addition`/`op_Concatenate`/`op_Implicit`), the project's
  `RootNamespace` before every namespace (not for `Global.`), `#If` symbols, `<Assembly:>` attributes
  project-wide, `Option`/`Imports` as file-level, token comparison ignoring case.
- `RootNamespace` comes from MSBuild (evaluator version 5) or a literal in the project file, else the
  project name.
- `VisualBasicIndexer`: types (classes not `MustInherit`, modules; `Inherits`/`Implements` as base names),
  members (private by VB's defaults: `Dim` fields private in classes and modules, methods public; `Shared`
  and constants static, module members reachable without the module's name; implicitly called through
  initializers, attributes, `Overrides`, `Handles`, `Implements`, `Default`), references including the
  rightmost names of `Imports` and `Handles` containers. Members are `IgnoreCase`: their sets compare
  ignoring case and the index finds them for a name in any case; C# lookups stay exact.
- `TestSelector` no longer falls back for Visual Basic projects or `.vb` files.
- Tests: `VisualBasicTests` (identities, root namespace, `Global`, cosmetic case changes, metadata,
  selection by name from C# and from VB in another case) and `VisualBasicRecordingTests` (a VB library
  under VB xUnit tests recorded for real: a changed member selects the classes that ran it, through a
  module too, by coverage).
- Not yet: the evaluation's sampler changes C# members only, so no corpus measurement of VB.

## As built: F# (2026-10-06)

- `FSharpSource` (FSharp.Compiler.Service's untyped tree and tokenizer, parsing only, one shared
  checker under a lock): declarations with the names the compiler gives them. A module is a type
  (`Shop.Calc.Helpers`; nested in a module with `+` at runtime), with `Module` after its name when a
  type of that name sits beside it or `[<CompilationRepresentation(ModuleSuffix)>]` says so; a file
  without a header is an implicit module named after the file with a capital first letter. Its `let`
  functions and values are members by name (a nameless `let ()`/`do` is `.cctor()`), type members by
  name with their argument patterns (`Tax(net:decimal)`, overloads apart), properties by name, the
  primary constructor and `new` as `.ctor`, class `let`s as private members.
- `FSharpChangeDetector`: the comparison runs over the compiler's significant tokens (no whitespace or
  comments; before each line's first token a marker of its column, since indentation is syntax), per
  declaration span, under every `#if` view like C#. Attribute tokens decide metadata changes;
  `[<assembly: ...>]` affects the project; tokens outside every member and non-module type (namespace
  and module lines, `open`s) mark every type of the file. A version that does not parse affects the
  project.
- `inline` functions and members carry `SymbolChange.Inlined`: the compiler copies their body into
  the callers, which the map credits, so coverage cannot answer and the callers are traced by name.
- `FSharpIndexer`: types (modules runnable; classes unless `[<AbstractClass>]`; `inherit` and
  `interface` as base names), members (private by `private`; module members reachable through an
  `open`, so not static access; implicitly called for values, overrides, interface members,
  attributed members and conventional names), references from identifier tokens plus the rightmost
  names of the file's `open`s. Case-sensitive.
- Recording: FSharp.Core (and the Visual Basic runtime) count as part of the framework in
  `ExternalContracts`: every F# lambda overrides `FSharpFunc.Invoke`, which made all code reached
  through a closure look like a third-party callback (run-once evidence) and selected every class that
  used the closure's types.
- `TestSelector` no longer falls back for F# projects; `.fsi` files fall back as non-source files.
- Tests: `FSharpTests` (identities, overloads, constructors, cosmetic vs indentation, inline,
  metadata, `open`s, added types, implicit modules, assembly attributes, `#if`, selection by name from
  C# and from F# test modules) and `FSharpRecordingTests` (an F# library under F# xUnit test modules
  recorded for real: a changed member selects the modules that ran it, through a closure too, by
  coverage; a changed inline function its callers by name).
- Cost: the CLI package carries FSharp.Compiler.Service and FSharp.Core (about 21 MB); the compilers' satellite
  resource assemblies are left out of it (`SatelliteResourceLanguages=en`).
- Not yet: the evaluation's sampler changes C# members only, so no corpus measurement of F# or VB.
