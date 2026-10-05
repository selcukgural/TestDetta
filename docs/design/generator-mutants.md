# Mutants in source generators

Status: implemented 2026-10-05 (eval only); measured on Fluid, Mapperly next. Follows docs/design/generator-output-changes.md (B), whose
evaluation could not show a failure caused by a generator.

## Problem

A synthetic mutant is written into the assemblies in the test outputs. A source generator does its
work inside the compiler of the projects that use it (`OutputItemType="Analyzer"`), and the eval
never compiles anything after the commit's build: the mutant reaches tests that call the generator
as a library (Mapperly's snapshot tests), never the code it generates into a consumer (Mapperly's
integration tests, Fluid's, Vogen's Testbench). And TestDetta's own read of the generated code
(`GeneratorWorkspace`) builds the behaviour-keeping source edit, so it always sees the same output.
"0 missed" on the generator repositories therefore says nothing about B's main claim: that a changed
generated member selects the tests that run it.

## Design

A mutant whose member is compiled into a project that another project uses as a generator (a
*generator mutant*) is evaluated as the change would really be:

1. **Schema in the generator's own output.** The schema step also rewrites the generator project's
   built assembly, in `obj` (the intermediate assembly) and `bin` (its `TargetPath`, which the
   consumer's analyzer item points at), with the same switches as the test outputs. Both copies get
   the same bytes, so an incremental build that copies `obj` to `bin` keeps the schema, and the
   generator's own compile stays up to date (its output is newer than its sources).

2. **Tests: the consumers are compiled with the mutant on.** For a generator mutant the worker's copy
   of the clone is copied again (copy-on-write, seconds). In that copy, for every evaluated test
   project that depends on a consumer, the consumers it depends on, the projects between them and the
   test project are built one by one, dependencies first, with `--no-dependencies`,
   `TESTDETTA_MUTANT=<id>`, `-p:UseSharedCompilation=false` and `-nr:false` (the compiler runs in a
   new process with the variable; a compiler server would keep the unswitched generator loaded, since
   a rewrite keeps the assembly's MVID). The generator's assembly is touched first, so every consumer
   compiles again. Nothing else builds: the sources the selection edited and wrote back are newer than
   the build, and a full build compiled the generator again over the schema (found on Fluid: the
   in-process generator tests stopped failing).
   The mutant's runs (full run, run outside the selection, confirmation alone) use that copy; the
   control run without the mutant uses the worker's own copy, whose build is the commit's. The copy
   is deleted afterwards, so nothing has to be restored.

3. **A test project that does not build with the mutant** (a throwing generator usually leaves the
   consumer without the members it needs) cannot run a test. `dotnet test` would fail on it, and so
   would `td test`, which builds what it compares (below), so the failure is seen if TestDetta runs
   anything of that project or stops on the build. It is missed only when TestDetta leaves the whole
   project out. Reported as killed class `Project: (does not build)`.

4. **TestDetta sees the mutant's output.** `GeneratorWorkspace` builds the source edit in its copy
   as before, then writes the mutant alone into the generator's `obj` and `bin` assemblies there,
   builds the test project again (no compiler server, so the consumers compile with the mutated
   generator), reads the generated code, and puts the generator's assemblies back (touched, so the
   next read compiles the consumers again). A build that fails is what `td test` would stop on: the
   selection of that mutant is "the analysis stopped on a failing build", judged like running every
   affected test project completely, and the mutant is not a miss. Reads stored before this design
   are not reused (key version 3).

5. **Stored outcomes.** An outcome stored for a generator mutant before this design ran the tests
   without compiling the consumers; it is not reused (`MutantOutcome.ConsumersCompiled`).

## Which mutants

The member's source file belongs to a project P (the edits' paths), and some project of the graph
lists P among its `GeneratorReferences`. The consumers are those projects; the evaluated test
projects that depend on a consumer (or are one) are reached by the mutant in addition to those whose
output holds a mutated copy.

## Measured

Fluid (2026-10-05): 7 of 100 sampled changes are in the generator. 60 failing mutants, none missed,
23% of classes selected. One Throw mutant breaks the generator: the test project does not build, and
TestDetta's analysis stops on the same failing build (counted, not in the share). Fluid.Tests runs
the generator in process (source in strings), so the other mutants are caught by those tests; its own
compile does not use the generator's output.

## Cost

Per generator mutant one copy of the worker's copy and one build of the reached test projects (the
generator's consumers compile again, the rest is up to date), and in the selection one more build of
the test project in the generator copy. Fluid and Mapperly: about 100 generator mutants each, a few
seconds to half a minute per build. Stored outcomes make later rounds free again.

## Not covered

Generators shipped as packages (no project in the repository), and analyzers that only report
diagnostics (no generated code; a changed diagnostic fails a build with warnings as errors, which
`td test` would also see).
