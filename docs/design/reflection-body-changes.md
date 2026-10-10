# Body changes in types found through reflection

Status: approved by the owner 2026-10-10 (precision triage), implemented the same day.

## Problem

A type that nothing names and that a framework may create through reflection (a handler, a controller, an EF
Core extension) runs every test project below it completely when the name spread reaches it
(docs/design/reflection-types-with-coverage.md). Since 2026-10-03 the maps answer for such a type when only
**code** reached it: the classes that ran any of its members are selected (`CoverageSelection.TryResolveType`).
When the **change itself** is in the type, they were never asked: a change may alter what reflection reads
(the declaration, the member list, attributes, defaults).

The precision triage of 2026-10-10 (`--explain --any-recording`, docs/evaluation.md) put that case first in
NLog: 51% of the classes its sampled changes selected came from "the change or its metadata reached it, not
only its code". Every sampled change there was a modified method body.

## What a body change can do

Reflection reads metadata without running code. A modified method or constructor body whose declaration and
metadata did not change (`SymbolChange.Kind == Modified`, no `MetadataChanged`: same signature, attributes and
parameter defaults) leaves everything reflection reads as it was: the type is found by the same code, with the
same members. It behaves differently only where the changed body runs, which the maps record. That is the
same situation as a type reached through its code, which the maps already answer.

## Rule

A change counts as reaching its type through code when it is a modified body of a method or constructor (a
member whose signature has a parameter list) that has code of its own (not abstract), with no metadata change.
Then `TryResolveType` is asked as for a code reach, with every one of its conditions: each map below the type
instruments it, no member of it ran outside any test or in a static constructor or cache fill, and no
third-party library calls it back; otherwise the project-level fallback stays, with that reason.

Everything else keeps the fallback: a type-level change, an added or removed member, a metadata change, a
field, a constant (compiled into callers and readable through reflection), a property (not covered by this
first version), and a static constructor.

## Risk

A miss would need a test whose result depends on the changed body without the body running in that test's
class while the map was recorded. A reflection-found type's code runs where the framework creates and calls
it, inside the tests that use it, which the map records; code that ran before or between tests is excluded by
the outside-test and initialization conditions. Measured on the corpus with mutants before shipping.

## Measured (2026-10-10)

The triage's count did not size the gain. The NLog reason "the change or its metadata reached it" covered two
paths: the change in the type itself, which this rule answers, and a type reached by name through its base type
(`RegexReplaceLayoutRendererWrapper` names `LayoutRenderer`, which the change reached), which it does not. NLog
and Argu selected exactly the same classes with and without the rule (100 mutants each; 0 missed of 55 and
64 failing mutants).

To find where the rule changes anything, every library repository was explained before and after it
(`--explain --any-recording`, no build, no test run), and `selectedShare` was compared per change. Of about
1,370 explained changes in 14 repositories, 12 selected less, none more:

| Repository | Narrower changes | Mean share before → after |
|---|---|---|
| Lamar | 4 | 38.3 % → 34.5 % |
| Open XML SDK | 3 | 31.9 % → 30.8 % |
| FluentValidation, Serilog, Markdig, Carter, OpenTelemetry | 1 each | down by 1 point or less |

Roslynator, MimeKit, Vogen, immediate-handlers and Argu lost the reason but kept the same selections:
another reason selects those classes. protobuf-net and Mapperly could not be explained (their generator
build fails in explain mode).

Full evaluations with the rule on the seven repositories with a narrower change (the 100-mutant sample the
explain used, so the 12 are in it): **0 missed of 437 failing mutants**. Of the 12 narrower changes, three
in Open XML SDK broke tests, and every failing class stayed selected; the others broke no test, so the
classes the rule leaves out are ones no test needed.

The rule is safe and small. The larger case, a reflection-found type reached by name through its base type,
is a separate proposal (NLog).
