using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace TestDetta.Core;

/// <summary>
/// Visual Basic syntax for the change detector and the source index, giving declarations the same
/// identities as C# (<c>Calc.Calculator::Add(Integer,Integer)</c>, <c>.ctor</c>, <c>`n</c> arity), so the
/// rest of the analysis and the coverage maps (IL names) work unchanged (docs/design/vb-fsharp-support.md).
/// Differences from C# it takes care of: the project's root namespace goes before every namespace a file
/// declares (unless it starts with <c>Global</c>), identifiers ignore case, operators are named as the
/// compiler names them (<c>op_Addition</c>), and <c>Module</c> members are static but reachable without
/// the module's name.
/// </summary>
internal sealed class VisualBasicLanguage : SourceLanguage
{
    /// <summary>The one instance.</summary>
    public static readonly VisualBasicLanguage Instance = new();

    /// <summary>Latest language version; documentation comments are plain comments.</summary>
    private static readonly VisualBasicParseOptions ParseOptions = VisualBasicParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Latest)
        .WithDocumentationMode(DocumentationMode.None);

    /// <summary>Metadata names of binary operators, by the operator's text.</summary>
    private static readonly FrozenDictionary<string, string> BinaryOperators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["+"] = "op_Addition", ["-"] = "op_Subtraction", ["*"] = "op_Multiply", ["/"] = "op_Division",
        ["\\"] = "op_IntegerDivision", ["^"] = "op_Exponent", ["&"] = "op_Concatenate", ["Like"] = "op_Like",
        ["Mod"] = "op_Modulus", ["And"] = "op_BitwiseAnd", ["Or"] = "op_BitwiseOr", ["Xor"] = "op_ExclusiveOr",
        ["<<"] = "op_LeftShift", [">>"] = "op_RightShift", ["="] = "op_Equality", ["<>"] = "op_Inequality",
        ["<"] = "op_LessThan", [">"] = "op_GreaterThan", ["<="] = "op_LessThanOrEqual", [">="] = "op_GreaterThanOrEqual",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Metadata names of unary operators, by the operator's text.</summary>
    private static readonly FrozenDictionary<string, string> UnaryOperators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["+"] = "op_UnaryPlus", ["-"] = "op_UnaryNegation", ["Not"] = "op_OnesComplement",
        ["IsTrue"] = "op_True", ["IsFalse"] = "op_False",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool IgnoresCase => true;

    /// <inheritdoc/>
    public override SyntaxNode Parse(SourceText? text, IReadOnlyCollection<string> symbols) =>
        VisualBasicSyntaxTree.ParseText(
            text ?? SourceText.From(string.Empty),
            symbols.Count == 0 ? ParseOptions : ParseOptions.WithPreprocessorSymbols(symbols.Select(static s => new KeyValuePair<string, object>(s, true)))).GetRoot();

    /// <inheritdoc/>
    public override List<string> ConditionalSymbols(params ReadOnlySpan<SyntaxNode> roots)
    {
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!root.ContainsDirectives)
            {
                continue;
            }

            // #If and #ElseIf; nested ones inside a disabled branch are directives too.
            foreach (var directive in root.DescendantNodes(descendIntoTrivia: true).OfType<IfDirectiveTriviaSyntax>())
            {
                foreach (var name in directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    symbols.Add(name.Identifier.ValueText);
                }
            }
        }

        return [.. symbols];
    }

    /// <inheritdoc/>
    public override Dictionary<string, Declaration> Collect(SyntaxNode root, string rootNamespace)
    {
        var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        Visit(root, containingType: null, rootNamespace, result);
        return result;
    }

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> ProjectWideNodes(SyntaxNode root) =>
        ((CompilationUnitSyntax)root).Attributes;

    /// <inheritdoc/>
    public override bool DescendFileShell(SyntaxNode node, SyntaxNode root) =>
        !IsDeclaration(node) || node is NamespaceBlockSyntax;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNode> MetadataNodes(SyntaxNode node, Func<SyntaxNode, SyntaxNode, bool> descend)
    {
        foreach (var child in node.DescendantNodes(n => descend(n, node)))
        {
            if (child is AttributeListSyntax or EqualsValueSyntax { Parent: ParameterSyntax })
            {
                yield return child;
            }
        }
    }

    /// <inheritdoc/>
    public override bool IsSignificant(SyntaxTrivia trivia) =>
        trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia);

    /// <inheritdoc/>
    public override bool HasTopLevelStatements(SyntaxNode root) => false;

    /// <summary>A type's own declaration without its members, which are declarations of their own.</summary>
    /// <param name="node">Candidate node.</param>
    /// <param name="type">The type block.</param>
    public static bool DescendTypeShell(SyntaxNode node, SyntaxNode type) =>
        node == type || node.Parent != type || !IsDeclaration(node);

    /// <summary>True for a namespace, type, enum, delegate or member declaration.</summary>
    /// <param name="node">A node.</param>
    internal static bool IsDeclaration(SyntaxNode node) => node is
        NamespaceBlockSyntax or TypeBlockSyntax or EnumBlockSyntax or DelegateStatementSyntax
        or MethodBlockBaseSyntax or MethodStatementSyntax or PropertyBlockSyntax or PropertyStatementSyntax
        or EventBlockSyntax or EventStatementSyntax or FieldDeclarationSyntax or DeclareStatementSyntax
        or EnumMemberDeclarationSyntax;

    /// <summary>Collects the declarations below <paramref name="container"/>.</summary>
    /// <param name="container">Compilation unit, namespace or type.</param>
    /// <param name="containingType">The enclosing type, or null outside types.</param>
    /// <param name="currentNamespace">Enclosing namespace (the root namespace at the top), empty for the global one.</param>
    /// <param name="result">Collected declarations; updated in place.</param>
    private static void Visit(SyntaxNode container, Declaration? containingType, string currentNamespace, Dictionary<string, Declaration> result)
    {
        foreach (var child in container.ChildNodes())
        {
            switch (child)
            {
                case NamespaceBlockSyntax ns:
                    var name = Compact(ns.NamespaceStatement.Name);
                    var qualified = name.StartsWith("Global.", StringComparison.OrdinalIgnoreCase) ? name["Global.".Length..]
                        : string.Equals(name, "Global", StringComparison.OrdinalIgnoreCase) ? string.Empty
                        : Qualify(currentNamespace, name);
                    Visit(ns, containingType: null, qualified, result);
                    break;

                case TypeBlockSyntax type:
                    AddType(type, type.BlockStatement.Identifier.ValueText + Arity(type.BlockStatement.TypeParameterList), containingType, currentNamespace, result, nested: true);
                    break;

                case EnumBlockSyntax @enum:
                    var enumDeclaration = AddType(@enum, @enum.EnumStatement.Identifier.ValueText, containingType, currentNamespace, result, nested: false);
                    foreach (var member in @enum.Members.OfType<EnumMemberDeclarationSyntax>())
                    {
                        AddMember(result, enumDeclaration, member.Identifier.ValueText, member);
                    }

                    break;

                case DelegateStatementSyntax @delegate:
                    AddType(@delegate, @delegate.Identifier.ValueText + Arity(@delegate.TypeParameterList), containingType, currentNamespace, result, nested: false, DeclarationWalker.DescendAll);
                    break;

                case StatementSyntax member when containingType is not null && MemberIdentity(member) is { } identity:
                    AddMember(result, containingType, identity, member);
                    break;
            }
        }
    }

    /// <summary>Adds a type (and, for a type block, its members) and returns its declaration.</summary>
    /// <param name="node">The type's node.</param>
    /// <param name="identity">Name with arity.</param>
    /// <param name="containingType">The enclosing type, or null.</param>
    /// <param name="currentNamespace">Enclosing namespace.</param>
    /// <param name="result">Collected declarations.</param>
    /// <param name="nested">True to visit the members of a type block.</param>
    /// <param name="descend">Which nodes belong to the type's own declaration; its shell by default.</param>
    private static Declaration AddType(
        SyntaxNode node,
        string identity,
        Declaration? containingType,
        string currentNamespace,
        Dictionary<string, Declaration> result,
        bool nested,
        Func<SyntaxNode, SyntaxNode, bool>? descend = null)
    {
        var declaration = new Declaration(
            Qualify(containingType?.TypeName ?? currentNamespace, identity),
            containingType is null ? Qualify(currentNamespace, identity) : $"{containingType.RuntimeTypeName}+{identity}",
            null,
            node,
            descend ?? DescendTypeShell);
        Add(result, declaration.TypeName, declaration);
        if (nested)
        {
            Visit(node, declaration, currentNamespace, result);
        }

        return declaration;
    }

    /// <summary>Adds a member of <paramref name="type"/>.</summary>
    /// <param name="result">Collected declarations.</param>
    /// <param name="type">The containing type.</param>
    /// <param name="identity">The member's identity.</param>
    /// <param name="node">The member's node.</param>
    private static void AddMember(Dictionary<string, Declaration> result, Declaration type, string identity, SyntaxNode node) =>
        Add(result, $"{type.TypeName}::{identity}", type with { MemberName = identity, Node = node, Descend = DeclarationWalker.DescendAll });

    /// <summary>Adds <paramref name="declaration"/> under <paramref name="key"/>, numbering duplicates.</summary>
    /// <param name="result">Collected declarations.</param>
    /// <param name="key">Identity.</param>
    /// <param name="declaration">Declaration.</param>
    private static void Add(Dictionary<string, Declaration> result, string key, Declaration declaration)
    {
        var unique = key;
        for (var n = 2; !result.TryAdd(unique, declaration); n++)
        {
            unique = $"{key}#{n}";
        }
    }

    /// <summary>
    /// The identity of a member statement, as C# members have theirs: <c>Name`n(Integer,ByRef String)</c>
    /// for methods, <c>.ctor(...)</c> and <c>.cctor(...)</c>, <c>op_Addition(A,B)</c> for operators, the name
    /// for properties (with parameters when they take some), events and enum members, and the comma-joined
    /// names of a field statement. Null for a node that declares nothing.
    /// </summary>
    /// <param name="member">A statement directly inside a type.</param>
    internal static string? MemberIdentity(StatementSyntax member) => member switch
    {
        ConstructorBlockSyntax c => $"{(Shared(c.BlockStatement.Modifiers) ? ".cctor" : ".ctor")}({Parameters(c.BlockStatement.ParameterList)})",
        OperatorBlockSyntax o => OperatorIdentity(o.OperatorStatement),
        OperatorStatementSyntax o => OperatorIdentity(o),
        MethodBlockSyntax m => MethodIdentity(m.SubOrFunctionStatement),
        MethodStatementSyntax m => MethodIdentity(m),
        DeclareStatementSyntax d => $"{d.Identifier.ValueText}({Parameters(d.ParameterList)})",
        PropertyBlockSyntax p => PropertyIdentity(p.PropertyStatement),
        PropertyStatementSyntax p => PropertyIdentity(p),
        EventBlockSyntax e => e.EventStatement.Identifier.ValueText,
        EventStatementSyntax e => e.Identifier.ValueText,
        FieldDeclarationSyntax f => string.Join(',', f.Declarators.SelectMany(static d => d.Names).Select(static n => n.Identifier.ValueText)),
        _ => null,
    };

    /// <summary>The statement that declares a member: the block's first statement, or the member itself.</summary>
    /// <param name="member">A member node.</param>
    internal static StatementSyntax Header(SyntaxNode member) => member switch
    {
        MethodBlockBaseSyntax block => block.BlockStatement,
        PropertyBlockSyntax property => property.PropertyStatement,
        EventBlockSyntax @event => @event.EventStatement,
        _ => (StatementSyntax)member,
    };

    /// <summary>The modifiers of a member or type node.</summary>
    /// <param name="node">A member or type node.</param>
    internal static SyntaxTokenList Modifiers(SyntaxNode node) => node switch
    {
        TypeBlockSyntax type => type.BlockStatement.Modifiers,
        EnumBlockSyntax @enum => @enum.EnumStatement.Modifiers,
        DelegateStatementSyntax @delegate => @delegate.Modifiers,
        _ => Header(node) switch
        {
            MethodBaseSyntax method => method.Modifiers,
            FieldDeclarationSyntax field => field.Modifiers,
            _ => default,
        },
    };

    /// <summary>The attribute lists of a member or type node.</summary>
    /// <param name="node">A member or type node.</param>
    internal static SyntaxList<AttributeListSyntax> Attributes(SyntaxNode node) => node switch
    {
        TypeBlockSyntax type => type.BlockStatement.AttributeLists,
        EnumBlockSyntax @enum => @enum.EnumStatement.AttributeLists,
        DelegateStatementSyntax @delegate => @delegate.AttributeLists,
        EnumMemberDeclarationSyntax member => member.AttributeLists,
        _ => Header(node) switch
        {
            MethodBaseSyntax method => method.AttributeLists,
            FieldDeclarationSyntax field => field.AttributeLists,
            _ => default,
        },
    };

    /// <summary>True when <c>Shared</c> is among <paramref name="modifiers"/>.</summary>
    /// <param name="modifiers">Modifiers.</param>
    internal static bool Shared(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.SharedKeyword);

    /// <summary>A method's identity: name, arity and parameter types.</summary>
    /// <param name="method">Sub or Function statement.</param>
    private static string MethodIdentity(MethodStatementSyntax method) =>
        $"{method.Identifier.ValueText}{Arity(method.TypeParameterList)}({Parameters(method.ParameterList)})";

    /// <summary>A property's identity: its name, with parameter types when it takes some (an indexed property).</summary>
    /// <param name="property">Property statement.</param>
    private static string PropertyIdentity(PropertyStatementSyntax property) =>
        property.ParameterList is { Parameters.Count: > 0 } parameters
            ? $"{property.Identifier.ValueText}({Parameters(parameters)})"
            : property.Identifier.ValueText;

    /// <summary>An operator's identity: the compiler's method name and the parameter types.</summary>
    /// <param name="statement">Operator statement.</param>
    private static string OperatorIdentity(OperatorStatementSyntax statement)
    {
        var token = statement.OperatorToken.ValueText;
        var parameters = statement.ParameterList?.Parameters.Count ?? 0;
        var name = token.Equals("CType", StringComparison.OrdinalIgnoreCase)
            ? (statement.Modifiers.Any(SyntaxKind.NarrowingKeyword) ? "op_Explicit" : "op_Implicit")
            : (parameters == 1 ? UnaryOperators : BinaryOperators).GetValueOrDefault(token, "op_" + token);
        return $"{name}({Parameters(statement.ParameterList)})";
    }

    /// <summary><c>`n</c> for a generic declaration, empty otherwise.</summary>
    /// <param name="parameters">Type parameter list, or null.</param>
    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is { Parameters.Count: > 0 } ? $"`{parameters.Parameters.Count}" : string.Empty;

    /// <summary>Comma-separated parameter types with <c>ByRef</c> and <c>ParamArray</c>.</summary>
    /// <param name="list">Parameter list, or null.</param>
    private static string Parameters(ParameterListSyntax? list) =>
        list is null ? string.Empty : string.Join(',', list.Parameters.Select(static p =>
        {
            var type = p.AsClause is null ? "Object" : Compact(p.AsClause.Type);
            var byRef = p.Modifiers.Any(SyntaxKind.ByRefKeyword) ? "ByRef " : string.Empty;
            var paramArray = p.Modifiers.Any(SyntaxKind.ParamArrayKeyword) ? "ParamArray " : string.Empty;
            return byRef + paramArray + type;
        }));

    /// <summary>Source text without whitespace, so formatting does not change identities.</summary>
    /// <param name="node">A name or type.</param>
    private static string Compact(SyntaxNode? node) =>
        node is null ? string.Empty : string.Concat(node.DescendantTokens().Select(static t => t.Text));

    /// <summary>Joins a namespace or type with a nested name.</summary>
    /// <param name="prefix">Enclosing namespace or type; empty for the global namespace.</param>
    /// <param name="name">Nested name.</param>
    private static string Qualify(string prefix, string name) => prefix.Length == 0 ? name : name.Length == 0 ? prefix : $"{prefix}.{name}";
}
