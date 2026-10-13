# Release notes: next version (draft)

Collected as changes merge; copied into the release notes on `TestDetta/action` at release (docs/release.md, step 5),
then emptied.

## Behaviour users must know about

- **Selection depends on the target frameworks the tests run for.** For a test project that targets several
  frameworks, a run with `-f <tfm>` loads only that framework's builds: a change compiled only for other frameworks
  (code under an `#if` that framework leaves out) now selects nothing in that run, when the changed project and every
  project between it and the test projects target that framework. The same change still selects its tests in a run
  without `-f`. A CI job that runs one framework can therefore skip tests only another framework's build could fail:
  run every framework somewhere, for example nightly (site: Limitations, "Target frameworks").
- **Maps need recording again after upgrading.** Maps are now of format 3; maps of format 2, which 0.1.x recorded,
  are ignored with the warning "record the maps again", and their test projects are selected by names, as without a
  map, until the next recording on the default branch writes new ones. Pull requests that run before it select more.
  The new recorder tracks assembly loads and keeps a test's own code in its class when the test builds a fixture type
  itself, where a format 2 map could credit that code to the fixture's users and miss the test, and it notes which
  classes read a type's shape, which a format 2 map does not (site: Limitations,
  "Maps recorded by an earlier version").

## Changes

### Safer: tests that were missed are selected

- **Top-level statements.** The code of a program's top-level statements (`Program.cs` of a minimal API) is recorded
  under the name the analysis gives it. A test class whose host ran it but none of its lambdas, such as one that only
  reads the OpenAPI document, was not selected for a change named there (a response record behind `Produces<T>()`).
- **A test that builds a fixture type itself** keeps what follows in its own class, and counts as a user of the
  fixture, whose bucket also gets what follows: a fixture type that shares what its first instance builds (a server
  cached per fixture type, FastEndpoints' `AppFixture`) no longer misses its users when such a test ran first.
- **Classes that read a type's shape.** Reflection and System.Text.Json schema calls (`GetCustomAttributes`,
  `GetProperties`, `JsonSchemaExporter.GetJsonSchemaAsNode` and the like) are recorded in test and third-party code,
  with the read type's base types and the types its properties hold. A change to a type's metadata, such as an
  attribute removed from a base controller, selects the classes that read its shape: OData's attribute routing tests
  read `[ODataAttributeRouting]` through derived controllers they never name, and were missed.
- **Types that may be found through reflection:** the classes the map says ran a changed member of such a type are
  always selected.
- **A package the repository makes itself:** a `PackageReference` to the id or assembly name of one of the repository's
  projects counts as a reference to that project when other references also reach it, since NuGet then builds the
  project in place of the package. Changes to it now reach the projects that take it as a package.

- **A plugin an earlier class loaded:** calls that enumerate loaded assemblies or an assembly's types
  (`AppDomain.GetAssemblies`, `AssemblyLoadContext.Assemblies`, `Assembly.GetTypes`, `GetExportedTypes`, `DefinedTypes`,
  `ExportedTypes`) are recorded in test code and in third-party code, so a class that inspects a plugin another class
  loaded is selected when the plugin changes; before, only the class that loaded it was. A scan inside a cache fill
  counts for the class that filled it. Still not seen: a class that only looks at `Type` objects an earlier class kept in
  a static field, without loading, scanning or reading their attributes itself, and one that finds plugin types only
  through `Module.GetTypes` or `Assembly.GetType(name)` (site: Limitations, "Code that runs once per process").

### More precise: fewer tests run that a change cannot affect

- **Test fixtures:** what a fixture's `Dispose` runs counts for the classes that use the fixture (xUnit class fixtures),
  not as code outside every test.
- **Shared test data and the repository's own test extensions:** a data member several classes use (`[ClassData]`, a
  shared `[MemberData]` source) and a test framework extension the repository ships and puts on its own tests
  (AutoFixture's `[AutoData]`, FsCheck's `[Property]`) count for the classes that use them.
- **F#, plugins, catalogs and proxies:** F# modules with only compiler attributes (`AutoOpen`, `RequireQualifiedAccess`,
  `CompilationRepresentation`); a project that compiles another's files again and no test references (`Markdig.Signed`);
  Castle DynamicProxy interceptors, which run per call; reflection-found types in plugins the tests load by path;
  assemblies a framework loads by name to find modules (Carter's module catalog), which no longer select every class
  that loaded them; `File.Exists` on a build output, which is no read of it (NUnit's engine checks every library of the
  dependency context whenever the runtime cannot resolve a name).

### Recording

- Recording is much faster for tests that store into the same field from several threads millions of times: the
  tracker no longer takes a lock to note a builder it already knows. Orleans' cache soak tests went from more than 45
  minutes to 13, so `Orleans.Core.Tests` can be recorded within the time limit instead of running in full on every change.
- Each shard of a parallel recording gets its own temporary directory, so fixtures that claim or clean fixed names
  there no longer fail each other; NuGet's lock folder stays shared (`NUGET_SCRATCH`), so tests that restore packages in
  two shards at once no longer fail on its HTTP cache.
  Microsoft.Testing.Platform's sockets stay in the system's temporary directory (`TESTINGPLATFORM_PIPE_DIRECTORY`):
  on macOS a shard's own folder made their path one byte too long, so with the hang or crash dump extension every shard
  ended at start and the project was recorded again in one process, slowly or not within the limit (Orleans).
- A shard whose test report was cut off while it was written counts as one without a report (its classes are selected
  whenever their project is affected) instead of failing the whole recording.

### Evidence

- A test class selected because it read a value the changed member helped build (a field another class filled) now
  says "read a value built by …, which changed" instead of "ran …", which it never did; the selection is the same.
