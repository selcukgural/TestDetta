namespace TestDetta.Core;

/// <summary>Language of a project, from its file extension.</summary>
public enum ProjectLanguage
{
    /// <summary><c>.csproj</c>: analysed down to members.</summary>
    CSharp,

    /// <summary><c>.fsproj</c>: analysed down to members through the F# compiler service.</summary>
    FSharp,

    /// <summary><c>.vbproj</c>: analysed down to members, names compared ignoring case.</summary>
    VisualBasic,
}

/// <summary>One project file and the projects it references.</summary>
/// <param name="Path">Normalized absolute path of the project file.</param>
/// <param name="Directory">Normalized absolute path of the directory containing the project file.</param>
/// <param name="IsTestProject">
/// True when the project sets <c>IsTestProject</c>, references <c>Microsoft.NET.Test.Sdk</c>,
/// or its file name ends with <c>Test</c> or <c>Tests</c>.
/// </param>
/// <param name="References">Normalized absolute paths of the projects named in <c>ProjectReference</c> items.</param>
public sealed record ProjectNode(string Path, string Directory, bool IsTestProject, IReadOnlyList<string> References)
{
    /// <summary>
    /// Name of the compiled assembly without extension: the <c>AssemblyName</c> property when the
    /// project or one of its imports sets it to a value known without evaluation, otherwise the
    /// project file name.
    /// </summary>
    public string AssemblyName { get; init; } = System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// True when the project or one of its imports sets <c>AssemblyName</c> to something only MSBuild
    /// can evaluate, such as <c>$(MSBuildProjectName.Substring(0, 12))</c>. <see cref="AssemblyName"/>
    /// is then the file name, which may be wrong; ask MSBuild before relying on it.
    /// </summary>
    public bool AssemblyNameUnresolved { get; init; }

    /// <summary>Language of the project.</summary>
    public ProjectLanguage Language { get; init; } = ProjectLanguage.CSharp;

    /// <summary>
    /// The <c>RootNamespace</c> property: Visual Basic puts it before every namespace a source file
    /// declares (a class <c>Calc</c> in a project with root namespace <c>Shop</c> compiles to <c>Shop.Calc</c>);
    /// C# ignores it. MSBuild's value, else a literal one in the project file, else the project file name,
    /// which is the SDK's default.
    /// </summary>
    public string RootNamespace { get; init; } = System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// False for a legacy (non-SDK) project file: no <c>Sdk</c> on <c>&lt;Project&gt;</c>, in an
    /// <c>&lt;Sdk&gt;</c> element or on an <c>&lt;Import&gt;</c>, usually with <c>packages.config</c>.
    /// Such a project restores and builds with the full MSBuild (<c>MSBuild.exe</c>, Mono's
    /// <c>msbuild</c>), not <c>dotnet build</c>, and its tests run from its compiled assembly
    /// (docs/design/legacy-projects.md).
    /// </summary>
    public bool SdkStyle { get; init; } = true;

    /// <summary>
    /// Files outside <see cref="Directory"/> that the project compiles or ships, such as
    /// <c>&lt;Compile Include="../Shared/*.cs" /&gt;</c> or the items of a shared <c>.projitems</c>:
    /// normalized absolute paths, possibly with wildcards.
    /// </summary>
    public IReadOnlyList<string> ExternalItems { get; init; } = [];

    /// <summary>
    /// <see cref="ExternalItems"/> as the XML reading found them, kept on a project MSBuild evaluated,
    /// for files that no longer exist only: MSBuild expands an item glob on the files present, so a
    /// deleted file the glob named (shared test data under the repository root) is in no evaluated item.
    /// </summary>
    public IReadOnlyList<string> ItemsOfDeletedFiles { get; init; } = [];

    /// <summary>
    /// MSBuild files the project imports beyond the implicit <c>Directory.Build.*</c> files (a shared
    /// <c>build/common.props</c>, a <c>.projitems</c>), found through its own imports and theirs.
    /// A change to one of them changes how the project builds.
    /// </summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>
    /// True when an import or an item outside the project directory is built from properties that
    /// cannot be known without evaluating MSBuild. Changed build or source files that no project
    /// claims are then assumed to affect this project.
    /// </summary>
    public bool HasUnresolvedInputs { get; init; }

    /// <summary>
    /// Patterns (anchored regular expressions over normalized project paths) for the
    /// <c>ProjectReference</c> items that could not be resolved to one project file: the parts only
    /// MSBuild can compute (a property function, an unknown property or item) match anything. The
    /// graph makes the project depend on every project a pattern matches; empty when every reference
    /// resolved.
    /// </summary>
    public IReadOnlyList<string> UnknownReferencePatterns { get; init; } = [];

    /// <summary>
    /// Patterns (as <see cref="UnknownReferencePatterns"/>) for compile and content items outside the
    /// project whose path could not be resolved, such as OpenTelemetry's
    /// <c>$(RepoRoot)\test\OpenTelemetry.Tests\Metrics\MetricApiTests.cs</c>: every file a pattern
    /// matches belongs to the project too.
    /// </summary>
    public IReadOnlyList<string> UnknownItemPatterns { get; init; } = [];

    /// <summary>
    /// The part of <see cref="References"/> the project does not compile against: references with an
    /// <c>OutputItemType</c> (<c>Analyzer</c>: an analyzer or source generator that runs inside the
    /// compiler) or with <c>ReferenceOutputAssembly="false"</c> (build order, or an output the project
    /// copies and runs). What such a project does reaches this one through its build or through
    /// another process, where neither name tracing nor coverage can follow it.
    /// </summary>
    public IReadOnlySet<string> OpaqueReferences { get; init; } = new HashSet<string>(Paths.Comparer);

    /// <summary>
    /// The part of <see cref="OpaqueReferences"/> used as an analyzer or source generator
    /// (<c>OutputItemType="Analyzer"</c>): it runs inside this project's compiler, and what it does
    /// reaches this project through the code it generates, which a build can show
    /// (docs/design/generator-output-changes.md). Known from the MSBuild evaluation only; a project
    /// read from its XML alone has none, and its opaque references stay opaque.
    /// </summary>
    public IReadOnlySet<string> GeneratorReferences { get; init; } = new HashSet<string>(Paths.Comparer);

    /// <summary>The part of <see cref="UnknownReferencePatterns"/> that comes from references as described in <see cref="OpaqueReferences"/>.</summary>
    public IReadOnlyList<string> UnknownOpaqueReferencePatterns { get; init; } = [];

    /// <summary>True when some <c>ProjectReference</c> could not be resolved; see <see cref="UnknownReferencePatterns"/>.</summary>
    public bool HasUnknownReferences => UnknownReferencePatterns.Count > 0;

    /// <summary>
    /// Packages the project references with <c>PackageReference</c>, in its own file, the files it
    /// imports or the <c>Directory.Build.*</c> files above it. Used to tell which projects a central
    /// package version change reaches.
    /// </summary>
    public IReadOnlySet<string> PackageReferences { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalized full paths of the files in the project's build items as MSBuild evaluated them
    /// (see <see cref="IProjectEvaluator"/>), over all its target frameworks; null when the project
    /// was not evaluated and its files are known only by directory and the XML reading.
    /// </summary>
    public IReadOnlySet<string>? EvaluatedItems { get; init; }

    /// <summary>
    /// The part of <see cref="EvaluatedItems"/> the build neither compiles, embeds, copies nor hands to a
    /// tool, under any of its target frameworks: <c>None</c> items without <c>CopyToOutputDirectory</c>
    /// that no item type known to build from files also lists (docs/design/non-code-files.md). A README
    /// or a changelog in the project folder is one. Null when the project was not evaluated, or by an
    /// evaluator that did not report it.
    /// </summary>
    public IReadOnlySet<string>? InertItems { get; init; }

    /// <summary>
    /// How the team's own runs of this project's tests are configured in its project files: a test case filter,
    /// a run settings file, testing platform arguments (docs/design/team-test-configuration.md). TestDetta keeps
    /// them in every run it starts. Null when the project sets none, or was not evaluated.
    /// </summary>
    public TestConfiguration? TestConfiguration { get; init; }

    /// <summary>
    /// The preprocessor symbols each of its target frameworks compiles with, from MSBuild evaluation;
    /// empty when unknown. Code under a mix of symbols (<c>#if A &amp;&amp; !B</c>) is only seen by
    /// parsing with these.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> SymbolSets { get; init; } = [];

    /// <summary>
    /// The preprocessor symbols by target framework, from MSBuild evaluation (<see cref="EvaluatedProject.FrameworkSymbols"/>);
    /// empty when unknown. Its keys are the frameworks the project targets.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FrameworkSymbols { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}
