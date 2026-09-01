/**
 * tools_catalog/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools_catalog/contracts.ts
//
// The full Tools.Catalog contract surface — composio pattern-port. Port of
// CircleAI.Tools.Catalog.Contracts.cs. Complements (does not replace) the
// lightweight IToolCatalog inside CircleAI.Hosting.
//
// NOTE: the TypeScript `catalog/` module is the UNRELATED model catalog, so
// this cluster lives in `tools_catalog/` per the port brief.
//
// Type mappings (C# → TS):
//   enum                       → TS enum
//   sealed record              → readonly interface (+ positional factory)
//   IReadOnlyList<T>           → readonly T[]
//   IReadOnlyDictionary<K,V>   → ReadonlyMap<K,V>
//   DateTimeOffset?            → Date | null
//   ValueTask<T>               → Promise<T>
//   CancellationToken          → AbortSignal (optional)

/** How the provider authenticates. Mirrors `AuthKind`. */
export enum AuthKind {
  None = "None",
  ApiKey = "ApiKey",
  BearerToken = "BearerToken",
  OAuth2 = "OAuth2",
  Basic = "Basic",
  Custom = "Custom",
}

/** OAuth2 configuration when {@link ProviderDescriptor.auth} = OAuth2. Mirrors `OAuth2Descriptor`. */
export interface OAuth2Descriptor {
  readonly authorizeUrl: string;
  readonly tokenUrl: string;
  readonly scopes: readonly string[];
  readonly userInfoUrl: string | null;
}

/** Constructs an {@link OAuth2Descriptor}. */
export function oauth2Descriptor(
  authorizeUrl: string,
  tokenUrl: string,
  scopes: readonly string[],
  userInfoUrl: string | null = null,
): OAuth2Descriptor {
  return { authorizeUrl, tokenUrl, scopes, userInfoUrl };
}

/** One provider in the catalog (Gmail, Slack, Linear, …). Mirrors `ProviderDescriptor`. */
export interface ProviderDescriptor {
  readonly providerId: string;
  readonly displayName: string;
  readonly description: string;
  readonly homepage: string | null;
  readonly auth: AuthKind;
  readonly tags: readonly string[];
  readonly capabilities: readonly string[];
  readonly oauth2: OAuth2Descriptor | null;
}

/** Constructs a {@link ProviderDescriptor}. */
export function providerDescriptor(
  providerId: string,
  displayName: string,
  description: string,
  homepage: string | null,
  auth: AuthKind,
  tags: readonly string[],
  capabilities: readonly string[],
  oauth2: OAuth2Descriptor | null = null,
): ProviderDescriptor {
  return { providerId, displayName, description, homepage, auth, tags, capabilities, oauth2 };
}

/** One stored credential for one user / one provider. Mirrors `CredentialBundle`. */
export interface CredentialBundle {
  readonly providerId: string;
  readonly userId: string;
  readonly fields: ReadonlyMap<string, string>;
  readonly expiresAtUtc: Date | null;
}

/** Constructs a {@link CredentialBundle}. */
export function credentialBundle(
  providerId: string,
  userId: string,
  fields: ReadonlyMap<string, string>,
  expiresAtUtc: Date | null = null,
): CredentialBundle {
  return { providerId, userId, fields, expiresAtUtc };
}

/** A quota / rate-limit policy on one (provider, user) pair. Mirrors `QuotaPolicy`. */
export interface QuotaPolicy {
  readonly providerId: string;
  readonly userId: string;
  readonly dailyCallBudget: number;
  readonly maxConcurrent: number;
  readonly perMinuteCap: number;
}

/** Constructs a {@link QuotaPolicy}. */
export function quotaPolicy(
  providerId: string,
  userId: string,
  dailyCallBudget: number,
  maxConcurrent: number,
  perMinuteCap: number,
): QuotaPolicy {
  return { providerId, userId, dailyCallBudget, maxConcurrent, perMinuteCap };
}

/** Namespace partition — keep one user's tool list separate from the next. Mirrors `ToolNamespace`. */
export interface ToolNamespace {
  readonly namespaceId: string;
  readonly ownerUserId: string;
  readonly providerIds: readonly string[];
}

/** Constructs a {@link ToolNamespace}. */
export function toolNamespace(
  namespaceId: string,
  ownerUserId: string,
  providerIds: readonly string[],
): ToolNamespace {
  return { namespaceId, ownerUserId, providerIds };
}

/** The provider directory. Mirrors `IProviderCatalog`. */
export interface IProviderCatalog {
  readonly backendId: string;

  listProvidersAsync(signal?: AbortSignal): Promise<readonly ProviderDescriptor[]>;
  getProviderAsync(providerId: string, signal?: AbortSignal): Promise<ProviderDescriptor | null>;

  /** Semantic search over the registered providers. */
  searchProvidersAsync(
    query: string,
    topK?: number,
    signal?: AbortSignal,
  ): Promise<readonly ProviderDescriptor[]>;
}

/** Credential storage. Implementations must encrypt at rest. Mirrors `ICredentialStore`. */
export interface ICredentialStore {
  readonly backendId: string;

  upsertAsync(bundle: CredentialBundle, signal?: AbortSignal): Promise<void>;
  getAsync(providerId: string, userId: string, signal?: AbortSignal): Promise<CredentialBundle | null>;
  deleteAsync(providerId: string, userId: string, signal?: AbortSignal): Promise<void>;
}

/** OAuth2 flow driver — initiate + complete a 3-legged flow. Mirrors `IOAuth2FlowDriver`. */
export interface IOAuth2FlowDriver {
  readonly backendId: string;

  /** Build the redirect URL for the user's browser. */
  startAsync(providerId: string, userId: string, redirectUri: string, signal?: AbortSignal): Promise<string>;

  /** Exchange the authorisation code returned to the redirect URI for a credential bundle. */
  completeAsync(
    providerId: string,
    userId: string,
    authorizationCode: string,
    redirectUri: string,
    signal?: AbortSignal,
  ): Promise<CredentialBundle>;
}

/** Per-(provider,user) quota enforcement. Mirrors `IQuotaGuard`. */
export interface IQuotaGuard {
  readonly backendId: string;

  tryAcquireAsync(providerId: string, userId: string, signal?: AbortSignal): Promise<boolean>;
  setPolicyAsync(policy: QuotaPolicy, signal?: AbortSignal): Promise<void>;
  getPolicyAsync(providerId: string, userId: string, signal?: AbortSignal): Promise<QuotaPolicy | null>;
}

/** Tool-namespace storage. Mirrors `IToolNamespaceStore`. */
export interface IToolNamespaceStore {
  readonly backendId: string;

  upsertAsync(ns: ToolNamespace, signal?: AbortSignal): Promise<void>;
  getAsync(namespaceId: string, signal?: AbortSignal): Promise<ToolNamespace | null>;
  listForUserAsync(userId: string, signal?: AbortSignal): Promise<readonly ToolNamespace[]>;
}
