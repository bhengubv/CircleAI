/**
 * tools_catalog/null_impls
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools_catalog/null_impls.ts
//
// Fail-closed defaults for every Tools.Catalog contract. Port of
// CircleAI.Tools.Catalog.NullImplementations.cs.

import type {
  CredentialBundle,
  ICredentialStore,
  IOAuth2FlowDriver,
  IProviderCatalog,
  IQuotaGuard,
  IToolNamespaceStore,
  ProviderDescriptor,
  QuotaPolicy,
  ToolNamespace,
} from "./contracts";

export class NullProviderCatalog implements IProviderCatalog {
  static readonly instance = new NullProviderCatalog();
  get backendId(): string {
    return "null";
  }
  listProvidersAsync(_signal?: AbortSignal): Promise<readonly ProviderDescriptor[]> {
    return Promise.resolve([]);
  }
  getProviderAsync(_p: string, _signal?: AbortSignal): Promise<ProviderDescriptor | null> {
    return Promise.resolve(null);
  }
  searchProvidersAsync(
    _q: string,
    _topK?: number,
    _signal?: AbortSignal,
  ): Promise<readonly ProviderDescriptor[]> {
    return Promise.resolve([]);
  }
}

export class NullCredentialStore implements ICredentialStore {
  static readonly instance = new NullCredentialStore();
  get backendId(): string {
    return "null";
  }
  upsertAsync(_b: CredentialBundle, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_p: string, _u: string, _signal?: AbortSignal): Promise<CredentialBundle | null> {
    return Promise.resolve(null);
  }
  deleteAsync(_p: string, _u: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
}

export class NullOAuth2FlowDriver implements IOAuth2FlowDriver {
  static readonly instance = new NullOAuth2FlowDriver();
  get backendId(): string {
    return "null";
  }
  startAsync(_p: string, _u: string, _r: string, _signal?: AbortSignal): Promise<string> {
    return Promise.resolve("about:blank");
  }
  completeAsync(
    _p: string,
    _u: string,
    _code: string,
    _redirect: string,
    _signal?: AbortSignal,
  ): Promise<CredentialBundle> {
    return Promise.reject(new Error("NullOAuth2FlowDriver: no real provider wired."));
  }
}

export class NullQuotaGuard implements IQuotaGuard {
  static readonly instance = new NullQuotaGuard();
  get backendId(): string {
    return "null";
  }
  tryAcquireAsync(_p: string, _u: string, _signal?: AbortSignal): Promise<boolean> {
    return Promise.resolve(false);
  }
  setPolicyAsync(_policy: QuotaPolicy, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getPolicyAsync(_p: string, _u: string, _signal?: AbortSignal): Promise<QuotaPolicy | null> {
    return Promise.resolve(null);
  }
}

export class NullToolNamespaceStore implements IToolNamespaceStore {
  static readonly instance = new NullToolNamespaceStore();
  get backendId(): string {
    return "null";
  }
  upsertAsync(_ns: ToolNamespace, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_nsId: string, _signal?: AbortSignal): Promise<ToolNamespace | null> {
    return Promise.resolve(null);
  }
  listForUserAsync(_userId: string, _signal?: AbortSignal): Promise<readonly ToolNamespace[]> {
    return Promise.resolve([]);
  }
}
