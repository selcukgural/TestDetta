# Twin owners: a second compilation no test references

Status: built 2026-10-11 (high-share repositories, coverage group: Markdig).

## Problem

`Markdig.Signed` compiles every file of `Markdig` a second time (`<Compile Include="..\Markdig\**\*.cs">`), to ship a
strong-named package. No project references it, and no test project depends on it. A changed file therefore belongs
to two owners, and the coverage trace asks the maps about each copy of the change:

- `Markdig`'s copy is answered: the classes that ran the member.
- `Markdig.Signed`'s copy got "no affected test project with a map depends on it" and kept spreading by name. Within
  `Markdig.Signed`, name lookups stay where its code can be named (`ImpactPropagation.CanName`), but an abstract member
  the spread reaches is answered through its implementations (`TestSelector.ResolveThroughImplementations`), and
  `SourceIndex.Implementations` finds them by base type name in every project. Test helpers that override `Markdig`'s
  `InlineParser.Match` (`ParentContainerReplacementInlineParser`, `EmphasisTestExtension`) were among them. Coverage
  answered for the helpers, and 154 classes that ran a helper were selected, on 64 of 100 sampled changes: 163 of 177
  classes, mean share 90.8 %.

## Rule

`CoverageSelection.TryResolve` answers a change, or a reached member, whose owner no affected test project with a map
depends on, with nothing to select. The coverage trace decides only for test projects with a map; the ones without are
traced by name apart. Only the owner and its dependents can name its code, so a spread from that copy has no
legitimate way to a test project with a map.

What stays as before:

- An owner with a map below it is answered by the maps as before.
- An owner whose maps cannot answer (not instrumented, ran outside tests, metadata) stays unanswered.
- A test project that loads the twin's output at run time, by path and without referencing it, would be outside the
  graph's dependents either way; the opaque-output rules cover referenced outputs only.

## Measured

Markdig, 100 sampled changes, `--explain` on the same recording: mean share 90.8 % → 47.3 %, 64 changes narrower, none
wider. Full run (fresh recording): 0 of 61 failing mutants missed.

## Left open

`SourceIndex.Implementations` matches base type names across all projects, whatever the abstract member's project can
see. A partial answer has side effects, too: `ResolveThroughImplementations` selects the runners of the implementations
answered before one that fails. Both only select more. They no longer matter for twins; they may for two unrelated
types of the same simple name.
