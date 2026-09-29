using System.Text;

namespace Sift.Core;

/// <summary>Builds <c>dotnet test --filter</c> expressions (VSTest syntax).</summary>
public static class TestFilter
{
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
