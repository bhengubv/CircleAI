// speech/pcm_io.ts
//
// Little-endian signed-16-bit PCM read/write helpers — the analogues of
// System.Buffers.Binary.BinaryPrimitives.{Read,Write}Int16LittleEndian used
// throughout the CircleAI.Speech DSP. Kept local to the speech module so the
// byte handling lives in one place.

/** Read one little-endian signed 16-bit sample at byte offset `byteOffset`. */
export function readInt16LE(buf: Uint8Array, byteOffset: number): number {
  let s = buf[byteOffset] | (buf[byteOffset + 1] << 8);
  if (s >= 0x8000) s -= 0x10000; // sign-extend to a signed short
  return s;
}

/**
 * Write `value` as a little-endian signed 16-bit sample at `byteOffset`. Value
 * is truncated to 16 bits (matching the C# `(short)` cast at the write sites).
 */
export function writeInt16LE(buf: Uint8Array, byteOffset: number, value: number): void {
  const v = value & 0xffff;
  buf[byteOffset] = v & 0xff;
  buf[byteOffset + 1] = (v >> 8) & 0xff;
}

/** `short.MaxValue` / `short.MinValue`. */
export const SHORT_MAX = 32767;
export const SHORT_MIN = -32768;

/** Clamp `v` to the signed-16-bit range (C# `Math.Clamp(v, short.MinValue, short.MaxValue)`). */
export function clampShort(v: number): number {
  if (v < SHORT_MIN) return SHORT_MIN;
  if (v > SHORT_MAX) return SHORT_MAX;
  return v;
}
