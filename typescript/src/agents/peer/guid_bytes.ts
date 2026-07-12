// agents/peer/guid_bytes.ts
//
// UUID-string ↔ 16-byte round-trip matching System.Guid's byte layout. The
// in-memory protocol carries the originating Invoke message's id in the first
// 16 bytes of the Response/Decline payload (C# `invoke.Id.ToByteArray()` /
// `new Guid(payload.AsSpan(0,16))`); this helper reproduces that exactly so a
// pure-TS mesh and a mixed C#/TS mesh agree on the correlation prefix.
//
// .NET's Guid.ToByteArray() is MIXED-endian: the first three groups
// (Data1 uint32, Data2 uint16, Data3 uint16) are written little-endian; the
// final 8 bytes (Data4) are written in order. new Guid(byte[]) reverses this.

/**
 * Serialise a canonical UUID string (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`) to
 * the 16-byte array System.Guid.ToByteArray() would produce. Non-hex / wrong
 * length input yields a zero-filled 16-byte buffer (fail-soft, matching the
 * "drop unrecognised correlation" behaviour of the caller).
 */
export function guidToBytes(uuid: string): Uint8Array {
  const out = new Uint8Array(16);
  const hex = uuid.replace(/-/g, "");
  if (hex.length !== 32 || /[^0-9a-fA-F]/.test(hex)) return out;

  const b = new Uint8Array(16);
  for (let i = 0; i < 16; i++) {
    b[i] = parseInt(hex.substr(i * 2, 2), 16);
  }
  // Byte-order as laid out by big-endian textual form b[0..15].
  // .NET little-endians Data1 (b0..b3), Data2 (b4..b5), Data3 (b6..b7).
  out[0] = b[3];
  out[1] = b[2];
  out[2] = b[1];
  out[3] = b[0];
  out[4] = b[5];
  out[5] = b[4];
  out[6] = b[7];
  out[7] = b[6];
  out[8] = b[8];
  out[9] = b[9];
  out[10] = b[10];
  out[11] = b[11];
  out[12] = b[12];
  out[13] = b[13];
  out[14] = b[14];
  out[15] = b[15];
  return out;
}

/**
 * Reconstruct the canonical UUID string from the first 16 bytes of `bytes`
 * (starting at `offset`), reversing {@link guidToBytes} — the analogue of
 * `new Guid(ReadOnlySpan<byte>)`.
 */
export function bytesToGuid(bytes: Uint8Array, offset = 0): string {
  const b = new Uint8Array(16);
  // Undo the mixed-endian layout back into big-endian textual order.
  b[0] = bytes[offset + 3];
  b[1] = bytes[offset + 2];
  b[2] = bytes[offset + 1];
  b[3] = bytes[offset + 0];
  b[4] = bytes[offset + 5];
  b[5] = bytes[offset + 4];
  b[6] = bytes[offset + 7];
  b[7] = bytes[offset + 6];
  b[8] = bytes[offset + 8];
  b[9] = bytes[offset + 9];
  b[10] = bytes[offset + 10];
  b[11] = bytes[offset + 11];
  b[12] = bytes[offset + 12];
  b[13] = bytes[offset + 13];
  b[14] = bytes[offset + 14];
  b[15] = bytes[offset + 15];

  const h = (n: number) => n.toString(16).padStart(2, "0");
  let s = "";
  for (let i = 0; i < 16; i++) s += h(b[i]);
  return `${s.substr(0, 8)}-${s.substr(8, 4)}-${s.substr(12, 4)}-${s.substr(16, 4)}-${s.substr(20, 12)}`;
}
