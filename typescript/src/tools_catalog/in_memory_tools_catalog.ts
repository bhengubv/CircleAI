// tools_catalog/in_memory_tools_catalog.ts
//
// Real in-memory tools-catalog primitives. Port of
// CircleAI.Tools.Catalog.InMemoryToolsCatalog.cs. The provider catalog supports
// substring + tag search; credentials are encrypted at rest via AES-GCM (behind
// the IAeadCipher seam) with a host-supplied 32-byte key. The OAuth2 flow driver
// builds standards-compliant authorize URLs; completeAsync delegates the
// token-exchange HTTP call (vendor-specific) to a host function. The quota guard
// enforces per-minute + daily + concurrent caps over a sliding window.

import {
  credentialBundle,
  type CredentialBundle,
  type ICredentialStore,
  type IOAuth2FlowDriver,
  type IProviderCatalog,
  type IQuotaGuard,
  type IToolNamespaceStore,
  type ProviderDescriptor,
  type QuotaPolicy,
  type ToolNamespace,
} from "./contracts.js";
import { WebCryptoAesGcmCipher, type IAeadCipher } from "./crypto.js";
import { randomBytes } from "node:crypto";

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryProviderCatalog
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IProviderCatalog} with substring + tag search. */
export class InMemoryProviderCatalog implements IProviderCatalog {
  // OrdinalIgnoreCase keys: store under the lowercased providerId.
  private readonly items = new Map<string, ProviderDescriptor>();

  get backendId(): string {
    return "in-memory";
  }

  /** Register (or replace) a provider descriptor. */
  register(p: ProviderDescriptor): void {
    if (p === null || p === undefined) throw new Error("provider is required.");
    this.items.set(p.providerId.toLowerCase(), p);
  }

  listProvidersAsync(_signal?: AbortSignal): Promise<readonly ProviderDescriptor[]> {
    const sorted = [...this.items.values()].sort((a, b) => ordinalCompare(a.providerId, b.providerId));
    return Promise.resolve(sorted);
  }

  getProviderAsync(providerId: string, _signal?: AbortSignal): Promise<ProviderDescriptor | null> {
    if (providerId === null || providerId === undefined || providerId.trim().length === 0) {
      throw new Error("providerId required");
    }
    return Promise.resolve(this.items.get(providerId.toLowerCase()) ?? null);
  }

  searchProvidersAsync(
    query: string,
    topK = 8,
    _signal?: AbortSignal,
  ): Promise<readonly ProviderDescriptor[]> {
    if (query === null || query === undefined) throw new Error("query is required.");
    if (topK <= 0) throw new RangeError("topK must be positive.");

    const hits = [...this.items.values()]
      .map((p) => ({ p, s: score(p, query) }))
      .filter((x) => x.s > 0)
      .sort((a, b) => b.s - a.s)
      .slice(0, topK)
      .map((x) => x.p);
    return Promise.resolve(hits);
  }
}

function score(p: ProviderDescriptor, q: string): number {
  const ql = q.toLowerCase();
  let s = 0;
  if (containsCi(p.displayName, ql)) s += 3;
  if (containsCi(p.description, ql)) s += 1;
  if (p.tags?.some((t) => containsCi(t, ql))) s += 2;
  if (p.capabilities?.some((c) => containsCi(c, ql))) s += 2;
  return s;
}

function containsCi(haystack: string | null | undefined, needleLower: string): boolean {
  return haystack != null && haystack.toLowerCase().includes(needleLower);
}

// ─────────────────────────────────────────────────────────────────────────────
// AesGcmCredentialStore
// ─────────────────────────────────────────────────────────────────────────────

/**
 * AES-GCM-encrypted credential store. The host supplies either a 32-byte key
 * (a default {@link WebCryptoAesGcmCipher} is used) or a custom
 * {@link IAeadCipher}. Byte-compatible with the C# `nonce || tag || ciphertext`
 * layout. Mirrors `CircleAI.Tools.Catalog.AesGcmCredentialStore`.
 */
export class AesGcmCredentialStore implements ICredentialStore {
  private readonly cipher: IAeadCipher;
  // Store ciphertext keyed by "provider/user" (Ordinal).
  private readonly enc = new Map<string, Uint8Array>();

  constructor(keyOrCipher: Uint8Array | IAeadCipher) {
    if (keyOrCipher instanceof Uint8Array) {
      if (keyOrCipher.length !== 32) {
        throw new Error("key must be 32 bytes (AES-256-GCM)");
      }
      this.cipher = new WebCryptoAesGcmCipher(keyOrCipher);
    } else if (keyOrCipher !== null && keyOrCipher !== undefined) {
      this.cipher = keyOrCipher;
    } else {
      throw new Error("key must be 32 bytes (AES-256-GCM)");
    }
  }

  get backendId(): string {
    return "aes-gcm";
  }

  async upsertAsync(bundle: CredentialBundle, _signal?: AbortSignal): Promise<void> {
    if (bundle === null || bundle === undefined) throw new Error("bundle is required.");
    const json = serializeBundle(bundle);
    const pt = new TextEncoder().encode(json);
    const combined = await this.cipher.encrypt(pt);
    this.enc.set(key(bundle.providerId, bundle.userId), combined);
  }

  async getAsync(
    providerId: string,
    userId: string,
    _signal?: AbortSignal,
  ): Promise<CredentialBundle | null> {
    if (providerId === null || providerId === undefined || providerId.trim().length === 0) {
      throw new Error("providerId required");
    }
    if (userId === null || userId === undefined || userId.trim().length === 0) {
      throw new Error("userId required");
    }
    const combined = this.enc.get(key(providerId, userId));
    if (combined === undefined) return null;

    const pt = await this.cipher.decrypt(combined);
    if (pt === null) return null; // auth failure → null (C# CryptographicException path)
    try {
      const json = new TextDecoder().decode(pt);
      return deserializeBundle(json);
    } catch {
      return null;
    }
  }

  deleteAsync(providerId: string, userId: string, _signal?: AbortSignal): Promise<void> {
    if (providerId === null || providerId === undefined || providerId.trim().length === 0) {
      throw new Error("providerId required");
    }
    if (userId === null || userId === undefined || userId.trim().length === 0) {
      throw new Error("userId required");
    }
    this.enc.delete(key(providerId, userId));
    return Promise.resolve();
  }
}

interface SerializedBundle {
  readonly providerId: string;
  readonly userId: string;
  readonly fields: Record<string, string>;
  readonly expiresAtUtc: string | null;
}

function serializeBundle(b: CredentialBundle): string {
  const fields: Record<string, string> = {};
  for (const [k, v] of b.fields) fields[k] = v;
  const payload: SerializedBundle = {
    providerId: b.providerId,
    userId: b.userId,
    fields,
    expiresAtUtc: b.expiresAtUtc === null ? null : b.expiresAtUtc.toISOString(),
  };
  return JSON.stringify(payload);
}

function deserializeBundle(json: string): CredentialBundle {
  const p = JSON.parse(json) as SerializedBundle;
  const fields = new Map<string, string>();
  if (p.fields != null) {
    for (const k of Object.keys(p.fields)) fields.set(k, p.fields[k]);
  }
  return credentialBundle(
    p.providerId,
    p.userId,
    fields,
    p.expiresAtUtc == null ? null : new Date(p.expiresAtUtc),
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// OAuth2FlowDriver
// ─────────────────────────────────────────────────────────────────────────────

/** Exchanges an authorisation code for a credential bundle (vendor-specific). */
export type OAuth2TokenExchange = (
  providerId: string,
  userId: string,
  authorizationCode: string,
  redirectUri: string,
  signal?: AbortSignal,
) => Promise<CredentialBundle>;

/**
 * OAuth2 flow driver — builds the authorise URL; the token exchange is delegated
 * to a host function. Mirrors `CircleAI.Tools.Catalog.OAuth2FlowDriver`.
 */
export class OAuth2FlowDriver implements IOAuth2FlowDriver {
  private readonly catalog: IProviderCatalog;
  private readonly clientIdFor: (providerId: string) => string;
  private readonly exchange: OAuth2TokenExchange;

  constructor(
    catalog: IProviderCatalog,
    clientIdFor: (providerId: string) => string,
    exchange: OAuth2TokenExchange,
  ) {
    if (catalog === null || catalog === undefined) throw new Error("catalog is required.");
    if (clientIdFor === null || clientIdFor === undefined) throw new Error("clientIdFor is required.");
    if (exchange === null || exchange === undefined) throw new Error("exchange is required.");
    this.catalog = catalog;
    this.clientIdFor = clientIdFor;
    this.exchange = exchange;
  }

  get backendId(): string {
    return "oauth2";
  }

  async startAsync(
    providerId: string,
    userId: string,
    redirectUri: string,
    signal?: AbortSignal,
  ): Promise<string> {
    if (providerId === null || providerId === undefined || providerId.trim().length === 0) {
      throw new Error("providerId required");
    }
    if (userId === null || userId === undefined || userId.trim().length === 0) {
      throw new Error("userId required");
    }
    if (redirectUri === null || redirectUri === undefined || redirectUri.trim().length === 0) {
      throw new Error("redirectUri required");
    }

    const provider = await this.catalog.getProviderAsync(providerId, signal);
    if (provider === null) throw new Error(`Unknown provider '${providerId}'.`);
    if (provider.oauth2 === null) throw new Error(`Provider '${providerId}' is not OAuth2.`);

    // base64url(16 random bytes), trailing '=' stripped, +/ → -_
    const state = base64UrlNoPad(randomBytes(16));
    const scopes = provider.oauth2.scopes.join(" ");
    const clientId = this.clientIdFor(providerId);
    const url =
      `${provider.oauth2.authorizeUrl}?response_type=code` +
      `&client_id=${webUtilityUrlEncode(clientId)}` +
      `&redirect_uri=${webUtilityUrlEncode(redirectUri)}` +
      `&scope=${webUtilityUrlEncode(scopes)}` +
      `&state=${webUtilityUrlEncode(state)}`;
    return url;
  }

  completeAsync(
    providerId: string,
    userId: string,
    authorizationCode: string,
    redirectUri: string,
    signal?: AbortSignal,
  ): Promise<CredentialBundle> {
    if (providerId === null || providerId === undefined || providerId.trim().length === 0) {
      throw new Error("providerId required");
    }
    if (userId === null || userId === undefined || userId.trim().length === 0) {
      throw new Error("userId required");
    }
    if (authorizationCode === null || authorizationCode === undefined || authorizationCode.trim().length === 0) {
      throw new Error("authorizationCode required");
    }
    if (redirectUri === null || redirectUri === undefined || redirectUri.trim().length === 0) {
      throw new Error("redirectUri required");
    }
    return this.exchange(providerId, userId, authorizationCode, redirectUri, signal);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SlidingWindowQuotaGuard
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Sliding-window per-minute + daily quota + max-concurrent semaphore. Mirrors
 * `CircleAI.Tools.Catalog.SlidingWindowQuotaGuard`. JavaScript is single-
 * threaded, so the C# `lock` sections are ordinary sequential mutations.
 */
export class SlidingWindowQuotaGuard implements IQuotaGuard {
  private readonly policies = new Map<string, QuotaPolicy>();
  private readonly calls = new Map<string, number[]>(); // epoch-ms timestamps
  private readonly inflight = new Map<string, number>();

  get backendId(): string {
    return "sliding-window";
  }

  tryAcquireAsync(providerId: string, userId: string, _signal?: AbortSignal): Promise<boolean> {
    const k = key(providerId, userId);
    const policy = this.policies.get(k);
    if (policy === undefined) return Promise.resolve(true); // no policy = unlimited

    const now = Date.now();
    const oneMinuteAgo = now - 60_000;
    const oneDayAgo = now - 86_400_000;

    // Per-minute cap.
    let list = this.calls.get(k);
    if (list === undefined) {
      list = [];
      this.calls.set(k, list);
    }
    // RemoveAll(t < now - 1min)
    for (let i = list.length - 1; i >= 0; i--) {
      if (list[i] < oneMinuteAgo) list.splice(i, 1);
    }
    if (list.length >= policy.perMinuteCap) return Promise.resolve(false);

    // Daily budget (of what remains after the per-minute prune, count last 24h).
    const dailyCount = list.reduce((acc, t) => (t >= oneDayAgo ? acc + 1 : acc), 0);
    if (dailyCount >= policy.dailyCallBudget) return Promise.resolve(false);

    // Concurrency.
    const inflight = this.inflight.get(k) ?? 0;
    if (inflight >= policy.maxConcurrent) return Promise.resolve(false);

    list.push(now);
    this.inflight.set(k, inflight + 1);
    return Promise.resolve(true);
  }

  /** Release one in-flight slot for (provider, user). */
  release(providerId: string, userId: string): void {
    const k = key(providerId, userId);
    const n = this.inflight.get(k);
    if (n !== undefined && n > 0) this.inflight.set(k, n - 1);
  }

  setPolicyAsync(policy: QuotaPolicy, _signal?: AbortSignal): Promise<void> {
    if (policy === null || policy === undefined) throw new Error("policy is required.");
    this.policies.set(key(policy.providerId, policy.userId), policy);
    return Promise.resolve();
  }

  getPolicyAsync(providerId: string, userId: string, _signal?: AbortSignal): Promise<QuotaPolicy | null> {
    return Promise.resolve(this.policies.get(key(providerId, userId)) ?? null);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryToolNamespaceStore
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IToolNamespaceStore}. */
export class InMemoryToolNamespaceStore implements IToolNamespaceStore {
  private readonly items = new Map<string, ToolNamespace>();

  get backendId(): string {
    return "in-memory";
  }

  upsertAsync(ns: ToolNamespace, _signal?: AbortSignal): Promise<void> {
    if (ns === null || ns === undefined) throw new Error("ns is required.");
    if (ns.namespaceId === null || ns.namespaceId === undefined || ns.namespaceId.trim().length === 0) {
      throw new Error("NamespaceId required");
    }
    this.items.set(ns.namespaceId, ns);
    return Promise.resolve();
  }

  getAsync(namespaceId: string, _signal?: AbortSignal): Promise<ToolNamespace | null> {
    if (namespaceId === null || namespaceId === undefined || namespaceId.trim().length === 0) {
      throw new Error("namespaceId required");
    }
    return Promise.resolve(this.items.get(namespaceId) ?? null);
  }

  listForUserAsync(userId: string, _signal?: AbortSignal): Promise<readonly ToolNamespace[]> {
    if (userId === null || userId === undefined || userId.trim().length === 0) {
      throw new Error("userId required");
    }
    return Promise.resolve([...this.items.values()].filter((n) => n.ownerUserId === userId));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// helpers
// ─────────────────────────────────────────────────────────────────────────────

function key(p: string, u: string): string {
  return `${p}/${u}`;
}

function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** base64url without padding — matches Convert.ToBase64String(...).TrimEnd('=').Replace('+','-').Replace('/','_'). */
function base64UrlNoPad(bytes: Uint8Array): string {
  const b64 = Buffer.from(bytes).toString("base64");
  return b64.replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

/**
 * The analogue of System.Net.WebUtility.UrlEncode: percent-encoding that emits
 * `+` for space (unlike encodeURIComponent, which emits %20). Everything else
 * that encodeURIComponent leaves unescaped stays, and its %20 is rewritten to +.
 */
function webUtilityUrlEncode(s: string): string {
  return encodeURIComponent(s).replace(/%20/g, "+");
}
