// A deterministic, in-memory builder of cross-build tables for tables.test.mjs: MMXT inputs and the
// server's MMXR results (contracts/wasm-api.md#cross-build-tables), so no binary table is committed.
//
// Every row the caller does not choose is filled from its index alone, so two builds of the same counts
// and the same chosen rows are byte-equal. Filled results are finite and non-zero (sign and mantissa
// mixed from the index, exponent 127: magnitudes in [1, 2)), so no filled row is a NaN that would match
// any build NaN under the comparison rule, and no filled row hides a chosen one.
//
// Each returned array is a view into a larger ArrayBuffer, with LEAD_LENGTH bytes before it and
// TRAIL_LENGTH after it set to 0xA5, as a pooled Node Buffer is a view into its pool. A reader must
// honour byteOffset and byteLength.

/** The MMXT and MMXR header: magic, u16 version, u16 0, u32 M, u32 P. */
export const HEADER_LENGTH = 16;

/** An MMXT math row: u8 function ID, 3 zero bytes, u32 a bits, u32 b bits. */
export const MATH_ROW_LENGTH = 12;

/** An MMXT pose row: u8 stance, u8 0, u16 AimYaw, f32 x, y, z. */
export const POSE_ROW_LENGTH = 16;

/** An MMXR math result: u32 r0 bits, u32 r1 bits. */
export const MATH_RESULT_LENGTH = 8;

/** An MMXR pose result: 25 f32. */
export const POSE_RESULT_LENGTH = 100;

/** The floats of a pose result: the head, torso and legs capsules (A, B, radius), then the sphere. */
export const POSE_FLOATS = 25;

/** The function IDs of the math rows. */
export const FunctionId = Object.freeze({
    Sin: 1,
    Cos: 2,
    Tan: 3,
    SinCos: 4,
    Asin: 5,
    Acos: 6,
    Atan: 7,
    Atan2: 8,
    SinCosBinaryAngle: 9,
    Sqrt: 10,
});

/** The stances of the pose rows. */
export const Stance = Object.freeze({ Standing: 0, Crouched: 1 });

/** Pose result floats by index: head A.x is 0, so the torso capsule's A.x is 7 and the sphere's radius 24. */
export const PoseFloat = Object.freeze({ HeadAX: 0, TorsoAX: 7, SphereRadius: 24 });

const VERSION = 1;
const LEAD_LENGTH = 8;
const TRAIL_LENGTH = 8;
const PADDING = 0xa5;

/**
 * Builds a whole table of `mathRows` math rows and `poseRows` pose rows.
 *
 * `math` maps a math row index to the fields it sets: `{ function, a, b, r0, r1 }`, each optional.
 * `pose` maps a pose row index to `{ stance, yaw, x, y, z, floats }`, each optional; `x`, `y` and `z`
 * are numbers a float holds exactly, and `floats` maps a result float index (0 to 24) to its bits.
 * Every other field and row takes its filled value.
 *
 * @param {{ mathRows: number, poseRows: number, math?: Record<number, object>, pose?: Record<number, object> }} spec
 * @returns {{ inputs: Uint8Array, results: Uint8Array, mathRows: number, poseRows: number }}
 */
export function buildTables({ mathRows, poseRows, math = {}, pose = {} }) {
    checkCount('mathRows', mathRows);
    checkCount('poseRows', poseRows);
    checkChosen('math', math, mathRows);
    checkChosen('pose', pose, poseRows);

    const inputs = paddedView(HEADER_LENGTH + (MATH_ROW_LENGTH * mathRows) + (POSE_ROW_LENGTH * poseRows));
    const results = paddedView(HEADER_LENGTH + (MATH_RESULT_LENGTH * mathRows) + (POSE_RESULT_LENGTH * poseRows));
    const input = dataView(inputs);
    const result = dataView(results);
    writeHeader(input, 'MMXT', mathRows, poseRows);
    writeHeader(result, 'MMXR', mathRows, poseRows);

    for (let i = 0; i < mathRows; i++) {
        const row = { ...filledMathRow(i), ...(math[i] ?? {}) };
        const at = mathRowOffset(i);
        input.setUint8(at, row.function);
        input.setUint32(at + 4, row.a >>> 0, true);
        input.setUint32(at + 8, row.b >>> 0, true);
        result.setUint32(mathResultOffset(i, 0), row.r0 >>> 0, true);
        result.setUint32(mathResultOffset(i, 1), row.r1 >>> 0, true);
    }

    for (let j = 0; j < poseRows; j++) {
        const chosen = pose[j] ?? {};
        const row = { ...filledPoseRow(j), ...chosen, floats: { ...filledPoseFloats(j), ...(chosen.floats ?? {}) } };
        const at = poseRowOffset(mathRows, j);
        input.setUint8(at, row.stance);
        input.setUint16(at + 2, row.yaw, true);
        input.setFloat32(at + 4, exactFloat('x', row.x), true);
        input.setFloat32(at + 8, exactFloat('y', row.y), true);
        input.setFloat32(at + 12, exactFloat('z', row.z), true);
        for (let k = 0; k < POSE_FLOATS; k++) {
            result.setUint32(poseResultOffset(mathRows, j, k), row.floats[k] >>> 0, true);
        }
    }

    return { inputs, results, mathRows, poseRows };
}

/** The offset of MMXT math row `row`. */
export function mathRowOffset(row) {
    return HEADER_LENGTH + (MATH_ROW_LENGTH * row);
}

/** The offset of MMXT pose row `row` in a table of `mathRows` math rows. */
export function poseRowOffset(mathRows, row) {
    return HEADER_LENGTH + (MATH_ROW_LENGTH * mathRows) + (POSE_ROW_LENGTH * row);
}

/** The offset of MMXR math result `row`'s r`r` (0 or 1). */
export function mathResultOffset(row, r) {
    return HEADER_LENGTH + (MATH_RESULT_LENGTH * row) + (4 * r);
}

/** The offset of MMXR pose result `row`'s float `float` (0 to 24) in a table of `mathRows` math rows. */
export function poseResultOffset(mathRows, row, float) {
    return HEADER_LENGTH + (MATH_RESULT_LENGTH * mathRows) + (POSE_RESULT_LENGTH * row) + (4 * float);
}

/** A byte-equal copy of `bytes`, padded as `buildTables` pads its arrays. */
export function copyBytes(bytes) {
    const copy = paddedView(bytes.byteLength);
    copy.set(bytes);
    return copy;
}

/** A copy of `bytes` with the little-endian u32 at `offset` set to `bits`. */
export function withBits(bytes, offset, bits) {
    const copy = copyBytes(bytes);
    dataView(copy).setUint32(offset, bits >>> 0, true);
    return copy;
}

/** The little-endian u32 at `offset` of `bytes`. */
export function readBits(bytes, offset) {
    return dataView(bytes).getUint32(offset, true);
}

function filledMathRow(i) {
    const fn = 1 + (i % 10);
    const a = mix32(i, 1);
    return {
        function: fn,
        a: fn === FunctionId.SinCosBinaryAngle ? a & 0xffff : a,
        b: fn === FunctionId.Atan2 ? mix32(i, 2) : 0,
        r0: finite(mix32(i, 3)),
        r1: fn === FunctionId.SinCos || fn === FunctionId.SinCosBinaryAngle ? finite(mix32(i, 4)) : 0,
    };
}

function filledPoseRow(j) {
    return {
        stance: j & 1,
        yaw: (j * 64) & 0xffff,
        x: (j % 16) * 0.25,
        y: 37.5,
        z: -(j % 9) * 0.125,
    };
}

function filledPoseFloats(j) {
    const floats = {};
    for (let k = 0; k < POSE_FLOATS; k++) {
        floats[k] = finite(mix32((j * POSE_FLOATS) + k, 5));
    }

    return floats;
}

/** A 32-bit mix of an index and a salt (a murmur3-style finaliser). */
function mix32(index, salt) {
    let h = Math.imul((index + Math.imul(salt, 0x9e3779b9)) | 0, 0x85ebca6b);
    h ^= h >>> 13;
    h = Math.imul(h, 0xc2b2ae35);
    h ^= h >>> 16;
    return h >>> 0;
}

/** Finite, non-zero float bits: the sign and mantissa of `bits` with exponent 127. */
function finite(bits) {
    return ((bits & 0x807fffff) | 0x3f800000) >>> 0;
}

function writeHeader(view, magic, mathRows, poseRows) {
    for (let i = 0; i < 4; i++) {
        view.setUint8(i, magic.charCodeAt(i));
    }

    view.setUint16(4, VERSION, true);
    view.setUint16(6, 0, true);
    view.setUint32(8, mathRows, true);
    view.setUint32(12, poseRows, true);
}

function paddedView(length) {
    const buffer = new ArrayBuffer(LEAD_LENGTH + length + TRAIL_LENGTH);
    new Uint8Array(buffer).fill(PADDING);
    const bytes = new Uint8Array(buffer, LEAD_LENGTH, length);
    bytes.fill(0);
    return bytes;
}

function dataView(bytes) {
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

function exactFloat(name, value) {
    if (Math.fround(value) !== value) {
        throw new RangeError(`pose ${name} ${value} is not a float value`);
    }

    return value;
}

function checkCount(name, count) {
    if (!Number.isInteger(count) || count < 0) {
        throw new RangeError(`${name} must be a non-negative integer, not ${count}`);
    }
}

function checkChosen(name, chosen, count) {
    for (const key of Object.keys(chosen)) {
        const index = Number(key);
        if (!Number.isInteger(index) || index < 0 || index >= count) {
            throw new RangeError(`${name} row ${key} is outside the ${count} rows`);
        }
    }
}
