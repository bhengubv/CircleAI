/**
 * tools_catalog/crypto
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools_catalog/crypto.ts
//
// The crypto seam behind AesGcmCredentialStore. The C# original calls
// System.Security.Cryptography.AesGcm directly; here the AEAD primitive is
// injected behind IAeadCipher so the store has real encryption without hard-
// wiring a specific backend (mirroring the "inject crypto behind an interface"
// rule). A default WebCrypto-backed AES-256-GCM implementation is provided.
//
// TYPE NOTE: node:crypto's `webcrypto` exposes its own SubtleCrypto whose
// CryptoKey / KeyUsage / BufferSource types differ from the ambient DOM lib
// types TypeScript resolves here. We therefore (a) let the imported-key type be
// inferred from `subtle.importKey` (no DOM-CryptoKey annotation), and (b) hand
// SubtleCrypto only freshly-allocated ArrayBuffer-backed Uint8Arrays so no
// SharedArrayBuffer / ArrayBufferLike variance surfaces.

import { webcrypto } from "../platform";

/**
 * Authenticated-encryption primitive over a fixed 32-byte key. `encrypt`
 * produces `nonce(12) || tag(16) || ciphertext`; `decrypt` reverses it and
 * returns `null` on authentication failure (the analogue of the C#
 * `CryptographicException` → `null` fallthrough in AesGcmCredentialStore.Get).
 */
export interface IAeadCipher {
  /** Encrypt `plaintext`, returning the combined nonce+tag+ciphertext buffer. */
  encrypt(plaintext: Uint8Array): Promise<Uint8Array>;
  /** Decrypt a combined nonce+tag+ciphertext buffer; `null` if auth fails. */
  decrypt(combined: Uint8Array): Promise<Uint8Array | null>;
}

const NONCE_LEN = 12;
const TAG_LEN = 16;

const subtle = webcrypto.subtle;
type ImportedKey = Awaited<ReturnType<typeof subtle.importKey>>;

/**
 * Default {@link IAeadCipher}: AES-256-GCM via the WebCrypto SubtleCrypto API,
 * with a random 12-byte nonce per message and a 16-byte tag — byte-compatible
 * with the C# `AesGcm(key, tagSizeInBytes: 16)` layout
 * (`nonce || tag || ciphertext`).
 */
export class WebCryptoAesGcmCipher implements IAeadCipher {
  private readonly keyPromise: Promise<ImportedKey>;

  constructor(key32: Uint8Array) {
    if (key32 === null || key32 === undefined || key32.length !== 32) {
      throw new Error("key must be 32 bytes (AES-256-GCM)");
    }
    // Import once; reuse for every operation.
    this.keyPromise = subtle.importKey("raw", toArrayBuffer(key32), { name: "AES-GCM" }, false, [
      "encrypt",
      "decrypt",
    ]);
  }

  async encrypt(plaintext: Uint8Array): Promise<Uint8Array> {
    const key = await this.keyPromise;
    const nonce = webcrypto.getRandomValues(new Uint8Array(NONCE_LEN));
    const ctWithTag = new Uint8Array(
      await subtle.encrypt(
        { name: "AES-GCM", iv: toArrayBuffer(nonce), tagLength: TAG_LEN * 8 },
        key,
        toArrayBuffer(plaintext),
      ),
    );
    // WebCrypto appends the tag AFTER the ciphertext; the C# layout is
    // nonce || tag || ciphertext. Split and re-order to match.
    const ctLen = ctWithTag.length - TAG_LEN;
    const cipher = ctWithTag.subarray(0, ctLen);
    const tag = ctWithTag.subarray(ctLen);

    const combined = new Uint8Array(NONCE_LEN + TAG_LEN + ctLen);
    combined.set(nonce, 0);
    combined.set(tag, NONCE_LEN);
    combined.set(cipher, NONCE_LEN + TAG_LEN);
    return combined;
  }

  async decrypt(combined: Uint8Array): Promise<Uint8Array | null> {
    if (combined.length < NONCE_LEN + TAG_LEN) return null;
    const key = await this.keyPromise;
    const nonce = combined.subarray(0, NONCE_LEN);
    const tag = combined.subarray(NONCE_LEN, NONCE_LEN + TAG_LEN);
    const cipher = combined.subarray(NONCE_LEN + TAG_LEN);

    // Reassemble ciphertext||tag for WebCrypto in a fresh ArrayBuffer.
    const ctWithTag = new Uint8Array(cipher.length + TAG_LEN);
    ctWithTag.set(cipher, 0);
    ctWithTag.set(tag, cipher.length);

    try {
      const pt = await subtle.decrypt(
        { name: "AES-GCM", iv: toArrayBuffer(nonce), tagLength: TAG_LEN * 8 },
        key,
        toArrayBuffer(ctWithTag),
      );
      return new Uint8Array(pt);
    } catch {
      return null;
    }
  }
}

/**
 * Copy `v` into a standalone, non-shared ArrayBuffer and return it — SubtleCrypto
 * requires a plain ArrayBuffer-backed BufferSource, and this also detaches the
 * view from any SharedArrayBuffer the caller might hold.
 */
function toArrayBuffer(v: Uint8Array): ArrayBuffer {
  const out = new ArrayBuffer(v.length);
  new Uint8Array(out).set(v);
  return out;
}
