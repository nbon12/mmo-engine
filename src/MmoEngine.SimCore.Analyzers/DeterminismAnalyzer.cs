using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MmoEngine.SimCore.Analyzers;

/// <summary>
/// The determinism analyzer attached to the shared simulation core only. It enforces the rules of
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md (MMO1001 to MMO1009), on operations,
/// on member, local function, lambda and delegate declarations and on preprocessor directives, in
/// generated code too.
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
        OperationKind.Coalesce,
        OperationKind.DeconstructionAssignment,
        OperationKind.VariableDeclarator,
        OperationKind.DeclarationPattern,
        OperationKind.RecursivePattern,
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
        context.RegisterOperationAction(AnalyzeFunctionDeclaration, OperationKind.LocalFunction, OperationKind.AnonymousFunction);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.Event, SymbolKind.NamedType);
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

    /// <summary>
    /// The member declarations: the types of fields (not constants), properties, events, return types
    /// and parameters; a method's <c>extern</c> modifier, its <c>[Conditional]</c>,
    /// <c>[UnsafeAccessor]</c> and <c>[LibraryImport]</c> attributes and its <c>Synchronized</c> flag; an
    /// accessor's <c>extern</c> modifier, attributes and <c>Synchronized</c> flag; and a delegate's
    /// return and parameter types.
    /// </summary>
    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || IsPartialImplementation(symbol))
        {
            return;
        }

        Action<Diagnostic> report = context.ReportDiagnostic;
        switch (symbol)
        {
            case IFieldSymbol { IsConst: false } field:
                ReportDeclaration(report, field.Type, field);
                break;
            case IPropertySymbol property:
                if (!IsPositionalRecordProperty(property, context.CancellationToken))
                {
                    ReportDeclaration(report, property.Type, property);
                }

                ReportParameters(report, property.Parameters, context.CancellationToken);
                break;
            case IEventSymbol @event:
                Report(report, EventDeclaration(@event));
                break;
            case IMethodSymbol accessor when IsAccessor(accessor):
                Report(report, AccessorDeclaration(accessor));
                break;
            case IMethodSymbol method:
                Report(report, MethodDeclaration(method, method.ReturnType, DeclaredLocation(method), method.Name));
                ReportParameters(report, method.Parameters, context.CancellationToken);
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } @delegate:
                ReportDeclaration(report, invoke.ReturnType, @delegate);
                ReportParameters(report, invoke.Parameters, context.CancellationToken);
                break;
        }
    }

    /// <summary>
    /// The declarations inside a body, which no symbol action sees: a local function's identifier and
    /// parameters, checked as a method's are, and a lambda's or anonymous method's <c>Synchronized</c>
    /// flag and the parameter and return types its syntax writes out. A lambda has no identifier, so
    /// its own finding is reported at the lambda.
    /// </summary>
    private static void AnalyzeFunctionDeclaration(OperationAnalysisContext context)
    {
        Action<Diagnostic> report = context.ReportDiagnostic;
        switch (context.Operation)
        {
            case ILocalFunctionOperation { Symbol: var local }:
                Report(report, MethodDeclaration(local, local.ReturnType, DeclaredLocation(local), local.Name));
                ReportParameters(report, local.Parameters, context.CancellationToken);
                break;
            case IAnonymousFunctionOperation { Symbol: var lambda, Syntax: var syntax }:
                var writtenReturnType = syntax is ParenthesizedLambdaExpressionSyntax { ReturnType: not null } ? lambda.ReturnType : null;
                Report(report, MethodDeclaration(lambda, writtenReturnType, syntax.GetLocation(), syntax.ToString()));
                ReportParameters(report, lambda.Parameters.Where(p => IsWrittenWithType(p, context.CancellationToken)), context.CancellationToken);
                break;
        }
    }

    /// <summary>MMO1008 on every <c>#if</c> and <c>#elif</c> directive of one tree, active or not, at the directive line.</summary>
    private static void AnalyzeSyntaxTree(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        if (!root.ContainsDirectives)
        {
            return;
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            var keyword = trivia.Kind() switch
            {
                SyntaxKind.IfDirectiveTrivia => "#if",
                SyntaxKind.ElifDirectiveTrivia => "#elif",
                _ => null,
            };
            if (keyword is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.Mmo1008, trivia.GetLocation(), keyword));
            }
        }
    }

    private static void ReportParameters(Action<Diagnostic> report, IEnumerable<IParameterSymbol> parameters, CancellationToken cancellationToken)
    {
        foreach (var parameter in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportDeclaration(report, parameter.Type, parameter);
        }
    }

    private static void ReportDeclaration(Action<Diagnostic> report, ITypeSymbol type, ISymbol declared) =>
        Report(report, Declaration(type, declared.Name, DeclaredLocation(declared)));

    private static void Report(Action<Diagnostic> report, Finding? finding)
    {
        if (finding is { } found)
        {
            report(found.ToDiagnostic());
        }
    }

    /// <summary>
    /// True for the implementation part of a partial method, property, event or accessor. Its defining
    /// declaration has the same signature and the attributes of both parts, so it reports for both, and
    /// a partial member carries one diagnostic, not one per part.
    /// </summary>
    private static bool IsPartialImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.PartialDefinitionPart is not null,
        IPropertySymbol property => property.PartialDefinitionPart is not null,
        IEventSymbol @event => @event.PartialDefinitionPart is not null,
        _ => false,
    };

    /// <summary>True for a lambda or anonymous method parameter whose type is written in the syntax, not inferred from the delegate type.</summary>
    private static bool IsWrittenWithType(IParameterSymbol parameter, CancellationToken cancellationToken) =>
        parameter.DeclaringSyntaxReferences.Length == 1
        && parameter.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is ParameterSyntax { Type: not null };

    /// <summary>
    /// True for a record's positional property, which the compiler declares from a primary constructor
    /// parameter at that parameter's identifier. The parameter reports the declaration, so the
    /// identifier carries one diagnostic, not two.
    /// </summary>
    private static bool IsPositionalRecordProperty(IPropertySymbol property, CancellationToken cancellationToken) =>
        property.DeclaringSyntaxReferences.Length == 1
        && property.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is ParameterSyntax;

    /// <summary>
    /// Property, indexer and event accessors: their types are reported on their property or event,
    /// and their own <c>extern</c> modifier, attributes and <c>Synchronized</c> flag at the accessor
    /// keyword.
    /// </summary>
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
            ?? PlatformPath(operation)
            ?? Ambient(operation)
            ?? Blocking(operation)
            ?? HashOrderedEnumeration(operation)
            ?? FloatConversion(operation)
            ?? NumericExpression(operation);
    }

    /// <summary>The first rule a declaration of type <paramref name="type"/> matches, reported at the declared identifier; null for no type.</summary>
    private static Finding? Declaration(ITypeSymbol? type, string name, Location location) =>
        TypeFinding(type, IsBannedVectorType, Descriptors.Mmo1001, name, location)
        ?? TypeFinding(type, IsBlockingType, Descriptors.Mmo1006, name, location)
        ?? TypeFinding(type, IsBannedNumericType, Descriptors.Mmo1007, name, location);

    /// <summary>
    /// The one rule a method, local function or lambda declaration reports at
    /// <paramref name="location"/>, the first in precedence order: a vector
    /// <paramref name="returnType"/> (MMO1001), native code or a platform-path attribute (MMO1008,
    /// <see cref="IsPlatformPathDeclaration"/>), the synchronized implementation flag (MMO1006), then
    /// the rest of the return type's rules. A lambda passes its return type only when its syntax writes
    /// one. Parameters report on their own identifiers.
    /// </summary>
    private static Finding? MethodDeclaration(IMethodSymbol method, ITypeSymbol? returnType, Location location, string name) =>
        TypeFinding(returnType, IsBannedVectorType, Descriptors.Mmo1001, name, location)
            ?? (IsPlatformPathDeclaration(method)
                ? new Finding(Descriptors.Mmo1008, location, name)
                : (Finding?)null)
            ?? (IsSynchronized(method)
                ? new Finding(Descriptors.Mmo1006, location, name)
                : (Finding?)null)
            ?? Declaration(returnType, name, location);

    /// <summary>
    /// The one rule an event declaration reports at its identifier: a vector type (MMO1001); a
    /// field-like event's implicitly declared accessors, which have no keyword to report at, being
    /// <c>extern</c> (MMO1008) or synchronized by <c>[method: MethodImpl(MethodImplOptions.Synchronized)]</c>
    /// (MMO1006); then the rest of its type's rules. The modifier and the attribute apply to both
    /// accessors, so the adder carries them.
    /// </summary>
    private static Finding? EventDeclaration(IEventSymbol @event)
    {
        var location = DeclaredLocation(@event);
        var implicitAdder = @event.AddMethod is { IsImplicitlyDeclared: true } adder ? adder : null;
        return TypeFinding(@event.Type, IsBannedVectorType, Descriptors.Mmo1001, @event.Name, location)
            ?? (implicitAdder is not null && IsPlatformPathDeclaration(implicitAdder)
                ? new Finding(Descriptors.Mmo1008, location, @event.Name)
                : (Finding?)null)
            ?? (implicitAdder is not null && IsSynchronized(implicitAdder)
                ? new Finding(Descriptors.Mmo1006, location, @event.Name)
                : (Finding?)null)
            ?? Declaration(@event.Type, @event.Name, location);
    }

    /// <summary>
    /// The one rule an explicitly declared property, indexer or event accessor reports at its keyword,
    /// named as the accessor (<c>C.P.get</c>): native code or a platform-path attribute (MMO1008), then
    /// the synchronized flag (MMO1006). Its types report on its property or event.
    /// </summary>
    private static Finding? AccessorDeclaration(IMethodSymbol accessor)
    {
        var location = DeclaredLocation(accessor);
        return IsPlatformPathDeclaration(accessor)
            ? new Finding(Descriptors.Mmo1008, location, Display(accessor))
            : IsSynchronized(accessor)
                ? new Finding(Descriptors.Mmo1006, location, Display(accessor))
                : null;
    }

    /// <summary>True when <paramref name="method"/>'s implementation flags include <c>Synchronized</c>, so calling it takes a monitor.</summary>
    private static bool IsSynchronized(IMethodSymbol method) => (method.MethodImplementationFlags & MethodImplAttributes.Synchronized) != 0;

    /// <summary>
    /// True for a declaration MMO1008 reports: an <c>extern</c> method, local function or accessor,
    /// whose body is native code or a runtime intrinsic that differs between builds (<c>[DllImport]</c>,
    /// <c>InternalCall</c>, <c>[UnsafeAccessor]</c>), or one carrying a
    /// <see cref="Tables.PlatformPathAttributes"/> attribute.
    /// </summary>
    private static bool IsPlatformPathDeclaration(IMethodSymbol method) => method.IsExtern || HasPlatformPathAttribute(method);

    /// <summary>True when <paramref name="method"/> carries <c>[Conditional]</c>, <c>[UnsafeAccessor]</c> or <c>[LibraryImport]</c>.</summary>
    private static bool HasPlatformPathAttribute(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is { } type && Tables.PlatformPathAttributes.Contains(Tables.FullName(type)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="descriptor"/> at <paramref name="location"/> when <paramref name="type"/> is or contains a type <paramref name="isBanned"/> accepts; <c>{1}</c> is that type.</summary>
    private static Finding? TypeFinding(ITypeSymbol? type, Func<INamedTypeSymbol, bool> isBanned, DiagnosticDescriptor descriptor, string name, Location location) =>
        FindType(type, isBanned) is { } found
            ? new Finding(descriptor, location, name, found.ToDisplayString())
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
    /// method group or a function pointer, unless allowlisted; and float <c>%</c> and <c>%=</c>, also
    /// through a type parameter that may be bound to float (<see cref="IsFloatRemainder"/>).
    /// </summary>
    private static Finding? PlatformMath(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation invocation:
                return PlatformMathMethod(invocation.TargetMethod, operation);
            case IMethodReferenceOperation reference:
                return PlatformMathMethod(reference.Method, operation);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Remainder } binary when IsFloatRemainder(binary.OperatorMethod, binary.LeftOperand.Type):
                return new Finding(Descriptors.Mmo1002, operation.Syntax.GetLocation(), "%", Tables.MathFallback);
            case ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Remainder } compound when IsFloatRemainder(compound.OperatorMethod, compound.Target.Type):
                return new Finding(Descriptors.Mmo1002, operation.Syntax.GetLocation(), "%=", Tables.MathFallback);
            default:
                return null;
        }
    }

    /// <summary>
    /// True for a float remainder: the built-in <c>%</c> on a <c>float</c> left operand, or the
    /// generic-math <c>IModulusOperators</c> operator on a type parameter that may be bound to
    /// <c>float</c> (<see cref="MayBeFloatingPoint"/>).
    /// </summary>
    private static bool IsFloatRemainder(IMethodSymbol? operatorMethod, ITypeSymbol? left) =>
        operatorMethod is null
            ? IsSingle(left)
            : left is ITypeParameterSymbol
                && operatorMethod.ContainingType is { } host
                && string.Equals(Tables.FullName(host), Tables.ModulusOperatorsInterface, StringComparison.Ordinal)
                && MayBeFloatingPoint(left);

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
    /// MMO1005: a member or constructor of a clock, timer, random source or hash-seed type, or one of
    /// the ambient members. A <c>GetHashCode</c> call reports only when it still targets
    /// <c>object</c>'s or <c>ValueType</c>'s, which Roslyn binds only when the receiver's type does not
    /// override it, and <c>string</c>'s, whose seed is per process.
    /// </summary>
    private static Finding? Ambient(IOperation operation) =>
        ReferencedMember(operation) is { ContainingType: { } type } member
        && (IsListedOrDerived(type, Tables.AmbientTypes) || Tables.AmbientMembers.Contains(Tables.MemberKey(member)))
            ? new Finding(Descriptors.Mmo1005, operation.Syntax.GetLocation(), Display(member))
            : null;

    /// <summary>
    /// MMO1006: every <c>lock</c> statement, at its keyword, and a member or constructor of a lock,
    /// wait handle, spin primitive or blocking collection type, or one of the blocking members.
    /// </summary>
    private static Finding? Blocking(IOperation operation)
    {
        if (operation is ILockOperation)
        {
            return new Finding(Descriptors.Mmo1006, ((LockStatementSyntax)operation.Syntax).LockKeyword.GetLocation(), "lock");
        }

        return ReferencedMember(operation) is { ContainingType: { } type } member
            && (IsListedOrDerived(type, Tables.BlockingTypes) || Tables.BlockingMembers.Contains(Tables.MemberKey(member)))
                ? new Finding(Descriptors.Mmo1006, operation.Syntax.GetLocation(), Display(member))
                : null;
    }

    /// <summary>A lock, wait handle, spin primitive or blocking collection type, or a class derived from one.</summary>
    private static bool IsBlockingType(INamedTypeSymbol type) => IsListedOrDerived(type, Tables.BlockingTypes);

    /// <summary>
    /// MMO1004: enumerating a hash-ordered collection by <c>foreach</c>, a <c>..</c> spread, an
    /// enumerating member (<c>GetEnumerator</c>, <c>CopyTo</c>, <c>ToArray</c>, or <c>Keys</c>,
    /// <c>Values</c> and <c>Items</c> whose result is not hash-ordered), or a conversion to a type that
    /// is not hash-ordered, except into an order-insensitive sink of a hash-ordered type. The two
    /// routes that reach such a type with no conversion operation count as conversions too: a
    /// declaration pattern, and a type argument bound to a type parameter that hides the order
    /// (<see cref="HashOrderedTypeArgument"/>).
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
            case IPatternOperation { InputType: { } input } pattern
                when pattern is IDeclarationPatternOperation { DeclaredSymbol: not null } or IRecursivePatternOperation { DeclaredSymbol: not null }
                    && IsHashOrdered(input)
                    && !IsHashOrdered(pattern.NarrowedType):
                return new Finding(Descriptors.Mmo1004, operation.Syntax.GetLocation(), operation.Syntax.ToString(), input.ToDisplayString());
        }

        return ReferencedMember(operation) is { } member && HashOrderedTypeArgument(member) is { } argument
            ? new Finding(Descriptors.Mmo1004, operation.Syntax.GetLocation(), Display(member), argument.ToDisplayString())
            : null;
    }

    /// <summary>
    /// The first hash-ordered type argument of <paramref name="member"/>, a generic method's own or one
    /// of its containing types', whose type parameter hides the order
    /// (<see cref="HidesHashOrder"/>), or null.
    /// </summary>
    private static ITypeSymbol? HashOrderedTypeArgument(ISymbol member)
    {
        var found = member is IMethodSymbol method ? HiddenTypeArgument(method.TypeParameters, method.TypeArguments) : null;
        for (var type = member.ContainingType; found is null && type is not null; type = type.ContainingType)
        {
            found = HiddenTypeArgument(type.TypeParameters, type.TypeArguments);
        }

        return found;
    }

    private static ITypeSymbol? HiddenTypeArgument(ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments)
    {
        for (var i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            if (IsHashOrdered(arguments[i]) && HidesHashOrder(parameters[i]))
            {
                return arguments[i];
            }
        }

        return null;
    }

    /// <summary>
    /// True for a type parameter whose code sees its argument as a type that is not hash-ordered: one
    /// constrained, directly or through another type parameter, to a class or interface
    /// (<c>where T : IEnumerable&lt;int&gt;</c>), and to no hash-ordered one. An unconstrained type
    /// parameter (<c>List&lt;T&gt;</c>, <c>Enumerable.First&lt;TSource&gt;</c>) gives its code nothing
    /// to enumerate the argument through, and each read gets the argument's own type back.
    /// </summary>
    private static bool HidesHashOrder(ITypeParameterSymbol parameter) => !IsHashOrdered(parameter) && HasConstraintType(parameter);

    private static bool HasConstraintType(ITypeParameterSymbol parameter) =>
        parameter.ConstraintTypes.Any(constraint => constraint is not ITypeParameterSymbol other || HasConstraintType(other));

    /// <summary>The <c>foreach</c> or spread of <paramref name="collection"/>, reported at the collection expression, when its type is hash-ordered.</summary>
    private static Finding? HashOrderedSource(IOperation collection, string keyword) =>
        collection.Type is { } type && IsHashOrdered(type)
            ? new Finding(Descriptors.Mmo1004, collection.Syntax.GetLocation(), keyword, type.ToDisplayString())
            : null;

    /// <summary>
    /// An argument whose order the result does not depend on: the collection passed to a set operation
    /// (<see cref="Tables.OrderInsensitiveSinkMethods"/>) declared on a listed hash-ordered type, or to a
    /// listed type's own constructor (its copy constructor). An argument to any other member, such as
    /// <c>Add</c>, <c>TryAdd</c> or a derived class's constructor, may store or enumerate the collection,
    /// so its conversion reports.
    /// </summary>
    private static bool IsOrderInsensitiveSink(IConversionOperation conversion) =>
        conversion.Parent is IArgumentOperation { Parent: var sink }
        && sink switch
        {
            IInvocationOperation { TargetMethod: var method } => Tables.OrderInsensitiveSinkMethods.Contains(method.Name) && IsListedHashType(method.ContainingType),
            IObjectCreationOperation { Constructor.ContainingType: var type } => IsListedHashType(type),
            _ => false,
        };

    /// <summary>True for a type of <see cref="Tables.HashOrderedTypes"/> itself, not a class derived from one.</summary>
    private static bool IsListedHashType(INamedTypeSymbol? type) => type is not null && Tables.HashOrderedTypes.Contains(Tables.FullName(type));

    /// <summary>
    /// True for a hash-ordered collection type (<see cref="Tables.HashOrderedTypes"/>) or a class
    /// derived from one, and for a type parameter constrained to one, directly or through another type
    /// parameter. Interfaces count only as themselves.
    /// </summary>
    private static bool IsHashOrdered(ITypeSymbol? type) => type switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } named => IsListedOrDerived(named, Tables.HashOrderedTypes),
        ITypeParameterSymbol parameter => parameter.ConstraintTypes.Any(IsHashOrdered),
        _ => false,
    };

    /// <summary>True when <paramref name="type"/> or one of its base classes is in <paramref name="listed"/>.</summary>
    private static bool IsListedOrDerived(INamedTypeSymbol? type, ImmutableHashSet<string> listed)
    {
        for (var named = type; named is not null; named = named.BaseType)
        {
            if (listed.Contains(Tables.FullName(named)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// MMO1008: every <c>dynamic</c> operation, and a member, constructor or operator of a platform
    /// query, <c>Debug</c>, <c>Trace</c>, <c>Environment</c> (but its clocks), <c>System.Type</c>,
    /// <c>Activator</c>, a System.Reflection type, <c>NativeLibrary</c>, <c>Marshal</c> or
    /// <c>Unsafe</c>, or one of the platform-size members.
    /// </summary>
    private static Finding? PlatformPath(IOperation operation)
    {
        if (operation is IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation)
        {
            return new Finding(Descriptors.Mmo1008, operation.Syntax.GetLocation(), operation.Syntax.ToString());
        }

        if ((ReferencedMember(operation) ?? OperatorMethod(operation)) is not { ContainingType: { } type } member)
        {
            return null;
        }

        var key = Tables.MemberKey(member);
        var name = Tables.FullName(type);
        var banned = Tables.PlatformPathMembers.Contains(key)
            || ((Tables.PlatformPathTypes.Contains(name) || name.StartsWith(Tables.ReflectionPrefix, StringComparison.Ordinal))
                && !Tables.PlatformPathExemptions.Contains(key));
        return banned ? new Finding(Descriptors.Mmo1008, operation.Syntax.GetLocation(), Display(member)) : null;
    }

    /// <summary>
    /// MMO1009: a non-constant conversion from <c>float</c> or <c>double</c> to an integer or enum type
    /// outside the bodies of SimMath's three saturating helpers; a non-constant <c>ulong</c>-to-<c>float</c>
    /// conversion anywhere; every <c>System.Convert</c> member; and an integer type's <c>Create*</c>
    /// from a <c>TOther</c> that is, or may be bound to, a floating-point type
    /// (<see cref="MayBeFloatingPoint"/>). Besides conversion operations, it checks the element
    /// conversions Roslyn makes without an operation of their own: between tuple elements, from a
    /// <c>foreach</c> element to its iteration variable, in a deconstruction, from a spread's elements
    /// to the collection's, and from the left operand of <c>??</c> to its result.
    /// </summary>
    private static Finding? FloatConversion(IOperation operation)
    {
        var model = operation.SemanticModel;
        switch (operation)
        {
            case IConversionOperation conversion:
                return conversion.ConstantValue.HasValue || conversion.Operand.Type is not { } operandType || conversion.Type is not { } convertedType
                    ? null
                    : ConversionFinding(ElementReason(operandType, convertedType, operation, throughOperators: false), operation.Syntax, operation.Syntax.ToString());
            case IForEachLoopOperation { Syntax: ForEachStatementSyntax statement, LoopControlVariable: IVariableDeclaratorOperation { Symbol: var local } }
                when model?.GetForEachStatementInfo(statement).ElementType is { } element:
                return ConversionFinding(ElementReason(element, local.Type, operation, throughOperators: true), statement.Type, statement.Type.ToString());
            case IForEachLoopOperation { Syntax: ForEachVariableStatementSyntax statement, LoopControlVariable.Type: var variableType }
                when model?.GetForEachStatementInfo(statement).ElementType is { } element:
                return ConversionFinding(DeconstructionReason(element, variableType, model.GetDeconstructionInfo(statement), operation), statement.Variable, statement.Variable.ToString());
            case IDeconstructionAssignmentOperation { Syntax: AssignmentExpressionSyntax syntax, Value: { Type: { } source } value, Target.Type: var target }
                when model is not null:
                return ConversionFinding(DeconstructionReason(source, target, model.GetDeconstructionInfo(syntax), operation), value.Syntax, value.Syntax.ToString());
            case ISpreadOperation { ElementType: { } element, Operand: var collection, Parent.Type: var collectionType }
                when CollectionElementType(collectionType) is { } target:
                return ConversionFinding(ElementReason(element, target, operation, throughOperators: true), collection.Syntax, "..");
            case ICoalesceOperation { Value: { Type: { } source } value, Type: { } target }:
                return ConversionFinding(ElementReason(source, target, operation, throughOperators: true), value.Syntax, value.Syntax.ToString());
        }

        if (ReferencedMember(operation) is not { ContainingType: { } type } member)
        {
            return null;
        }

        var banned = string.Equals(Tables.FullName(type), Tables.ConvertType, StringComparison.Ordinal)
            || (member is IMethodSymbol { TypeArguments.Length: 1 } method
                && Tables.IntegerCreateMethods.Contains(Tables.MemberKey(method))
                && MayBeFloatingPoint(method.TypeArguments[0]));
        return banned ? new Finding(Descriptors.Mmo1009, operation.Syntax.GetLocation(), Display(member), Tables.FloatToIntegerReason) : null;
    }

    /// <summary>MMO1009 at <paramref name="syntax"/>, naming it <paramref name="text"/>, when <paramref name="reason"/> is not null.</summary>
    private static Finding? ConversionFinding(string? reason, SyntaxNode syntax, string text) =>
        reason is null ? null : new Finding(Descriptors.Mmo1009, syntax.GetLocation(), text, reason);

    /// <summary>
    /// The first MMO1009 reason a conversion from <paramref name="source"/> to <paramref name="target"/>
    /// gives, or null. Between two tuple types it pairs the elements, in order and recursively, because
    /// a tuple conversion converts each element with no operation of its own. With
    /// <paramref name="throughOperators"/>, a user-defined conversion is also checked through the
    /// standard conversions on either side of its operator; a conversion operation passes false at the
    /// top, because Roslyn gives those standard conversions operations of their own.
    /// </summary>
    private static string? ElementReason(ITypeSymbol source, ITypeSymbol target, IOperation operation, bool throughOperators)
    {
        var from = Underlying(source);
        var to = Underlying(target);
        if (from is INamedTypeSymbol { IsTupleType: true } fromTuple && to is INamedTypeSymbol { IsTupleType: true } toTuple)
        {
            for (var i = 0; i < fromTuple.TupleElements.Length && i < toTuple.TupleElements.Length; i++)
            {
                if (ElementReason(fromTuple.TupleElements[i].Type, toTuple.TupleElements[i].Type, operation, throughOperators: true) is { } reason)
                {
                    return reason;
                }
            }

            return null;
        }

        return DirectReason(from, to, operation)
            ?? (throughOperators
                && !SymbolEqualityComparer.Default.Equals(from, to)
                && operation.SemanticModel?.Compilation.ClassifyCommonConversion(from, to) is { IsUserDefined: true, MethodSymbol: { } conversionOperator }
                    ? DirectReason(from, Underlying(conversionOperator.Parameters[0].Type), operation) ?? DirectReason(Underlying(conversionOperator.ReturnType), to, operation)
                    : null);
    }

    /// <summary>MMO1009's reason for one standard conversion between two non-nullable types, or null.</summary>
    private static string? DirectReason(ITypeSymbol from, ITypeSymbol to, IOperation operation) =>
        IsFloatOrDouble(from) && IsIntegerOrEnum(to)
            ? (IsInsideSimMathIntegerHelper(operation) ? null : Tables.FloatToIntegerReason)
            : (from.SpecialType == SpecialType.System_UInt64 && IsSingle(to) ? Tables.UlongToFloatReason : null);

    /// <summary>
    /// The first MMO1009 reason in a deconstruction of <paramref name="source"/> into
    /// <paramref name="target"/>, a tuple type. Each part (one of <c>Deconstruct</c>'s out parameters, or
    /// one of the tuple's elements) converts to its target element as an element conversion, or is
    /// deconstructed again when <paramref name="info"/> nests.
    /// </summary>
    private static string? DeconstructionReason(ITypeSymbol source, ITypeSymbol? target, DeconstructionInfo info, IOperation operation)
    {
        if (info.Nested.IsEmpty)
        {
            return target is null ? null : ElementReason(source, target, operation, throughOperators: true);
        }

        var parts = info.Method is { } deconstruct
            ? deconstruct.Parameters.Skip(deconstruct.Parameters.Length - info.Nested.Length).Select(p => p.Type).ToImmutableArray()
            : TupleElementTypes(source);
        var targets = TupleElementTypes(target);
        for (var i = 0; i < info.Nested.Length && i < parts.Length && i < targets.Length; i++)
        {
            if (DeconstructionReason(parts[i], targets[i], info.Nested[i], operation) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>The element types of a tuple type, or none for any other type.</summary>
    private static ImmutableArray<ITypeSymbol> TupleElementTypes(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsTupleType: true } tuple
            ? tuple.TupleElements.Select(e => e.Type).ToImmutableArray()
            : ImmutableArray<ITypeSymbol>.Empty;

    /// <summary>
    /// The element type of a collection expression's target type: an array's element type, a span's
    /// type argument, or the <c>T</c> of the <c>IEnumerable&lt;T&gt;</c> it is or implements; otherwise null.
    /// </summary>
    private static ITypeSymbol? CollectionElementType(ITypeSymbol? collection) =>
        collection is IArrayTypeSymbol array
            ? array.ElementType
            : collection is INamedTypeSymbol named
                ? (Tables.SpanTypes.Contains(Tables.FullName(named))
                    ? named.TypeArguments[0]
                    : named.AllInterfaces.Insert(0, named).FirstOrDefault(IsEnumerableOfT)?.TypeArguments[0])
                : null;

    /// <summary>True for a constructed <c>IEnumerable&lt;T&gt;</c>.</summary>
    private static bool IsEnumerableOfT(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

    /// <summary>
    /// True when the method whose body holds <paramref name="operation"/> is one of
    /// <c>SimMath.FloorToInt(float)</c>, <c>RoundToInt(float)</c> and <c>TruncateToInt(float)</c>,
    /// matched by documentation comment ID. A local function or lambda inside them is the enclosing
    /// symbol of its own body and has no ID, and another overload has another ID, so neither is exempt.
    /// </summary>
    private static bool IsInsideSimMathIntegerHelper(IOperation operation) =>
        operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart) is IMethodSymbol { MethodKind: MethodKind.Ordinary } method
        && method.GetDocumentationCommentId() is { } id
        && Tables.SimMathIntegerHelpers.Contains(id);

    /// <summary>
    /// True for <c>float</c>, <c>double</c>, <c>Half</c> and <c>NFloat</c>, and for a type parameter
    /// that could be bound to one: any type parameter not constrained, directly or through another type
    /// parameter, to <c>IBinaryInteger&lt;T&gt;</c> or an interface that extends it.
    /// </summary>
    private static bool MayBeFloatingPoint(ITypeSymbol? type) =>
        type is ITypeParameterSymbol parameter
            ? !IsBinaryIntegerConstrained(parameter)
            : type is INamedTypeSymbol named && Tables.FloatingPointTypes.Contains(Tables.FullName(named));

    private static bool IsBinaryIntegerConstrained(ITypeParameterSymbol parameter) =>
        parameter.ConstraintTypes.Any(constraint =>
            constraint is ITypeParameterSymbol other
                ? IsBinaryIntegerConstrained(other)
                : constraint is INamedTypeSymbol named && (IsBinaryIntegerInterface(named) || named.AllInterfaces.Any(IsBinaryIntegerInterface)));

    private static bool IsBinaryIntegerInterface(INamedTypeSymbol type) =>
        string.Equals(Tables.FullName(type), Tables.BinaryIntegerInterface, StringComparison.Ordinal);

    /// <summary>
    /// MMO1007: a non-constant expression whose type is, or contains as a type argument, array element
    /// or pointer target, <c>double</c>, <c>Half</c>, <c>NFloat</c>, <c>nint</c> or <c>nuint</c>, or
    /// whose referenced member has one in its type arguments (<see cref="BannedNumericTypeArgument"/>),
    /// so <c>Count&lt;nint&gt;(n)</c> reports although it returns <c>int</c>. Only the expression kinds
    /// the analyzer registers count, so a parent that is never reported cannot hide a child.
    /// </summary>
    private static Finding? NumericExpression(IOperation operation)
    {
        var expression = operation is IInvocationOperation
            or IPropertyReferenceOperation
            or IFieldReferenceOperation
            or IMethodReferenceOperation
            or IEventReferenceOperation
            or IObjectCreationOperation
            or IConversionOperation
            or IBinaryOperation
            or ICompoundAssignmentOperation
            or IUnaryOperation
            or IDefaultValueOperation;
        if (!expression || operation.ConstantValue.HasValue)
        {
            return null;
        }

        var member = ReferencedMember(operation);
        if ((FindType(operation.Type, IsBannedNumericType) ?? BannedNumericTypeArgument(member)) is not { } found)
        {
            return null;
        }

        var text = member is not null ? Display(member) : operation.Syntax.ToString();
        return new Finding(Descriptors.Mmo1007, operation.Syntax.GetLocation(), text, found.ToDisplayString());
    }

    /// <summary>
    /// The first banned numeric type in <paramref name="member"/>'s type arguments: a generic method's
    /// own, then those of its containing types, outermost last (<c>Foo&lt;double&gt;.Count</c>). The
    /// containing type itself does not count, so a member of <c>double</c> reports through its operands
    /// and result, not again for its host.
    /// </summary>
    private static ITypeSymbol? BannedNumericTypeArgument(ISymbol? member)
    {
        var found = member is IMethodSymbol method ? FirstBannedNumericType(method.TypeArguments) : null;
        for (var type = member?.ContainingType; found is null && type is not null; type = type.ContainingType)
        {
            found = FirstBannedNumericType(type.TypeArguments);
        }

        return found;
    }

    private static ITypeSymbol? FirstBannedNumericType(ImmutableArray<ITypeSymbol> types)
    {
        foreach (var type in types)
        {
            if (FindType(type, IsBannedNumericType) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary><c>double</c>, <c>nint</c>, <c>nuint</c>, <c>Half</c> or <c>NFloat</c>.</summary>
    private static bool IsBannedNumericType(INamedTypeSymbol type) =>
        type.SpecialType is SpecialType.System_Double or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
        || Tables.BannedNumericTypes.Contains(Tables.FullName(type));

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

    /// <summary>The type under <c>Nullable&lt;T&gt;</c>, or <paramref name="type"/> itself.</summary>
    private static ITypeSymbol Underlying(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    /// <summary>True for <c>float</c> and <c>float?</c>.</summary>
    private static bool IsSingle(ITypeSymbol? type) => type is not null && Underlying(type).SpecialType == SpecialType.System_Single;

    /// <summary>True for <c>float</c> and <c>double</c>, nullable or not.</summary>
    private static bool IsFloatOrDouble(ITypeSymbol type) => Underlying(type).SpecialType is SpecialType.System_Single or SpecialType.System_Double;

    /// <summary>
    /// True for the targets of MMO1009's float conversions, nullable or not: the fixed-size integer
    /// types, <c>char</c>, <c>Int128</c>, <c>UInt128</c>, and an enum, by its underlying type.
    /// </summary>
    private static bool IsIntegerOrEnum(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        var underlying = Underlying(type);
        if (underlying is INamedTypeSymbol { EnumUnderlyingType: { } enumUnderlying })
        {
            underlying = enumUnderlying;
        }

        return underlying.SpecialType is SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Char
            || (underlying is INamedTypeSymbol named && Tables.WideIntegerTypes.Contains(Tables.FullName(named)));
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
