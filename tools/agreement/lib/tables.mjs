/**
 * The agreement runners' comparison of a build's cross-build table results with the server's
 * (spec 015, US3.8b, US5.5b and the bit half of SC-004; contracts/wasm-api.md#cross-build-tables,
 * comparing).
 *
 * `compareTables(inputs, serverResults, buildResults, buildName)` takes the `MMXT` inputs, the server's
 * `MMXR` results and one build's `MMXR` results, and returns `{ match, firstMismatch }`: `match` true and
 * `firstMismatch` null when every result matches, else `match` false and the one line that says where the
 * build first differs. It produces the same lines as `src/MmoEngine.SimCore.Server/TablesComparison.cs`,
 * which `simcore agreement compare` uses, so a runner's failure and the job's report name a mismatch in
 * the same words:
 *
 * - The inputs and the server's results are the reference, so they must hold one whole table: the inputs'
 *   length must match their counts, and the server's results must carry the same counts and the length
 *   those counts need. Otherwise it throws, as `TablesComparison.Compare` does, instead of comparing.
 * - The 16-byte headers must be equal: `tables: build <b>, results header differs from the server's`.
 * - So must the lengths: `tables: build <b>, results are <n> bytes; the server's are <m>`.
 * - A math row matches when r0 and r1 are bit-identical, or, where the server's value is NaN (exponent all
 *   ones, mantissa non-zero), when the build's is any NaN (research R4: NaN bits are not portable):
 *   `tables: build <b>, first mismatch at math row <i>: <function>(a 0x%08X[, b 0x%08X]) r<0|1> server 0x%08X build 0x%08X`.
 * - A pose row compares all 25 floats bit for bit:
 *   `tables: build <b>, first mismatch at pose row <j>: <stance> yaw 0x%04X at (<x>, <y>, <z>) <float> server 0x%08X build 0x%08X`.
 *
 * Positions are written as .NET writes a float with the "R" format, which `TablesComparison` uses: the
 * shortest decimal that reads back as the same float, in fixed notation unless the decimal exponent is
 * above max(digits, 9) or below −3. Every position of the fixed table (0, 4096.25, 37.5, −7000.125,
 * −8191.5, −200, 8191.5) is also JavaScript's shortest round-trip form, and a float such as 0.1f, whose
 * double reads 0.10000000149011612, is written 0.1 as the server writes it.
 *
 * This module runs in Node and in a browser Worker (`web/worker.mjs` imports it through the agreement
 * server), so it uses no Node API. Each argument is an `ArrayBuffer` or a view of one (a `Uint8Array`, a
 * Node `Buffer`); a view's `byteOffset` and `byteLength` are honoured.
 */

/** The `MMXT` and `MMXR` header: magic, `u16` version, `u16` 0, `u32` math row count M, `u32` pose row count P. */
const HEADER_LENGTH = 16;

/** The header offsets of M and P. */
const MATH_COUNT_OFFSET = 8;
const POSE_COUNT_OFFSET = 12;

/** An `MMXT` math row: `u8` function ID, 3 zero bytes, `u32` a bits, `u32` b bits. */
const MATH_ROW_LENGTH = 12;

/** An `MMXT` pose row: `u8` stance, `u8` 0, `u16` AimYaw, `f32` x, y and z. */
const POSE_ROW_LENGTH = 16;

/** An `MMXR` math result: `u32` r0 bits, `u32` r1 bits. */
const MATH_RESULT_LENGTH = 8;

/** An `MMXR` pose result: 25 `f32`. */
const POSE_RESULT_LENGTH = 100;
const POSE_FLOATS = POSE_RESULT_LENGTH / 4;

/** Atan2's function ID, the one function with a b argument (a is y, b is x). */
const ATAN2 = 8;

/** The functions by ID; index 0 is no function, and an ID outside 1 to 10 is named `function <n>`. */
const FUNCTIONS = [
    '',
    'Sin',
    'Cos',
    'Tan',
    'SinCos',
    'Asin',
    'Acos',
    'Atan',
    'Atan2',
    'SinCosBinaryAngle',
    'Sqrt',
];

/** The stances by value; another value is named `stance <n>`. */
const STANCES = ['Standing', 'Crouched'];

/** The 25 floats of a pose result: the head, torso and legs capsules (A, B, radius), then the sphere. */
const POSE_FIELDS = poseFields();

/** The magnitude bits of a float, and +∞'s: a float is NaN when its magnitude is above +∞'s. */
const MAGNITUDE_MASK = 0x7fffffff;
const PLUS_INFINITY = 0x7f800000;

/** .NET's float precision, the fewest digits below which "R" switches to scientific notation. */
const SINGLE_PRECISION = 9;

/**
 * Compares `buildResults`, from the build `buildName`, with `serverResults`, both evaluations of
 * `inputs`, under the cross-build comparison rule (see the module comment).
 *
 * @param {ArrayBuffer | ArrayBufferView} inputs the `MMXT` inputs
 * @param {ArrayBuffer | ArrayBufferView} serverResults the server's `MMXR` results of `inputs`
 * @param {ArrayBuffer | ArrayBufferView} buildResults the build's `MMXR` results of `inputs`
 * @param {string} buildName the build's name, as the line names it
 * @returns {{ match: boolean, firstMismatch: string | null }}
 * @throws {TypeError} an argument is not bytes, or the build name is not a string
 * @throws {Error} the inputs and the server's results do not hold the same whole table
 */
export function compareTables(inputs, serverResults, buildResults, buildName) {
    if (typeof buildName !== 'string') {
        throw new TypeError(`compareTables: buildName must be a string, not ${typeof buildName}`);
    }

    const input = bytesView('inputs', inputs);
    const server = bytesView('serverResults', serverResults);
    const build = bytesView('buildResults', buildResults);
    const { mathRows, poseRows } = counts(input, server);

    if (build.byteLength < HEADER_LENGTH || !sameBytes(build, server, HEADER_LENGTH)) {
        return mismatch(`tables: build ${buildName}, results header differs from the server's`);
    }

    if (build.byteLength !== server.byteLength) {
        return mismatch(
            `tables: build ${buildName}, results are ${build.byteLength} bytes; the server's are ${server.byteLength}`,
        );
    }

    for (let i = 0; i < mathRows; i++) {
        const offset = HEADER_LENGTH + (MATH_RESULT_LENGTH * i);
        for (let r = 0; r < 2; r++) {
            const expected = server.getUint32(offset + (4 * r), true);
            const actual = build.getUint32(offset + (4 * r), true);
            if (expected !== actual && !(isNaN32(expected) && isNaN32(actual))) {
                return mismatch(
                    `tables: build ${buildName}, first mismatch at math row ${i}: ${mathRow(input, i)} `
                        + `r${r} server 0x${hex(expected, 8)} build 0x${hex(actual, 8)}`,
                );
            }
        }
    }

    const poseStart = HEADER_LENGTH + (MATH_RESULT_LENGTH * mathRows);
    for (let j = 0; j < poseRows; j++) {
        const offset = poseStart + (POSE_RESULT_LENGTH * j);
        for (let k = 0; k < POSE_FLOATS; k++) {
            const expected = server.getUint32(offset + (4 * k), true);
            const actual = build.getUint32(offset + (4 * k), true);
            if (expected !== actual) {
                return mismatch(
                    `tables: build ${buildName}, first mismatch at pose row ${j}: ${poseRow(input, mathRows, j)} `
                        + `${POSE_FIELDS[k]} server 0x${hex(expected, 8)} build 0x${hex(actual, 8)}`,
                );
            }
        }
    }

    return { match: true, firstMismatch: null };
}

/**
 * The math and pose row counts of the inputs, which the server's results must hold exactly. Throws, in
 * `TablesComparison`'s words, when the inputs and the server's results are not one whole table.
 */
function counts(input, server) {
    const mathRows = input.byteLength >= HEADER_LENGTH ? input.getUint32(MATH_COUNT_OFFSET, true) : -1;
    const poseRows = input.byteLength >= HEADER_LENGTH ? input.getUint32(POSE_COUNT_OFFSET, true) : -1;
    const inputsLength = HEADER_LENGTH + (MATH_ROW_LENGTH * mathRows) + (POSE_ROW_LENGTH * poseRows);
    const resultsLength = HEADER_LENGTH + (MATH_RESULT_LENGTH * mathRows) + (POSE_RESULT_LENGTH * poseRows);
    if (mathRows < 0
        || input.byteLength !== inputsLength
        || server.byteLength !== resultsLength
        || server.getUint32(MATH_COUNT_OFFSET, true) !== mathRows
        || server.getUint32(POSE_COUNT_OFFSET, true) !== poseRows) {
        throw new Error(
            `The inputs (${input.byteLength} bytes) and the server's results (${server.byteLength} bytes) `
                + 'do not hold the same whole table.',
        );
    }

    return { mathRows, poseRows };
}

/** A math row as `<function>(a 0x%08X)`, or `Atan2(a 0x%08X, b 0x%08X)`. */
function mathRow(input, i) {
    const start = HEADER_LENGTH + (MATH_ROW_LENGTH * i);
    const id = input.getUint8(start);
    const name = id >= 1 && id < FUNCTIONS.length ? FUNCTIONS[id] : `function ${id}`;
    const a = hex(input.getUint32(start + 4, true), 8);
    return id === ATAN2
        ? `${name}(a 0x${a}, b 0x${hex(input.getUint32(start + 8, true), 8)})`
        : `${name}(a 0x${a})`;
}

/** A pose row as `<Standing|Crouched> yaw 0x%04X at (<x>, <y>, <z>)`, the position as .NET's "R" writes it. */
function poseRow(input, mathRows, j) {
    const start = HEADER_LENGTH + (MATH_ROW_LENGTH * mathRows) + (POSE_ROW_LENGTH * j);
    const value = input.getUint8(start);
    const stance = value < STANCES.length ? STANCES[value] : `stance ${value}`;
    const yaw = hex(input.getUint16(start + 2, true), 4);
    const x = floatText(input.getUint32(start + 4, true));
    const y = floatText(input.getUint32(start + 8, true));
    const z = floatText(input.getUint32(start + 12, true));
    return `${stance} yaw 0x${yaw} at (${x}, ${y}, ${z})`;
}

/**
 * A float, given as its bits, as .NET's `float.ToString("R", CultureInfo.InvariantCulture)` writes it:
 * `NaN`, `Infinity`, `-Infinity`, `0` or `-0`; otherwise the shortest decimal digits that read back as
 * the same float, in fixed notation, or as `d[.ddd]E±XX` when the decimal exponent (the digits before the
 * point) is above max(digit count, 9) or below −3.
 */
function floatText(bits) {
    const sign = bits >>> 31 === 1 ? '-' : '';
    const magnitude = bits & MAGNITUDE_MASK;
    if (magnitude > PLUS_INFINITY) {
        return 'NaN';
    }

    if (magnitude === PLUS_INFINITY) {
        return `${sign}Infinity`;
    }

    if (magnitude === 0) {
        return `${sign}0`;
    }

    const { digits, scale } = shortestFloatDigits(magnitude);
    if (scale > Math.max(digits.length, SINGLE_PRECISION) || scale < -3) {
        const exponent = scale - 1;
        const mantissa = digits.length > 1 ? `${digits[0]}.${digits.slice(1)}` : digits;
        return `${sign}${mantissa}E${exponent < 0 ? '-' : '+'}${String(Math.abs(exponent)).padStart(2, '0')}`;
    }

    if (scale <= 0) {
        return `${sign}0.${'0'.repeat(-scale)}${digits}`;
    }

    if (scale >= digits.length) {
        return `${sign}${digits}${'0'.repeat(scale - digits.length)}`;
    }

    return `${sign}${digits.slice(0, scale)}.${digits.slice(scale)}`;
}

/**
 * The shortest significant digits, with no trailing zero, of the decimal that .NET writes for the
 * positive finite float with bits `magnitude`, and its scale: the float is about 0.d1d2… × 10^scale.
 *
 * As .NET's shortest formatting does, it takes the fewest digits p for which a p-digit decimal reads back
 * as the float (lies in its rounding interval: half the gap to each neighbour, the boundaries included
 * when the significand is even, as round-half-to-even reading takes them), and of the p-digit decimals
 * that do, the closest to the float, the even one on a tie. The candidates at p digits are the correctly
 * rounded decimal and its two neighbours; the comparisons are exact, in BigInt.
 */
function shortestFloatDigits(magnitude) {
    const exponentField = magnitude >>> 23;
    const fraction = magnitude & 0x7fffff;
    const significand = BigInt(exponentField === 0 ? fraction : fraction | 0x800000);
    const binaryExponent = exponentField === 0 ? -149 : exponentField - 150;
    const evenSignificand = (significand & 1n) === 0n;

    // In units of 2^(binaryExponent − 2): the float is 4·significand, the upper neighbour is 4 units
    // above it and the lower one 4 below, or 2 below at a power of two above the smallest normal exponent.
    const lowerHalfGap = fraction === 0 && exponentField > 1 ? 1n : 2n;
    const value = 4n * significand;
    const low = value - lowerHalfGap;
    const high = value + 2n;
    const binaryScale = binaryExponent - 2;

    // Every bound is (units) × 2^binaryScale and a candidate is N × 10^K; both are scaled to integers by
    // 2^max(0, −binaryScale) × 10^max(0, −K) before they are compared.
    const twoUp = 2n ** BigInt(Math.max(0, binaryScale));
    const twoDown = 2n ** BigInt(Math.max(0, -binaryScale));
    const float = new Float32Array(1);
    new Uint32Array(float.buffer)[0] = magnitude;

    for (let precision = 1; precision <= SINGLE_PRECISION; precision++) {
        const [, lead, rest = '', exponent] = /^(\d)(?:\.(\d+))?e([+-]\d+)$/.exec(float[0].toExponential(precision - 1));
        const nearest = BigInt(`${lead}${rest}`);
        const decimalExponent = Number(exponent) - (precision - 1);
        const tenUp = 10n ** BigInt(Math.max(0, decimalExponent));
        const tenDown = 10n ** BigInt(Math.max(0, -decimalExponent));
        const scaled = (units) => units * twoUp * tenDown;

        let best = null;
        for (const candidate of [nearest - 1n, nearest, nearest + 1n]) {
            if (candidate <= 0n) {
                continue;
            }

            const at = candidate * tenUp * twoDown;
            const inside = evenSignificand
                ? at >= scaled(low) && at <= scaled(high)
                : at > scaled(low) && at < scaled(high);
            if (!inside) {
                continue;
            }

            const distance = at > scaled(value) ? at - scaled(value) : scaled(value) - at;
            if (best === null || distance < best.distance || (distance === best.distance && (candidate & 1n) === 0n)) {
                best = { candidate, distance };
            }
        }

        if (best !== null) {
            const text = best.candidate.toString();
            return { digits: text.replace(/0+$/, ''), scale: text.length + decimalExponent };
        }
    }

    throw new RangeError(`floatText: no ${SINGLE_PRECISION}-digit decimal reads back as the float 0x${hex(magnitude, 8)}`);
}

/** The pose result's float names, in result order. */
function poseFields() {
    const fields = [];
    for (const part of ['head', 'torso', 'legs']) {
        for (const end of ['A', 'B']) {
            for (const axis of ['x', 'y', 'z']) {
                fields.push(`${part} ${end}.${axis}`);
            }
        }

        fields.push(`${part} radius`);
    }

    for (const axis of ['x', 'y', 'z']) {
        fields.push(`sphere centre.${axis}`);
    }

    fields.push('sphere radius');
    return fields;
}

/** A `DataView` over exactly the bytes of `bytes`, an `ArrayBuffer` or a view of one. */
function bytesView(name, bytes) {
    if (ArrayBuffer.isView(bytes)) {
        return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    }

    try {
        return new DataView(bytes);
    } catch {
        throw new TypeError(`compareTables: ${name} must be an ArrayBuffer or a view of one`);
    }
}

/** Whether the first `length` bytes of two views are equal. */
function sameBytes(left, right, length) {
    for (let i = 0; i < length; i++) {
        if (left.getUint8(i) !== right.getUint8(i)) {
            return false;
        }
    }

    return true;
}

/** Whether float bits are a NaN: exponent all ones, mantissa non-zero. */
function isNaN32(bits) {
    return (bits & MAGNITUDE_MASK) > PLUS_INFINITY;
}

/** `value` as uppercase hexadecimal of at least `width` digits. */
function hex(value, width) {
    return value.toString(16).toUpperCase().padStart(width, '0');
}

function mismatch(line) {
    return { match: false, firstMismatch: line };
}
