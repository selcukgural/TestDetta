using System.Text;

namespace TestDetta.Core;

/// <summary>How a test project's runner takes a filter by test class.</summary>
public enum TestFilterSyntax
{
    /// <summary>The VSTest <c>--filter</c> expression, which most runners accept.</summary>
    VSTest,

    /// <summary>xUnit v3 under Microsoft.Testing.Platform: <c>--filter-class</c>, <c>--filter-not-class</c>.</summary>
    XunitV3,

    /// <summary>TUnit: <c>--treenode-filter</c>; it ignores <c>--filter</c> and then runs no test at all.</summary>
    TUnit,
}

/// <summary>Builds <c>dotnet test</c> filters by test class in the syntax each runner takes.</summary>
public static class TestFilter
{
    /// <summary>The filter syntax of <paramref name="project"/> under <paramref name="mode"/>.</summary>
    /// <param name="project">Test project.</param>
    /// <param name="mode">Runner <c>dotnet test</c> uses.</param>
    public static TestFilterSyntax SyntaxFor(ProjectNode project, DotnetTestMode mode) =>
        IsTUnit(project) ? TestFilterSyntax.TUnit
        : mode == DotnetTestMode.TestingPlatform && IsXunitV3(project) ? TestFilterSyntax.XunitV3
        : TestFilterSyntax.VSTest;

    /// <summary>
    /// Arguments that run only <paramref name="testClasses"/>, or everything but them. xUnit v3 under
    /// Microsoft.Testing.Platform has its own options and rejects the VSTest <c>--filter</c> with
    /// "Unknown option"; TUnit takes a tree-node filter and ignores <c>--filter</c>; everything else
    /// takes <c>--filter</c>.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    /// <param name="exclude">True to run every test outside the classes.</param>
    /// <param name="syntax">Filter syntax of the project's runner.</param>
    public static List<string> Arguments(IReadOnlyCollection<string> testClasses, bool exclude, TestFilterSyntax syntax)
    {
        if (syntax == TestFilterSyntax.TUnit)
        {
            return ["--treenode-filter", TreeNodeFilter(testClasses, exclude)];
        }

        if (syntax == TestFilterSyntax.VSTest)
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

    /// <summary>
    /// The filter arguments that run <paramref name="testClasses"/> within the user's own filter, and the
    /// rest of <paramref name="userArguments"/> to append after them; null when the runner cannot combine
    /// the two. <c>dotnet test</c> rejects a second <c>--filter</c>, so a VSTest filter of the user's is
    /// joined with the class filter (<c>(user)&amp;(classes)</c>). xUnit v3 joins options of different kinds
    /// with "and" (a trait, method or namespace filter of the user's narrows the class filter as it should),
    /// but joins two class filters with "or" and throws when a query filter meets a class filter; TUnit
    /// takes one tree-node filter. Those cases return null: the project then runs under the user's filter alone.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    /// <param name="syntax">Filter syntax of the project's runner.</param>
    /// <param name="userArguments">The user's <c>dotnet test</c> arguments; those after a <c>--</c> are run settings and stay as they are.</param>
    public static (List<string> Filter, List<string> Others)? WithinUserFilter(IReadOnlyCollection<string> testClasses, TestFilterSyntax syntax, IReadOnlyList<string> userArguments)
    {
        var ours = Arguments(testClasses, exclude: false, syntax);
        var end = userArguments.Count;
        for (var i = 0; i < userArguments.Count; i++)
        {
            if (userArguments[i] == "--")
            {
                end = i;
                break;
            }
        }

        if (syntax != TestFilterSyntax.VSTest)
        {
            string[] conflicting = syntax == TestFilterSyntax.TUnit ? ["--treenode-filter"] : ["--filter-class", "--filter-query"];
            for (var i = 0; i < end; i++)
            {
                var argument = userArguments[i];
                if (conflicting.Any(option => argument == option || argument.StartsWith(option + "=", StringComparison.Ordinal)))
                {
                    return null;
                }
            }

            return (ours, [.. userArguments]);
        }

        var userFilters = new List<string>();
        var rest = new List<string>(userArguments.Count);
        for (var i = 0; i < end; i++)
        {
            if (userArguments[i] == "--filter" && i + 1 < end)
            {
                userFilters.Add(userArguments[++i]);
            }
            else if (userArguments[i].StartsWith("--filter=", StringComparison.Ordinal))
            {
                userFilters.Add(userArguments[i]["--filter=".Length..]);
            }
            else
            {
                rest.Add(userArguments[i]);
            }
        }

        rest.AddRange(userArguments.Skip(end));
        if (userFilters.Count == 0)
        {
            return (ours, rest);
        }

        var combined = string.Join('&', userFilters.Select(static filter => $"({filter})")) + $"&({ours[1]})";
        return (["--filter", combined], rest);
    }

    /// <summary>True when <paramref name="project"/> references TUnit, whose runner takes a tree-node filter.</summary>
    /// <param name="project">Test project.</param>
    public static bool IsTUnit(ProjectNode project) =>
        project.PackageReferences.Any(static p => p.Equals("TUnit", StringComparison.OrdinalIgnoreCase) || p.StartsWith("TUnit.", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A TUnit tree-node filter (<c>/assembly/namespace/class/test</c>) for the given classes:
    /// <c>/*/*/(OrderTests)|(Outer+CartTests)/**</c>, or <c>/*/*/(!OrderTests)&amp;(!Outer+CartTests)/**</c>
    /// to run everything else. A class node is named by its runtime name without the namespace, and the
    /// namespace is left open because a filter cannot pair a namespace with each class: a class
    /// sharing its name with one in another namespace is selected (or left out) with it.
    /// </summary>
    /// <param name="testClasses">Runtime class names, nested classes joined with '+'.</param>
    /// <param name="exclude">True to match every class but these.</param>
    public static string TreeNodeFilter(IEnumerable<string> testClasses, bool exclude)
    {
        var filter = new StringBuilder("/*/*/");
        var first = true;
        foreach (var testClass in testClasses)
        {
            filter.Append(first ? string.Empty : exclude ? "&" : "|").Append(exclude ? "(!" : "(");
            var nested = testClass.IndexOf('+', StringComparison.Ordinal);
            var dot = testClass.LastIndexOf('.', nested < 0 ? testClass.Length - 1 : nested);
            foreach (var c in testClass[(dot + 1)..])
            {
                if (c is '\\' or '/' or '*' or '(' or ')' or '|' or '&' or '!' or '[' or ']' or '=')
                {
                    filter.Append('\\');
                }

                filter.Append(c);
            }

            filter.Append(')');
            first = false;
        }

        return filter.Append("/**").ToString();
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
