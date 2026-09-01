/**
 * tools_catalog/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// tools_catalog/index.ts
//
// Barrel for the CircleAI.Tools.Catalog port — the composio-pattern provider
// directory + credential vault + OAuth2 flow + quota guard + tool-namespace
// store. Lives in `tools_catalog/` because the existing TS `catalog/` module is
// the unrelated MODEL catalog. Faithful port of the CircleAI.Tools.Catalog C#
// project.
//
// Contents:
//   • Contracts + records — AuthKind, ProviderDescriptor / OAuth2Descriptor /
//     CredentialBundle / QuotaPolicy / ToolNamespace, and the five contracts
//     (IProviderCatalog / ICredentialStore / IOAuth2FlowDriver / IQuotaGuard /
//     IToolNamespaceStore).
//   • Crypto seam — IAeadCipher + a default WebCrypto AES-256-GCM cipher (the
//     injectable primitive behind AesGcmCredentialStore).
//   • In-memory impls — InMemoryProviderCatalog (substring/tag search),
//     AesGcmCredentialStore (encrypt-at-rest), OAuth2FlowDriver (authorize-URL
//     builder + host token-exchange delegate), SlidingWindowQuotaGuard
//     (per-minute + daily + concurrency caps), InMemoryToolNamespaceStore.
//   • Fail-closed Null* defaults.

// Contracts + records.
export {
  AuthKind,
  oauth2Descriptor,
  providerDescriptor,
  credentialBundle,
  quotaPolicy,
  toolNamespace,
  type OAuth2Descriptor,
  type ProviderDescriptor,
  type CredentialBundle,
  type QuotaPolicy,
  type ToolNamespace,
  type IProviderCatalog,
  type ICredentialStore,
  type IOAuth2FlowDriver,
  type IQuotaGuard,
  type IToolNamespaceStore,
} from "./contracts";

// Crypto seam.
export { WebCryptoAesGcmCipher, type IAeadCipher } from "./crypto";

// In-memory implementations.
export {
  InMemoryProviderCatalog,
  AesGcmCredentialStore,
  OAuth2FlowDriver,
  SlidingWindowQuotaGuard,
  InMemoryToolNamespaceStore,
  type OAuth2TokenExchange,
} from "./in_memory_tools_catalog";

// Fail-closed Null* defaults.
export {
  NullProviderCatalog,
  NullCredentialStore,
  NullOAuth2FlowDriver,
  NullQuotaGuard,
  NullToolNamespaceStore,
} from "./null_impls";
