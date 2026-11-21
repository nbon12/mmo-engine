#!/usr/bin/env python3
"""Offline generator of SimMath's float32 polynomial coefficients and split constants.

Spec 015 (specs/015-shared-simulation-core/research.md R3). This is a clean-room tool: it runs in a
throwaway virtual environment with the versions pinned in requirements.txt (mpmath, numpy; BSD), never
in CI, and nothing it imports is linked into the core. emit_cs.py turns its output into
src/MmoEngine.SimCore/Math/SimMathCoefficients.g.cs.

Each kernel polynomial is a weighted minimax (Remez exchange) fit at 60 significant digits of the
kernel's relative error. The coefficients are then rounded to binary32 one at a time, lowest degree
first, and after each rounding the remaining coefficients are refitted with the rounded ones held
fixed. Every stage is checked against naive rounding of the remaining coefficients; where Remez with
fixed coefficients diverges or does worse (tan at degree 15 is the known case), the stage falls back
to naive rounding.

The kernels (z = x*x; every polynomial is evaluated by Horner in SimMath):

    sin(x)  ~ x + x^3 * (S1 + z*(S2 + z*S3))                       degree 7,  |x| <= KERNEL_LIMIT
    cos(x)  ~ 1 - z/2 + z^2 * (C1 + z*(C2 + z*(C3 + z*C4)))        degree 10, |x| <= KERNEL_LIMIT
    tan(x)  ~ x + x^3 * (T1 + z*(T2 + ... + z*T7))                 degree 15, |x| <= KERNEL_LIMIT
    asin(x) ~ x + x * R(z),  R(z) = z*(A0 + z*(A1 + ... + z*A4))   R of degree 5, |x| <= 1/2
    atan(x) ~ x - x * Q(z),  Q(z) = z*(B0 + z*(B1 + ... + z*B5))   Q of degree 6, |x| <= 7/16

KERNEL_LIMIT is pi/4 plus the largest overshoot of the Cody-Waite quadrant count k, which SimMath
computes as RoundToInt(x * (2/pi)) in float32.

Besides the polynomials, it emits the split constants: the 4-part Cody-Waite split of pi/2 (11, 11,
11 and 24 bits; the first three truncated, so k * part is exact for k < 2^13), the binary-angle step
pi/32768 as an 8-bit high part (so i * hi is exact for |i| <= 8192) and a 24-bit low part, and the
hi/lo pairs of pi/2, pi/4, pi, atan(1/2) and atan(3/2).
"""

import struct
import sys

import mpmath
from mpmath import mp, mpf

mp.dps = 60

# |x| bound of the sin, cos and tan kernels: pi/4, plus 1/512 of slack for the float32 quadrant count.
KERNEL_LIMIT = mp.pi / 4 + mpf(1) / 512

# The smallest z the fits start at, relative to the interval's end. The weights vanish at z = 0.
Z_FLOOR = mpf("1e-12")


# --- binary32 helpers -------------------------------------------------------------------------------


def to_f32(value):
    """Rounds an mpf to the nearest binary32 (ties to even), as an exact mpf."""
    with mp.workprec(24):
        rounded = +mpf(value)
    bits = f32_bits(rounded)
    return f32_from_bits(bits)


def f32_bits(value):
    """The binary32 bit pattern of an mpf that is already a binary32 value."""
    as_double = float(value)
    if mpf(as_double) != mpf(value):
        raise ValueError(f"{value} is not exactly representable")
    packed = struct.pack("<f", as_double)
    if struct.unpack("<f", packed)[0] != as_double:
        raise ValueError(f"{value} is not a binary32 value")
    return struct.unpack("<I", packed)[0]


def f32_from_bits(bits):
    return mpf(struct.unpack("<f", struct.pack("<I", bits))[0])


def significant_bits(value):
    """The number of significant bits of a nonzero binary value."""
    man, _ = mpmath.mpf(value).man_exp
    return abs(int(man)).bit_length()


def truncate_bits(value, bits):
    """value truncated toward zero to `bits` significant bits."""
    value = mpf(value)
    exponent = int(mpmath.floor(mpmath.log(abs(value), 2)))
    scale = mpf(2) ** (bits - 1 - exponent)
    return mpmath.floor(abs(value) * scale) / scale * mpmath.sign(value)


def round_bits(value, bits):
    """value rounded to nearest (ties to even) at `bits` significant bits."""
    with mp.workprec(bits):
        return +mpf(value)


# --- Remez exchange ---------------------------------------------------------------------------------


class RemezDiverged(Exception):
    """Raised when the exchange does not converge."""


def _solve(points, target, weight, n):
    """Solves sum_j c_j t_i^j + (-1)^i E / w(t_i) = f(t_i) at the n + 1 reference points."""
    rows = []
    rhs = []
    for i, t in enumerate(points):
        rows.append([t**j for j in range(n)] + [(-1) ** i / weight(t)])
        rhs.append(target(t))
    solution = mpmath.lu_solve(mpmath.matrix(rows), mpmath.matrix(rhs))
    return [solution[j] for j in range(n)], solution[n]


def _poly(coefficients, t):
    total = mpf(0)
    for c in reversed(coefficients):
        total = total * t + c
    return total


def weighted_error(coefficients, target, weight, t):
    return weight(t) * (target(t) - _poly(coefficients, t))


def _extrema(coefficients, target, weight, a, b, grid):
    """Local extrema of the weighted error on [a, b]: a dense grid, then golden-section refinement."""
    ts = [a + (b - a) * (1 - mpmath.cos(mp.pi * k / grid)) / 2 for k in range(grid + 1)]
    es = [weighted_error(coefficients, target, weight, t) for t in ts]
    found = []
    for k in range(grid + 1):
        left = abs(es[k - 1]) if k > 0 else mpf(-1)
        right = abs(es[k + 1]) if k < grid else mpf(-1)
        if abs(es[k]) >= left and abs(es[k]) >= right:
            lo = ts[k - 1] if k > 0 else ts[k]
            hi = ts[k + 1] if k < grid else ts[k]
            found.append(_refine(coefficients, target, weight, lo, hi, ts[k]))
    return found


def _refine(coefficients, target, weight, lo, hi, guess):
    if lo == hi:
        return guess
    sign = mpmath.sign(weighted_error(coefficients, target, weight, guess))

    def score(t):
        return sign * weighted_error(coefficients, target, weight, t)

    ratio = (mpmath.sqrt(5) - 1) / 2
    x1 = hi - ratio * (hi - lo)
    x2 = lo + ratio * (hi - lo)
    f1, f2 = score(x1), score(x2)
    for _ in range(60):
        if f1 > f2:
            hi, x2, f2 = x2, x1, f1
            x1 = hi - ratio * (hi - lo)
            f1 = score(x1)
        else:
            lo, x1, f1 = x1, x2, f2
            x2 = lo + ratio * (hi - lo)
            f2 = score(x2)
    best = x1 if f1 > f2 else x2
    return best if score(best) > score(guess) else guess


def remez(target, weight, a, b, n, iterations=40, grid=600):
    """Weighted minimax polynomial of n coefficients (degree n - 1) for target on [a, b].

    Returns (coefficients, maximum weighted error). Raises RemezDiverged if the exchange fails.
    """
    points = [a + (b - a) * (1 - mpmath.cos(mp.pi * (k + mpf(1) / 2) / (n + 1))) / 2 for k in range(n + 1)]
    best = None
    for _ in range(iterations):
        try:
            coefficients, level = _solve(points, target, weight, n)
        except ZeroDivisionError as error:
            raise RemezDiverged(str(error)) from error
        extrema = _extrema(coefficients, target, weight, a, b, grid)
        errors = [weighted_error(coefficients, target, weight, t) for t in extrema]
        peak = max(abs(e) for e in errors)
        if best is None or peak < best[1]:
            best = (coefficients, peak)
        # Keep one extremum per run of equal sign (the largest), then the n + 1 largest adjacent run.
        runs = []
        for t, e in zip(extrema, errors):
            if runs and mpmath.sign(runs[-1][1]) == mpmath.sign(e):
                if abs(e) > abs(runs[-1][1]):
                    runs[-1] = (t, e)
            else:
                runs.append((t, e))
        if len(runs) < n + 1:
            raise RemezDiverged(f"only {len(runs)} alternations for {n} coefficients")
        while len(runs) > n + 1:
            if abs(runs[0][1]) < abs(runs[-1][1]):
                runs.pop(0)
            else:
                runs.pop()
        points = [t for t, _ in runs]
        if peak - abs(level) <= peak * mpf("1e-12"):
            return coefficients, peak
    if best is not None and best[1] < mpf(1):
        return best
    raise RemezDiverged("no convergence")


def max_weighted_error(coefficients, target, weight, a, b, grid=4000):
    extrema = _extrema(coefficients, target, weight, a, b, grid)
    return max(abs(weighted_error(coefficients, target, weight, t)) for t in extrema)


def fit_rounded(name, target, weight, a, b, n):
    """Remez fit of n coefficients, then binary32 rounding one coefficient at a time with refits.

    Returns (binary32 coefficients as mpf, report lines).
    """
    report = []
    free, peak = remez(target, weight, a, b, n)
    report.append(f"{name}: minimax {mpmath.nstr(peak, 6)} (exact coefficients)")
    fixed = []
    for stage in range(n):
        rounded = to_f32(free[0])
        fixed.append(rounded)
        remaining = n - len(fixed)
        naive = fixed + [to_f32(c) for c in free[1:]]
        naive_peak = max_weighted_error(naive, target, weight, a, b)
        if remaining == 0:
            free = []
            report.append(f"{name}: stage {stage}: {mpmath.nstr(naive_peak, 6)}")
            break

        held = list(fixed)
        order = len(held)

        def residual(t, held=held, order=order):
            return (target(t) - _poly(held, t)) / t**order

        def residual_weight(t, order=order):
            return weight(t) * t**order

        try:
            refit, _ = remez(residual, residual_weight, a, b, remaining)
            candidate = fixed + refit
            refit_peak = max_weighted_error(fixed + [to_f32(c) for c in refit], target, weight, a, b)
        except RemezDiverged as error:
            refit_peak = None
            report.append(f"{name}: stage {stage}: refit diverged ({error}); naive rounding")
        if refit_peak is not None and refit_peak <= naive_peak:
            free = candidate[len(fixed):]
            report.append(f"{name}: stage {stage}: refit {mpmath.nstr(refit_peak, 6)} <= naive {mpmath.nstr(naive_peak, 6)}")
        else:
            if refit_peak is not None:
                report.append(f"{name}: stage {stage}: refit {mpmath.nstr(refit_peak, 6)} > naive {mpmath.nstr(naive_peak, 6)}; naive rounding")
            free = free[1:]
    final = max_weighted_error(fixed, target, weight, a, b)
    report.append(f"{name}: final relative error {mpmath.nstr(final, 6)} ({mpmath.nstr(final / mpf(2) ** -24, 4)} ulp at 2^-24)")
    return fixed, report


# --- the kernels ------------------------------------------------------------------------------------


def _root(z):
    return mpmath.sqrt(z)


def sin_fit():
    end = KERNEL_LIMIT**2
    return fit_rounded(
        "sin",
        lambda z: (mpmath.sin(_root(z)) - _root(z)) / (z * _root(z)),
        lambda z: z * _root(z) / mpmath.sin(_root(z)),
        end * Z_FLOOR,
        end,
        3,
    )


def cos_fit():
    end = KERNEL_LIMIT**2
    return fit_rounded(
        "cos",
        lambda z: (mpmath.cos(_root(z)) - 1 + z / 2) / (z * z),
        lambda z: z * z / mpmath.cos(_root(z)),
        end * Z_FLOOR,
        end,
        4,
    )


def tan_fit():
    end = KERNEL_LIMIT**2
    return fit_rounded(
        "tan",
        lambda z: (mpmath.tan(_root(z)) - _root(z)) / (z * _root(z)),
        lambda z: z * _root(z) / mpmath.tan(_root(z)),
        end * Z_FLOOR,
        end,
        7,
    )


def asin_fit():
    end = mpf(1) / 4
    return fit_rounded(
        "asin",
        lambda z: (mpmath.asin(_root(z)) - _root(z)) / (z * _root(z)),
        lambda z: z * _root(z) / mpmath.asin(_root(z)),
        end * Z_FLOOR,
        end,
        5,
    )


def atan_fit():
    end = (mpf(7) / 16) ** 2
    return fit_rounded(
        "atan",
        lambda z: (_root(z) - mpmath.atan(_root(z))) / (z * _root(z)),
        lambda z: z * _root(z) / mpmath.atan(_root(z)),
        end * Z_FLOOR,
        end,
        6,
    )


def cody_waite_parts():
    """pi/2 as P1 + P2 + P3 (11 bits each, truncated) + P4 (24 bits, rounded)."""
    rest = mp.pi / 2
    parts = []
    for bits in (11, 11, 11):
        part = truncate_bits(rest, bits)
        parts.append(part)
        rest -= part
    parts.append(round_bits(rest, 24))
    for part, bits in zip(parts, (11, 11, 11, 24)):
        if significant_bits(part) > bits:
            raise ValueError(f"Cody-Waite part {part} has more than {bits} bits")
    return parts


def binary_angle_parts():
    """pi/32768, one binary-angle unit in radians, as an 8-bit high part and a 24-bit low part."""
    step = mp.pi / 32768
    high = round_bits(step, 8)
    low = round_bits(step - high, 24)
    if significant_bits(high) > 8:
        raise ValueError("the binary-angle high part has more than 8 bits")
    return high, low


def hi_lo(value):
    """value as the nearest binary32 and the binary32 nearest to what is left."""
    high = to_f32(value)
    return high, to_f32(value - high)


def constants():
    """Every generated constant, in emit order, as (name, binary32 value as mpf, comment)."""
    rows = []
    reports = []

    def add_polynomial(prefix, fit, first, lowest_power, description):
        values, report = fit()
        reports.extend(report)
        for index, value in enumerate(values):
            power = lowest_power + 2 * index
            rows.append((f"{prefix}{first + index}", value, f"The x^{power} coefficient of {description}"))

    add_polynomial("SinS", sin_fit, 1, 3, "sin(x) ~ x + x^3 * (S1 + z*(S2 + z*S3))")
    add_polynomial("CosC", cos_fit, 1, 4, "cos(x) ~ 1 - z/2 + z^2 * (C1 + z*(C2 + z*(C3 + z*C4)))")
    add_polynomial("TanT", tan_fit, 1, 3, "tan(x) ~ x + x^3 * (T1 + z*(T2 + ... + z*T7))")
    add_polynomial("AsinA", asin_fit, 0, 3, "asin(x) ~ x + x * z*(A0 + z*(A1 + ... + z*A4))")
    add_polynomial("AtanB", atan_fit, 0, 3, "atan(x) ~ x - x * z*(B0 + z*(B1 + ... + z*B5))")

    for index, part in enumerate(cody_waite_parts(), start=1):
        rows.append((f"HalfPiPart{index}", to_f32(part), f"Part {index} of the Cody-Waite split of pi/2 into 11, 11, 11 and 24 bits"))
    rows.append(("TwoOverPiSingle", to_f32(2 / mp.pi), "2/pi to binary32, for the Cody-Waite quadrant count"))

    angle_high, angle_low = binary_angle_parts()
    rows.append(("BinaryAngleHi", to_f32(angle_high), "pi/32768, one binary-angle unit in radians, rounded to 8 bits"))
    rows.append(("BinaryAngleLo", to_f32(angle_low), "pi/32768 minus BinaryAngleHi, rounded to binary32"))

    for name, label, value in (
        ("HalfPi", "pi/2", mp.pi / 2),
        ("QuarterPi", "pi/4", mp.pi / 4),
        ("Pi", "pi", mp.pi),
        ("AtanHalf", "atan(1/2)", mpmath.atan(mpf(1) / 2)),
        ("AtanThreeHalves", "atan(3/2)", mpmath.atan(mpf(3) / 2)),
    ):
        high, low = hi_lo(value)
        rows.append((f"{name}Hi", high, f"{label}, rounded to binary32"))
        rows.append((f"{name}Lo", low, f"{label} minus {name}Hi, rounded to binary32"))
    rows.append(("ThreeQuarterPi", to_f32(3 * mp.pi / 4), "3*pi/4, rounded to binary32"))
    return rows, reports


def main():
    rows, reports = constants()
    for line in reports:
        print(line)
    for name, value, _ in rows:
        print(f"{name} 0x{f32_bits(value):08X} {mpmath.nstr(value, 12)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
