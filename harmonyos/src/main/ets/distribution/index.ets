/**
 * distribution/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// distribution/index.ts
// Full-parity port of the scoped CircleAI.Distribution surface (C#). C# is the
// exact spec.
//
// The four distribution "ubiquity rails" in scope — app-store submission,
// signed delta updates, OEM preload catalog, carrier preload catalog — plus
// their records and default in-memory implementations. (The remaining ~73 UBI
// rails in the C# UbiquityRails.cs live outside this work unit's scope.)
//
// C# namespace: CircleAI.Distribution.Ubiquity.
//
// Type mappings (C# → TS):
//   byte[] Payload / Signature          → Uint8Array
//   IReadOnlyDictionary<string,string>  → ReadonlyMap<string,string>
//   IReadOnlyList<string> Partners      → readonly string[]
//   ValueTask<bool>                     → Promise<boolean>
//   HMACSHA256 + FixedTimeEquals        → createHmac('sha256') + timingSafeEqual

import { createHmac, timingSafeEqual } from "../platform";

// ─────────────────────────────────────────────────────────────────────────────
// App-store submission
// ─────────────────────────────────────────────────────────────────────────────

/** A packaged app submission. Mirrors C# `AppStorePackage` record. */
export interface AppStorePackage {
  readonly storeName: string;
  readonly packagePath: string;
  readonly version: string;
  readonly metadata: ReadonlyMap<string, string>;
}

/** Constructs an {@link AppStorePackage}. */
export function appStorePackage(
  storeName: string,
  packagePath: string,
  version: string,
  metadata: ReadonlyMap<string, string>,
): AppStorePackage {
  return { storeName, packagePath, version, metadata };
}

/** Submits packages to an app store. Mirrors C# `IAppStoreSubmitter`. */
export interface IAppStoreSubmitter {
  submitAsync(pkg: AppStorePackage, signal?: AbortSignal): Promise<boolean>;
}

/**
 * Default app-store submitter — validates the package (required fields + a known
 * store) and records the submission keyed by `store/version`. Mirrors C#
 * `DefaultAppStoreSubmitter`.
 */
export class DefaultAppStoreSubmitter implements IAppStoreSubmitter {
  private readonly submittedMap = new Map<string, AppStorePackage>();
  private static readonly KNOWN_STORES = new Set(
    ["PlayStore", "AppStore", "Galaxy Store", "Huawei AppGallery", "Microsoft Store", "F-Droid"].map(
      (s) => s.toLowerCase(),
    ),
  );

  async submitAsync(pkg: AppStorePackage, _signal?: AbortSignal): Promise<boolean> {
    if (pkg == null) throw new Error("package required");
    if (isBlank(pkg.storeName)) throw new Error("StoreName required");
    if (isBlank(pkg.packagePath)) throw new Error("PackagePath required");
    if (isBlank(pkg.version)) throw new Error("Version required");
    if (!DefaultAppStoreSubmitter.KNOWN_STORES.has(pkg.storeName.toLowerCase())) {
      return false;
    }
    this.submittedMap.set(`${pkg.storeName}/${pkg.version}`, pkg);
    return true;
  }

  /** All recorded submissions. Mirrors C# `Submitted`. */
  get submitted(): readonly AppStorePackage[] {
    return [...this.submittedMap.values()];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Signed delta updates
// ─────────────────────────────────────────────────────────────────────────────

/** A signed channel delta update. Mirrors C# `DeltaUpdate` record. */
export interface DeltaUpdate {
  readonly channel: string;
  readonly fromVersion: string;
  readonly toVersion: string;
  readonly payload: Uint8Array;
  readonly signature: Uint8Array;
}

/** Constructs a {@link DeltaUpdate}. */
export function deltaUpdate(
  channel: string,
  fromVersion: string,
  toVersion: string,
  payload: Uint8Array,
  signature: Uint8Array,
): DeltaUpdate {
  return { channel, fromVersion, toVersion, payload, signature };
}

/** Applies signed delta updates. Mirrors C# `ISignedDeltaUpdater`. */
export interface ISignedDeltaUpdater {
  applyAsync(update: DeltaUpdate, signal?: AbortSignal): Promise<boolean>;
}

/**
 * Signed delta updater — verifies an HMAC-SHA256 signature over
 * `Channel|FromVersion|ToVersion|Payload` (constant-time) and enforces the
 * per-channel version chain before applying. Mirrors C# `DefaultSignedDeltaUpdater`.
 */
export class DefaultSignedDeltaUpdater implements ISignedDeltaUpdater {
  private readonly hmacKey: Uint8Array;
  private readonly channelVersion = new Map<string, string>();

  constructor(hmacKey: Uint8Array) {
    if (hmacKey == null || hmacKey.length < 16) {
      throw new Error("hmacKey must be at least 16 bytes");
    }
    this.hmacKey = hmacKey;
  }

  applyAsync(update: DeltaUpdate, _signal?: AbortSignal): Promise<boolean> {
    if (update == null) throw new Error("update required");
    if (isBlank(update.channel) || isBlank(update.toVersion)) return Promise.resolve(false);

    const current = this.channelVersion.get(update.channel);
    if (current !== undefined && current !== update.fromVersion) {
      return Promise.resolve(false);
    }

    // HMAC over Channel|FromVersion|ToVersion| + Payload bytes.
    const prefix = Buffer.from(`${update.channel}|${update.fromVersion}|${update.toVersion}|`, "utf8");
    const msg = Buffer.concat([prefix, Buffer.from(update.payload)]);
    const expected = createHmac("sha256", Buffer.from(this.hmacKey)).update(msg).digest();

    const presented = Buffer.from(update.signature);
    if (expected.length !== presented.length || !timingSafeEqual(expected, presented)) {
      return Promise.resolve(false);
    }
    this.channelVersion.set(update.channel, update.toVersion);
    return Promise.resolve(true);
  }

  /** Current applied version for a channel, or null. Mirrors C# `CurrentVersion`. */
  currentVersion(channel: string): string | null {
    return this.channelVersion.get(channel) ?? null;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Preload catalogs
// ─────────────────────────────────────────────────────────────────────────────

/** OEM preload partner catalog. Mirrors C# `IOemPreloadCatalog`. */
export interface IOemPreloadCatalog {
  readonly partners: readonly string[];
}

/** Default OEM partner catalog. Mirrors C# `DefaultOemPreloadCatalog`. */
export class DefaultOemPreloadCatalog implements IOemPreloadCatalog {
  readonly partners: readonly string[] = ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"];
}

/** Carrier preload catalog. Mirrors C# `ICarrierPreloadCatalog`. */
export interface ICarrierPreloadCatalog {
  readonly carriers: readonly string[];
}

/** Default carrier catalog. Mirrors C# `DefaultCarrierPreloadCatalog`. */
export class DefaultCarrierPreloadCatalog implements ICarrierPreloadCatalog {
  readonly carriers: readonly string[] = [
    "MTN",
    "Vodacom",
    "Cell C",
    "Telkom",
    "Safaricom",
    "Airtel",
  ];
}

// ─────────────────────────────────────────────────────────────────────────────
// Abuse-safe mode (failure-mode rail)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Abuse-safe engagement plus a deterministic, per-owner spoken safety phrase.
 * Mirrors C# `IAbusiveEnvironmentMode`.
 */
export interface IAbusiveEnvironmentMode {
  engageAsync(ownerId: string, signal?: AbortSignal): Promise<void>;
  /** Test phrase the user can speak to silently invoke abuse-safe mode. Generated per owner. */
  safetyPhrase(ownerId: string): string;
  isEngaged(ownerId: string): boolean;
}

/** Benign 8-word vocabulary for {@link DefaultAbusiveEnvironmentMode.safetyPhrase}. Mirrors C# `words`. */
const ABUSE_VOCAB = ["thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen"] as const;

/**
 * FNV-1a 32-bit over UTF-8 — deterministic and byte-identical across all
 * language ports (unlike a per-process-randomised runtime string hash). Uses
 * `Math.imul` + `>>> 0` to keep the multiply in unsigned 32-bit space (a plain
 * `*` overflows to float and diverges from the C# `unchecked((h ^ b) * prime)`).
 * Mirrors C# `Fnv1a32`.
 */
function fnv1a32(s: string): number {
  let h = 2166136261 >>> 0; // FNV offset basis
  for (const b of new TextEncoder().encode(s)) {
    h = Math.imul(h ^ b, 16777619) >>> 0; // XOR byte, multiply by FNV prime (wraps mod 2^32)
  }
  return h >>> 0;
}

/**
 * Default abuse-safe mode. Tracks engagement and issues a deterministic
 * per-owner safety phrase from an 8-word benign vocabulary, seeded by an
 * FNV-1a-32 hash of the owner id so the phrase is stable across restarts AND
 * byte-identical across every language port. Mirrors C# `DefaultAbusiveEnvironmentMode`.
 */
export class DefaultAbusiveEnvironmentMode implements IAbusiveEnvironmentMode {
  private readonly engaged = new Set<string>();
  private readonly phrases = new Map<string, string>();

  engageAsync(ownerId: string, _signal?: AbortSignal): Promise<void> {
    if (isBlank(ownerId)) throw new Error("ownerId required");
    this.engaged.add(ownerId);
    return Promise.resolve();
  }

  safetyPhrase(ownerId: string): string {
    if (isBlank(ownerId)) throw new Error("ownerId required");
    let phrase = this.phrases.get(ownerId);
    if (phrase === undefined) {
      const h = fnv1a32(ownerId);
      phrase = `the ${ABUSE_VOCAB[h % 8]} ${ABUSE_VOCAB[(h >>> 8) % 8]} is ${ABUSE_VOCAB[(h >>> 16) % 8]}`;
      this.phrases.set(ownerId, phrase);
    }
    return phrase;
  }

  isEngaged(ownerId: string): boolean {
    return this.engaged.has(ownerId);
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
