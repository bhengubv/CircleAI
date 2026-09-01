/**
 * workflows/paca_auth
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_auth.ts
//
// (3.3.0) Auth primitives ported from paca (PacaAuth.cs): JWT (access +
// refresh) + API-key validation. Issuance and verification use HMAC-SHA256.
// API keys live in an in-memory store keyed by hashed prefix.
//
// Crypto seam: the C# uses System.Security.Cryptography (HMACSHA256, SHA256,
// RandomNumberGenerator) directly. The TS port uses node:crypto — the runtime
// analogue — the same way voice/ uses node primitives directly; no injection
// seam is needed for the standard-library crypto surface.

import { createHash, createHmac, randomBytes, randomUUID } from "../platform";

/** (3.3.0) Token-shaped JWT result. Mirrors C# `JwtPair`. */
export interface JwtPair {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly accessExpiresAtUtc: Date;
  readonly refreshExpiresAtUtc: Date;
}

/** (3.3.0) Verified JWT payload. Mirrors C# `JwtPayload`. */
export interface JwtPayload {
  readonly subject: string;
  readonly claims: ReadonlyMap<string, string>;
  readonly expiresAtUtc: Date;
}

/** (3.3.0) HMAC-SHA256 JWT issuer + verifier. Mirrors C# `HmacJwtAuthenticator`. */
export class HmacJwtAuthenticator {
  private readonly secret: Buffer;
  private readonly accessLifetimeMs: number;
  private readonly refreshLifetimeMs: number;
  private readonly clock: () => Date;

  /**
   * @param signingSecret Signing key; must be at least 16 chars.
   * @param accessLifetimeMs Access-token lifetime in ms (default 15 minutes).
   * @param refreshLifetimeMs Refresh-token lifetime in ms (default 7 days).
   */
  constructor(
    signingSecret: string,
    accessLifetimeMs?: number | null,
    refreshLifetimeMs?: number | null,
    clock?: (() => Date) | null,
  ) {
    if (signingSecret == null || signingSecret.length < 16) {
      throw new Error("Signing secret must be at least 16 characters.");
    }
    this.secret = Buffer.from(signingSecret, "utf8");
    this.accessLifetimeMs = accessLifetimeMs ?? 15 * 60 * 1000;
    this.refreshLifetimeMs = refreshLifetimeMs ?? 7 * 24 * 60 * 60 * 1000;
    this.clock = clock ?? ((): Date => new Date());
  }

  /** (3.3.0) Issue access + refresh tokens for `subject`. */
  issue(subject: string, claims?: ReadonlyMap<string, string> | null): JwtPair {
    if (isBlank(subject)) throw new Error("subject required");
    const now = this.clock().getTime();
    const accessExp = new Date(now + this.accessLifetimeMs);
    const refreshExp = new Date(now + this.refreshLifetimeMs);
    const access = this.encodeToken(subject, "access", accessExp, claims ?? null);
    const refresh = this.encodeToken(subject, "refresh", refreshExp, null);
    return { accessToken: access, refreshToken: refresh, accessExpiresAtUtc: accessExp, refreshExpiresAtUtc: refreshExp };
  }

  /** (3.3.0) Verify a token; returns the payload or null if invalid/expired. */
  verify(token: string, expectedType = "access"): JwtPayload | null {
    if (isBlank(token)) return null;
    const parts = token.split(".");
    if (parts.length !== 3) return null;

    const [header, payload, sig] = parts;
    const signing = `${header}.${payload}`;
    const expected = this.signBase64Url(signing);
    if (!fixedTimeEquals(expected, sig)) return null;

    let json: Map<string, unknown>;
    try {
      const jsonBytes = base64UrlDecode(payload);
      json = JSON.parse(jsonBytes.toString("utf8")) as Map<string, unknown>;
    } catch {
      return null;
    }
    if (json == null || typeof json !== "object") return null;

    if (typeof json["typ"] !== "string" || json["typ"] !== expectedType) return null;
    if (typeof json["sub"] !== "string") return null;
    const subject = json["sub"];
    const expRaw = json["exp"];
    if (typeof expRaw !== "number" || !Number.isFinite(expRaw)) return null;
    const expSeconds = Math.trunc(expRaw);
    const exp = new Date(expSeconds * 1000);
    if (exp.getTime() <= this.clock().getTime()) return null;

    const extraClaims = new Map<string, string>();
    for (const [k, v] of Object.entries(json)) {
      if (k === "typ" || k === "sub" || k === "exp") continue;
      extraClaims.set(k, typeof v === "string" ? v : String(v));
    }
    return { subject, claims: extraClaims, expiresAtUtc: exp };
  }

  private encodeToken(
    subject: string,
    type: string,
    expires: Date,
    claims: ReadonlyMap<string, string> | null,
  ): string {
    const header = '{"alg":"HS256","typ":"JWT"}';
    const payload: Map<string, unknown> = {
      sub: subject,
      typ: type,
      exp: Math.trunc(expires.getTime() / 1000),
    };
    if (claims !== null) {
      for (const [k, v] of claims) payload[k] = v;
    }
    const headerB = base64UrlEncode(Buffer.from(header, "utf8"));
    const payloadB = base64UrlEncode(Buffer.from(JSON.stringify(payload), "utf8"));
    const signing = `${headerB}.${payloadB}`;
    const sig = this.signBase64Url(signing);
    return `${signing}.${sig}`;
  }

  private signBase64Url(signing: string): string {
    const sig = createHmac("sha256", this.secret).update(Buffer.from(signing, "utf8")).digest();
    return base64UrlEncode(sig);
  }
}

/** (3.3.0) Issued API key — store hashes only. Mirrors C# `PacaApiKeyRecord`. */
export interface PacaApiKeyRecord {
  readonly keyId: string;
  readonly label: string;
  readonly hashedSecret: string;
  readonly createdAtUtc: Date;
  readonly revokedAtUtc: Date | null;
}

/** Return type of {@link PacaApiKeyAuthenticator.issue}. */
export interface IssuedApiKey {
  readonly record: PacaApiKeyRecord;
  /** The raw secret — returned ONCE for the caller to store. */
  readonly rawSecret: string;
}

/** (3.3.0) API-key registry separate from JWT user auth. Mirrors C# `PacaApiKeyAuthenticator`. */
export class PacaApiKeyAuthenticator {
  private readonly keys = new Map<string, PacaApiKeyRecord>();
  private readonly clock: () => Date;

  constructor(clock?: (() => Date) | null) {
    this.clock = clock ?? ((): Date => new Date());
  }

  /** (3.3.0) Generate a fresh key; the raw secret is returned ONCE for the caller to store. */
  issue(label: string): IssuedApiKey {
    if (isBlank(label)) throw new Error("label required");
    const keyId = randomUUID().replace(/-/g, "");
    const secret = trimBase64Padding(randomBytes(32).toString("base64"));
    const hashed = hashSecret(secret);
    const record: PacaApiKeyRecord = { keyId, label, hashedSecret: hashed, createdAtUtc: this.clock(), revokedAtUtc: null };
    this.keys.set(keyId, record);
    return { record, rawSecret: secret };
  }

  /** (3.3.0) Verify an incoming key. Returns the record if valid and live. */
  verify(keyId: string, presentedSecret: string): PacaApiKeyRecord | null {
    const record = this.keys.get(keyId);
    if (record === undefined) return null;
    if (record.revokedAtUtc !== null) return null;
    const hashed = hashSecret(presentedSecret);
    return fixedTimeEquals(hashed, record.hashedSecret) ? record : null;
  }

  /** (3.3.0) Revoke a key. Idempotent. */
  revoke(keyId: string): void {
    const existing = this.keys.get(keyId);
    if (existing === undefined || existing.revokedAtUtc !== null) return;
    this.keys.set(keyId, { ...existing, revokedAtUtc: this.clock() });
  }
}

// ── Base64URL + constant-time helpers (mirror the C# private statics) ────────

function base64UrlEncode(bytes: Buffer): string {
  return bytes.toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

function base64UrlDecode(input: string): Buffer {
  let s = input.replace(/-/g, "+").replace(/_/g, "/");
  switch (s.length % 4) {
    case 2:
      s += "==";
      break;
    case 3:
      s += "=";
      break;
    default:
      break;
  }
  return Buffer.from(s, "base64");
}

function hashSecret(secret: string): string {
  return trimBase64Padding(createHash("sha256").update(Buffer.from(secret, "utf8")).digest().toString("base64"));
}

function trimBase64Padding(s: string): string {
  return s.replace(/=+$/, "");
}

/** Constant-time string comparison over UTF-8 bytes. Mirrors C# `FixedTimeEquals`/`SlowEquals`. */
function fixedTimeEquals(a: string, b: string): boolean {
  const ba = Buffer.from(a, "utf8");
  const bb = Buffer.from(b, "utf8");
  if (ba.length !== bb.length) return false;
  let diff = 0;
  for (let i = 0; i < ba.length; i++) diff |= ba[i] ^ bb[i];
  return diff === 0;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
