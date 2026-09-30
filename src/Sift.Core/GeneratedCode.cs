using Microsoft.CodeAnalysis.Text;

namespace Sift.Core;

/// <summary>
/// What a source generator wrote for one type: the names its code mentions. Generated code is not
/// in the source tree, so without this name tracing cannot see generated callers, such as the
/// registration code the MVVM Toolkit generates for a test's message recipients.
/// </summary>
/// <param name="Project">Project that compiled the generated code, relative to the repository root.</param>
/// <param name="TypeName">Qualified name of the generated (usually partial) type.</param>
/// <param name="References">Simple names mentioned by the generated code of the type.</param>
public sealed record GeneratedType(string Project, string TypeName, IReadOnlyList<string> References);

/// <summary>Reads generated sources into <see cref="GeneratedType"/> entries.</summary>
public static class GeneratedCode
{
    /// <summary>
    /// The generated types of one project, with every name their code mentions. Several generated
    /// files may add to one partial type; their names are combined.
    /// </summary>
    /// <param name="project">Project that compiled the sources, relative to the repository root.</param>
    /// <param name="sources">Generated source texts.</param>
    public static List<GeneratedType> Collect(string project, IEnumerable<string> sources)
    {
        var names = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var root in DeclarationWalker.ParseVariants(SourceText.From(source)))
            {
                foreach (var declaration in DeclarationWalker.Collect(root).Values)
                {
                    if (!names.TryGetValue(declaration.TypeName, out var references))
                    {
                        references = new SortedSet<string>(StringComparer.Ordinal);
                        names[declaration.TypeName] = references;
                    }

                    references.UnionWith(SourceIndexBuilder.References(declaration.Node, declaration.Descend));
                }
            }
        }

        return [.. names.Select(p => new GeneratedType(project, p.Key, [.. p.Value]))];
    }
}
