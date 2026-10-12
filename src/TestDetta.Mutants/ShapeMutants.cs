using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using TestDetta.Core;
using TestDetta.Coverage;

namespace TestDetta.Mutants;

/// <summary>A declaration a shape kind can change, and the source edit that changes it.</summary>
/// <param name="Kind"><see cref="MutantKind.AttributeRemoved"/> or <see cref="MutantKind.Unsealed"/>.</param>
/// <param name="Edits">The source edits: the attribute's text, or every <c>sealed</c> of the type's declarations, taken out.</param>
/// <param name="Owner">The member whose coverage stands for the declaration's; null for a type, which any of its members stands for.</param>
/// <param name="IsField">True when <paramref name="Owner"/> is a field, whose declaration a constructor runs.</param>
internal sealed record ShapeCandidate(MutantKind Kind, IReadOnlyList<SourceEdit> Edits, CoveredMethod? Owner, bool IsField);

/// <summary>
/// The shape kinds (docs/evaluation.md, "Shape mutants"): a change to a declaration's metadata, not to its code, so the
/// evaluation can show misses of tests that read a type's shape through reflection, which mutants of code never fail.
/// The sample names a target as a member of its type: <c>Type.&lt;sealed&gt;</c> for <see cref="MutantKind.Unsealed"/>,
/// <c>Type.[Name]</c> and <c>Type.Member[Name]</c> for <see cref="MutantKind.AttributeRemoved"/> on the type or one of its
/// members, <c>Name</c> being the attribute's short name without <c>Attribute</c>. The source edit is the real change:
/// the edited files are only parsed by TestDetta, never compiled, and the mutant makes the same change in the compiled
/// metadata (<see cref="Apply"/>). C# only: F# and Visual Basic declarations are left out.
/// </summary>
internal static class ShapeMutants
{
    /// <summary>Member name of an <see cref="MutantKind.Unsealed"/> target.</summary>
    public const string SealedMember = "<sealed>";

    /// <summary>
    /// Attributes left out, by short name: ones the compiler stores as flags rather than as attributes (pseudo-attributes
    /// such as <c>Serializable</c> or <c>StructLayout</c>, which no metadata edit can take away), and ones only the
    /// compiler, analyzers, the debugger, trimming or code coverage read, which no test of the code's behaviour sees.
    /// </summary>
    private static readonly FrozenSet<string> Ignored = FrozenSet.Create(
        StringComparer.Ordinal,
        "Serializable", "NonSerialized", "StructLayout", "FieldOffset", "DllImport", "LibraryImport", "MethodImpl", "MarshalAs",
        "In", "Out", "Optional", "PreserveSig", "ComImport", "SpecialName", "SkipLocalsInit", "ModuleInitializer",
        "CompilerGenerated", "Nullable", "NullableContext", "NullablePublicOnly", "IsReadOnly", "Extension",
        "AsyncStateMachine", "IteratorStateMachine", "InterpolatedStringHandler", "CallerMemberName",
        "CallerFilePath", "CallerLineNumber", "CallerArgumentExpression", "EnumeratorCancellation", "SetsRequiredMembers",
        "OverloadResolutionPriority", "UnscopedRef", "Experimental", "CollectionBuilder", "InlineArray",
        "DebuggerStepThrough", "DebuggerHidden", "DebuggerNonUserCode", "DebuggerBrowsable", "DebuggerDisplay",
        "DebuggerTypeProxy", "ExcludeFromCodeCoverage", "GeneratedCode", "SuppressMessage", "UnconditionalSuppressMessage",
        "RequiresUnreferencedCode", "RequiresDynamicCode", "RequiresAssemblyFiles", "DynamicallyAccessedMembers",
        "DynamicDependency", "MaybeNull", "MaybeNullWhen", "NotNull", "NotNullWhen", "NotNullIfNotNull", "AllowNull",
        "DisallowNull", "DoesNotReturn", "DoesNotReturnIf", "MemberNotNull", "MemberNotNullWhen", "StringSyntax",
        "ConstantExpected", "Pure", "MustUseReturnValue", "UsedImplicitly", "PublicAPI", "CanBeNull",
        "TargetedPatchingOptOut", "SecuritySafeCritical", "SecurityCritical", "AllowPartiallyTrustedCallers");

    /// <summary>
    /// Every declaration of the tested library files that a kind in <paramref name="kinds"/> can change, by its target name.
    /// A type is unsealed when one of its declarations says <c>sealed</c> (and none <c>static</c>); a type or member keeps
    /// its first attribute that is not <see cref="Ignored"/> and has no target (<c>[return: …]</c>). Overloaded members
    /// are left out: the compiled metadata names a member by name alone, so the mutant could not tell which one the edit
    /// changed.
    /// </summary>
    /// <param name="files">Source files of tested library code.</param>
    /// <param name="graph">Projects of the repository, for each file's preprocessor symbols.</param>
    /// <param name="kinds">The shape kinds asked for.</param>
    /// <param name="framework">Target framework of the run, or null.</param>
    public static Dictionary<CoveredMethod, ShapeCandidate> Candidates(IEnumerable<string> files, ProjectGraph graph, IReadOnlySet<MutantKind> kinds, string? framework)
    {
        var candidates = new Dictionary<CoveredMethod, ShapeCandidate>();
        var unsealed = new Dictionary<string, List<SourceEdit>>(StringComparer.Ordinal);
        var notSealable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = DeclarationWalker.Parse(SourceText.From(File.ReadAllText(file)), TargetSampler.Symbols(graph, file, framework));
            var declarations = DeclarationWalker.Collect(root).Values.ToList();
            var overloaded = Overloaded(declarations);
            foreach (var declaration in declarations)
            {
                if (declaration.Node is not MemberDeclarationSyntax node)
                {
                    continue;
                }

                if (declaration.MemberName is null && node is TypeDeclarationSyntax type)
                {
                    NoteSealed(declaration.TypeName, file, type, unsealed, notSealable);
                }

                if (!kinds.Contains(MutantKind.AttributeRemoved) || Owner(declaration) is not { } owner || (owner.Member is { } member && overloaded.Contains(member)))
                {
                    continue;
                }

                if (FirstAttribute(node) is not { } attribute)
                {
                    continue;
                }

                var target = new CoveredMethod(declaration.TypeName, $"{owner.Member?.MemberName}[{ShortName(attribute)}]");
                candidates.TryAdd(target, new ShapeCandidate(MutantKind.AttributeRemoved, [Removal(file, attribute)], owner.Member, owner.IsField));
            }
        }

        if (kinds.Contains(MutantKind.Unsealed))
        {
            foreach (var (type, edits) in unsealed)
            {
                if (!notSealable.Contains(type))
                {
                    candidates[new CoveredMethod(type, SealedMember)] = new ShapeCandidate(MutantKind.Unsealed, edits, null, IsField: false);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// True when a test ran the declaration's code: the member (a field's constructor), or for a type any member of it.
    /// </summary>
    /// <param name="candidate">A shape candidate.</param>
    /// <param name="target">Its target name, whose type a type's candidate is.</param>
    /// <param name="covered">Members that ran in some test, class, fixture or start-up.</param>
    /// <param name="coveredTypes">The types of <paramref name="covered"/>; see <see cref="TypesOf"/>.</param>
    public static bool IsCovered(ShapeCandidate candidate, CoveredMethod target, IReadOnlySet<CoveredMethod> covered, IReadOnlySet<string> coveredTypes) =>
        candidate.Owner is not { } owner ? coveredTypes.Contains(target.TypeName)
        : candidate.IsField ? covered.Contains(new CoveredMethod(owner.TypeName, ".ctor")) || covered.Contains(new CoveredMethod(owner.TypeName, ".cctor"))
        : covered.Contains(owner);

    /// <summary>The type names of <paramref name="covered"/>, so a type's coverage is one lookup.</summary>
    /// <param name="covered">Members that ran in some test, class, fixture or start-up.</param>
    public static HashSet<string> TypesOf(IReadOnlySet<CoveredMethod> covered) => covered.Select(static c => c.TypeName).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Makes the change of <paramref name="target"/> in the compiled <paramref name="type"/> when it is the target's type:
    /// clears <c>sealed</c>, or removes the first custom attribute of the declaration with the target's attribute name.
    /// </summary>
    /// <param name="type">A type of the module being rewritten.</param>
    /// <param name="target">A shape mutant.</param>
    /// <returns>True when the metadata changed.</returns>
    public static bool Apply(TypeDefinition type, SchemaTarget target)
    {
        if (MethodNames.TypeName(type) != target.Member.TypeName)
        {
            return false;
        }

        if (target.Kind == MutantKind.Unsealed)
        {
            if (!type.IsSealed || type.IsAbstract)
            {
                return false;
            }

            type.IsSealed = false;
            return true;
        }

        var name = target.Member.MemberName;
        var open = name.LastIndexOf('[');
        if (target.Kind != MutantKind.AttributeRemoved || open < 0 || !name.EndsWith(']'))
        {
            return false;
        }

        var attributeName = name[(open + 1)..^1];
        foreach (var provider in Providers(type, name[..open]))
        {
            var attribute = provider.CustomAttributes.FirstOrDefault(a => ShortName(a.AttributeType.Name) == attributeName);
            if (attribute is not null)
            {
                provider.CustomAttributes.Remove(attribute);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The compiled declarations named <paramref name="member"/> in <paramref name="type"/>: the type itself for an empty
    /// name, else its properties, events, fields and methods (accessors aside) with that source name.
    /// </summary>
    /// <param name="type">A compiled type.</param>
    /// <param name="member">The member's source name, as <see cref="MethodNames.Map"/> gives it; empty for the type.</param>
    private static IEnumerable<ICustomAttributeProvider> Providers(TypeDefinition type, string member)
    {
        if (member.Length == 0)
        {
            yield return type;
            yield break;
        }

        foreach (var property in type.Properties.Where(p => p.Name == member))
        {
            yield return property;
        }

        foreach (var @event in type.Events.Where(e => e.Name == member))
        {
            yield return @event;
        }

        foreach (var field in type.Fields.Where(f => f.Name == member))
        {
            yield return field;
        }

        foreach (var method in type.Methods.Where(m => !m.IsGetter && !m.IsSetter && !m.IsAddOn && !m.IsRemoveOn && MethodNames.Map(m).MemberName == member))
        {
            yield return method;
        }
    }

    /// <summary>
    /// Notes a type declaration's <c>sealed</c> for <see cref="MutantKind.Unsealed"/>: an edit per declaration that says it.
    /// A <c>static</c> class compiles to <c>abstract sealed</c> and a struct is sealed without the keyword; neither is taken.
    /// </summary>
    /// <param name="typeName">The type's name.</param>
    /// <param name="file">Normalized file path.</param>
    /// <param name="type">One declaration of the type (a partial type has several).</param>
    /// <param name="unsealed">Edits per sealed type; updated in place.</param>
    /// <param name="notSealable">Types left out; updated in place.</param>
    private static void NoteSealed(string typeName, string file, TypeDeclarationSyntax type, Dictionary<string, List<SourceEdit>> unsealed, HashSet<string> notSealable)
    {
        if (type is not (ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: not (int)SyntaxKind.StructKeyword }))
        {
            return;
        }

        if (type.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            notSealable.Add(typeName);
            return;
        }

        var sealedKeyword = type.Modifiers.FirstOrDefault(static m => m.IsKind(SyntaxKind.SealedKeyword));
        if (!sealedKeyword.IsKind(SyntaxKind.SealedKeyword))
        {
            return;
        }

        if (!unsealed.TryGetValue(typeName, out var edits))
        {
            edits = [];
            unsealed[typeName] = edits;
        }

        edits.Add(new SourceEdit(file, sealedKeyword.SpanStart, string.Empty) { Replaces = sealedKeyword.Span.Length });
    }

    /// <summary>
    /// What the attributes of a declaration belong to in the compiled metadata: the type (null member) or a member and
    /// whether it is a field; null for declarations no shape kind changes (operators, indexers, finalizers, enum members,
    /// delegates, fields declared several to a line).
    /// </summary>
    /// <param name="declaration">A declaration.</param>
    private static (CoveredMethod? Member, bool IsField)? Owner(Declaration declaration) => declaration.Node switch
    {
        BaseTypeDeclarationSyntax when declaration.MemberName is null => (null, false),
        FieldDeclarationSyntax { Declaration.Variables: [var field] } => (new CoveredMethod(declaration.TypeName, field.Identifier.ValueText), true),
        EventFieldDeclarationSyntax { Declaration.Variables: [var @event] } => (new CoveredMethod(declaration.TypeName, @event.Identifier.ValueText), false),
        _ => TargetSampler.MemberKey(declaration) is { } member ? (member, false) : null,
    };

    /// <summary>Members of a file declared more than once in their type (overloads), whose metadata the name cannot tell apart.</summary>
    /// <param name="declarations">The file's declarations.</param>
    private static HashSet<CoveredMethod> Overloaded(List<Declaration> declarations)
    {
        var seen = new HashSet<CoveredMethod>();
        var overloaded = new HashSet<CoveredMethod>();
        foreach (var declaration in declarations)
        {
            if (declaration.MemberName is not null && Owner(declaration) is { Member: { } member } && !seen.Add(member))
            {
                overloaded.Add(member);
            }
        }

        return overloaded;
    }

    /// <summary>The first attribute of a declaration that a shape mutant removes: in a list without a target, not <see cref="Ignored"/>.</summary>
    /// <param name="node">A type or member declaration.</param>
    private static AttributeSyntax? FirstAttribute(MemberDeclarationSyntax node) =>
        node.AttributeLists
            .Where(static l => l.Target is null)
            .SelectMany(static l => l.Attributes)
            .FirstOrDefault(static a => !Ignored.Contains(ShortName(a)));

    /// <summary>
    /// The edit that takes <paramref name="attribute"/> out: its whole list when it is alone there, else the attribute and
    /// the comma next to it.
    /// </summary>
    /// <param name="file">Normalized file path.</param>
    /// <param name="attribute">The attribute.</param>
    private static SourceEdit Removal(string file, AttributeSyntax attribute)
    {
        var list = (AttributeListSyntax)attribute.Parent!;
        var attributes = list.Attributes;
        var index = attributes.IndexOf(attribute);
        var span = attributes.Count == 1 ? list.Span
            : index < attributes.Count - 1 ? TextSpan.FromBounds(attribute.SpanStart, attributes[index + 1].SpanStart)
            : TextSpan.FromBounds(attributes[index - 1].Span.End, attribute.Span.End);
        return new SourceEdit(file, span.Start, string.Empty) { Replaces = span.Length };
    }

    /// <summary>An attribute's name as source may write it, without namespace, generic arguments or the <c>Attribute</c> suffix.</summary>
    /// <param name="attribute">An attribute in source.</param>
    private static string ShortName(AttributeSyntax attribute) => ShortName(attribute.Name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => attribute.Name.ToString(),
    });

    /// <summary>A type name without generic arity (<c>`1</c>) and without the <c>Attribute</c> suffix.</summary>
    /// <param name="name">A source or metadata type name.</param>
    private static string ShortName(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }
}
