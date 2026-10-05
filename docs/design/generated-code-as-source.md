# Generated code indexed as source

Status: implemented 2026-10-05. Precision round, group A.

## Problem

Source generators write code that is in no file of the repository: polyfills (Meziantou.Polyfill's
`PolyfillExtensions`, PolySharp's `ExperimentalAttribute` and friends), logging, MVVM properties.
The recorder read it from the PDBs, but kept only a summary: per generated type, the names its code
mentions. The index added one member called `<generated>` per type, with every name of the type.

The precision round (`--explain`, `eval/precision/attribute.py`) found two costs:

- **Generated types counted as removed.** A type the spread reached was looked up among the source
  types, did not exist there, and was reported as "removed and nothing names it", which runs the
  whole project. Mapperly: 77% of the classes it selected that did not fail; GraphQL: 75%.
- **One member per type.** Meziantou.Polyfill writes `PolyfillExtensions` with 987 members, which 89
  of Mapperly's 100 test classes run. Any name one of them mentions reached the whole type.

A third gap showed up on the way: a C# 14 `extension(...)` block was read as a nested type with an
empty name (`PolyfillExtensions.`, `PolyfillExtensions.`1`), while the compiler emits its members as
static methods of the enclosing class, which is where the maps record them. Coverage never found them.
This holds for hand-written code too.

## Design

- **The map stores the generated files**: per file, its project, its PDB path (relative to the
  repository when inside it) and its text, gzip-compressed. Map format 2. A map of another format is
  not used: the selection warns and continues without it, which selects more, never less (before,
  an unreadable map ended the command).
- **The index reads them like source files** of their project, under a path no file on disk has
  (`generated:<project>/<name>`), so a diff never matches them. Generated types are types with members,
  base lists and attributes; every rule that applies to source applies to them (the attribute rule,
  reflection discovery, the run-once rules), and the maps answer member by member.
- **Extension blocks belong to their enclosing class.** A member of an `extension(...)` block is a
  member of the static class around it; an instance member's signature starts with the receiver type,
  as compiled (`Shout(string)`), which also tells the same member of two blocks apart.

What went away: `GeneratedType`, `GeneratedCode.Collect`, the `<generated>` member and its special
"may be called implicitly" flag. Generated code with `[ModuleInitializer]` or other attributes is
still called implicitly, through the same rule as source.

## Results

Verified with stored outcomes on Mapperly, GraphQL, Mediator, Vogen, Immediate.Handlers, Polly and
CommunityToolkit: no missed mutant. The share did not drop on Mapperly (93.7%) or GraphQL (50.0%):
the "removed" rule had been hiding the next layer. In Mapperly, the polyfill's
`IsWindowsVersionAtLeast` reads `Version.Build`, which name tracing cannot tell from Mapperly's own
`Build`, and the integration tests' map, which cannot know generator code, kept coverage from
answering (see docs/design/opaque-project-references.md, "Consumers that run completely"). In GraphQL,
the rest is coverage reached through names and the constructor rule (the run-once rules).

## Tests

- A generated type reached by name is answered by the classes that ran its reached member, not the
  whole project and not every class that ran a member of the type.
- Generated code in a test project is still traced by name.
- A removed type nothing names still runs its whole project, with or without a map.
- A map of an older format is ignored and the change is traced by name.
- Extension block members are members of the enclosing class (change detection).
- Generated files and their names are read from portable and embedded PDBs (slow).
