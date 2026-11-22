using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MmoEngine.SimCore.Tests.Architecture;

/// <summary>
/// The core owns no state and reads no clock (FR-001, FR-015, data-model.md#ownership): it has no
/// static mutable field, and no method of it takes a time span or a measured frame time. The step
/// length comes only from <c>CompiledConfig</c>.
/// </summary>
public sealed class CoreStateTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Assembly Core = typeof(SimCoreVersion).Assembly;

    /// <summary>Parameter types that carry a clock reading or a measured duration (FR-015).</summary>
    private static readonly Type[] TimeTypes = [typeof(TimeSpan), typeof(DateTime), typeof(DateTimeOffset), typeof(Stopwatch)];

    /// <summary>Parameter names that mean a measured frame time when the parameter is a float or a double.</summary>
    private static readonly string[] FrameTimeNames = ["dt", "deltaTime", "frameTime", "elapsed", "elapsedSeconds"];

    /// <summary>
    /// Every static field of the core is a literal (<c>const</c>) or <c>initonly</c>, and every
    /// <c>initonly</c> one holds a value that cannot change: a primitive, an enum, a string, a readonly
    /// struct, an <see cref="ImmutableArray{T}"/>, or a sealed type whose instance fields are all
    /// <c>initonly</c>. Arrays are refused, because their elements can be written. Compiler-generated
    /// types and fields are skipped, except the backing field of a static auto-property, which is that
    /// property's state.
    /// </summary>
    [Fact]
    public void CoreHasNoStaticMutableFields()
    {
        var offenders = new List<string>();
        foreach (var type in Core.GetTypes().Where(t => !IsCompilerGenerated(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var field in type.GetFields(Declared | BindingFlags.Static))
            {
                if ((IsCompilerGenerated(field) && !IsAutoPropertyBackingField(field)) || field.IsLiteral)
                {
                    continue;
                }

                if (!field.IsInitOnly)
                {
                    offenders.Add($"{type.FullName}.{field.Name} (static and writable)");
                }
                else if (!IsUnchangeable(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} (static readonly {field.FieldType}, whose contents can change)");
                }
            }
        }

        Assert.True(offenders.Count == 0, $"The core must have no static mutable field (data-model.md#ownership): {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// No public or internal method or constructor of the core takes a <see cref="TimeSpan"/>,
    /// <see cref="DateTime"/>, <see cref="DateTimeOffset"/> or <see cref="Stopwatch"/>, or a float or
    /// double named as a frame time (dt, deltaTime, frameTime, elapsed or elapsedSeconds, in any case).
    /// The step length comes only from <c>CompiledConfig</c> (FR-015).
    /// </summary>
    [Fact]
    public void NoCoreMethodTakesATimeSpanOrAFrameTime()
    {
        var offenders = new List<string>();
        foreach (var type in Core.GetTypes().Where(t => !IsCompilerGenerated(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var members = type.GetMethods(Declared | BindingFlags.Instance | BindingFlags.Static)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(Declared | BindingFlags.Instance | BindingFlags.Static))
                .Where(m => m.IsPublic || m.IsAssembly || m.IsFamilyOrAssembly);

            foreach (var member in members)
            {
                foreach (var parameter in member.GetParameters())
                {
                    var parameterType = Unwrap(parameter.ParameterType);
                    if (TimeTypes.Contains(parameterType))
                    {
                        offenders.Add($"{type.FullName}.{member.Name}({parameter.Name}: {parameterType.Name})");
                    }
                    else if ((parameterType == typeof(float) || parameterType == typeof(double))
                        && FrameTimeNames.Contains(parameter.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{type.FullName}.{member.Name}({parameterType.Name} {parameter.Name})");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, $"No core method may take a clock reading or a measured frame time; the step length comes from CompiledConfig (FR-015): {string.Join("; ", offenders)}");
    }

    /// <summary>A static readonly field of this type holds a value nothing can change.</summary>
    private static bool IsUnchangeable(Type type)
    {
        if (type.IsArray)
        {
            return false;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || IsReadOnlyStruct(type))
        {
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            return true;
        }

        return type.IsClass && type.IsSealed && AllInstanceFieldsAreInitOnly(type);
    }

    private static bool IsReadOnlyStruct(Type type) =>
        type.IsValueType
        && type.GetCustomAttributes(inherit: false).Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");

    /// <summary>Every instance field of the type and of each of its base types is <c>initonly</c>.</summary>
    private static bool AllInstanceFieldsAreInitOnly(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (current.GetFields(Declared | BindingFlags.Instance).Any(f => !f.IsInitOnly))
            {
                return false;
            }
        }

        return true;
    }

    private static Type Unwrap(Type type)
    {
        var element = type.IsByRef ? type.GetElementType()! : type;
        return Nullable.GetUnderlyingType(element) ?? element;
    }

    /// <summary>The type, or a type it is nested in, was generated by the compiler.</summary>
    private static bool IsCompilerGenerated(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || current.Name.StartsWith('<'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompilerGenerated(FieldInfo field) =>
        field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || field.Name.StartsWith('<');

    private static bool IsAutoPropertyBackingField(FieldInfo field) =>
        field.Name.StartsWith('<') && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal);
}
