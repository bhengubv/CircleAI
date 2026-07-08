// companion/herjarvis/guid.ts
//
// C# `Guid.NewGuid().ToString("n")` yields a 32-character lowercase hex string
// with no dashes. `randomUUID()` gives the dashed 8-4-4-4-12 form; stripping the
// dashes reproduces the "n" format. The value is a non-deterministic identity
// token in the C# too (a fresh Guid each call), so there is no wire value to
// byte-match — only the shape (32 lowercase hex chars) matters.

import { randomUUID } from "node:crypto";

/** 32-character lowercase hex id, matching `Guid.NewGuid().ToString("n")`. */
export function newGuidN(): string {
  return randomUUID().replace(/-/g, "");
}
