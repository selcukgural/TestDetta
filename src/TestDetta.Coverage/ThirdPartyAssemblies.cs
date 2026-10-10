namespace TestDetta.Coverage;

/// <summary>
/// The third-party assemblies of a test output whose file reads a recording makes visible
/// (docs/design/non-code-files.md): every <c>.dll</c> under the output directory, its <c>runtimes/</c>
/// folders included, that is not the repository's, not TestDetta's own and not part of the test
/// platform. Only files inside the output are listed, so a package resolved from the NuGet cache is
/// never touched.
/// </summary>
internal static class ThirdPartyAssemblies
{
    /// <summary>
    /// File name prefixes of assemblies left alone: the test frameworks, platforms, runners, adapters, their
    /// telemetry and the coverage collectors read their own settings and list the test output before any test
    /// runs (bUnit: xunit.v3's testing-platform bridge listing the output, which a linked
    /// <c>xunit.runner.json</c> turned into every file under <c>tests/</c>), which would only add reads outside
    /// the tests; and TestDetta's tracker and startup hook. A test framework reads no file a test depends on.
    /// </summary>
    private static readonly string[] Skipped =
    [
        "testhost", "Microsoft.TestPlatform", "Microsoft.VisualStudio.TestPlatform.", "Microsoft.VisualStudio.CodeCoverage.",
        "Microsoft.Testing.", "Microsoft.CodeCoverage.", "Microsoft.ApplicationInsights", "xunit.", "nunit.", "NUnit",
        "MSTest.", "Microsoft.VisualStudio.TestTools.UnitTesting", "Microsoft.MSTest.", "TUnit.", "coverlet.", "TestDetta.Coverage.",
    ];

    /// <summary>The third-party assemblies under <paramref name="outputDirectory"/>, sorted by path.</summary>
    /// <param name="outputDirectory">The test project's output directory.</param>
    /// <param name="repositoryFiles">
    /// File names of every assembly a project of the repository builds: those the test project compiles
    /// against are instrumented in full, and the others (an executable copied into the output to run in
    /// another process) stay untouched, as they always did.
    /// </param>
    public static List<string> Find(string outputDirectory, IEnumerable<string> repositoryFiles)
    {
        var own = new HashSet<string>(repositoryFiles, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // Satellite assemblies hold resources and no code.
            if (own.Contains(name) || name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
                || Array.Exists(Skipped, p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(file);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
