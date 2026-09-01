/**
 * companion/herjarvis/guid
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/guid.ts
//
// C# `Guid.NewGuid().ToString("n")` yields a 32-character lowercase hex string
// with no dashes. `randomUUID()` gives the dashed 8-4-4-4-12 form; stripping the
// dashes reproduces the "n" format. The value is a non-deterministic identity
// token in the C# too (a fresh Guid each call), so there is no wire value to
// byte-match — only the shape (32 lowercase hex chars) matters.

import { randomUUID } from "../../platform";

/** 32-character lowercase hex id, matching `Guid.NewGuid().ToString("n")`. */
export function newGuidN(): string {
  return randomUUID().replace(/-/g, "");
}
