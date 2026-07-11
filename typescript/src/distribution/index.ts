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

import { createHmac, timingSafeEqual } from "node:crypto";

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

  submitAsync(pkg: AppStorePackage, _signal?: AbortSignal): Promise<boolean> {
    if (pkg == null) throw new Error("package required");
    if (isBlank(pkg.storeName)) throw new Error("StoreName required");
    if (isBlank(pkg.packagePath)) throw new Error("PackagePath required");
    if (isBlank(pkg.version)) throw new Error("Version required");
    if (!DefaultAppStoreSubmitter.KNOWN_STORES.has(pkg.storeName.toLowerCase())) {
      return Promise.resolve(false);
    }
    this.submittedMap.set(`${pkg.storeName}/${pkg.version}`, pkg);
    return Promise.resolve(true);
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

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
