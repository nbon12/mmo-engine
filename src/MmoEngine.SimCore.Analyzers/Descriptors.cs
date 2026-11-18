using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MmoEngine.SimCore.Analyzers;

/// <summary>
/// The nine determinism rules, per specs/015-shared-simulation-core/contracts/analyzer-rules.md#rules
/// and #messages. Every rule is an Error, enabled by default, in category <c>Determinism</c>, and
/// tagged <see cref="WellKnownDiagnosticTags.NotConfigurable"/>, so no <c>#pragma</c>, editorconfig
/// severity, <c>NoWarn</c> or <c>TreatWarningsAsErrors=false</c> can lower it. The titles and message
/// formats are the contract's, verbatim, and AnalyzerReleases.Unshipped.md lists the same nine.
/// </summary>
internal static class Descriptors
{
    private const string Category = "Determinism";

    /// <summary>MMO1001: a vector or SIMD type outside the lane-wise <c>Vector128&lt;float&gt;</c> allowlist. <c>{1}</c> is the type.</summary>
    public static readonly DiagnosticDescriptor Mmo1001 = Create(
        "MMO1001",
        "Vector or SIMD type outside the lane-wise allowlist",
        "'{0}' uses '{1}', whose results can differ between the server and WebAssembly builds; use the core's scalar float types, or lane-wise Vector128<float> arithmetic from the allowlist");

    /// <summary>MMO1002: platform math outside the allowlist. <c>{1}</c> is the replacement.</summary>
    public static readonly DiagnosticDescriptor Mmo1002 = Create(
        "MMO1002",
        "Platform math outside the allowlist",
        "'{0}' is platform math, which is not correctly rounded and can differ between builds; use {1}");

    /// <summary>MMO1003: a fused multiply-add.</summary>
    public static readonly DiagnosticDescriptor Mmo1003 = Create(
        "MMO1003",
        "Fused multiply-add",
        "'{0}' fuses (or may fuse) a multiply and an add, which rounds differently from 'a * b + c'; write the multiply and the add as separate float operations");

    /// <summary>MMO1004: iteration over a hash-ordered collection. <c>{1}</c> is the collection type.</summary>
    public static readonly DiagnosticDescriptor Mmo1004 = Create(
        "MMO1004",
        "Iteration over a hash-ordered collection",
        "'{0}' enumerates '{1}', whose order is not defined; iterate a sorted array or List<T> kept alongside it, or a SortedDictionary or SortedSet");

    /// <summary>MMO1005: ambient time, randomness or a per-process hash seed.</summary>
    public static readonly DiagnosticDescriptor Mmo1005 = Create(
        "MMO1005",
        "Ambient time, randomness or a per-process hash seed",
        "'{0}' reads ambient time, randomness or a per-process hash seed; take the step number and step length from the caller, and draw randomness from the core's seeded spread hash");

    /// <summary>MMO1006: a lock or blocking synchronization.</summary>
    public static readonly DiagnosticDescriptor Mmo1006 = Create(
        "MMO1006",
        "Lock or blocking synchronization",
        "'{0}' takes a lock or blocks, which gameplay code must not do (constitution Principle II); the core is a pure step function: take state in and return state out");

    /// <summary>MMO1007: a non-float floating-point or platform-sized number. <c>{1}</c> is the banned type.</summary>
    public static readonly DiagnosticDescriptor Mmo1007 = Create(
        "MMO1007",
        "Non-float floating point or platform-sized number",
        "'{0}' uses '{1}'; the core computes in 'float' only, and in 'int' or 'long' for fixed point and counters, so every build rounds and overflows the same way");

    /// <summary>MMO1008: a platform query, conditional compilation or reflection.</summary>
    public static readonly DiagnosticDescriptor Mmo1008 = Create(
        "MMO1008",
        "Platform query, conditional compilation or reflection",
        "'{0}' lets the server and WebAssembly builds take different paths; the core has one code path for every build");

    /// <summary>MMO1009: a float-to-integer or ulong-to-float conversion outside the SimMath helpers. <c>{1}</c> is the reason.</summary>
    public static readonly DiagnosticDescriptor Mmo1009 = Create(
        "MMO1009",
        "Float-to-integer or ulong-to-float conversion outside the SimMath helpers",
        "'{0}' {1}");

    /// <summary>The nine rules in ID order, as <see cref="DeterminismAnalyzer.SupportedDiagnostics"/> lists them.</summary>
    public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
        Mmo1001,
        Mmo1002,
        Mmo1003,
        Mmo1004,
        Mmo1005,
        Mmo1006,
        Mmo1007,
        Mmo1008,
        Mmo1009);

    private static DiagnosticDescriptor Create(string id, string title, string messageFormat) =>
        new(
            id,
            title,
            messageFormat,
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            customTags: [WellKnownDiagnosticTags.NotConfigurable]);
}
