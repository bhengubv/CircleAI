/**
 * inference/server/options
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/options.ts
//
// Port of CircleAI.Inference.Server.Options.InferenceServerOptions (+ Auth /
// ApiKey / Jwt subtrees). Strongly-typed configuration for the inference
// server. Field defaults match the C# initializers exactly.

/** API-key auth configuration. Ported from ApiKeyOptions. */
export interface ApiKeyOptions {
  /** When true, requests without a valid API key are 401-d. Default true. */
  enabled: boolean;
  /** HTTP header carrying the API key. Default "X-CircleAI-Api-Key". */
  headerName: string;
  /** Allow-listed keys. */
  keys: string[];
}

/** JWT-bearer auth configuration. Ported from JwtOptions. */
export interface JwtOptions {
  enabled: boolean;
  issuer: string;
  audience: string;
  signingKey: string;
}

/** Auth subtree. Ported from AuthOptions. */
export interface AuthOptions {
  apiKey: ApiKeyOptions;
  jwt: JwtOptions;
}

/** Root configuration for the inference server. Ported from InferenceServerOptions. */
export interface InferenceServerOptions {
  runtimeCacheRoot: string;
  modelStorageRoot: string;
  maxConcurrentRequests: number;
  requestTimeoutSeconds: number;
  auth: AuthOptions;
}

/** Top-level config section name. Matches InferenceServerOptions.SectionName. */
export const INFERENCE_SERVER_SECTION_NAME = "CircleAIServer";

/** Build InferenceServerOptions with the C# defaults, overridable per field. */
export function defaultInferenceServerOptions(
  overrides: DeepPartial<InferenceServerOptions> = {},
): InferenceServerOptions {
  const base: InferenceServerOptions = {
    runtimeCacheRoot: "%LOCALAPPDATA%/CircleAI/runtime",
    modelStorageRoot: "%LOCALAPPDATA%/CircleAI/models",
    maxConcurrentRequests: 16,
    requestTimeoutSeconds: 120,
    auth: {
      apiKey: { enabled: true, headerName: "X-CircleAI-Api-Key", keys: [] },
      jwt: { enabled: false, issuer: "", audience: "", signingKey: "" },
    },
  };
  const apiKeyOverride = overrides.auth?.apiKey;
  const apiKey: ApiKeyOptions = {
    enabled: apiKeyOverride?.enabled ?? base.auth.apiKey.enabled,
    headerName: apiKeyOverride?.headerName ?? base.auth.apiKey.headerName,
    keys: (apiKeyOverride?.keys as string[] | undefined) ?? base.auth.apiKey.keys,
  };
  const jwtOverride = overrides.auth?.jwt;
  const jwt: JwtOptions = {
    enabled: jwtOverride?.enabled ?? base.auth.jwt.enabled,
    issuer: jwtOverride?.issuer ?? base.auth.jwt.issuer,
    audience: jwtOverride?.audience ?? base.auth.jwt.audience,
    signingKey: jwtOverride?.signingKey ?? base.auth.jwt.signingKey,
  };
  return {
    runtimeCacheRoot: overrides.runtimeCacheRoot ?? base.runtimeCacheRoot,
    modelStorageRoot: overrides.modelStorageRoot ?? base.modelStorageRoot,
    maxConcurrentRequests: overrides.maxConcurrentRequests ?? base.maxConcurrentRequests,
    requestTimeoutSeconds: overrides.requestTimeoutSeconds ?? base.requestTimeoutSeconds,
    auth: { apiKey, jwt },
  };
}

type DeepPartial<T> = {
  [P in keyof T]?: T[P] extends object ? DeepPartial<T[P]> : T[P];
};
