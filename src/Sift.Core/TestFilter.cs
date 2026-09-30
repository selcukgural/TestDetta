using System.Text;

namespace Sift.Core;

/// <summary>Builds <c>dotnet test --filter</c> expressions (VSTest syntax) and xUnit v3's own class filters.</summary>
public static class TestFilter
{
    /// <summary>
    /// Arguments that run only <paramref name="testClasses"/>, or everything but them. xUnit v3 under
    /// Microsoft.Testing.Platform has its own options (<c>--filter-class</c>, <c>--filter-not-class</c>)
    /// and rejects the VSTest <c>--filter</c> with "Unknown option"; everything else takes <c>--filter</c>.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    /// <param name="exclude">True to run every test outside the classes.</param>
    /// <param name="xunitV3OnTestingPlatform">True for an xUnit v3 project run by Microsoft.Testing.Platform.</param>
    public static List<string> Arguments(IReadOnlyCollection<string> testClasses, bool exclude, bool xunitV3OnTestingPlatform)
    {
        if (!xunitV3OnTestingPlatform)
        {
            return ["--filter", exclude ? ExcludingClasses(testClasses) : ForClasses(testClasses)];
        }

        var arguments = new List<string>(testClasses.Count * 2);
        foreach (var testClass in testClasses)
        {
            arguments.Add(exclude ? "--filter-not-class" : "--filter-class");
            arguments.Add(testClass);
        }

        return arguments;
    }

    /// <summary>True when <paramref name="project"/> references xUnit v3, whose runner has its own filter options.</summary>
    /// <param name="project">Test project.</param>
    public static bool IsXunitV3(ProjectNode project) =>
        project.PackageReferences.Any(static p => p.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A filter that matches every test method of the given classes:
    /// <c>FullyQualifiedName~App.OrderTests.|FullyQualifiedName~App.CartTests.</c>. The trailing dot
    /// keeps <c>OrderTests</c> from also matching <c>OrderTestsBase</c>.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    public static string ForClasses(IEnumerable<string> testClasses)
    {
        var filter = new StringBuilder();
        foreach (var testClass in testClasses)
        {
            if (filter.Length > 0)
            {
                filter.Append('|');
            }

            filter.Append("FullyQualifiedName~");
            AppendEscaped(filter, testClass);
            filter.Append('.');
        }

        return filter.ToString();
    }

    /// <summary>
    /// A filter that matches every test outside the given classes:
    /// <c>FullyQualifiedName!~App.OrderTests.&amp;FullyQualifiedName!~App.CartTests.</c>.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    public static string ExcludingClasses(IEnumerable<string> testClasses)
    {
        var filter = new StringBuilder();
        foreach (var testClass in testClasses)
        {
            if (filter.Length > 0)
            {
                filter.Append('&');
            }

            filter.Append("FullyQualifiedName!~");
            AppendEscaped(filter, testClass);
            filter.Append('.');
        }

        return filter.ToString();
    }

    /// <summary>Appends <paramref name="value"/>, escaping the characters that are operators in the filter language.</summary>
    /// <param name="builder">Destination.</param>
    /// <param name="value">Class name.</param>
    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }
}
