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
  itself, where a format 2 map could credit that code to the fixture's users and miss the test (site: Limitations,
  "Maps recorded by an earlier version").

## Changes

- Recording: each shard of a parallel recording gets its own temporary directory, so fixtures that claim or clean fixed
  names there no longer fail each other; NuGet's lock folder stays shared (`NUGET_SCRATCH`), so tests that restore
  packages in two shards at once no longer fail on its HTTP cache.
- Selection, more precise: F# modules with only compiler attributes (`AutoOpen`, `RequireQualifiedAccess`,
  `CompilationRepresentation`); a project that compiles another's files again and no test references
  (`Markdig.Signed`); Castle DynamicProxy interceptors, which run per call; reflection-found types in plugins the tests
  load by path; assemblies a framework loads by name to find modules (Carter's module catalog), which no longer select
  every class that loaded them; `File.Exists` on a build output, which is no read of it (NUnit's engine checks every
  library of the dependency context whenever the runtime cannot resolve a name).
- Analysis: a `PackageReference` to a package the repository itself makes (the id or assembly name of one of its
  projects) counts as a reference to that project when some project's references also reach that project, since NuGet
  then uses the project in place of the package. Changes to such a project now reach the projects that take it as a
  package.
- Recording: what a test fixture's `Dispose` runs counts for the classes that use the fixture, not as code outside
  every test, so changes to code it reaches select those classes instead of being traced by name (xUnit class fixtures).
- Selection, safer: the classes the map says ran a changed member of a type that may be found through reflection are
  always selected.
- Recording: a test data member several classes use (`[ClassData]`, a shared `[MemberData]` source) and a test framework
  extension the repository ships and puts on its own tests (AutoFixture's `[AutoData]`, FsCheck's `[Property]`) count
  for the classes that use them, not as code outside every test. Maps record their users (`dataSourceUsers`); a map
  from an earlier version has none and selects as before.
- Recording: a test that builds a fixture type itself keeps what follows in its own class, instead of crediting it to
  the fixture's users; a shard whose test report was cut off counts as one without a report instead of failing the
  recording.
- Evidence: a test class selected because it read a value the changed member helped build (a field another class
  filled) now says "read a value built by …, which changed" instead of "ran …", which it never did; the selection is
  the same.
- Recording: calls that enumerate loaded assemblies or an assembly's types (`AppDomain.GetAssemblies`,
  `AssemblyLoadContext.Assemblies`, `Assembly.GetTypes`, `GetExportedTypes`, `DefinedTypes`, `ExportedTypes`) are recorded
  in test code and in third-party code, so a class that inspects a plugin an earlier class loaded is selected when the
  plugin changes; before, only the class that loaded it was. A class that reads a value such a class built (types kept
  in a static cache) counts as a user too.
- Recording, safer: the code of a program's top-level statements (`Program.cs` of a minimal API) is recorded under the
  name the analysis gives it. A test class whose host ran it but none of its lambdas, such as one that only reads the
  OpenAPI document, was not selected for a change named there (a response record behind `Produces<T>()`).
