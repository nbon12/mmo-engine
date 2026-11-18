using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MmoEngine.SimCore.Analyzers;

/// <summary>
/// The determinism analyzer attached to the shared simulation core only. It enforces the rules of
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md (MMO1001 to MMO1009), on operations,
/// on member declarations and on preprocessor directives, in generated code too.
/// </summary>
/// <remarks>
/// <para>
/// Each operation reports at most one rule, the first it matches in the contract's precedence order
/// (MMO1003 &gt; MMO1001 &gt; MMO1002 &gt; MMO1008 &gt; MMO1005 &gt; MMO1006 &gt; MMO1004 &gt; MMO1009
/// &gt; MMO1007), and is skipped when an enclosing operation in the same statement, or the field,
/// property or parameter initializer it belongs to, matches the same rule. Declarations report
/// separately from the operations inside them. An operation under an attribute application reports
/// nothing: attribute arguments are metadata, never run by a step.
/// </para>
/// <para>
/// Matching compares original definitions by metadata name (<see cref="Tables"/>), which catches
/// generic math (<c>T.Cos</c> resolves to an interface member in System.Numerics) and does not depend on
/// <c>GetTypeByMetadataName</c>, which returns null for an ambiguous name.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The operations the rules inspect (contracts/analyzer-rules.md#wiring).</summary>
    private static readonly ImmutableArray<OperationKind> OperationKinds = ImmutableArray.Create(
        OperationKind.Invocation,
        OperationKind.PropertyReference,
        OperationKind.FieldReference,
        OperationKind.MethodReference,
        OperationKind.EventReference,
        OperationKind.ObjectCreation,
        OperationKind.Lock,
        OperationKind.Loop,
        OperationKind.Conversion,
        OperationKind.Spread,
        OperationKind.VariableDeclarator,
        OperationKind.Binary,
        OperationKind.CompoundAssignment,
        OperationKind.Unary,
        OperationKind.TypeOf,
        OperationKind.DefaultValue,
        OperationKind.DynamicInvocation,
        OperationKind.DynamicMemberReference,
        OperationKind.DynamicIndexerAccess,
        OperationKind.DynamicObjectCreation);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterOperationAction(AnalyzeOperation, OperationKinds);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.Event);
        context.RegisterSyntaxTreeAction(AnalyzeSyntaxTree);
    }

    /// <summary>
    /// The first rule <paramref name="operation"/> matches, in precedence order, as its ID, or null.
    /// Pure: it reads the operation and its symbols only.
    /// </summary>
    private static string? Classify(IOperation operation) => Find(operation)?.Descriptor.Id;

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        if (IsUnderAttribute(operation) || Find(operation) is not { } finding || EnclosingOperationMatches(operation, finding.Descriptor.Id))
        {
            return;
        }

        context.ReportDiagnostic(finding.ToDiagnostic());
    }

    /// <summary>The member declarations' types: fields (not constants), properties, events, return types and parameters.</summary>
    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared)
        {
            return;
        }

        switch (symbol)
        {
            case IFieldSymbol { IsConst: false } field:
                ReportDeclaration(context, field.Type, field);
                break;
            case IPropertySymbol property:
                ReportDeclaration(context, property.Type, property);
                ReportParameters(context, property.Parameters);
                break;
            case IEventSymbol @event:
                ReportDeclaration(context, @event.Type, @event);
                break;
            case IMethodSymbol method when !IsAccessor(method):
                ReportDeclaration(context, method.ReturnType, method);
                ReportParameters(context, method.Parameters);
                break;
        }
    }

    /// <summary>The preprocessor directives of one tree. No rule reports from here yet; the <c>#if</c> and <c>#elif</c> rule (MMO1008) does.</summary>
    private static void AnalyzeSyntaxTree(SyntaxTreeAnalysisContext context)
    {
    }

    private static void ReportParameters(SymbolAnalysisContext context, ImmutableArray<IParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            ReportDeclaration(context, parameter.Type, parameter);
        }
    }

    private static void ReportDeclaration(SymbolAnalysisContext context, ITypeSymbol type, ISymbol declared)
    {
        if (Declaration(type, declared.Name, DeclaredLocation(declared)) is { } finding)
        {
            context.ReportDiagnostic(finding.ToDiagnostic());
        }
    }

    /// <summary>Property and event accessors are reported on their property or event.</summary>
    private static bool IsAccessor(IMethodSymbol method) =>
        method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise;

    /// <summary>True when an ancestor of <paramref name="operation"/> is an attribute application (contracts/analyzer-rules.md#attribute-applications).</summary>
    private static bool IsUnderAttribute(IOperation operation)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IAttributeOperation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an enclosing operation in the same statement, or the initializer
    /// <paramref name="operation"/> belongs to, matches rule <paramref name="id"/>: that operation's
    /// diagnostic covers this one. The walk stops after the nearest enclosing statement.
    /// </summary>
    private static bool EnclosingOperationMatches(IOperation operation, string id)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (string.Equals(Classify(parent) ?? InitializerRule(parent), id, StringComparison.Ordinal))
            {
                return true;
            }

            if (parent.Syntax is StatementSyntax)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>A field, property or parameter initializer counts as its declaration: the rule its declared type matches.</summary>
    private static string? InitializerRule(IOperation operation)
    {
        var declared = operation switch
        {
            IFieldInitializerOperation { InitializedFields.Length: > 0 } field when !field.InitializedFields[0].IsConst => field.InitializedFields[0].Type,
            IPropertyInitializerOperation { InitializedProperties.Length: > 0 } property => property.InitializedProperties[0].Type,
            IParameterInitializerOperation initializer => initializer.Parameter.Type,
            _ => null,
        };
        return declared is null ? null : Declaration(declared, string.Empty, Location.None)?.Descriptor.Id;
    }

    // ---- Classification -----------------------------------------------------------------------

    /// <summary>The first rule <paramref name="operation"/> matches, in the contract's precedence order, with its location and message arguments.</summary>
    private static Finding? Find(IOperation operation)
    {
        if (operation is IVariableDeclaratorOperation declarator)
        {
            var local = declarator.Symbol;
            return Declaration(local.Type, local.Name, DeclaredLocation(local));
        }

        return FusedMultiplyAdd(operation)
            ?? VectorOrIntrinsic(operation)
            ?? PlatformMath(operation)
            ?? HashOrderedEnumeration(operation);
    }

    /// <summary>The first rule a declaration of type <paramref name="type"/> matches, reported at the declared identifier.</summary>
    private static Finding? Declaration(ITypeSymbol type, string name, Location location) =>
        FindType(type, IsBannedVectorType) is { } vector
            ? new Finding(Descriptors.Mmo1001, location, name, vector.ToDisplayString())
            : null;

    /// <summary>
    /// MMO1003: a member named <c>FusedMultiply*</c> or <c>MultiplyAddEstimate</c> on any type, and every
    /// member of the x86 <c>Fma</c> classes.
    /// </summary>
    private static Finding? FusedMultiplyAdd(IOperation operation)
    {
        if (ReferencedMember(operation) is not { } member)
        {
            return null;
        }

        var name = member.Name;
        var fused = name.StartsWith(Tables.FusedPrefix, StringComparison.Ordinal)
            || Tables.FusedMembers.Contains(name)
            || (member.ContainingType is { } type && Tables.FmaTypes.Contains(Tables.FullName(type)));
        return fused ? new Finding(Descriptors.Mmo1003, operation.Syntax.GetLocation(), Display(member)) : null;
    }

    /// <summary>
    /// MMO1001: a System.Numerics vector type, or a System.Runtime.Intrinsics type or member outside the
    /// lane-wise <c>Vector128&lt;float&gt;</c> allowlist, wherever it appears: a referenced member's
    /// containing type, type arguments, return, parameter or value type, a user-defined operator, a
    /// conversion's target, a <c>default</c> value or a <c>typeof</c> operand.
    /// </summary>
    private static Finding? VectorOrIntrinsic(IOperation operation)
    {
        if ((ReferencedMember(operation) ?? OperatorMethod(operation)) is { } member)
        {
            return BannedVectorMember(member) is { } banned
                ? new Finding(Descriptors.Mmo1001, operation.Syntax.GetLocation(), Display(member), banned.ToDisplayString())
                : null;
        }

        var type = operation switch
        {
            ITypeOfOperation typeOf => typeOf.TypeOperand,
            IConversionOperation or IDefaultValueOperation or IObjectCreationOperation => operation.Type,
            _ => null,
        };
        return FindType(type, IsBannedVectorType) is { } found
            ? new Finding(Descriptors.Mmo1001, operation.Syntax.GetLocation(), operation.Syntax.ToString(), found.ToDisplayString())
            : null;
    }

    /// <summary>
    /// The banned vector or intrinsic type behind a member: its containing type when that is a
    /// System.Numerics vector type, or an intrinsics type and the member is not on the Vector128
    /// allowlist; otherwise the first banned type in its signature.
    /// </summary>
    private static ITypeSymbol? BannedVectorMember(ISymbol member)
    {
        if (member.ContainingType is not { } containing)
        {
            return null;
        }

        var name = Tables.FullName(containing);
        if (Tables.NumericsVectorTypes.Contains(name)
            || (name.StartsWith(Tables.IntrinsicsPrefix, StringComparison.Ordinal) && !Tables.Vector128Allowlist.Contains(Tables.MemberKey(member))))
        {
            return containing;
        }

        var found = FindType(containing, IsBannedVectorType);
        switch (member)
        {
            case IMethodSymbol method:
                foreach (var typeArgument in method.TypeArguments)
                {
                    found ??= FindType(typeArgument, IsBannedVectorType);
                }

                found ??= FindType(method.ReturnType, IsBannedVectorType);
                return found ?? FirstParameterType(method.Parameters, IsBannedVectorType);
            case IPropertySymbol property:
                return found ?? FindType(property.Type, IsBannedVectorType) ?? FirstParameterType(property.Parameters, IsBannedVectorType);
            case IFieldSymbol field:
                return found ?? FindType(field.Type, IsBannedVectorType);
            case IEventSymbol @event:
                return found ?? FindType(@event.Type, IsBannedVectorType);
            default:
                return found;
        }
    }

    /// <summary>
    /// A System.Numerics vector type, or any System.Runtime.Intrinsics type except
    /// <c>Vector128&lt;float&gt;</c> and the static <c>Vector128</c> class, whose members the allowlist
    /// decides.
    /// </summary>
    private static bool IsBannedVectorType(INamedTypeSymbol type)
    {
        var name = Tables.FullName(type);
        if (Tables.NumericsVectorTypes.Contains(name))
        {
            return true;
        }

        if (!name.StartsWith(Tables.IntrinsicsPrefix, StringComparison.Ordinal) || string.Equals(name, Tables.Vector128, StringComparison.Ordinal))
        {
            return false;
        }

        return !(string.Equals(name, Tables.Vector128OfT, StringComparison.Ordinal) && type.TypeArguments[0].SpecialType == SpecialType.System_Single);
    }

    /// <summary>
    /// MMO1002: a static method of a math host type or of a generic-math interface, as an invocation, a
    /// method group or a function pointer, unless allowlisted; and float <c>%</c> and <c>%=</c>.
    /// </summary>
    private static Finding? PlatformMath(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation invocation:
                return PlatformMathMethod(invocation.TargetMethod, operation);
            case IMethodReferenceOperation reference:
                return PlatformMathMethod(reference.Method, operation);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Remainder, OperatorMethod: null } binary when IsSingle(binary.Type):
                return new Finding(Descriptors.Mmo1002, operation.Syntax.GetLocation(), "%", Tables.MathFallback);
            case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Remainder, OperatorMethod: null } compound when IsSingle(compound.Type):
                return new Finding(Descriptors.Mmo1002, operation.Syntax.GetLocation(), "%=", Tables.MathFallback);
            default:
                return null;
        }
    }

    private static Finding? PlatformMathMethod(IMethodSymbol method, IOperation operation)
    {
        if (!method.IsStatic || method.ContainingType is not { } host)
        {
            return null;
        }

        var hostName = Tables.FullName(host);
        var isHost = Tables.MathHostTypes.Contains(hostName)
            || (host.TypeKind == TypeKind.Interface && host.ContainingNamespace?.ToDisplayString() == Tables.GenericMathNamespace);
        if (!isHost || IsAllowedMath(method, hostName, operation))
        {
            return null;
        }

        var replacement = Tables.MathReplacements.TryGetValue(method.Name, out var text) ? text : Tables.MathFallback;
        return new Finding(Descriptors.Mmo1002, operation.Syntax.GetLocation(), Display(method), replacement);
    }

    /// <summary>The math allowlist (contracts/analyzer-rules.md#math-allowlist-mmo1002).</summary>
    private static bool IsAllowedMath(IMethodSymbol method, string hostName, IOperation operation)
    {
        var key = hostName + "." + method.Name;
        var floatOperands = method.Parameters.Length > 0 && method.Parameters[0].Type.SpecialType == SpecialType.System_Single;
        if (floatOperands)
        {
            return Tables.FloatOperandMath.Contains(key)
                || Tables.RoundOverloads.Contains(method.OriginalDefinition.GetDocumentationCommentId() ?? string.Empty)
                || (string.Equals(hostName, Tables.PredicateHost, StringComparison.Ordinal)
                    && method.Name.StartsWith(Tables.PredicatePrefix, StringComparison.Ordinal)
                    && method.ReturnType.SpecialType == SpecialType.System_Boolean)
                || (Tables.SimMathOnlyMath.Contains(key) && IsInsideSimMath(operation));
        }

        return method.Parameters.Length > 0 && IsInteger(method.Parameters[0].Type) && Tables.IntegerOperandMath.Contains(key);
    }

    /// <summary>True when the innermost type containing <paramref name="operation"/> is <c>MmoEngine.SimCore.Math.SimMath</c>.</summary>
    private static bool IsInsideSimMath(IOperation operation)
    {
        var symbol = operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart);
        var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        return type is not null && string.Equals(Tables.FullName(type), Tables.SimMathType, StringComparison.Ordinal);
    }

    /// <summary>
    /// MMO1004: enumerating a hash-ordered collection by <c>foreach</c>, a <c>..</c> spread, an
    /// enumerating member (<c>GetEnumerator</c>, <c>CopyTo</c>, <c>ToArray</c>, or <c>Keys</c> and
    /// <c>Values</c> whose result is not hash-ordered), or a conversion to a type that is not
    /// hash-ordered, except into an order-insensitive sink of a hash-ordered type.
    /// </summary>
    private static Finding? HashOrderedEnumeration(IOperation operation)
    {
        switch (operation)
        {
            case IForEachLoopOperation loop:
                return HashOrderedSource(StripConversions(loop.Collection), "foreach");
            case ISpreadOperation spread:
                return HashOrderedSource(StripConversions(spread.Operand), "..");
            case IInvocationOperation { Instance.Type: { } receiver } invocation
                when Tables.EnumeratingMethods.Contains(invocation.TargetMethod.Name) && IsHashOrdered(receiver):
                return new Finding(Descriptors.Mmo1004, operation.Syntax.GetLocation(), Display(invocation.TargetMethod), receiver.ToDisplayString());
            case IPropertyReferenceOperation { Instance.Type: { } receiver } property
                when Tables.EnumeratingProperties.Contains(property.Property.Name) && IsHashOrdered(receiver) && !IsHashOrdered(property.Type):
                return new Finding(Descriptors.Mmo1004, operation.Syntax.GetLocation(), Display(property.Property), receiver.ToDisplayString());
            case IConversionOperation { Operand.Type: { } source } conversion
                when IsHashOrdered(source) && !IsHashOrdered(conversion.Type) && !IsOrderInsensitiveSink(conversion):
                return new Finding(Descriptors.Mmo1004, operation.Syntax.GetLocation(), operation.Syntax.ToString(), source.ToDisplayString());
            default:
                return null;
        }
    }

    /// <summary>The <c>foreach</c> or spread of <paramref name="collection"/>, reported at the collection expression, when its type is hash-ordered.</summary>
    private static Finding? HashOrderedSource(IOperation collection, string keyword) =>
        collection.Type is { } type && IsHashOrdered(type)
            ? new Finding(Descriptors.Mmo1004, collection.Syntax.GetLocation(), keyword, type.ToDisplayString())
            : null;

    /// <summary>An argument to a hash-ordered type's own method or constructor (<c>UnionWith</c>, a copy constructor), which does not depend on the order.</summary>
    private static bool IsOrderInsensitiveSink(IConversionOperation conversion) =>
        conversion.Parent is IArgumentOperation { Parent: var sink }
        && sink switch
        {
            IInvocationOperation invocation => IsHashOrdered(invocation.TargetMethod.ContainingType),
            IObjectCreationOperation creation => IsHashOrdered(creation.Type),
            _ => false,
        };

    /// <summary>
    /// True for a hash-ordered collection type (<see cref="Tables.HashOrderedTypes"/>) or a class
    /// derived from one. Interfaces count only as themselves.
    /// </summary>
    private static bool IsHashOrdered(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind is not (TypeKind.Class or TypeKind.Interface))
        {
            return false;
        }

        for (var named = type as INamedTypeSymbol; named is not null; named = named.BaseType)
        {
            if (Tables.HashOrderedTypes.Contains(Tables.FullName(named)))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>The member an operation invokes, reads, groups or constructs, or null.</summary>
    private static ISymbol? ReferencedMember(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IMethodReferenceOperation reference => reference.Method,
        IPropertyReferenceOperation property => property.Property,
        IFieldReferenceOperation field => field.Field,
        IEventReferenceOperation @event => @event.Event,
        IObjectCreationOperation creation => creation.Constructor,
        _ => null,
    };

    /// <summary>The user-defined operator a binary, unary or compound-assignment operation calls, or null.</summary>
    private static IMethodSymbol? OperatorMethod(IOperation operation) => operation switch
    {
        IBinaryOperation binary => binary.OperatorMethod,
        IUnaryOperation unary => unary.OperatorMethod,
        ICompoundAssignmentOperation compound => compound.OperatorMethod,
        _ => null,
    };

    /// <summary>
    /// The first type, in <paramref name="type"/> or inside it (array elements, pointer targets,
    /// function pointer signatures, type arguments, and the type arguments of containing types), that
    /// <paramref name="isBanned"/> accepts.
    /// </summary>
    private static ITypeSymbol? FindType(ITypeSymbol? type, Func<INamedTypeSymbol, bool> isBanned)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return FindType(array.ElementType, isBanned);
            case IPointerTypeSymbol pointer:
                return FindType(pointer.PointedAtType, isBanned);
            case IFunctionPointerTypeSymbol functionPointer:
                return FindType(functionPointer.Signature.ReturnType, isBanned) ?? FirstParameterType(functionPointer.Signature.Parameters, isBanned);
            case INamedTypeSymbol named:
                if (isBanned(named))
                {
                    return named;
                }

                foreach (var typeArgument in named.TypeArguments)
                {
                    if (FindType(typeArgument, isBanned) is { } found)
                    {
                        return found;
                    }
                }

                return FindType(named.ContainingType, isBanned);
            default:
                return null;
        }
    }

    private static ITypeSymbol? FirstParameterType(ImmutableArray<IParameterSymbol> parameters, Func<INamedTypeSymbol, bool> isBanned)
    {
        foreach (var parameter in parameters)
        {
            if (FindType(parameter.Type, isBanned) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The operand under any chain of conversions: the collection a <c>foreach</c> or spread names.</summary>
    private static IOperation StripConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>True for <c>float</c> and <c>float?</c>.</summary>
    private static bool IsSingle(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type?.SpecialType == SpecialType.System_Single;
    }

    /// <summary>True for the built-in integer types, <c>nint</c> and <c>nuint</c> included.</summary>
    private static bool IsInteger(ITypeSymbol type) => type.SpecialType is SpecialType.System_SByte
        or SpecialType.System_Byte
        or SpecialType.System_Int16
        or SpecialType.System_UInt16
        or SpecialType.System_Int32
        or SpecialType.System_UInt32
        or SpecialType.System_Int64
        or SpecialType.System_UInt64
        or SpecialType.System_IntPtr
        or SpecialType.System_UIntPtr;

    /// <summary>A member as the messages show it (<c>MathF.Sin(float)</c>).</summary>
    private static string Display(ISymbol member) => member.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    /// <summary>A declared symbol's identifier, or <see cref="Location.None"/> when it has none in source.</summary>
    private static Location DeclaredLocation(ISymbol symbol) => symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;

    /// <summary>One rule match: the descriptor, where to report it and the message arguments.</summary>
    private readonly struct Finding
    {
        public Finding(DiagnosticDescriptor descriptor, Location location, params object[] arguments)
        {
            Descriptor = descriptor;
            Location = location;
            Arguments = arguments;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        public object[] Arguments { get; }

        public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, Arguments);
    }
}
