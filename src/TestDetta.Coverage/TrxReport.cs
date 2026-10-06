using System.Xml.Linq;

namespace TestDetta.Coverage;

/// <summary>What a TRX report says about one test run.</summary>
/// <param name="Total">Number of tests that ran.</param>
/// <param name="FailedClasses">Test classes (runtime names) with at least one failed test.</param>
/// <param name="ExecutedClasses">Test classes (runtime names) with at least one test that ran, passed or failed.</param>
internal sealed record TrxOutcome(int Total, IReadOnlySet<string> FailedClasses, IReadOnlySet<string> ExecutedClasses)
{
    /// <summary>
    /// Test classes (runtime names) with any test in the report, executed or not. Those not in
    /// <see cref="ExecutedClasses"/> had every test skipped.
    /// </summary>
    public IReadOnlySet<string> ReportedClasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Seconds per test class (runtime names): the sum of its tests' reported durations, without the class's
    /// setup outside its tests, which reports do not time per class.
    /// </summary>
    public IReadOnlyDictionary<string, double> ClassSeconds { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>
/// Reads the parts of a test report TestDetta needs: how many tests ran, which classes failed and which
/// had every test skipped. Visual Studio TRX reports, and NUnit's own result XML for NUnit runs that
/// cannot write TRX.
/// </summary>
internal static class TrxReport
{
    /// <summary>Reads every <c>.trx</c> file and NUnit result file in <paramref name="directory"/> and combines them.</summary>
    /// <param name="directory">Results directory of one or more runs.</param>
    /// <returns>The combined outcome, or null when the directory holds no report.</returns>
    public static TrxOutcome? Read(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        var nunitFiles = Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories);
        if (files.Length == 0 && nunitFiles.Length == 0)
        {
            return null;
        }

        var total = 0;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var reports = files.Length;
        foreach (var file in nunitFiles)
        {
            var document = XDocument.Load(file);
            if (document.Root?.Name.LocalName != "test-run")
            {
                continue;
            }

            reports++;
            foreach (var testCase in document.Descendants("test-case"))
            {
                total++;
                if (testCase.Attribute("classname")?.Value is not { } testClass)
                {
                    continue;
                }

                // Skipped and inconclusive tests never enter their class, as with TRX.
                reported.Add(testClass);
                if (double.TryParse(testCase.Attribute("duration")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                {
                    seconds[testClass] = seconds.GetValueOrDefault(testClass) + duration;
                }
                var result = testCase.Attribute("result")?.Value;
                if (result is "Passed" or "Failed" or "Warning")
                {
                    executed.Add(testClass);
                }

                if (result == "Failed")
                {
                    failed.Add(testClass);
                }
            }
        }

        if (reports == 0)
        {
            return null;
        }

        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            var ns = document.Root!.Name.Namespace;

            // Results reference their definition by test id; the definition holds the class name.
            var classById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var test in document.Descendants(ns + "UnitTest"))
            {
                if (test.Attribute("id")?.Value is { } id && test.Element(ns + "TestMethod") is { } method
                    && method.Attribute("className")?.Value is { } className)
                {
                    classById[id] = ClassName(className, method);
                }
            }

            foreach (var result in document.Descendants(ns + "UnitTestResult"))
            {
                total++;
                var outcome = result.Attribute("outcome")?.Value;
                if (result.Attribute("testId")?.Value is not { } testId || !classById.TryGetValue(testId, out var testClass))
                {
                    continue;
                }

                // A skipped test never enters its class, so it proves nothing about the map.
                reported.Add(testClass);
                if (TimeSpan.TryParse(result.Attribute("duration")?.Value, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                {
                    seconds[testClass] = seconds.GetValueOrDefault(testClass) + duration.TotalSeconds;
                }
                if (outcome is not ("NotExecuted" or "Inconclusive" or "Pending"))
                {
                    executed.Add(testClass);
                }

                if (outcome == "Failed")
                {
                    failed.Add(testClass);
                }
            }
        }

        return new TrxOutcome(total, failed, executed) { ReportedClasses = reported, ClassSeconds = seconds };
    }

    /// <summary>Adapter of Machine.Specifications (MSpec), which names tests unlike the others.</summary>
    private const string MSpecAdapter = "executor://machine.vstestadapter";

    /// <summary>
    /// The class name as test filters and coverage maps spell it. VSTest may write it with the
    /// assembly appended (<c>Ns.Class, Assembly</c>). MSpec writes only the namespace as the class and
    /// <c>Context::Specification</c> as the test name; its class is the context, so it is put back
    /// together from both. Left as the namespace, it would match no class in the source, and a failing
    /// context would never be selected.
    /// </summary>
    /// <param name="className">Class name from the report.</param>
    /// <param name="method">The report's <c>TestMethod</c> element, for the adapter and the test name.</param>
    private static string ClassName(string className, XElement method)
    {
        var comma = className.IndexOf(',', StringComparison.Ordinal);
        var name = (comma < 0 ? className : className[..comma]).Trim();
        if (method.Attribute("adapterTypeName")?.Value.StartsWith(MSpecAdapter, StringComparison.OrdinalIgnoreCase) != true
            || method.Attribute("name")?.Value is not { } testName)
        {
            return name;
        }

        var separator = testName.IndexOf("::", StringComparison.Ordinal);
        return separator > 0 ? $"{name}.{testName[..separator]}" : name;
    }
}
