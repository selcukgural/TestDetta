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
- **Maps need recording again after upgrading.** Recording now tracks assembly loads. A map recorded by an earlier
  version runs a test project completely for a changed project it references with `ReferenceOutputAssembly="false"`,
  until the map is recorded again (site: What runs everything).

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
- Selection, safer: the classes the map says ran a changed member of a type that may be found through reflection are
  always selected.
