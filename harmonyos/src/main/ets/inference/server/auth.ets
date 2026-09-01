/**
 * inference/server/auth
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/auth.ts
//
// Port of CircleAI.Inference.Server.Auth.ApiKeyAuthHandler + AuthSchemes.
// The C# handler extends ASP.NET's AuthenticationHandler; here it is a
// framework-free handler that takes the request headers + the option block and
// returns a typed AuthenticateResult. The constant-time key comparison
// (CryptographicOperations.FixedTimeEquals) and the auth-disabled anonymous
// path are ported faithfully.

import { timingSafeEqual } from "../../platform";
import type { InferenceServerOptions } from "./options";

/** Identifiers for the auth schemes the server registers. Ported from AuthSchemes. */
export const AuthSchemes = {
  /** API-key auth scheme name. */
  ApiKey: "ApiKey",
  /** JWT Bearer auth scheme name (matches Microsoft's default constant). */
  Jwt: "Bearer",
  /** Policy name for endpoints requiring an authenticated caller. */
  AuthenticatedPolicy: "Authenticated",
} as const;

/** Kind of authentication outcome. Mirrors ASP.NET AuthenticateResult states. */
export enum AuthResultKind {
  /** Succeeded — a principal was established. */
  Success = "success",
  /** No credential was presented — the endpoint decides whether to challenge. */
  NoResult = "no_result",
  /** A credential was presented but rejected. */
  Fail = "fail",
}

/** One claim on an authenticated principal. */
export interface Claim {
  readonly type: string;
  readonly value: string;
}

/** Result of an authentication attempt. */
export interface AuthenticateResult {
  readonly kind: AuthResultKind;
  /** Present when kind is Success. */
  readonly claims?: readonly Claim[];
  /** Present when kind is Fail. */
  readonly failureReason?: string;
}

/**
 * API-key authentication handler. Reads the header named ApiKey.headerName and
 * matches against ApiKey.keys. When the option block has enabled=false the
 * handler succeeds with a synthetic "anonymous" principal so dev environments
 * don't need keys. Ported from ApiKeyAuthHandler.
 *
 * Headers are supplied as a case-insensitive lookup (a plain object or Map);
 * the C# HttpRequest.Headers dictionary is case-insensitive, so we lower-case
 * on lookup.
 */
export class ApiKeyAuthHandler {
  private readonly serverOptions: () => InferenceServerOptions;

  /**
   * @param serverOptions Accessor for the current options (mirrors
   *   IOptionsMonitor.CurrentValue — call it each authenticate).
   */
  constructor(serverOptions: () => InferenceServerOptions) {
    this.serverOptions = serverOptions;
  }

  /**
   * Authenticate a request given its headers. Ported from
   * HandleAuthenticateAsync.
   */
  authenticate(headers: Record<string, string> | Map<string, string>): AuthenticateResult {
    const cfg = this.serverOptions().auth.apiKey;

    if (!cfg.enabled) {
      // Auth disabled — succeed with a marker identity.
      return {
        kind: AuthResultKind.Success,
        claims: [
          { type: "name", value: "anonymous" },
          { type: "scheme", value: AuthSchemes.ApiKey },
          { type: "auth_disabled", value: "true" },
        ],
      };
    }

    const raw = getHeader(headers, cfg.headerName);
    if (raw === undefined || raw.trim().length === 0) {
      return { kind: AuthResultKind.NoResult };
    }

    if (!tryMatchKey(raw, cfg.keys)) {
      return { kind: AuthResultKind.Fail, failureReason: "Invalid API key." };
    }

    return {
      kind: AuthResultKind.Success,
      claims: [
        { type: "name", value: "api-key-caller" },
        { type: "scheme", value: AuthSchemes.ApiKey },
      ],
    };
  }
}

/**
 * Constant-time match against any configured key. Ported from
 * ApiKeyAuthHandler.TryMatchKey (FixedTimeEquals over UTF-8 bytes, only
 * comparing when lengths match).
 */
export function tryMatchKey(presented: string, allowed: readonly string[]): boolean {
  if (!allowed || allowed.length === 0) return false;
  const presentedBytes = Buffer.from(presented, "utf-8");
  for (const k of allowed) {
    if (!k) continue;
    const bytes = Buffer.from(k, "utf-8");
    if (bytes.length !== presentedBytes.length) continue;
    if (timingSafeEqual(bytes, presentedBytes)) return true;
  }
  return false;
}

function getHeader(
  headers: Record<string, string> | Map<string, string>,
  name: string,
): string | undefined {
  const lower = name.toLowerCase();
  if (headers instanceof Map) {
    for (const [k, v] of headers) if (k.toLowerCase() === lower) return v;
    return undefined;
  }
  for (const k of Object.keys(headers)) if (k.toLowerCase() === lower) return headers[k];
  return undefined;
}
