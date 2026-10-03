using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sift.Core;

/// <summary>A type, delegate or member declaration found in a syntax tree.</summary>
/// <param name="TypeName">
/// Qualified name of the type, or of the containing type for a member. Nested types are joined with
/// '.', generic arity is written as <c>`n</c>: <c>App.Outer.Box`1</c>.
/// </param>
/// <param name="RuntimeTypeName">
/// The same type as the runtime and <c>dotnet test --filter</c> spell it, with '+' between nested
/// types: <c>App.Outer+Box`1</c>.
/// </param>
/// <param name="MemberName">Member signature such as <c>Place(string,int)</c>, or null for the type declaration itself.</param>
/// <param name="Node">The declaration node.</param>
/// <param name="Descend">
/// Which child nodes belong to this declaration, given a candidate node and <see cref="Node"/>.
/// A type excludes its members, which are declarations of their own; a member includes everything.
/// </param>
internal sealed record Declaration(
    string TypeName,
    string RuntimeTypeName,
    string? MemberName,
    MemberDeclarationSyntax Node,
    Func<SyntaxNode, SyntaxNode, bool> Descend);

/// <summary>
/// Walks a C# syntax tree and lists its declarations with stable identities, so two versions of a
/// file can be matched declaration by declaration and references can be traced by name.
/// </summary>
internal static class DeclarationWalker
{
    /// <summary>Name of the type the compiler generates for top-level statements.</summary>
    public const string TopLevelProgramType = "Program";

    /// <summary>
    /// Preview language version so that any syntax the SDK accepts parses; doc comments are read as
    /// plain comments because they never influence behaviour.
    /// </summary>
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithDocumentationMode(DocumentationMode.None);

    /// <summary>
    /// Parses <paramref name="text"/> with no preprocessor symbols defined; a missing file parses as
    /// an empty compilation unit.
    /// </summary>
    /// <param name="text">File content, or null.</param>
    public static CompilationUnitSyntax Parse(SourceText? text) => Parse(text, []);

    /// <summary>Parses <paramref name="text"/> with the given preprocessor symbols defined.</summary>
    /// <param name="text">File content, or null.</param>
    /// <param name="symbols">Symbols to define, such as <c>NET8_0_OR_GREATER</c>.</param>
    public static CompilationUnitSyntax Parse(SourceText? text, IReadOnlyCollection<string> symbols) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            text ?? SourceText.From(string.Empty),
            symbols.Count == 0 ? ParseOptions : ParseOptions.WithPreprocessorSymbols(symbols)).GetRoot();

    /// <summary>
    /// Every symbol named in an <c>#if</c> or <c>#elif</c> condition of the given trees, sorted.
    /// Which symbols a project defines (target framework, <c>DefineConstants</c>) is not known
    /// without MSBuild, so callers parse once with none and once with all of these: code that is
    /// disabled without symbols, such as a whole file inside <c>#if NET8_0_OR_GREATER</c>, is then
    /// seen too. Code only active under a mix (<c>#if A &amp;&amp; !B</c>) is still hidden in both.
    /// </summary>
    /// <param name="roots">Parsed trees, typically the old and the new version of a file.</param>
    public static List<string> ConditionalSymbols(params ReadOnlySpan<CompilationUnitSyntax> roots)
    {
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!root.ContainsDirectives)
            {
                continue;
            }

            // #if and #elif; nested ones inside a disabled branch are directives too.
            foreach (var directive in root.DescendantNodes(descendIntoTrivia: true).OfType<ConditionalDirectiveTriviaSyntax>())
            {
                foreach (var name in directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    symbols.Add(name.Identifier.ValueText);
                }
            }
        }

        return [.. symbols];
    }

    /// <summary>
    /// The file parsed with no symbols and, when it has conditional directives, again with all
    /// symbols of <see cref="ConditionalSymbols"/> defined. See there for why both are needed.
    /// </summary>
    /// <param name="text">File content, or null.</param>
    public static List<CompilationUnitSyntax> ParseVariants(SourceText? text) => ParseVariants(text, []);

    /// <summary>
    /// <see cref="ParseVariants(SourceText?)"/>, plus one parse per symbol set the compiler really uses
    /// (each target framework's <c>DefineConstants</c>), which also enters code under a mix of symbols.
    /// Only the symbols the file's conditions name matter, so sets that agree on those parse once.
    /// </summary>
    /// <param name="text">File content, or null.</param>
    /// <param name="symbolSets">Symbol sets of the projects compiling the file; empty when unknown.</param>
    public static List<CompilationUnitSyntax> ParseVariants(SourceText? text, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var root = Parse(text);
        var named = ConditionalSymbols(root);
        if (named.Count == 0)
        {
            return [root];
        }

        var roots = new List<CompilationUnitSyntax> { root, Parse(text, named) };
        foreach (var relevant in RelevantSets(named, symbolSets))
        {
            roots.Add(Parse(text, relevant));
        }

        return roots;
    }

    /// <summary>
    /// Each set reduced to the symbols in <paramref name="named"/>, without duplicates, and without the
    /// two sets every caller parses anyway: none, and all of <paramref name="named"/>.
    /// </summary>
    /// <param name="named">Symbols the file's conditions name.</param>
    /// <param name="symbolSets">Symbol sets the compiler uses.</param>
    public static List<List<string>> RelevantSets(IReadOnlyList<string> named, IReadOnlyCollection<IReadOnlyList<string>> symbolSets)
    {
        var namedSet = new HashSet<string>(named, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Empty, string.Join(';', named) };
        var result = new List<List<string>>();
        foreach (var set in symbolSets)
        {
            var relevant = set.Where(namedSet.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (seen.Add(string.Join(';', relevant)))
            {
                result.Add(relevant);
            }
        }

        return result;
    }

    /// <summary>Collects every type, delegate and member declaration of a file, keyed by identity.</summary>
    /// <param name="root">Compilation unit.</param>
    /// <remarks>
    /// Keys are <c>TypeName</c> for types and <c>TypeName::MemberName</c> for members. Duplicates,
    /// such as two parts of a partial type in one file, get a <c>#n</c> suffix in source order.
    /// </remarks>
    public static Dictionary<string, Declaration> Collect(CompilationUnitSyntax root)
    {
        var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        Visit(root, containingType: null, currentNamespace: string.Empty, result);
        return result;
    }

    /// <summary>Type name without namespace, containing types or arity: <c>App.Outer.Box`1</c> gives <c>Box</c>.</summary>
    /// <param name="typeName">Qualified type name as produced by <see cref="Collect"/>.</param>
    public static string SimpleTypeName(string typeName)
    {
        var name = typeName.AsSpan();
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        var tick = name.IndexOf('`');
        return (tick < 0 ? name : name[..tick]).ToString();
    }

    /// <summary>
    /// Identifiers a member can be referenced by in source: <c>Place(string,int)</c> gives
    /// <c>Place</c>, a field declaration <c>a,b</c> gives both. Members that are never called by
    /// name (constructors, operators, indexers, finalizers) give none.
    /// </summary>
    /// <param name="memberName">Member signature as produced by <see cref="Collect"/>.</param>
    public static IReadOnlyList<string> ReferenceNames(string memberName)
    {
        if (memberName.StartsWith('.') || memberName.StartsWith("operator ", StringComparison.Ordinal)
            || memberName.Contains("this[", StringComparison.Ordinal) || memberName == "Finalize()")
        {
            return [];
        }

        var end = memberName.AsSpan().IndexOfAny('(', '`');
        var name = end < 0 ? memberName.AsSpan() : memberName.AsSpan(0, end);

        // Explicit interface implementations are prefixed with the interface: IFoo.Run.
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        if (!name.Contains(','))
        {
            return [name.ToString()];
        }

        var names = new List<string>();
        foreach (var range in name.Split(','))
        {
            names.Add(name[range].ToString());
        }

        return names;
    }

    /// <summary>
    /// Compares a type's own declaration but not its members. Takes the type as an argument so one
    /// delegate works for the old and the new tree.
    /// </summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="type">The type declaration.</param>
    public static bool DescendTypeShell(SyntaxNode node, SyntaxNode type) =>
        node == type || node.Parent != type || node is not MemberDeclarationSyntax;

    /// <summary>
    /// The parts of a declaration the compiler stores as metadata, which reflection reads without
    /// running any code: attribute lists (also on parameters, accessors and return values) and
    /// parameter default values. Nested declarations are left out as <paramref name="descend"/> says.
    /// </summary>
    /// <param name="node">Declaration node.</param>
    /// <param name="descend">Which child nodes belong to the declaration.</param>
    public static IEnumerable<SyntaxNode> MetadataNodes(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        foreach (var child in node.DescendantNodes(n => descend(n, node)))
        {
            if (child is AttributeListSyntax or EqualsValueClauseSyntax { Parent: ParameterSyntax })
            {
                yield return child;
            }
        }
    }

    /// <summary>Includes everything below a node.</summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="root">Declaration node.</param>
    public static bool DescendAll(SyntaxNode node, SyntaxNode root) => true;

    /// <summary>Recursively collects declarations below <paramref name="container"/>.</summary>
    /// <param name="container">Compilation unit, namespace or type.</param>
    /// <param name="containingType">The enclosing type, or null outside types.</param>
    /// <param name="currentNamespace">Enclosing namespace, empty for the global namespace.</param>
    /// <param name="result">Collected declarations; updated in place.</param>
    private static void Visit(
        SyntaxNode container,
        Declaration? containingType,
        string currentNamespace,
        Dictionary<string, Declaration> result)
    {
        foreach (var child in container.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    Visit(ns, containingType: null, Qualify(currentNamespace, ns.Name.ToString()), result);
                    break;

                case BaseTypeDeclarationSyntax type:
                    var identity = TypeIdentity(type);
                    var typeDeclaration = new Declaration(
                        Qualify(containingType?.TypeName ?? currentNamespace, identity),
                        containingType is null ? Qualify(currentNamespace, identity) : $"{containingType.RuntimeTypeName}+{identity}",
                        null,
                        type,
                        DescendTypeShell);
                    Add(result, typeDeclaration.TypeName, typeDeclaration);
                    Visit(type, typeDeclaration, currentNamespace, result);
                    break;

                case DelegateDeclarationSyntax @delegate:
                    var delegateIdentity = @delegate.Identifier.ValueText + Arity(@delegate.TypeParameterList);
                    var delegateDeclaration = new Declaration(
                        Qualify(containingType?.TypeName ?? currentNamespace, delegateIdentity),
                        containingType is null ? Qualify(currentNamespace, delegateIdentity) : $"{containingType.RuntimeTypeName}+{delegateIdentity}",
                        null,
                        @delegate,
                        DescendAll);
                    Add(result, delegateDeclaration.TypeName, delegateDeclaration);
                    break;

                case MemberDeclarationSyntax member when containingType is not null:
                    var memberName = MemberIdentity(member);
                    Add(
                        result,
                        $"{containingType.TypeName}::{memberName}",
                        containingType with { MemberName = memberName, Node = member, Descend = DescendAll });
                    break;
            }
        }
    }

    /// <summary>Adds <paramref name="declaration"/> under <paramref name="key"/>, numbering duplicates.</summary>
    /// <param name="result">Collected declarations.</param>
    /// <param name="key">Identity of the declaration.</param>
    /// <param name="declaration">The declaration to add.</param>
    private static void Add(Dictionary<string, Declaration> result, string key, Declaration declaration)
    {
        var unique = key;
        for (var n = 2; !result.TryAdd(unique, declaration); n++)
        {
            unique = $"{key}#{n}";
        }
    }

    /// <summary>Type name with generic arity, such as <c>Box`1</c>.</summary>
    /// <param name="type">Type declaration.</param>
    private static string TypeIdentity(BaseTypeDeclarationSyntax type) =>
        type.Identifier.ValueText + (type is TypeDeclarationSyntax t ? Arity(t.TypeParameterList) : string.Empty);

    /// <summary>Signature that tells overloads apart: name, generic arity and parameter types.</summary>
    /// <param name="member">Member declaration inside a type.</param>
    private static string MemberIdentity(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m =>
            $"{Explicit(m.ExplicitInterfaceSpecifier)}{m.Identifier.ValueText}{Arity(m.TypeParameterList)}({Parameters(m.ParameterList)})",
        ConstructorDeclarationSyntax c =>
            $"{(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}({Parameters(c.ParameterList)})",
        DestructorDeclarationSyntax => "Finalize()",
        PropertyDeclarationSyntax p => Explicit(p.ExplicitInterfaceSpecifier) + p.Identifier.ValueText,
        IndexerDeclarationSyntax i => $"{Explicit(i.ExplicitInterfaceSpecifier)}this[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => Explicit(e.ExplicitInterfaceSpecifier) + e.Identifier.ValueText,
        BaseFieldDeclarationSyntax f => string.Join(',', f.Declaration.Variables.Select(static v => v.Identifier.ValueText)),
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}({Parameters(o.ParameterList)})",
        ConversionOperatorDeclarationSyntax c =>
            $"operator {c.ImplicitOrExplicitKeyword.Text} {Compact(c.Type)}({Parameters(c.ParameterList)})",
        EnumMemberDeclarationSyntax e => e.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };

    /// <summary><c>`n</c> for a generic declaration, empty otherwise.</summary>
    /// <param name="parameters">Type parameter list, or null.</param>
    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is { Parameters.Count: > 0 } ? $"`{parameters.Parameters.Count}" : string.Empty;

    /// <summary>Interface prefix of an explicit implementation, such as <c>IDisposable.</c>.</summary>
    /// <param name="specifier">Explicit interface specifier, or null.</param>
    private static string Explicit(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier is null ? string.Empty : Compact(specifier.Name) + ".";

    /// <summary>Comma-separated parameter types including <c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c>.</summary>
    /// <param name="list">Parameter list.</param>
    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(',', list.Parameters.Select(static p =>
            p.Modifiers.Count == 0 ? Compact(p.Type) : $"{p.Modifiers.ToString()} {Compact(p.Type)}"));

    /// <summary>Source text of <paramref name="node"/> without any whitespace, so formatting does not change identities.</summary>
    /// <param name="node">Type syntax or name, or null.</param>
    private static string Compact(SyntaxNode? node) =>
        node is null ? string.Empty : string.Concat(node.DescendantTokens().Select(static t => t.Text));

    /// <summary>Joins a namespace or type with a nested name.</summary>
    /// <param name="prefix">Enclosing namespace or type; empty for the global namespace.</param>
    /// <param name="name">Nested name.</param>
    private static string Qualify(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";
}
