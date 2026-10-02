# Built-once values: field-read probes

Status: design 2026-10-02. Replaces three interim fixes in "Known gaps behind shipped fixes":
static constructors (7cd8528), object pools (d462594, 6badd5d), hand-rolled lazy initialisation
(5d01d7a).

## Problem

Code that builds a value once and stores it in a field runs in the bucket of whichever test
needed the value first. Every later test that reads the field depends on that code, but the map
does not show it, so a change to the builder can miss them:

- a static field initializer (`static readonly X Instance = Build();`) read by other types'
  code without calling a member of the declaring type;
- `_x ??= new X()` and `if (_x == null) _x = Create();` on a field of a shared instance;
- object pools and hand-rolled caches (`Stack<T>`, `[ThreadStatic]` buffers) that create on a miss.

Today's interim rules recognise some shapes (code inside a static constructor or a known
cache-filling call, `Interlocked.CompareExchange`, pool policies) and trace those changes by name,
which is broad. The shapes they do not recognise are not covered at all.

## Idea

Record what each field's stored value was built from, and which buckets read the field. A change
to a method selects the classes that ran it, plus the classes that read a field whose value that
method helped build. No pattern list is needed: a value built by any code is credited to its readers.

## Design

### Instrumentation

- **Tracked fields**: fields of instrumented assemblies whose type is a reference type (values of
  value types are copied, and a copy built once is still built once, so include structs too when
  their type is a user type), excluding `const` and compiler-generated fields of state machines and
  closures, which hold a flow's locals rather than shared values. Field ids are assigned like
  method ids and stored in the map.
- **Store probe**: before every `stsfld`/`stfld` of a tracked field, `SiftTracker.Stored(fieldId)`.
- **Read probe**: after every `ldsfld`/`ldfld` of a tracked field, `SiftTracker.Read(fieldId)`
  (one call; the bucket bit for the field is set like a method hit).
- Methods of the declaring type's own static constructor and instance constructors store their
  fields too; a field stored in a constructor of a per-test instance is read by that test's own
  bucket, so it adds nothing; a field of a shared instance is what this is for.

### What built a value

- The tracker keeps, per thread, the ids of the methods hit since the last store of a tracked field
  in the current outermost call into instrumented code ("since the builder started"). Keeping a
  growing list per hit is too costly, so builders are bounded instead: a store probe takes the set
  of methods hit on this thread since the enclosing method of the store was entered.
- Concretely: methods are hit at entry; the tracker keeps a per-thread stack of entered method ids
  only while a *build* is in progress, and a build is in progress from the moment a method that
  contains a tracked store is entered until it returns. The instrumenter knows which methods
  contain tracked stores and gives them a begin/end pair (`BeginBuild(methodId)` at entry,
  `EndBuild()` before returns and in a `finally`), so other methods pay nothing.
- While a build is in progress, `Hit` appends the method id to the thread's build set (one
  thread-static check per hit outside builds: the same cost as the existing depth checks).
- `Stored(fieldId)` unites the current build set into the field's builder set. Values built on one
  thread and stored on another (a task that builds and returns) are covered by the method that
  awaits and stores, whose build set contains the awaited methods only when they ran inline; the
  rest stays covered by today's name-tracing rules, which remain.

### Map and selection

- New map sections: `fields` (id → declaring type and name), `fieldBuilders` (field → method ids),
  and per bucket the field ids read.
- Selection: a changed method selects every class that ran it (as now) and every class that read a
  field whose builders include it. A class reading a field with no builder recorded (built outside
  instrumented code) gains nothing.
- The run-once name-tracing rule stays as a fallback for builders the probes do not see (values
  built on another thread and handed over).

### Cost

Read probes are the expensive part: a hot field read becomes a call. Measure recording time on
three corpus repositories (NodaTime, Newtonsoft.Json, OpenTelemetry) with and without probes
before choosing between "every tracked read" and "the first read per method per bucket"
(the instrumenter can probe one read per field per method, since later reads in the same method
add nothing to the bucket).

## Tests

- A shared instance with `_x ??= new X()`, three classes in two processes reading it; a change to
  `X`'s constructor selects all three (red without the probes).
- A static readonly field initializer read directly by another type's code.
- A pool that creates on a miss; a `[ThreadStatic]` buffer.
- A per-test instance field built in the test's constructor adds no class.
- Recording time on the three repositories, before and after.
