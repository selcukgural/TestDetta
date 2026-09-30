using System.Xml.Linq;

namespace Sift.Coverage;

/// <summary>What a TRX report says about one test run.</summary>
/// <param name="Total">Number of tests that ran.</param>
/// <param name="FailedClasses">Test classes (runtime names) with at least one failed test.</param>
/// <param name="ExecutedClasses">Test classes (runtime names) with at least one test that ran, passed or failed.</param>
internal sealed record TrxOutcome(int Total, IReadOnlySet<string> FailedClasses, IReadOnlySet<string> ExecutedClasses);

/// <summary>Reads the parts of a Visual Studio TRX test report Sift needs: how many tests ran and which classes failed.</summary>
internal static class TrxReport
{
    /// <summary>Reads every <c>.trx</c> file in <paramref name="directory"/> and combines them.</summary>
    /// <param name="directory">Results directory of one or more runs.</param>
    /// <returns>The combined outcome, or null when the directory holds no report.</returns>
    public static TrxOutcome? Read(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var files = Directory.GetFiles(directory, "*.trx", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            return null;
        }

        var total = 0;
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var executed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            var ns = document.Root!.Name.Namespace;

            // Results reference their definition by test id; the definition holds the class name.
            var classById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var test in document.Descendants(ns + "UnitTest"))
            {
                if (test.Attribute("id")?.Value is { } id && test.Element(ns + "TestMethod")?.Attribute("className")?.Value is { } className)
                {
                    classById[id] = ClassName(className);
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

        return new TrxOutcome(total, failed, executed);
    }

    /// <summary>
    /// The class name as test filters and coverage maps spell it. VSTest may write it with the
    /// assembly appended (<c>Ns.Class, Assembly</c>).
    /// </summary>
    /// <param name="className">Class name from the report.</param>
    private static string ClassName(string className)
    {
        var comma = className.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? className : className[..comma]).Trim();
    }
}
