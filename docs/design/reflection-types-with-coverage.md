# Types found through reflection, with a coverage map

Status: implemented 2026-10-03. First change of the precision round (`eval/precision/attribute.py`).

## Problem

A type that nothing else names and that has a base list or attributes may be created by a framework
through reflection (controllers, handlers, generated protobuf messages). Name tracing cannot find
who runs it, so when the spread reaches such a type, every test project downstream of it runs
completely ("X is affected and nothing names it, so it may be created through reflection").

The rule fired far more often than intended. The precision round (round-2 maps, 100 mutants each)
attributed the selected classes that did not fail:

| Repository | Selected but did not fail | From this rule |
|---|---|---|
| protobuf-net | 91% | 88% (97 of 100 changes) |
| MimeKit | 78% | 48% |

In protobuf-net none of the 97 changes touched the type itself. The spread reached a member of
`RuntimeTypeModel.SerializerPair` (dead code) or of the generated `FeatureSetDefaults` because the
member's code names a changed name. The type name was then spread, because the member may run
without being named (it implements an interface or is a field of a type with a base list). With a
coverage map, the map already says where that code runs. In these cases it says nowhere: no test
ran any member of either type.

## What a map can and cannot answer

Reflection reads a type's **metadata** without running it: attributes, the member list, parameter
default values. Coverage records only code that runs. That means:

- A change to the type or one of its members may change metadata. The map cannot answer it, and the
  project-level fallback stays. The classes the map says ran the changed member are still selected (2026-10-11):
  the fallback needs the type's name spread, which a member named elsewhere does not do (a C# extension method, an
  F# `AutoOpen` module's function), and the name trace alone left out a class that ran the member through a path no
  name shows (Argu's `CliParser`: 4 of the 11 classes that ran it;
  `The_classes_that_ran_a_changed_member_of_a_reflection_found_type_are_selected`).
- A member reached only through its **code** (its body names a changed name) has the same
  declaration as before. It behaves differently only when it runs, which the map records.
- A member reached through its **metadata** (`[DefaultValue(Limits.Max)]` when `Limits.Max`
  changed) changed metadata. This holds for every type, not only reflection-found ones: a
  serializer or schema generator reads the attribute without running the member. Before this
  change, the map answered such members by the classes that ran the member's code. That was a miss
  (proven by `Member_reached_through_its_attributes_is_traced_through_its_type_even_with_a_map`).

## Design

1. **Metadata references.** The source index records, per member, the names in its attribute lists
   (including those on parameters and accessors) and in parameter default values
   (`IndexedMember.MetadataReferences`).
2. **How a member was reached** (`ImpactPropagation.Reach`):
   - `Change`: the member is a change itself. The selector asked coverage about it before seeding,
     so propagation does not ask again. Before this, the seed went through the "reached" callback,
     which would now answer a reflection type's own change.
   - `Code`: the looked-up name is in the member's code. Coverage may answer, as before.
   - `Metadata`: the name is in its metadata. Coverage is not asked, and the type name is spread:
     whatever names the type may read its metadata (`typeof(T).GetProperties()`). A member that
     coverage already answered through its code is spread after all when its metadata is reached
     later.
3. **Reached members of reflection-found types** are answerable (`CoverageSelection.IsAnswerable`
   with `reached`). (Until 2026-10-06, any member of such a type that ran in a single bucket per
   process counted as run-once code; the single-bucket rule was removed after the corpus showed no
   miss without it, see precision-round-2026-10-05.md.)
4. **Reached reflection-found types** (`CoverageSelection.TryResolveType`). This applies only when
   members reached through their code spread the type name. If a change or metadata spread it,
   it does not apply. In every affected test project with a map downstream of the type, the
   classes that ran any member of the type (or a nested type) are selected. A type that no test
   ran took part in no test with the old code. Its discovery (declaration) did not change, so it
   takes part in none now, and nothing is selected for it. The fallback stays when:
   - a map does not instrument the type: its assembly is not loaded by the tests, for example
     code compiled into a source generator;
   - a member of the type ran outside any test (ambient), so its users are unknown;
   - a member ran inside a static constructor or cache fill;
   - the type ran in only one bucket, or in only one bucket per recording process.
5. **Ambient members in type-level lookups.** When a member of a type ran outside any test, the
   map's classes for the type are only some of its users (`CoverageIndex.TypeRanOutsideTests`).
   A type-level answer and the constructor rule ("every class that ran a member of the type") still
   select those classes, and the change is traced by name as well. That fixes a miss, proven by
   `Constructor_change_is_traced_by_name_too_when_a_member_of_its_type_ran_outside_any_test`.
   An earlier version of this change dropped the known classes instead: CI caught it with
   `InjectedServiceTests`, where a DI singleton built before the first test is used through an
   interface no test class names.

### Direct metadata changes (2026-10-03, later the same day)

A member that changes itself has the same problem when its attributes or parameter defaults change
(`[ProtoMember(1)]` → `(2)`). The change detector compares each modified declaration's metadata nodes
(`DeclarationWalker.MetadataNodes`, shared with the source index) and sets
`SymbolChange.MetadataChanged`. A changed using also sets it on every type of the file, since an
attribute name may bind to another type. With the flag, `CoverageSelection.TryResolve` keeps the
classes the maps name but does not stop there. The propagation seeds the member as a `Metadata`
reach, so its type name spreads. A parameter default is also compiled into every call site, so the
callers change too (`Change_to_a_parameter_default_is_traced_through_its_type_even_with_a_map`).

A whole-project reason now ends with the chain that brought the type in
(`ImpactPropagation.ExplainSeeded`), so a user can see why a change ran an unrelated project.

## Results

Measured on round-2 maps with `testdetta-eval --explain` and `attribute.py`, with no missed mutant in
any of the three repositories:

- NodaTime and MimeKit: unchanged. MimeKit's rule is driven by a type changed directly.
- protobuf-net: unchanged overall. `SerializerPair` dropped out (6495 → 36 extra classes), but
  `FeatureSetDefaults` stays: `Descriptor.cs` is also compiled into `protobuf-net.BuildTools`, a
  source generator. No map instruments that copy, so the fallback rightly stays. Looking into this
  found the next gap: project references used as an analyzer or source generator, and
  `ReferenceOutputAssembly="false"` references, are treated as ordinary runtime dependencies (see
  the backlog, Known gaps).
