using System.Text.RegularExpressions;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Syntax;
using FSharp.Compiler.Tokenization;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using FsSourceText = FSharp.Compiler.Text.SourceText;
using FsRangeType = FSharp.Compiler.Text.Range;

namespace TestDetta.Core;

/// <summary>A span of an F# file: lines 1-based, columns 0-based, the end exclusive.</summary>
/// <param name="StartLine">First line.</param>
/// <param name="StartColumn">First column.</param>
/// <param name="EndLine">Last line.</param>
/// <param name="EndColumn">Column after the end.</param>
internal readonly record struct FsSpan(int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    /// <summary>The span of a compiler range.</summary>
    /// <param name="range">An F# range.</param>
    public static FsSpan Of(FsRangeType range) => new(range.StartLine, range.StartColumn, range.EndLine, range.EndColumn);

    /// <summary>The smallest span covering this one and the given attribute lists, which are written before it.</summary>
    /// <param name="attributes">Attribute lists of the declaration.</param>
    public FsSpan With(FSharpList<SynAttributeList> attributes)
    {
        var span = this;
        foreach (var list in attributes)
        {
            var range = list.Range;
            if (range.StartLine < span.StartLine || (range.StartLine == span.StartLine && range.StartColumn < span.StartColumn))
            {
                span = span with { StartLine = range.StartLine, StartColumn = range.StartColumn };
            }
        }

        return span;
    }

    /// <summary>True when the position is inside this span.</summary>
    /// <param name="line">Line, 1-based.</param>
    /// <param name="column">Column, 0-based.</param>
    public bool Contains(int line, int column) =>
        (line > StartLine || (line == StartLine && column >= StartColumn)) && (line < EndLine || (line == EndLine && column < EndColumn));
}

/// <summary>A token that changes what compiles: no whitespace or comment; a line's first token carries its indentation.</summary>
/// <param name="Line">Line, 1-based.</param>
/// <param name="Column">Column, 0-based.</param>
/// <param name="Text">The token's text.</param>
/// <param name="IsIdentifier">True for an identifier (backticks removed from <see cref="Name"/>).</param>
internal sealed record FsToken(int Line, int Column, string Text, bool IsIdentifier)
{
    /// <summary>The identifier without backticks.</summary>
    public string Name => Text.StartsWith("``", StringComparison.Ordinal) && Text.EndsWith("``", StringComparison.Ordinal) && Text.Length >= 4 ? Text[2..^2] : Text;
}

/// <summary>A type or member of an F# file with the identity the coverage maps use (IL names).</summary>
/// <param name="TypeName">Qualified type name: a module is a type (<c>Shop.Calc.Helpers</c>), nested types with '.'.</param>
/// <param name="RuntimeTypeName">The same with '+' between nested types.</param>
/// <param name="MemberName">Member identity (<c>twice</c>, <c>Tax(net:decimal)</c>, <c>.ctor</c>), or null for the type.</param>
/// <param name="Span">Where it is written.</param>
/// <param name="Excluded">Spans inside it that belong to other declarations (a type's members).</param>
/// <param name="AttributeSpans">Its attribute lists (metadata).</param>
internal sealed record FsDeclaration(string TypeName, string RuntimeTypeName, string? MemberName, FsSpan Span, IReadOnlyList<FsSpan> Excluded, IReadOnlyList<FsSpan> AttributeSpans)
{
    /// <summary>For a type: a module, whose header and <c>open</c>s apply to everything below it.</summary>
    public bool IsModule { get; init; }

    /// <summary>For a member: <c>inline</c>, so its body is compiled into its callers.</summary>
    public bool IsInline { get; init; }

    /// <summary>For a type: a module, or a class that is not abstract; test frameworks run these.</summary>
    public bool IsRunnableClass { get; init; }

    /// <summary>For a type: a member or function directly inside has a test attribute.</summary>
    public bool HasTestMethods { get; init; }

    /// <summary>For a type: simple names of what it inherits and implements.</summary>
    public IReadOnlyList<string> BaseTypeNames { get; init; } = [];

    /// <summary>For a member: private to its type or module.</summary>
    public bool IsPrivate { get; init; }

    /// <summary>For a member: reached through its type's name (a static member of a class).</summary>
    public bool IsStatic { get; init; }

    /// <summary>For a member: code may run it without naming it (override, interface, initializer, attribute).</summary>
    public bool MayBeCalledImplicitly { get; init; }

    /// <summary>For a member: one of its attributes is a test attribute.</summary>
    public bool HasTestAttribute { get; init; }
}

/// <summary>
/// Parses and tokenizes F# files with the F# compiler service, and lists their declarations with the
/// names the compiler gives them (docs/design/vb-fsharp-support.md): a module is a static class (with
/// <c>Module</c> after its name when a type of that name sits beside it), its <c>let</c> functions and
/// values its members, a file without a module or namespace an implicit module named after the file,
/// an implicit or explicit constructor <c>.ctor</c>, and type members by name (methods with their argument
/// patterns, which tell overloads apart).
/// </summary>
internal static partial class FSharpSource
{
    /// <summary>The compiler service; parsing only, created once.</summary>
    private static readonly Lazy<FSharpChecker> Checker = new(static () => FSharpChecker.Create(
        FSharpOption<int>.None, FSharpOption<bool>.None, FSharpOption<bool>.None, null, null, FSharpOption<bool>.None,
        FSharpOption<bool>.None, FSharpOption<bool>.None, FSharpOption<bool>.None, FSharpOption<bool>.None, FSharpOption<bool>.None,
        null, FSharpOption<bool>.None, null));

    /// <summary>Serializes parses: the checker is shared and the index parses files in parallel.</summary>
    private static readonly Lock Gate = new();

    /// <summary>Attribute names that mark a test (shared with the C# index).</summary>
    private static bool IsTestAttribute(string name) => SourceIndexBuilder.IsTestAttribute(name);

    /// <summary>True for an F# implementation file, which this reads; a signature file (<c>.fsi</c>) changes the project as a whole.</summary>
    /// <param name="path">File path.</param>
    public static bool IsSource(string path) => path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase);

    /// <summary>The symbols the file's <c>#if</c> and <c>#elif</c> lines name, sorted.</summary>
    /// <param name="texts">File contents.</param>
    public static List<string> ConditionalSymbols(params string?[] texts)
    {
        var symbols = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            foreach (Match match in ConditionalLine().Matches(text ?? string.Empty))
            {
                foreach (Match identifier in Identifier().Matches(match.Groups["condition"].Value))
                {
                    symbols.Add(identifier.Value);
                }
            }
        }

        return [.. symbols];
    }

    /// <summary>
    /// The significant tokens of <paramref name="text"/> with <paramref name="defines"/> defined: no
    /// whitespace or comments, directives kept, and before a line's first token a marker of its column,
    /// since indentation is syntax in F# (moving a line out of a block changes what it does).
    /// </summary>
    /// <param name="text">File content.</param>
    /// <param name="defines">Preprocessor symbols.</param>
    public static List<FsToken> Tokens(string text, IReadOnlyCollection<string> defines)
    {
        var tokenizer = new FSharpSourceTokenizer(ListModule.OfSeq(defines), FSharpOption<string>.Some("file.fs"), FSharpOption<string>.None, FSharpOption<bool>.None);
        var state = FSharpTokenizerLexState.Initial;
        var tokens = new List<FsToken>();
        var lines = text.Split('\n');
        for (var number = 0; number < lines.Length; number++)
        {
            var line = lines[number].TrimEnd('\r');
            var lineTokenizer = tokenizer.CreateLineTokenizer(line);
            var first = true;
            while (true)
            {
                var (token, next) = lineTokenizer.ScanToken(state);
                state = next;
                if (token is null)
                {
                    break;
                }

                var info = token.Value;
                var right = Math.Min(info.RightColumn, line.Length - 1);
                if (info.LeftColumn > right)
                {
                    continue;
                }

                var piece = line[info.LeftColumn..(right + 1)];
                var significant = info.CharClass is not (FSharpTokenCharKind.WhiteSpace or FSharpTokenCharKind.Comment or FSharpTokenCharKind.LineComment)
                                  || piece.StartsWith('#');
                if (!significant || piece.Trim().Length == 0)
                {
                    continue;
                }

                if (first)
                {
                    tokens.Add(new FsToken(number + 1, info.LeftColumn, "\u0001" + info.LeftColumn, IsIdentifier: false));
                    first = false;
                }

                tokens.Add(new FsToken(number + 1, info.LeftColumn, piece, info.CharClass == FSharpTokenCharKind.Identifier));
            }
        }

        return tokens;
    }

    /// <summary>The tokens inside <paramref name="span"/> but outside <paramref name="excluded"/>.</summary>
    /// <param name="tokens">A file's significant tokens.</param>
    /// <param name="span">The span.</param>
    /// <param name="excluded">Spans to leave out.</param>
    public static IEnumerable<FsToken> Within(List<FsToken> tokens, FsSpan span, IReadOnlyList<FsSpan> excluded) =>
        tokens.Where(t => span.Contains(t.Line, t.Column) && !excluded.Any(e => e.Contains(t.Line, t.Column)));

    /// <summary>Parses <paramref name="text"/>; null when it is not an implementation file.</summary>
    /// <param name="text">File content.</param>
    /// <param name="fileName">File name, for an implicit module's name.</param>
    /// <param name="defines">Preprocessor symbols.</param>
    public static ParsedImplFileInput? Parse(string text, string fileName, IReadOnlyCollection<string> defines)
    {
        lock (Gate)
        {
            var name = Path.GetFileName(fileName);
            var (options, _) = Checker.Value.GetParsingOptionsFromCommandLineArgs(
                ListModule.OfArray<string>([name]),
                ListModule.OfSeq(defines.Select(static d => "--define:" + d)),
                FSharpOption<bool>.None,
                FSharpOption<bool>.None);
            var result = FSharpAsync.RunSynchronously(
                Checker.Value.ParseFile(name, FsSourceText.ofString(text), options, FSharpOption<bool>.None, FSharpOption<string>.None),
                FSharpOption<int>.None,
                FSharpOption<CancellationToken>.None);
            return result.ParseTree is ParsedInput.ImplFile implementation ? implementation.Item : null;
        }
    }

    /// <summary>The spans of <c>[&lt;assembly: ...&gt;]</c> attributes, which apply to the whole project.</summary>
    /// <param name="file">Parsed file.</param>
    public static List<FsSpan> AssemblyAttributes(ParsedImplFileInput file)
    {
        var spans = new List<FsSpan>();
        foreach (var module in file.contents)
        {
            Collect(module.decls);
        }

        return spans;

        void Collect(FSharpList<SynModuleDecl> decls)
        {
            foreach (var decl in decls)
            {
                if (decl is SynModuleDecl.Attributes attributes
                    && attributes.attributes.SelectMany(static l => l.Attributes).Any(static a => a.Target?.Value.idText is "assembly" or "module"))
                {
                    spans.Add(FsSpan.Of(attributes.range));
                }
                else if (decl is SynModuleDecl.NestedModule nested)
                {
                    Collect(nested.decls);
                }
            }
        }
    }

    /// <summary>The rightmost names of the file's <c>open</c>s: opened modules' members are reached without the module's name.</summary>
    /// <param name="file">Parsed file.</param>
    public static List<string> Opened(ParsedImplFileInput file)
    {
        var names = new List<string>();
        foreach (var module in file.contents)
        {
            Collect(module.decls);
        }

        return names;

        void Collect(FSharpList<SynModuleDecl> decls)
        {
            foreach (var decl in decls)
            {
                if (decl is SynModuleDecl.Open { target: SynOpenDeclTarget.ModuleOrNamespace target } && LastName(target.longId.LongIdent) is { } last)
                {
                    names.Add(last);
                }
                else if (decl is SynModuleDecl.NestedModule nested)
                {
                    Collect(nested.decls);
                }
            }
        }
    }

    /// <summary>Lists the declarations of a parsed file, keyed like C#'s (<c>Type</c>, <c>Type::Member</c>).</summary>
    /// <param name="file">Parsed file.</param>
    /// <param name="fileName">File name, which names an implicit module.</param>
    /// <param name="text">The file's content, from which argument patterns are read.</param>
    public static Dictionary<string, FsDeclaration> Collect(ParsedImplFileInput file, string fileName, string text)
    {
        var lines = text.Split('\n');
        var result = new Dictionary<string, FsDeclaration>(StringComparer.Ordinal);
        foreach (var module in file.contents)
        {
            var path = string.Join('.', module.longId.Select(static i => i.idText));
            if (module.kind.IsDeclaredNamespace)
            {
                VisitContainer(lines, module.decls, path, path, isModule: false, result);
            }
            else if (module.kind.IsGlobalNamespace)
            {
                VisitContainer(lines, module.decls, string.Empty, string.Empty, isModule: false, result);
            }
            else
            {
                // A named module (module A.B.M) or the implicit one of a file without a header.
                var name = module.kind.IsAnonModule ? ImplicitModuleName(fileName) : path;
                AddModule(lines, module.decls, name, name, FsSpan.Of(module.range), module.attribs, result);
            }
        }

        return result;
    }

    /// <summary>The implicit module of a file without a module or namespace: its file name with a capital first letter.</summary>
    /// <param name="fileName">File name.</param>
    private static string ImplicitModuleName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Visits the declarations of a namespace or module and adds its types, modules and (for a module) members.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="decls">Declarations.</param>
    /// <param name="typeName">Qualified name of the container ('.' between nested types).</param>
    /// <param name="runtimeName">The same with '+' between nested types.</param>
    /// <param name="isModule">True for a module, whose <c>let</c>s are members.</param>
    /// <param name="result">Collected declarations.</param>
    /// <returns>The spans of the declarations added, which a module's own shell leaves out.</returns>
    private static List<FsSpan> VisitContainer(string[] lines, FSharpList<SynModuleDecl> decls, string typeName, string runtimeName, bool isModule, Dictionary<string, FsDeclaration> result)
    {
        var spans = new List<FsSpan>();
        var typeNames = decls.OfType<SynModuleDecl.Types>().SelectMany(static t => t.typeDefns).Select(static t => LastName(t.typeInfo.longId)!)
            .Concat(decls.OfType<SynModuleDecl.Exception>().Select(static e => e.exnDefn.exnRepr.caseName.ident.ident.idText))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var decl in decls)
        {
            switch (decl)
            {
                case SynModuleDecl.NestedModule nested:
                    var moduleName = LastName(nested.moduleInfo.longId)!;
                    // A module beside a type of its name compiles with "Module" after its name.
                    if (typeNames.Contains(moduleName) || HasModuleSuffix(lines, nested.moduleInfo.attributes))
                    {
                        moduleName += "Module";
                    }

                    spans.Add(FsSpan.Of(nested.range));
                    AddModule(lines, nested.decls, Qualify(typeName, moduleName), Nest(runtimeName, moduleName, isModule), FsSpan.Of(nested.range), nested.moduleInfo.attributes, result);
                    break;

                case SynModuleDecl.Types types:
                    foreach (var type in types.typeDefns)
                    {
                        // The first type of a type ... and ... group starts at the keyword.
                        var typeSpan = (type == types.typeDefns.Head ? FsSpan.Of(type.Range) with { StartLine = types.range.StartLine, StartColumn = types.range.StartColumn } : FsSpan.Of(type.Range))
                            .With(type.typeInfo.attributes);
                        spans.Add(typeSpan);
                        AddType(lines, type, typeSpan, typeName, runtimeName, isModule, result);
                    }

                    break;

                case SynModuleDecl.Exception exception:
                    var exceptionName = exception.exnDefn.exnRepr.caseName.ident.ident.idText;
                    spans.Add(FsSpan.Of(exception.range));
                    Add(result, Qualify(typeName, exceptionName), new FsDeclaration(Qualify(typeName, exceptionName), Nest(runtimeName, exceptionName, isModule), null, FsSpan.Of(exception.range), [], []));
                    break;

                case SynModuleDecl.Let let when isModule:
                    foreach (var binding in let.bindings)
                    {
                        var span = LetSpan(binding, let.bindings, let.range);
                        spans.Add(span);
                        // A let without a name (let () = ...) runs when the module initializes.
                        var names = BindingNames(binding.headPat);
                        var identity = names.Count > 0 ? string.Join(',', names) : ".cctor()";
                        var isFunction = binding.headPat is SynPat.LongIdent { argPats: SynArgPats.Pats { pats.IsEmpty: false } };
                        Add(result, $"{typeName}::{identity}", new FsDeclaration(typeName, runtimeName, identity, span, [], AttributeSpans(binding.attributes))
                        {
                            IsInline = binding.isInline,
                            IsPrivate = Accessibility(binding.headPat) is { IsPrivate: true },
                            // Module members are reached through an open, without the module's name.
                            IsStatic = false,
                            // A value is computed when the module initializes, whoever reads it first.
                            MayBeCalledImplicitly = !isFunction || !binding.attributes.IsEmpty,
                            HasTestAttribute = HasTest(binding.attributes),
                        });
                    }

                    break;

                case SynModuleDecl.Expr expression when isModule:
                    // A do in a module runs when the module initializes.
                    var doSpan = FsSpan.Of(expression.range);
                    spans.Add(doSpan);
                    Add(result, $"{typeName}::.cctor()", new FsDeclaration(typeName, runtimeName, ".cctor()", doSpan, [], []) { MayBeCalledImplicitly = true });
                    break;
            }
        }

        return spans;
    }

    /// <summary>Adds a module as a type, and its contents.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="decls">The module's declarations.</param>
    /// <param name="typeName">Its qualified name.</param>
    /// <param name="runtimeName">Its runtime name.</param>
    /// <param name="span">Where it is written.</param>
    /// <param name="attributes">Its attributes.</param>
    /// <param name="result">Collected declarations.</param>
    private static void AddModule(string[] lines, FSharpList<SynModuleDecl> decls, string typeName, string runtimeName, FsSpan span, FSharpList<SynAttributeList> attributes, Dictionary<string, FsDeclaration> result)
    {
        var inner = VisitContainer(lines, decls, typeName, runtimeName, isModule: true, result);
        var hasTests = decls.OfType<SynModuleDecl.Let>().SelectMany(static l => l.bindings).Any(static b => HasTest(b.attributes));
        Add(result, typeName, new FsDeclaration(typeName, runtimeName, null, span, inner, AttributeSpans(attributes))
        {
            IsModule = true,
            IsRunnableClass = true,
            HasTestMethods = hasTests,
        });
    }

    /// <summary>Adds a type and its members.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="type">Type definition.</param>
    /// <param name="span">Where it is written, from its keyword or attributes.</param>
    /// <param name="containerName">Qualified name of the namespace or module it is in.</param>
    /// <param name="containerRuntimeName">Runtime name of that container.</param>
    /// <param name="inModule">True when the container is a module (the type is nested in its class).</param>
    /// <param name="result">Collected declarations.</param>
    private static void AddType(string[] lines, SynTypeDefn type, FsSpan span, string containerName, string containerRuntimeName, bool inModule, Dictionary<string, FsDeclaration> result)
    {
        var info = type.typeInfo;
        var arity = info.typeParams?.Value.TyparDecls.Length ?? 0;
        var name = LastName(info.longId) + (arity > 0 ? $"`{arity}" : string.Empty);
        var typeName = Qualify(containerName, name);
        var runtimeName = Nest(containerRuntimeName, name, inModule);
        var members = new List<SynMemberDefn>(type.members);
        if (type.typeRepr is SynTypeDefnRepr.ObjectModel objectModel)
        {
            members.AddRange(objectModel.members);
        }

        if (type.implicitConstructor?.Value is { } implicitConstructor && !members.Contains(implicitConstructor))
        {
            members.Add(implicitConstructor);
        }

        var memberSpans = new List<FsSpan>();
        var bases = new List<string>();
        var hasTests = false;
        foreach (var member in members)
        {
            hasTests |= AddMember(lines, member, typeName, runtimeName, result, memberSpans, bases, implementsInterface: false);
        }

        var isAbstract = info.attributes.SelectMany(static l => l.Attributes).Any(static a => LastName(a.TypeName.LongIdent) is "AbstractClass" or "AbstractClassAttribute");
        var isClass = type.typeRepr is SynTypeDefnRepr.ObjectModel { kind: var kind } && (kind.IsClass || kind.IsUnspecified);
        Add(result, typeName, new FsDeclaration(typeName, runtimeName, null, span, memberSpans, AttributeSpans(info.attributes))
        {
            IsRunnableClass = isClass && !isAbstract,
            HasTestMethods = hasTests,
            BaseTypeNames = bases,
        });
    }

    /// <summary>Adds one member of a type; returns true when it has a test attribute.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="member">The member definition.</param>
    /// <param name="typeName">Qualified name of the type.</param>
    /// <param name="runtimeName">Runtime name of the type.</param>
    /// <param name="result">Collected declarations.</param>
    /// <param name="spans">Spans of the type's members; updated in place.</param>
    /// <param name="bases">Simple names of inherited and implemented types; updated in place.</param>
    /// <param name="implementsInterface">True for a member of an <c>interface ... with</c> block.</param>
    private static bool AddMember(string[] lines, SynMemberDefn member, string typeName, string runtimeName, Dictionary<string, FsDeclaration> result, List<FsSpan> spans, List<string> bases, bool implementsInterface)
    {
        switch (member)
        {
            case SynMemberDefn.Member { memberDefn: var binding }:
                var flags = binding.valData.memberFlags?.Value;
                var memberName = MemberName(lines, binding, flags);
                if (memberName is null)
                {
                    return false;
                }

                var span = FsSpan.Of(member.Range).With(binding.attributes);
                spans.Add(span);
                Add(result, $"{typeName}::{memberName}", new FsDeclaration(typeName, runtimeName, memberName, span, [], AttributeSpans(binding.attributes))
                {
                    IsInline = binding.isInline,
                    IsPrivate = Accessibility(binding.headPat) is { IsPrivate: true },
                    IsStatic = flags is { IsInstance: false } && !memberName.StartsWith('.'),
                    MayBeCalledImplicitly = implementsInterface || flags is { IsOverrideOrExplicitImpl: true } || !binding.attributes.IsEmpty,
                    HasTestAttribute = HasTest(binding.attributes),
                });
                return HasTest(binding.attributes);

            case SynMemberDefn.GetSetMember getSet:
                var accessor = getSet.memberDefnForGet?.Value ?? getSet.memberDefnForSet?.Value;
                if (accessor is null || MemberName(lines, accessor, accessor.valData.memberFlags?.Value) is not { } propertyName)
                {
                    return false;
                }

                var propertySpan = FsSpan.Of(getSet.range);
                spans.Add(propertySpan);
                Add(result, $"{typeName}::{propertyName}", new FsDeclaration(typeName, runtimeName, propertyName, propertySpan, [], AttributeSpans(accessor.attributes))
                {
                    IsStatic = accessor.valData.memberFlags?.Value is { IsInstance: false },
                    MayBeCalledImplicitly = implementsInterface || accessor.valData.memberFlags?.Value is { IsOverrideOrExplicitImpl: true } || !accessor.attributes.IsEmpty,
                });
                return false;

            case SynMemberDefn.AutoProperty property:
                var autoSpan = FsSpan.Of(property.range);
                spans.Add(autoSpan);
                Add(result, $"{typeName}::{property.ident.idText}", new FsDeclaration(typeName, runtimeName, property.ident.idText, autoSpan, [], AttributeSpans(property.attributes))
                {
                    IsStatic = property.isStatic,
                    // Its initializer runs in the constructor (or type initializer), whoever reads it.
                    MayBeCalledImplicitly = true,
                });
                return false;

            case SynMemberDefn.AbstractSlot slot:
                var slotSpan = FsSpan.Of(slot.range);
                spans.Add(slotSpan);
                Add(result, $"{typeName}::{slot.slotSig.ident.ident.idText}", new FsDeclaration(typeName, runtimeName, slot.slotSig.ident.ident.idText, slotSpan, [], []) { MayBeCalledImplicitly = true });
                return false;

            case SynMemberDefn.ImplicitCtor constructor:
                // Its arguments: the type's name before them is the type's, not something the constructor names.
                var constructorSpan = FsSpan.Of(constructor.ctorArgs.Range).With(constructor.attributes);
                spans.Add(constructorSpan);
                Add(result, $"{typeName}::.ctor()", new FsDeclaration(typeName, runtimeName, ".ctor()", constructorSpan, [], AttributeSpans(constructor.attributes)) { MayBeCalledImplicitly = true });
                return false;

            case SynMemberDefn.LetBindings lets:
                // Class lets and dos run in the constructor (static ones in the type initializer).
                foreach (var binding in lets.bindings)
                {
                    var bindingSpan = LetSpan(binding, lets.bindings, lets.range);
                    spans.Add(bindingSpan);
                    var names = BindingNames(binding.headPat);
                    var identity = names.Count > 0 ? string.Join(',', names) : lets.isStatic ? ".cctor()" : ".ctor()";
                    Add(result, $"{typeName}::{identity}", new FsDeclaration(typeName, runtimeName, identity, bindingSpan, [], AttributeSpans(binding.attributes))
                    {
                        IsPrivate = true,
                        MayBeCalledImplicitly = binding.headPat is not SynPat.LongIdent { argPats: SynArgPats.Pats { pats.IsEmpty: false } },
                    });
                }

                return false;

            case SynMemberDefn.Interface @interface:
                if (Rightmost(@interface.interfaceType) is { } interfaceName)
                {
                    bases.Add(interfaceName);
                }

                var tests = false;
                foreach (var inner in @interface.members?.Value ?? [])
                {
                    tests |= AddMember(lines, inner, typeName, runtimeName, result, spans, bases, implementsInterface: true);
                }

                return tests;

            case SynMemberDefn.ImplicitInherit inherit when Rightmost(inherit.inheritType) is { } inheritName:
                bases.Add(inheritName);
                return false;

            case SynMemberDefn.Inherit inherit when inherit.baseType?.Value is { } baseType && Rightmost(baseType) is { } baseName:
                bases.Add(baseName);
                return false;

            case SynMemberDefn.ValField field when field.fieldInfo.idOpt?.Value is { } fieldName:
                var fieldSpan = FsSpan.Of(field.range);
                spans.Add(fieldSpan);
                Add(result, $"{typeName}::{fieldName.idText}", new FsDeclaration(typeName, runtimeName, fieldName.idText, fieldSpan, [], []));
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// A member's identity: <c>.ctor(...)</c> for a constructor, the name for a property or a member
    /// without arguments, otherwise the name and its argument patterns (overloads differ there).
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="binding">The member binding.</param>
    /// <param name="flags">Its member flags.</param>
    private static string? MemberName(string[] lines, SynBinding binding, SynMemberFlags? flags)
    {
        if (binding.headPat is not SynPat.LongIdent longIdent || LastName(longIdent.longDotId.LongIdent) is not { } last)
        {
            return binding.headPat is SynPat.Named named ? named.ident.ident.idText : null;
        }

        var arguments = longIdent.argPats is SynArgPats.Pats pats ? string.Concat(pats.pats.Select(p => Parenthesized(Compact(Text(lines, p.Range))))) : string.Empty;
        if (flags is not null && flags.MemberKind.IsConstructor)
        {
            return arguments.Length == 0 ? ".ctor()" : ".ctor" + arguments;
        }

        var property = flags is not null && (flags.MemberKind.IsPropertyGet || flags.MemberKind.IsPropertySet || flags.MemberKind.IsPropertyGetSet);
        return property || arguments.Length == 0 ? last : last + arguments;
    }

    /// <summary>
    /// Where a <c>let</c> binding is written: the whole declaration (keyword, attributes, modifiers) when
    /// it is alone, else from the keyword for the first binding and the binding itself for the others.
    /// </summary>
    /// <param name="binding">One binding.</param>
    /// <param name="bindings">The declaration's bindings.</param>
    /// <param name="declaration">The declaration's range.</param>
    private static FsSpan LetSpan(SynBinding binding, FSharpList<SynBinding> bindings, FsRangeType declaration)
    {
        var own = FsSpan.Of(binding.RangeOfBindingWithRhs).With(binding.attributes);
        if (bindings.Length == 1)
        {
            return FsSpan.Of(declaration).With(binding.attributes);
        }

        return binding == bindings.Head ? own with { StartLine = declaration.StartLine, StartColumn = declaration.StartColumn } : own;
    }

    /// <summary>The names a <c>let</c> pattern binds: one for a function or value, several for a tuple.</summary>
    /// <param name="pattern">The binding's pattern.</param>
    private static List<string> BindingNames(SynPat pattern) => pattern switch
    {
        SynPat.LongIdent longIdent when LastName(longIdent.longDotId.LongIdent) is { } last => [last],
        SynPat.Named named => [named.ident.ident.idText],
        SynPat.Tuple tuple => [.. tuple.elementPats.SelectMany(BindingNames)],
        SynPat.Paren paren => BindingNames(paren.pat),
        SynPat.Typed typed => BindingNames(typed.pat),
        SynPat.Attrib attributed => BindingNames(attributed.pat),
        _ => [],
    };

    /// <summary>The accessibility written on a binding's pattern.</summary>
    /// <param name="pattern">The pattern.</param>
    private static SynAccess? Accessibility(SynPat pattern) => pattern switch
    {
        SynPat.LongIdent longIdent => longIdent.accessibility?.Value,
        SynPat.Named named => named.accessibility?.Value,
        _ => null,
    };

    /// <summary>The last identifier of a type, for base names.</summary>
    /// <param name="type">A type.</param>
    private static string? Rightmost(SynType type) => type switch
    {
        SynType.LongIdent longIdent => LastName(longIdent.longDotId.LongIdent),
        SynType.App app => Rightmost(app.typeName),
        _ => null,
    };

    /// <summary>True when an attribute is a test attribute.</summary>
    /// <param name="attributes">Attribute lists.</param>
    private static bool HasTest(FSharpList<SynAttributeList> attributes) =>
        attributes.SelectMany(static l => l.Attributes).Any(static a => LastName(a.TypeName.LongIdent) is { } name && IsTestAttribute(name));

    /// <summary>True for <c>[&lt;CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)&gt;]</c>.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="attributes">A module's attributes.</param>
    private static bool HasModuleSuffix(string[] lines, FSharpList<SynAttributeList> attributes) =>
        attributes.SelectMany(static l => l.Attributes)
            .Any(a => (LastName(a.TypeName.LongIdent) ?? string.Empty).StartsWith("CompilationRepresentation", StringComparison.Ordinal)
                      && Text(lines, a.Range).Contains("ModuleSuffix", StringComparison.Ordinal));

    /// <summary>The source text of a range.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="range">A range of the file.</param>
    private static string Text(string[] lines, FsRangeType range)
    {
        var builder = new System.Text.StringBuilder();
        for (var line = range.StartLine; line <= range.EndLine && line <= lines.Length; line++)
        {
            var text = lines[line - 1].TrimEnd('\r');
            var start = line == range.StartLine ? Math.Min(range.StartColumn, text.Length) : 0;
            var end = line == range.EndLine ? Math.Min(range.EndColumn, text.Length) : text.Length;
            builder.Append(text, start, Math.Max(0, end - start)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The spans of attribute lists.</summary>
    /// <param name="attributes">Attribute lists.</param>
    private static List<FsSpan> AttributeSpans(FSharpList<SynAttributeList> attributes) =>
        [.. attributes.Select(static l => FsSpan.Of(l.Range))];

    /// <summary>Adds a declaration, numbering duplicates.</summary>
    /// <param name="result">Collected declarations.</param>
    /// <param name="key">Identity.</param>
    /// <param name="declaration">Declaration.</param>
    private static void Add(Dictionary<string, FsDeclaration> result, string key, FsDeclaration declaration)
    {
        var unique = key;
        for (var n = 2; !result.TryAdd(unique, declaration); n++)
        {
            unique = $"{key}#{n}";
        }
    }

    /// <summary>The last identifier of a dotted name, or null when it is empty.</summary>
    /// <param name="identifiers">A long identifier.</param>
    private static string? LastName(FSharpList<FSharp.Compiler.Syntax.Ident> identifiers) =>
        identifiers.IsEmpty ? null : identifiers[^1].idText;

    /// <summary>An argument pattern in parentheses: <c>(a:int,b:int)</c> stays, <c>x</c> becomes <c>(x)</c>.</summary>
    /// <param name="pattern">Compacted pattern text.</param>
    private static string Parenthesized(string pattern) =>
        pattern.StartsWith('(') && pattern.EndsWith(')') ? pattern : $"({pattern})";

    /// <summary>Text without whitespace.</summary>
    /// <param name="text">Text.</param>
    private static string Compact(string text) => string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));

    /// <summary>Joins a container and a name with '.'.</summary>
    /// <param name="prefix">Container; empty for the global namespace.</param>
    /// <param name="name">Name.</param>
    private static string Qualify(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";

    /// <summary>A runtime name: '+' after a module (a type nested in its class), '.' after a namespace.</summary>
    /// <param name="prefix">Container's runtime name.</param>
    /// <param name="name">Name.</param>
    /// <param name="nested">True when the container is a type (a module).</param>
    private static string Nest(string prefix, string name, bool nested) => prefix.Length == 0 ? name : $"{prefix}{(nested ? '+' : '.')}{name}";

    /// <summary>A <c>#if</c> or <c>#elif</c> line.</summary>
    [GeneratedRegex(@"^\s*#(if|elif)\b(?<condition>.*)$", RegexOptions.Multiline)]
    private static partial Regex ConditionalLine();

    /// <summary>An identifier in a condition.</summary>
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();
}
