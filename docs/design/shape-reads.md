# Shape reads: which classes read a type's shape

Status: recording and the shape-reader rule (which only adds classes) in this change, 2026-10-12. The narrowing rule for
types that only carry data stays on branch feat/shape-rule (owner, 2026-10-12): in the shape A/B on 11 repositories it
caused no miss (86 failing shape mutants) but moved shares by at most 0.9 points, since the corpus has few such types;
its target, AfterApply's response records, cannot be measured here (no Docker for its Testcontainers, CI closed until
2026-11-01). It is decided once AfterApply can be measured.

## Problem

A change to a record's metadata, such as an optional parameter added to a response record, is traced by name. In
AfterApply the endpoint registrations name the record (`.Produces<ApplicationDetailResponse>()`), every class's host runs
them at start-up, and 96 of 99 integration classes run (backlog, 2026-10-09). Answering through coverage of the record's
constructor and accessors alone was withdrawn: a test that reads the OpenAPI document depends on the record's shape and
runs none of its code. Nothing recorded "this class read the type's shape".

An experiment modelled on AfterApply (minimal API, `Produces<T>()`, a class serializing the record, a class reading
`/openapi/v1.json`, an unrelated class) also found a real miss: the class reading the document was skipped and failed,
because the recording named the top-level statements `Main` (fixed in #44).

## What reads a shape

Most of it happens inside the shared framework: System.Text.Json builds its metadata, ASP.NET its endpoint metadata. No
rewrite reaches the shared framework. But the calls into it come from code the recording does rewrite:
`Microsoft.AspNetCore.OpenApi` (a package in the test output) calls `JsonSchemaExporter.GetJsonSchemaAsNode`,
`GetProperties` and `GetCustomAttributes`; a test or a third-party library calls `Type.GetProperties`, `IsSealed`,
`GetCustomAttributes`. Serializing runs the record's accessors, deserializing its constructor: coverage sees those.

## Recording

- `FileReadCalls.ShapeReaders`: the members, attribute and schema calls of `Type`, `MemberInfo`, `TypeInfo`,
  `CustomAttributeExtensions`, `Attribute`, `JsonSchemaExporter.GetJsonSchemaAsNode` and
  `JsonSerializerOptions.GetTypeInfo`, non-generic overloads, wrapped in repository and third-party code. Their first two
  arguments go to `TestDettaTracker.ShapeRead`.
- The tracker takes the type a `Type`, a member (its declaring type) or a `JsonTypeInfo` stands for, and records each
  repository type as `shape:<full name>` in the bucket's output uses, with the repository types its public properties and
  fields hold, two levels down (a schema lists them too). Computed once per type.
- A repository type is one whose assembly holds `<TestDettaRepository>`, a type the instrumenter adds to every
  repository module.

Experiment: `OpenApiSchemaTests` records `shape:Contracts.ApplicationDetailResponse`; the class that serializes it and the
unrelated one record only `shape:Program` (the test factory reflects over the entry point).

## Every metadata change selects the shape readers (2026-10-12)

A shape mutant on aspnetcore-odata removed `[ODataAttributeRouting]` from `ODataController`: 23 classes failed, and two
were not selected, `AttributeRoutingConventionTests` and `ODataRoutingApplicationModelProviderTests`. OData's routing reads
the attribute of the controllers derived from it (`inherit: true`); the two classes name neither. Both were recorded as
reading `shape:...ODataController` (a read type's base types are part of its shape). For any change to a type's
metadata, `CoverageSelection.AddShapeReaders` now adds the classes that read its shape and the users of a fixture whose
setup did; it only adds classes, whatever else answers the change (`ShapeReaderSelectionTests`).

## Limits

- Generic overloads (`GetCustomAttribute<T>`) are not wrapped yet.
- Shape reading inside the framework that no wrapped call starts, such as ASP.NET model metadata at start-up.
- A cache across classes: a document generated once and served from a host that several classes share (a collection
  fixture, a static host) is read by the first class only. Per-class hosts (`IClassFixture`, AfterApply) read it per class.

## Proposed rule (on branch feat/shape-rule)

For changed metadata of a type whose members only carry data: the classes that ran its constructor or accessors, the
classes that read its shape (`shape:`), and the users of a shared host or fixture one of them used, instead of the name
trace through the code that names it. Measured before deciding: AfterApply (a clone, never the working copy), the shape
mutants (#42: attribute-removed, unsealed) on the corpus, and the experiment.
