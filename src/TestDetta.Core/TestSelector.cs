using Microsoft.Extensions.Logging;

namespace TestDetta.Core;

/// <summary>
/// Narrows the affected test projects down to test classes. Falls back to running a whole project
/// whenever a change cannot be traced by name: non-C# files, build configuration, global usings,
/// and types that nothing names (and so are probably found through reflection).
/// </summary>
/// <param name="logger">Receives the reasons for every fallback and every selected class.</param>
/// <param name="detector">Compares generated code before and after a change; null without a <see cref="TestSelectionRequest.GeneratedCode"/> reader.</param>
public sealed partial class TestSelector(ILogger<TestSelector> logger, CodeChangeDetector? detector = null)
{
    /// <summary>Decides, per affected test project, which tests to run.</summary>
    /// <param name="request">Changes, project graph and the project-level impact.</param>
    /// <returns>One entry per affected test project, ordered by path.</returns>
    public IReadOnlyList<TestProjectSelection> Select(TestSelectionRequest request)
    {
        // Files no project owns select the tests that read them (docs/design/runtime-file-reads.md).
        var readers = FindFileReaders(request);
        var affectedTests = request.Impact.Tests.ToList();
        if (affectedTests.Count == 0)
        {
            return WithFileReaders([], readers, request.CoverageMaps);
        }

        (request, var driftRunAll) = WithoutDriftedMaps(request);
        var generatorUses = new List<GeneratorUse>();
        var outputUses = new List<OutputUse>();
        var projectLevel = FindProjectLevelChanges(request.Graph, request.RepoRoot, request.ChangedFiles, request.Impact, generatorUses, outputUses);
        AddOutputUsers(request, outputUses, projectLevel, readers);
        AddLoadedOutputUsers(request, projectLevel, readers);
        var generatorOutput = CompareGeneratorOutput(request, projectLevel, generatorUses);
        var affectedProjects = new HashSet<string>(Paths.Comparer);
        foreach (var affected in request.Impact.Affected)
        {
            affectedProjects.Add(affected.Project.Path);
        }

        // What the generators write now comes first: the index keeps the first file of each name. Of a
        // map with several frameworks, the one its coverage was recorded for.
        var generated = generatorOutput.Current.Concat(request.CoverageMaps.Values.SelectMany(static m =>
            m.Generated.Where(g => g.Framework is null || string.Equals(g.Framework, m.Framework, StringComparison.OrdinalIgnoreCase))));
        var index = new SourceIndexBuilder(logger).Build(request.Graph, request.RepoRoot, affectedProjects, generated);
        // Projects that run completely whatever coverage says have no say in what it can answer.
        var runsCompletely = RunAll(request.Graph, projectLevel).Keys.Concat(driftRunAll.Keys).ToHashSet(Paths.Comparer);
        var coverage = new CoverageSelection(request, affectedTests, index, runsCompletely, logger);

        // Test projects without a coverage map are decided by name alone; those with one use a second
        // spread that stops wherever the maps know which tests run the reached code.
        var byName = Trace(request, index, projectLevel, coverage: null, bridged: generatorOutput.Changes);
        var withCoverage = coverage.HasAnyMap
            ? Trace(request, index, projectLevel, coverage, [.. Bridge(request, index, byName), .. generatorOutput.Changes])
            : byName;

        var decisions = Decide(new Decision(request.Graph, affectedTests, index, byName, withCoverage, coverage) { DriftRunAll = driftRunAll });
        decisions = WithFileReaders(decisions, readers, request.CoverageMaps);
        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i] = decisions[i] with { Estimate = coverage.Estimate(decisions[i]) };
        }

        return decisions;
    }

    /// <summary>Result of one spread: the propagation and the projects that must run completely.</summary>
    /// <param name="Propagation">Finished propagation.</param>
    /// <param name="Fallbacks">Projects whose whole downstream must run, with the reason.</param>
    private sealed record TraceResult(ImpactPropagation Propagation, Dictionary<string, string> Fallbacks);

    /// <summary>
    /// Drops the maps whose drift changed something no member describes (build files, non-C# files,
    /// global usings) in a project the test project depends on: the map's view of that project may be
    /// wrong anywhere. With <see cref="DriftFallback.RunAll"/> (the default) such a test project runs
    /// completely; with <see cref="DriftFallback.ByName"/> it is decided by name, as without a map.
    /// </summary>
    /// <param name="request">Selection input with every usable map.</param>
    /// <returns>The request without those maps, and the test projects that must run completely, with the reason.</returns>
    private (TestSelectionRequest Request, Dictionary<string, string> RunAll) WithoutDriftedMaps(TestSelectionRequest request)
    {
        var runAll = new Dictionary<string, string>(Paths.Comparer);
        if (request.Drifts.Count == 0)
        {
            return (request, runAll);
        }

        var maps = new Dictionary<string, CoverageMap>(request.CoverageMaps, Paths.Comparer);
        foreach (var drift in request.Drifts)
        {
            var fallbacks = FindProjectLevelChanges(request.Graph, request.RepoRoot, drift.ChangedFiles, drift.Impact);
            foreach (var (project, reason) in RunAll(request.Graph, fallbacks))
            {
                if (maps.TryGetValue(project, out var map) && string.Equals(map.Commit, drift.FromCommit, StringComparison.OrdinalIgnoreCase))
                {
                    maps.Remove(project);
                    LogDriftedMap(project, drift.FromCommit, reason);
                    if (request.DriftFallback == DriftFallback.RunAll)
                    {
                        runAll.TryAdd(project, $"since the coverage map was recorded ({drift.FromCommit[..Math.Min(12, drift.FromCommit.Length)]}), {reason}");
                    }
                }
            }
        }

        return (request with { CoverageMaps = maps }, runAll);
    }

    /// <summary>
    /// The drift bridge: every member the drift added or changed that can reach one of the changes by
    /// name. A test class that reaches a change only through code changed since the map was recorded
    /// ran one of these members at the map's commit (or is new and found by name), so selecting the
    /// classes the map credits to them, or tracing them by name where it cannot answer, covers what the
    /// map does not know (docs/design/map-drift-bridge.md).
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="byName">The uncut name spread of the changes.</param>
    private List<(SymbolChange Change, ProjectNode Owner)> Bridge(TestSelectionRequest request, SourceIndex index, TraceResult byName)
    {
        var bridged = new List<(SymbolChange, ProjectNode)>();
        var root = Paths.Normalize(request.RepoRoot);
        foreach (var drift in request.Drifts)
        {
            foreach (var change in drift.Impact.CodeChanges)
            {
                foreach (var owner in request.Graph.ResolveFile(Paths.Normalize(Path.Combine(root, change.Path))))
                {
                    if (owner.Role != FileRole.Source)
                    {
                        continue;
                    }

                    foreach (var symbol in change.Symbols)
                    {
                        // Code removed before the base is on no path at the base.
                        if (symbol.Kind != SymbolChangeKind.Removed && ReachesChange(index, byName.Propagation, symbol))
                        {
                            LogBridged(symbol.TypeName, symbol.MemberName ?? "(type)", drift.FromCommit);
                            bridged.Add((symbol, owner.Project));
                        }
                    }
                }
            }
        }

        return bridged;
    }

    /// <summary>True when the name spread of the changes reached <paramref name="symbol"/>, or for a type, any of its members.</summary>
    /// <param name="index">Indexed source code.</param>
    /// <param name="propagation">Finished name spread.</param>
    /// <param name="symbol">A member or type changed by the drift.</param>
    private static bool ReachesChange(SourceIndex index, ImpactPropagation propagation, SymbolChange symbol) =>
        symbol.MemberName is null
            ? propagation.AffectedTypes.Contains(symbol.TypeName)
            : index.FindMembers(symbol.TypeName, symbol.MemberName).Any(propagation.Reached);

    /// <summary>
    /// Seeds the detected changes and spreads them by name. With <paramref name="coverage"/>, a change or
    /// reached member that the maps can answer is taken from coverage and not spread further.
    /// </summary>
    /// <param name="request">Selection input.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="projectLevel">Project-level fallbacks from non-C# and build changes.</param>
    /// <param name="coverage">Coverage maps to stop at, or null for a pure name spread.</param>
    /// <param name="bridged">
    /// Members to resolve like changes besides the source changes, each with the project it is compiled
    /// into: drift members (<see cref="Bridge"/>) and generated members that changed (<see cref="CompareGeneratorOutput"/>).
    /// </param>
    private TraceResult Trace(
        TestSelectionRequest request,
        SourceIndex index,
        Dictionary<string, string> projectLevel,
        CoverageSelection? coverage,
        List<(SymbolChange Change, ProjectNode Owner)> bridged)
    {
        LogTraceStart(coverage is null ? "by name, for test projects without a coverage map" : "stopping where coverage maps know the tests");
        var fallbacks = new Dictionary<string, string>(projectLevel, Paths.Comparer);
        var propagation = new ImpactPropagation(
            index,
            logger,
            coverage is null
                ? null
                : member => Resolve(member) || (member.IsAbstract && ResolveThroughImplementations(member)),
            request.Graph);
        var root = Paths.Normalize(request.RepoRoot);

        // Coverage answers for a member the spread reached.
        bool Resolve(IndexedMember member) => coverage!.TryResolve(
            new SymbolChange(member.TypeName, member.MemberName, SymbolChangeKind.Modified),
            request.Graph[member.ProjectPath],
            reached: true);

        // An abstract member has no code a map could record: a call through it runs an implementation.
        // When the repository declares implementations and coverage answers for every one of them, the
        // classes that ran them are the answer; tracing the member's simple name instead reaches every
        // call of an unrelated member of that name (AfterApply's IApplicationService.CreateAsync). An
        // implementation outside the repository (a mock) runs none of the changed code.
        bool ResolveThroughImplementations(IndexedMember member)
        {
            var implementations = index.Implementations(member);
            if (implementations.Count == 0)
            {
                return false;
            }

            foreach (var implementation in implementations)
            {
                if (!Resolve(implementation))
                {
                    return false;
                }
            }

            LogAnsweredThroughImplementations(member.TypeName, member.MemberName!, implementations.Count);
            return true;
        }

        foreach (var change in request.Impact.CodeChanges)
        {
            // A linked file is compiled into every project that includes it; each gets the change.
            foreach (var owner in request.Graph.ResolveFile(Paths.Normalize(Path.Combine(root, change.Path))))
            {
                if (owner.Role != FileRole.Source || fallbacks.ContainsKey(owner.Project.Path))
                {
                    continue;
                }

                foreach (var symbol in change.Symbols)
                {
                    if (coverage?.TryResolve(symbol, owner.Project) == true)
                    {
                        LogResolvedByCoverage(symbol.TypeName, symbol.MemberName ?? "(type)");
                        continue;
                    }

                    propagation.Seed(symbol, owner.Project.Path);
                }
            }
        }

        foreach (var (symbol, owner) in bridged)
        {
            if (fallbacks.ContainsKey(owner.Path) || coverage?.TryResolve(symbol, owner) == true)
            {
                continue;
            }

            propagation.Seed(symbol, owner.Path);
        }

        propagation.Run();
        AddUnreferencedTypes(request.Graph, index, propagation, fallbacks, coverage);
        return new TraceResult(propagation, fallbacks);
    }

    /// <summary>
    /// Projects whose change cannot be described by symbols, with the reason: build configuration,
    /// non-C# files, C# files without a detected change, and global usings or assembly attributes.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="repoRoot">Repository root directory.</param>
    /// <param name="changedFiles">Changed paths relative to <paramref name="repoRoot"/>.</param>
    /// <param name="impact">Project-level impact of the same changes.</param>
    /// <param name="generatorUses">
    /// Receives the projects that use an affected project as a source generator, instead of a fallback,
    /// for <see cref="CompareGeneratorOutput"/> to decide; null to fall back for them too (map drift,
    /// where nothing builds the map's commit).
    /// </param>
    /// <param name="outputUses">
    /// Receives the projects that reference an affected project without compiling against it and whose
    /// build does not use its output (<see cref="BuildOutputUse"/>), instead of a fallback, for
    /// <see cref="AddOutputUsers"/> to decide; null to fall back for them too.
    /// </param>
    private Dictionary<string, string> FindProjectLevelChanges(
        ProjectGraph graph,
        string repoRoot,
        IReadOnlyList<string> changedFiles,
        ImpactResult impact,
        List<GeneratorUse>? generatorUses = null,
        List<OutputUse>? outputUses = null)
    {
        var fallbacks = new Dictionary<string, string>(Paths.Comparer);
        var codeChanges = new Dictionary<string, CodeChange>(StringComparer.Ordinal);
        foreach (var change in impact.CodeChanges)
        {
            codeChanges[change.Path] = change;
        }

        var cosmetic = new HashSet<string>(impact.CosmeticFiles, StringComparer.Ordinal);
        var root = Paths.Normalize(repoRoot);

        foreach (var file in changedFiles)
        {
            if (cosmetic.Contains(file))
            {
                continue;
            }

            foreach (var owner in graph.ResolveFile(Paths.Normalize(Path.Combine(root, file))))
            {
                var project = owner.Project.Path;
                if (owner.Role == FileRole.BuildInput)
                {
                    AddFallback(fallbacks, project, $"{file} is part of the project's build");
                    continue;
                }

                if (!SourceLanguage.IsSource(file))
                {
                    AddFallback(fallbacks, project, $"{file} is not C#, Visual Basic or F# code");
                    continue;
                }

                if (!codeChanges.TryGetValue(file, out var change))
                {
                    AddFallback(fallbacks, project, $"{file} could not be compared");
                    continue;
                }

                if (change.AffectsWholeProject)
                {
                    AddFallback(fallbacks, project, $"{file} changes a global using or an assembly attribute");
                }
            }
        }

        // After the file loop, so the file that caused a fallback is what its reason names.
        foreach (var affected in impact.Affected)
        {
            if (affected.Reason == ImpactReason.BuildConfiguration)
            {
                AddFallback(fallbacks, affected.Project.Path, "build configuration changed");
            }

            // An analyzer or source generator runs inside the consumer's compiler, and an output the
            // consumer does not compile against is run in another process if at all: neither name
            // tracing nor coverage sees what the change does there, so the consumer changed as a whole.
            // What a generator does shows in the code it generates, which a build can compare; an
            // output the build does not use reaches only the tests that start or load it.
            foreach (var consumer in graph.OpaqueConsumersOf(affected.Project.Path))
            {
                var generator = consumer.GeneratorReferences.Contains(affected.Project.Path);
                if (generatorUses is not null && generator)
                {
                    generatorUses.Add(new GeneratorUse(consumer, affected.Project));
                    continue;
                }

                var buildUse = generator ? null : BuildOutputUse.Find(repoRoot, consumer, affected.Project);
                if (outputUses is not null && !generator)
                {
                    outputUses.Add(new OutputUse(consumer, affected.Project, buildUse));
                    continue;
                }

                AddFallback(
                    fallbacks,
                    consumer.Path,
                    buildUse is null
                        ? $"it uses {Path.GetFileName(affected.Project.Path)}, which is affected, as an analyzer or source generator or without compiling against it"
                        : $"it uses {Path.GetFileName(affected.Project.Path)}, which is affected, in its build: {buildUse}");
            }
        }

        return fallbacks;
    }

    /// <summary>
    /// Adds a fallback for every spread type outside test projects that no other type names and
    /// that a framework could discover (it has a base list or attributes): controllers, message
    /// handlers, hosted services. Name tracing cannot see who runs those. A plain type that nothing
    /// names is dead code and cannot affect a test. A type that no longer exists is unknown, so it
    /// also falls back.
    /// </summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="index">Indexed source code.</param>
    /// <param name="propagation">Finished propagation.</param>
    /// <param name="fallbacks">Project-level fallbacks; updated in place.</param>
    /// <param name="coverage">
    /// Coverage maps, for a type reached through its code only (<see cref="CoverageSelection.TryResolveType"/>); null for a pure name spread.
    /// </param>
    private void AddUnreferencedTypes(
        ProjectGraph graph,
        SourceIndex index,
        ImpactPropagation propagation,
        Dictionary<string, string> fallbacks,
        CoverageSelection? coverage)
    {
        foreach (var (typeName, projects) in propagation.SeededTypes)
        {
            // Every project compiling the type gets the same answer: a fallback for one of them alone
            // left the tests under the others to a name trace that cannot see reflection (Meziantou.Analyzer).
            var candidates = projects.Where(p => !graph[p].IsTestProject && !fallbacks.ContainsKey(p)).ToList();
            if (candidates.Count == 0 || index.IsReferencedOutside(DeclarationWalker.SimpleTypeName(typeName), typeName))
            {
                continue;
            }

            var type = index.FindType(typeName);
            if (type is { MayBeFoundByReflection: false })
            {
                LogDeadType(typeName);
                continue;
            }

            // Reflection finds an attribute through its uses, which name it: one nothing names has no instance.
            if (type is not null && index.IsAttributeClass(typeName))
            {
                LogUnusedAttribute(typeName);
                continue;
            }

            if (type is not null && propagation.SeededThroughCodeOnly(typeName) && coverage?.TryResolveType(typeName, candidates) == true)
            {
                LogUnreferencedTypeResolvedByCoverage(typeName);
                continue;
            }

            // The chain says which reached member brought the type in, the first thing to look at when
            // a whole project runs for a change that seems unrelated.
            var reason = type is null
                ? $"{typeName} was removed and nothing names it, so it may have been created through reflection"
                : $"{typeName} is affected and nothing names it, so it may be created through reflection ({propagation.ExplainSeeded(typeName, limit: 4)})";
            foreach (var projectPath in candidates)
            {
                AddFallback(fallbacks, projectPath, reason);
            }
        }
    }

    /// <summary>Everything <see cref="Decide"/> combines.</summary>
    /// <param name="Graph">Projects of the repository.</param>
    /// <param name="AffectedTests">Test projects affected at project level.</param>
    /// <param name="Index">Indexed source code.</param>
    /// <param name="ByName">Spread used for test projects without a coverage map.</param>
    /// <param name="WithCoverage">Spread used for test projects with a coverage map.</param>
    /// <param name="Coverage">Classes selected from coverage maps.</param>
    private sealed record Decision(
        ProjectGraph Graph,
        List<AffectedProject> AffectedTests,
        SourceIndex Index,
        TraceResult ByName,
        TraceResult WithCoverage,
        CoverageSelection Coverage)
    {
        /// <summary>Test projects that run completely because their map's drift cannot be bridged, with the reason.</summary>
        public Dictionary<string, string> DriftRunAll { get; init; } = [];
    }

    /// <summary>Turns fallbacks, name propagation and coverage into one decision per test project.</summary>
    /// <param name="decision">Inputs of the decision.</param>
    private List<TestProjectSelection> Decide(Decision decision)
    {
        var (graph, affectedTests, index, byName, withCoverage, coverage) = decision;
        var runAllByName = RunAll(graph, byName.Fallbacks);
        var runAllWithCoverage = RunAll(graph, withCoverage.Fallbacks);
        var classesByProject = new Dictionary<string, List<string>>(Paths.Comparer);
        var candidatesByProject = new Dictionary<string, int>(Paths.Comparer);
        var decisionsByProject = new Dictionary<string, List<ClassDecision>>(Paths.Comparer);
        foreach (var type in index.Types)
        {
            // A class the runner reported while the map was recorded holds tests, whatever attributes
            // the index recognised (MSpec contexts have none).
            if (!type.InTestProject || !(type.CanContainTests || coverage.Reported(type)))
            {
                continue;
            }

            candidatesByProject[type.ProjectPath] = candidatesByProject.GetValueOrDefault(type.ProjectPath) + 1;
            var trace = coverage.HasMap(type.ProjectPath) ? withCoverage : byName;
            var classDecision = DecideClass(type, trace, coverage);
            if (!decisionsByProject.TryGetValue(type.ProjectPath, out var decisions))
            {
                decisions = [];
                decisionsByProject[type.ProjectPath] = decisions;
            }

            decisions.Add(classDecision);
            if (!classDecision.Selected)
            {
                continue;
            }

            if (!classesByProject.TryGetValue(type.ProjectPath, out var classes))
            {
                classes = [];
                classesByProject[type.ProjectPath] = classes;
            }

            classes.Add(type.RuntimeTypeName);
        }

        AddRecordedClassesTheIndexMisses(affectedTests, coverage, decisionsByProject, candidatesByProject, classesByProject);

        var result = new List<TestProjectSelection>(affectedTests.Count);
        foreach (var test in affectedTests)
        {
            var project = test.Project;
            var classDecisions = decisionsByProject.GetValueOrDefault(project.Path) ?? [];
            classDecisions.Sort(static (a, b) => string.CompareOrdinal(a.TestClass, b.TestClass));
            var runAll = coverage.HasMap(project.Path) ? runAllWithCoverage : runAllByName;
            if (decision.DriftRunAll.TryGetValue(project.Path, out var reason) || runAll.TryGetValue(project.Path, out reason))
            {
                var whole = classDecisions.ConvertAll(d => d with { Selected = true, Kind = ClassDecisionKind.WholeProject, Evidence = reason });
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], reason) { Decisions = whole }));
                continue;
            }

            if (!classesByProject.TryGetValue(project.Path, out var classes))
            {
                var skipReason = coverage.HasMap(project.Path)
                    ? "no test class runs or references the changed code"
                    : "no test class references the changed code";
                result.Add(Log(new TestProjectSelection(project, TestRunMode.Skipped, [], skipReason) { Decisions = classDecisions }));
                continue;
            }

            if (classes.Count == candidatesByProject[project.Path])
            {
                result.Add(Log(new TestProjectSelection(project, TestRunMode.All, [], "every test class references the changed code") { Decisions = classDecisions }));
                continue;
            }

            classes.Sort(StringComparer.Ordinal);
            var source = coverage.HasMap(project.Path) ? "run or reference" : "reference";
            result.Add(Log(new TestProjectSelection(
                project,
                TestRunMode.Filtered,
                classes,
                $"{classes.Count} of {candidatesByProject[project.Path]} test class(es) {source} the changed code") { Decisions = classDecisions }));
        }

        return result;
    }

    /// <summary>
    /// Makes every class the runner reported while a map was recorded a candidate, also when the source
    /// index does not see it: code under a mix of preprocessor symbols that neither parsed variant enters
    /// (Newtonsoft.Json's DateOnlyTests in <c>#if NET6_0_OR_GREATER &amp;&amp; !NETSTANDARD2_0</c>) still ran,
    /// and the map knows what it ran. Such a class is decided by what the map says alone: its coverage,
    /// a failure while recording, or tests skipped on another platform.
    /// </summary>
    /// <param name="affectedTests">Test projects affected at project level.</param>
    /// <param name="coverage">Classes selected from coverage maps.</param>
    /// <param name="decisionsByProject">Decisions so far; updated in place.</param>
    /// <param name="candidatesByProject">Candidate counts; updated in place.</param>
    /// <param name="classesByProject">Selected classes; updated in place.</param>
    private static void AddRecordedClassesTheIndexMisses(
        List<AffectedProject> affectedTests,
        CoverageSelection coverage,
        Dictionary<string, List<ClassDecision>> decisionsByProject,
        Dictionary<string, int> candidatesByProject,
        Dictionary<string, List<string>> classesByProject)
    {
        foreach (var test in affectedTests)
        {
            var project = test.Project.Path;
            var known = new HashSet<string>(
                decisionsByProject.GetValueOrDefault(project)?.Select(static d => d.TestClass) ?? [], StringComparer.Ordinal);
            foreach (var testClass in coverage.ReportedClasses(project).Where(c => !known.Contains(c)).Order(StringComparer.Ordinal).ToList())
            {
                // The build has it under another name, and that class was decided as this one.
                if (coverage.RecordedUnderAnotherName(project, testClass))
                {
                    continue;
                }

                var decision = coverage.Evidence(project, testClass) is { } ran
                    ? new ClassDecision(testClass, Selected: true, ClassDecisionKind.Coverage, ran)
                    : coverage.FailedWhileRecording(project, testClass)
                        ? new ClassDecision(testClass, Selected: true, ClassDecisionKind.FailedWhileRecording,
                            $"a test of it failed while the map was recorded (map {CoverageSelection.Short(coverage.MapCommit(project)!)})")
                        : coverage.SkippedWhileRecording(project, testClass) && coverage.RecordedElsewhere(project)
                            ? new ClassDecision(testClass, Selected: true, ClassDecisionKind.SkippedWhileRecording,
                                $"every test of it was skipped while the map was recorded on {coverage.MapPlatform(project)}, so the map cannot say what it runs on {coverage.Platform} (map {CoverageSelection.Short(coverage.MapCommit(project)!)})")
                            : new ClassDecision(testClass, Selected: false, ClassDecisionKind.Skipped,
                                $"ran none of the changed code (map {CoverageSelection.Short(coverage.MapCommit(project)!)}); the name trace cannot see its source");
                candidatesByProject[project] = candidatesByProject.GetValueOrDefault(project) + 1;
                if (!decisionsByProject.TryGetValue(project, out var decisions))
                {
                    decisions = [];
                    decisionsByProject[project] = decisions;
                }

                decisions.Add(decision);
                if (!decision.Selected)
                {
                    continue;
                }

                if (!classesByProject.TryGetValue(project, out var classes))
                {
                    classes = [];
                    classesByProject[project] = classes;
                }

                classes.Add(testClass);
            }
        }
    }

    /// <summary>
    /// What is decided for one candidate test class and why: coverage first (the most direct
    /// evidence), then the name spread, then what the map knows about the class not running (a failure,
    /// skipped tests, a disagreement with the build); otherwise skipped, saying whether a map was there
    /// to ask. A class the map recorded under another name is looked up by that name
    /// (docs/design/unrecorded-classes.md).
    /// </summary>
    /// <param name="type">Candidate test class.</param>
    /// <param name="trace">The spread that decides its project.</param>
    /// <param name="coverage">Classes selected from coverage maps.</param>
    private static ClassDecision DecideClass(IndexedType type, TraceResult trace, CoverageSelection coverage)
    {
        var name = type.RuntimeTypeName;
        var project = type.ProjectPath;
        var recorded = coverage.RecordedName(type);
        var recordedAs = recorded == name ? "" : $"; recorded as {recorded}";
        if (coverage.Evidence(project, recorded) is { } ran)
        {
            return new ClassDecision(name, Selected: true, ClassDecisionKind.Coverage, ran + recordedAs);
        }

        if (trace.Propagation.AffectedTypes.Contains(type.TypeName))
        {
            return new ClassDecision(name, Selected: true, ClassDecisionKind.Name, trace.Propagation.Explain(type.TypeName) ?? "names the changed code");
        }

        if (coverage.MapCommit(project) is not { } commit)
        {
            return new ClassDecision(name, Selected: false, ClassDecisionKind.Skipped, "names none of the changed code (no coverage map)");
        }

        var map = $"map {CoverageSelection.Short(commit)}";
        if (coverage.FailedWhileRecording(project, recorded))
        {
            return new ClassDecision(name, Selected: true, ClassDecisionKind.FailedWhileRecording,
                $"a test of it failed while the map was recorded ({map}{recordedAs})");
        }

        if (coverage.SkippedWhileRecording(project, recorded))
        {
            // On the platform the map came from the tests skip again; elsewhere they may run.
            return coverage.RecordedElsewhere(project)
                ? new ClassDecision(name, Selected: true, ClassDecisionKind.SkippedWhileRecording,
                    $"every test of it was skipped while the map was recorded on {coverage.MapPlatform(project)}, so the map cannot say what it runs on {coverage.Platform} ({map}{recordedAs})")
                : new ClassDecision(name, Selected: false, ClassDecisionKind.Skipped,
                    $"every test of it was skipped while the map was recorded on this platform ({coverage.Platform}), so they skip here too ({map}{recordedAs})");
        }

        if (coverage.Mismatch(type) is { } orphans)
        {
            return new ClassDecision(name, Selected: true, ClassDecisionKind.MapMismatch,
                $"the map does not know it and recorded {string.Join(", ", orphans)} in its file, which the build no longer has; record the map again ({map})");
        }

        return new ClassDecision(name, Selected: false, ClassDecisionKind.Skipped, $"ran none of the changed code and names none of it ({map}{recordedAs})");
    }

    /// <summary>Every project downstream of a fallback, with the first reason that reaches it.</summary>
    /// <param name="graph">Projects of the repository.</param>
    /// <param name="fallbacks">Projects that cannot be narrowed, with reasons.</param>
    private static Dictionary<string, string> RunAll(ProjectGraph graph, Dictionary<string, string> fallbacks)
    {
        var runAll = new Dictionary<string, string>(Paths.Comparer);
        foreach (var (project, reason) in fallbacks)
        {
            var projectName = Path.GetFileName(project);
            foreach (var dependent in graph.WithDependents([project]))
            {
                runAll.TryAdd(dependent, dependent == project ? reason : $"depends on {projectName}: {reason}");
            }
        }

        return runAll;
    }

    /// <summary>Records the first reason a project needs to run completely.</summary>
    /// <param name="fallbacks">Fallbacks so far.</param>
    /// <param name="project">Normalized .csproj path.</param>
    /// <param name="reason">Why the project cannot be narrowed.</param>
    private void AddFallback(Dictionary<string, string> fallbacks, string project, string reason)
    {
        if (fallbacks.TryAdd(project, reason))
        {
            LogFallback(project, reason);
        }
    }

    /// <summary>Logs a decision and passes it through.</summary>
    /// <param name="selection">The decision.</param>
    private TestProjectSelection Log(TestProjectSelection selection)
    {
        LogDecision(selection.Project.Path, selection.Mode, selection.Reason);
        return selection;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tracing the changes {Mode}")]
    private partial void LogTraceStart(string mode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} is affected, but it is an attribute class nothing uses: it has no instance")]
    private partial void LogUnusedAttribute(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} is affected and nothing names it, but only through its code: the coverage maps say which classes ran it")]
    private partial void LogUnreferencedTypeResolvedByCoverage(string typeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Not using the coverage map of {Project} from {MapCommit}: since then, {Reason}")]
    private partial void LogDriftedMap(string project, string mapCommit, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName} changed since {MapCommit} and can reach the change; bridged")]
    private partial void LogBridged(string typeName, string memberName, string mapCommit);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "{TypeName} is named by nothing and has no base list or attributes; treated as unused")]
    private partial void LogDeadType(string typeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: no code of its own; answered through the coverage of its {Count} implementation(s)")]
    private partial void LogAnsweredThroughImplementations(string typeName, string memberName, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{TypeName} {MemberName}: test classes taken from coverage")]
    private partial void LogResolvedByCoverage(string typeName, string memberName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project} cannot be narrowed below project level: {Reason}")]
    private partial void LogFallback(string project, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Project}: {Mode} ({Reason})")]
    private partial void LogDecision(string project, TestRunMode mode, string reason);
}
