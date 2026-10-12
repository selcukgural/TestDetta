# Shape reads: which classes read a type's shape

Status: recording on branch feat/shape-reads; the selection rule on feat/shape-rule, built for measurement with the
owner's approval (2026-10-12). It narrows, so it merges only after the owner has seen the measurement.

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

## Limits

- Generic overloads (`GetCustomAttribute<T>`) are not wrapped yet.
- Shape reading inside the framework that no wrapped call starts, such as ASP.NET model metadata at start-up.
- A cache across classes: a document generated once and served from a host that several classes share (a collection
  fixture, a static host) is read by the first class only. Per-class hosts (`IClassFixture`, AfterApply) read it per class.

## Rule (feat/shape-rule, being measured)

For changed metadata of a type that only carries data (`IndexedType.CarriesDataOnly`: positional parameters and
auto-properties, nothing else in any part), `CoverageSelection.SelectShapeReaders` selects, besides the classes that ran
its members, the classes that read its shape and the users of a fixture whose setup read it, and the change is not
traced through the code that names it. It keeps the name trace when a downstream map did not track shape reads
(`CoverageMap.TracksShapeReads`, written by the recorder) or saw the shape read outside every test or while initializing.
Experiment: 2 of 3 classes (the one serializing the record and the one reading the OpenAPI document; the full run fails
only the latter), where the name trace through `<Main>$` ran all 3. `ShapeReadSelectionTests`.

Not yet covered: a document cached in a host several classes share through a collection fixture (only the first reader
is recorded); a class fixture per class, as AfterApply's, is not affected. Measured before deciding: AfterApply (a clone,
never the working copy), the shape mutants (#42: attribute-removed, unsealed) on the corpus, and the experiment.
