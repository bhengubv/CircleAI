/**
 * inference/runtime
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// Getting a model onto the device, knowing why when that fails, and the code
// agent that runs on top of it.
//
// THE DOWNLOAD IS THE EXPENSIVE PART, and not in seconds. A 4 GB model on a
// South African mobile bundle is real money, and a gate that defaults to "go
// ahead" spends somebody else's airtime. So the gate defaults to REFUSING on
// anything not known to be free, and says what it would cost.
//
// "DOWNLOAD FAILED" IS NOT A DIAGNOSIS. It sends a person to reboot a router
// when the answer is a captive portal, a clock so wrong that TLS refuses, or a
// disk with no room. The preflight here tells those apart, because each has a
// different fix and only one of them is the router.
//
// NO MODEL NAME IS HARDCODED ANYWHERE. The catalogue supplies them. A hardcoded
// name is a model that cannot be replaced without a release.

// ─────────────────────────────────────────────────────────────────────────────
// Failures

/** A download failed for a reason that is not policy. */
export class ModelDownloadException extends Error {
  constructor(
    readonly modelId: string,
    message: string,
    /** Carried so a caller can retry the right thing instead of everything. */
    readonly fault?: NetworkFault,
  ) {
    super(message);
    this.name = "ModelDownloadException";
  }
}

/**
 * A download was REFUSED, not failed.
 *
 * A separate type because the two demand opposite handling: a failure should be
 * retried and a refusal must never be. Retrying a refusal in a loop is how a
 * gate gets worn down until somebody disables it.
 */
export class ModelDownloadBlockedException extends ModelDownloadException {
  constructor(
    modelId: string,
    readonly reason: string,
    readonly estimatedBytes = 0,
  ) {
    super(modelId, reason);
    this.name = "ModelDownloadBlockedException";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The gate

/** What the link looks like right now. */
export interface NetworkConditions {
  readonly isConnected: boolean;
  /**
   * The operating system's word for it. Trusted when true and NOT trusted when
   * false: a phone tethering to another phone reports unmetered while spending
   * the other phone's bundle.
   */
  readonly isMetered: boolean;
  readonly isRoaming: boolean;
  /** True only for a link known to be free. Unknown counts as NOT free. */
  readonly isKnownUnmetered: boolean;
  readonly estimatedKbps: number;
}

export const networkConditions = (
  partial: Partial<NetworkConditions> = {},
): NetworkConditions =>
  Object.freeze({
    isConnected: partial.isConnected ?? false,
    isMetered: partial.isMetered ?? false,
    isRoaming: partial.isRoaming ?? false,
    isKnownUnmetered: partial.isKnownUnmetered ?? false,
    estimatedKbps: partial.estimatedKbps ?? 0,
  });

/** Decides whether a model may be fetched now. */
export interface ModelDownloadGate {
  /** Returns the reason on ALLOW as well as on refuse, so a log says why
   * something was permitted and not only why it was stopped. */
  mayDownload(modelId: string, sizeBytes: number): { allowed: boolean; reason: string };
}

/**
 * Allows everything.
 *
 * Named so choosing it is a visible decision. Right on a desktop on a fixed
 * line, wrong on a phone.
 */
export class AlwaysAllowDownloadGate implements ModelDownloadGate {
  mayDownload(): { allowed: boolean; reason: string } {
    return { allowed: true, reason: "no gate is configured on this device" };
  }
}

/**
 * Refuses a large download on a link that costs money.
 *
 * THE DEFAULT IS REFUSE ON ANYTHING NOT KNOWN TO BE FREE, which is stricter
 * than refusing on "metered". The operating system reports unmetered for a
 * tether, so trusting it spends the other phone's bundle - which is the exact
 * case this is written for.
 */
export class MeteredNetworkDownloadGate implements ModelDownloadGate {
  /** Below this the cost is not worth an interruption. Roughly a photograph. */
  static readonly FREE_PASS_BYTES = 4 * 1024 * 1024;

  private readonly consented = new Set<string>();

  constructor(
    private readonly conditions: () => NetworkConditions = () => networkConditions(),
    consentedModelIds: readonly string[] = [],
  ) {
    // Consent is PER MODEL. Agreeing to spend a bundle on one model is not
    // agreeing to spend it on every model the catalogue later carries.
    for (const id of consentedModelIds) this.consented.add(id.trim().toLowerCase());
  }

  consent(modelId: string): void {
    this.consented.add(modelId.trim().toLowerCase());
  }

  /** In the units a person thinks in. A bundle is sold in gigabytes, so a
   * refusal saying "4194304000 bytes" has communicated nothing. */
  static describeSize(sizeBytes: number): string {
    if (sizeBytes >= 1 << 30) return `${(sizeBytes / (1 << 30)).toFixed(1)} GB`;
    if (sizeBytes >= 1 << 20) return `${Math.round(sizeBytes / (1 << 20))} MB`;
    return `${Math.max(1, Math.floor(sizeBytes / 1024))} KB`;
  }

  mayDownload(modelId: string, sizeBytes: number): { allowed: boolean; reason: string } {
    const c = this.conditions();
    if (!c.isConnected) return { allowed: false, reason: "this device is not on a network" };
    if (sizeBytes <= MeteredNetworkDownloadGate.FREE_PASS_BYTES) {
      return { allowed: true, reason: "small enough that the link does not matter" };
    }
    if (this.consented.has(modelId.trim().toLowerCase())) {
      return { allowed: true, reason: `you agreed to fetch ${modelId} on this connection` };
    }
    if (c.isRoaming) {
      // Roaming is checked BEFORE metered: a roaming link may report unmetered
      // and still cost more per megabyte than anything else somebody pays for
      // this year.
      return {
        allowed: false,
        reason: `${MeteredNetworkDownloadGate.describeSize(sizeBytes)} while roaming would be expensive - this can wait for Wi-Fi`,
      };
    }
    if (!c.isKnownUnmetered) {
      return {
        allowed: false,
        reason: `this would use about ${MeteredNetworkDownloadGate.describeSize(sizeBytes)} of your data - ask again on Wi-Fi, or say to go ahead`,
      };
    }
    return { allowed: true, reason: "on a connection known to be free" };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Preflight

/** Why a fetch could not happen. Each has a DIFFERENT fix. */
export enum NetworkFault {
  None = "none",
  /** No link at all. The one people expect, and the least common in practice. */
  Offline = "offline",
  /** Names are not resolving while the link is up. Usually a DNS server that
   * went away, not a dead connection. */
  Dns = "dns",
  /** A network that answers everything with a login page. Looks like a
   * successful fetch of the wrong bytes, which is why it is checked for. */
  CaptivePortal = "captive-portal",
  /** TLS refused. Very often a device clock wrong by more than a certificate's
   * validity - nothing to do with the network, and never guessed correctly. */
  Tls = "tls",
  Server = "server",
  Metered = "metered",
  NoSpace = "no-space",
  Timeout = "timeout",
}

/** What is actually wrong, and what to do about it. */
export interface NetworkDiagnosis {
  readonly fault: NetworkFault;
  /** Written for a PERSON, not a log. This is shown on a screen. */
  readonly message: string;
  /** What they can do. Empty when there is nothing - itself worth saying. */
  readonly suggestion: string;
  readonly canRetry: boolean;
}

/** The standard wording per fault, so the same problem reads the same way
 * wherever it surfaces. */
const DIAGNOSES: Readonly<Record<string, Omit<NetworkDiagnosis, "fault">>> = Object.freeze({
  [NetworkFault.Offline]: {
    message: "this device is not on a network",
    suggestion: "connect to Wi-Fi or turn on mobile data",
    canRetry: true,
  },
  [NetworkFault.Dns]: {
    message: "the network is up but names are not resolving",
    suggestion: "this usually fixes itself; if not, the network's DNS is down",
    canRetry: true,
  },
  [NetworkFault.CaptivePortal]: {
    message: "this network wants you to sign in first",
    suggestion: "open a browser and complete the network's login page",
    canRetry: true,
  },
  [NetworkFault.Tls]: {
    message: "the secure connection was refused",
    suggestion: "check this device's date and time - a clock that is wrong breaks every secure connection",
    canRetry: true,
  },
  [NetworkFault.Server]: {
    message: "the server refused the request",
    suggestion: "nothing to do here; it is not this device",
    canRetry: true,
  },
  [NetworkFault.Metered]: {
    message: "this connection costs money",
    suggestion: "wait for Wi-Fi, or say to go ahead anyway",
    canRetry: false,
  },
  [NetworkFault.NoSpace]: {
    message: "there is not enough room on this device",
    suggestion: "free some space and try again",
    canRetry: false,
  },
  [NetworkFault.Timeout]: {
    message: "the connection was too slow to finish",
    suggestion: "try again on a faster connection",
    canRetry: true,
  },
});

export const networkDiagnosis = (fault: NetworkFault): NetworkDiagnosis =>
  fault === NetworkFault.None
    ? Object.freeze({ fault, message: "", suggestion: "", canRetry: true })
    : Object.freeze({ fault, ...DIAGNOSES[fault] });

export const isHealthy = (d: NetworkDiagnosis): boolean => d.fault === NetworkFault.None;

/** Checks whether a fetch can work before starting one. */
export interface NetworkPreflightContract {
  check(url?: string): Promise<NetworkDiagnosis>;
}

/**
 * The default preflight.
 *
 * ORDER MATTERS and it is cheapest-first: no link, then space, then a name,
 * then a handshake. Probing TLS on a device with no link wastes a timeout to
 * learn what one flag already said.
 */
export class NetworkPreflight implements NetworkPreflightContract {
  constructor(
    private readonly conditions: () => NetworkConditions = () => networkConditions(),
    private readonly resolve?: (host: string) => Promise<boolean>,
    private readonly probe?: (url: string) => Promise<number>,
    private readonly freeBytes?: () => number,
    private readonly requiredBytes = 0,
  ) {}

  async check(url = ""): Promise<NetworkDiagnosis> {
    const c = this.conditions();
    if (!c.isConnected) return networkDiagnosis(NetworkFault.Offline);

    // Space is checked BEFORE the network. Spending a gigabyte of somebody's
    // bundle and then failing to write it is the worst possible order.
    if (this.requiredBytes > 0 && this.freeBytes && this.freeBytes() < this.requiredBytes) {
      return networkDiagnosis(NetworkFault.NoSpace);
    }

    const host = url ? /^[a-z][a-z0-9+.-]*:\/\/([^/:?#]+)/i.exec(url)?.[1] ?? "" : "";
    if (host && this.resolve && !(await this.resolve(host))) {
      return networkDiagnosis(NetworkFault.Dns);
    }

    if (url && this.probe) {
      let status: number;
      try {
        status = await this.probe(url);
      } catch (error) {
        const text = (error instanceof Error ? error.message : String(error)).toLowerCase();
        // A certificate error is reported as TLS, not as a generic failure,
        // because the fix is almost always the device clock and nobody guesses
        // that.
        if (text.includes("certificate") || text.includes("ssl") || text.includes("tls")) {
          return networkDiagnosis(NetworkFault.Tls);
        }
        if (text.includes("timeout") || text.includes("timed out")) {
          return networkDiagnosis(NetworkFault.Timeout);
        }
        return networkDiagnosis(NetworkFault.Server);
      }
      if (status === 200 || status === 204) return networkDiagnosis(NetworkFault.None);
      // A redirect to a login page is how a captive portal answers, and it is
      // indistinguishable from success unless it is looked for.
      if ([301, 302, 303, 307, 511].includes(status)) {
        return networkDiagnosis(NetworkFault.CaptivePortal);
      }
      return networkDiagnosis(NetworkFault.Server);
    }

    return c.isKnownUnmetered
      ? networkDiagnosis(NetworkFault.None)
      : networkDiagnosis(NetworkFault.Metered);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Native runtime

/** Where the native pieces landed. */
export interface NativeRuntimePaths {
  readonly libraryDirectory: string;
  readonly resolvedLibrary: string;
  readonly abi: string;
  /** Every path looked in. "libmnn.so not found" is unactionable; the list of
   * places looked in is a diagnosis. */
  readonly searched: readonly string[];
}

/**
 * Finds the native library for THIS device's ABI.
 *
 * Android packs one directory per ABI and a device may run more than one - an
 * arm64 phone happily runs armeabi-v7a. Picking the first that EXISTS rather
 * than the first the device PREFERS silently runs 32-bit code on a 64-bit
 * phone, which works, is slower, and is invisible.
 */
export class NativeLibraryResolver {
  /** Most capable first. The order is the answer, not the contents. */
  static readonly ANDROID_ABIS = Object.freeze(["arm64-v8a", "armeabi-v7a", "x86_64", "x86"]);
  static readonly APPLE_ABIS = Object.freeze(["arm64", "x86_64"]);

  constructor(
    private readonly supportedAbis: readonly string[] = [],
    private readonly exists: (path: string) => boolean = () => false,
    private readonly separator = "/",
  ) {}

  preferredAbis(): readonly string[] {
    const known = this.supportedAbis.filter(
      (a) =>
        NativeLibraryResolver.ANDROID_ABIS.includes(a) ||
        NativeLibraryResolver.APPLE_ABIS.includes(a),
    );
    // The device's own ordering is trusted where it exists; where it does not,
    // the fallback is most-capable-first.
    return known.length ? known : NativeLibraryResolver.ANDROID_ABIS;
  }

  private join(...parts: string[]): string {
    return parts.filter(Boolean).join(this.separator);
  }

  resolve(root: string, libraryName: string): NativeRuntimePaths {
    const searched: string[] = [];
    for (const abi of this.preferredAbis()) {
      const candidate = this.join(root, abi, libraryName);
      searched.push(candidate);
      if (this.exists(candidate)) {
        return Object.freeze({
          libraryDirectory: this.join(root, abi),
          resolvedLibrary: candidate,
          abi,
          searched: Object.freeze([...searched]),
        });
      }
    }
    const flat = this.join(root, libraryName);
    searched.push(flat);
    if (this.exists(flat)) {
      return Object.freeze({
        libraryDirectory: root,
        resolvedLibrary: flat,
        abi: "",
        searched: Object.freeze([...searched]),
      });
    }
    return Object.freeze({
      libraryDirectory: "",
      resolvedLibrary: "",
      abi: "",
      searched: Object.freeze([...searched]),
    });
  }
}

/** How the native runtime is set up. */
export interface MnnRuntimeConfig {
  readonly threadCount: number;
  /**
   * Big cores only by default. Spreading inference across little cores makes it
   * slower AND hotter: the little cores finish late and the big ones idle
   * waiting on the barrier.
   */
  readonly bigCoresOnly: boolean;
  readonly useGpu: boolean;
  readonly useFp16: boolean;
  readonly memoryMode: "low" | "normal" | "high";
}

export const mnnRuntimeConfig = (partial: Partial<MnnRuntimeConfig> = {}): MnnRuntimeConfig =>
  Object.freeze({
    threadCount: partial.threadCount ?? 4,
    bigCoresOnly: partial.bigCoresOnly ?? true,
    useGpu: partial.useGpu ?? false,
    useFp16: partial.useFp16 ?? true,
    memoryMode: partial.memoryMode ?? "low",
  });

/**
 * Half the cores, at least one, at most six.
 *
 * NEVER ALL OF THEM: a runtime that takes every core makes the UI thread wait,
 * and an assistant that freezes the phone while it thinks is worse than one
 * that thinks a little slower.
 */
export const threadsFor = (availableCores: number): MnnRuntimeConfig =>
  mnnRuntimeConfig({ threadCount: Math.max(1, Math.min(6, Math.floor(availableCores / 2))) });

/**
 * What the native side reports about itself.
 *
 * Every value OPTIONAL and absent when unknown. A diagnostics screen that
 * invents a zero where it has no measurement is worse than one that says it
 * does not know, because a zero looks like a finding.
 */
export class MnnNativeDiagnostics {
  private readonly values = new Map<string, unknown>();

  record(key: string, value: Object): void {
    this.values.set(key, value);
  }

  get(key: string): Object {
    return this.values.get(key);
  }

  snapshot(): Readonly<Map<string, unknown>> {
    return Object.freeze(Object.fromEntries(this.values));
  }

  describe(): string {
    if (this.values.size === 0) return "the native runtime has not reported anything";
    return [...this.values.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([k, v]) => `${k}=${String(v)}`)
      .join(", ");
  }
}

/**
 * Gets the native side ready, once.
 *
 * IDEMPOTENT. Preparation runs from whichever call arrives first, and on a
 * phone that is often two at the same time - a warm-up and a real request
 * racing on separate threads.
 */
export class NativeRuntimePrep {
  private paths?: NativeRuntimePaths;
  private prepared = false;

  constructor(
    private readonly resolver: NativeLibraryResolver = new NativeLibraryResolver(),
    readonly diagnostics: MnnNativeDiagnostics = new MnnNativeDiagnostics(),
  ) {}

  get isPrepared(): boolean {
    return this.prepared;
  }

  get resolvedPaths(): NativeRuntimePaths | undefined {
    return this.paths;
  }

  prepare(root: string, libraryName: string): boolean {
    if (this.prepared) return true;
    const paths = this.resolver.resolve(root, libraryName);
    this.paths = paths;
    this.diagnostics.record("abi", paths.abi || "unresolved");
    this.diagnostics.record("searched", paths.searched.length);
    if (!paths.resolvedLibrary) {
      this.diagnostics.record("error", "no native library for this device");
      return false;
    }
    this.diagnostics.record("library", paths.resolvedLibrary);
    this.prepared = true;
    return true;
  }
}

/**
 * Maps weights instead of reading them.
 *
 * A 4 GB model READ into a phone's heap is a 4 GB allocation the system will
 * refuse or kill. Mapped, the pages come in on demand and the kernel evicts
 * them under pressure - the difference between a model that runs on a 6 GB
 * phone and one that does not.
 */
export class MmapWeightLoader {
  constructor(private readonly mapFile?: (path: string, offset: number, length: number) => Uint8Array) {}

  /**
   * Map when the file is more than a QUARTER of memory.
   *
   * Not half: the model is not the only thing running, and by the time a file
   * is half of RAM the allocation has already failed.
   */
  static shouldMap(fileBytes: number, availableRamBytes: number): boolean {
    if (fileBytes <= 0 || availableRamBytes <= 0) return false;
    return fileBytes * 4 > availableRamBytes;
  }

  load(path: string, offset = 0, length = 0): Uint8Array | undefined {
    if (!this.mapFile || length <= 0) return undefined;
    return this.mapFile(path, offset, length);
  }
}

/** Finds the shards a layer-streamed model is split into. */
export class LayerShardDiscovery {
  /**
   * Shards are ordered by their INDEX, parsed as a number.
   *
   * Sorting the file names as text puts shard-10 before shard-2, and a model
   * assembled in that order produces fluent nonsense - the weights are all
   * present and in the wrong layers.
   */
  static order(fileNames: readonly string[]): readonly string[] {
    const indexed = fileNames
      .map((name) => ({ name, index: Number(/(\d+)(?=[^\d]*$)/.exec(name)?.[1] ?? NaN) }))
      .filter((e) => Number.isFinite(e.index));
    if (indexed.length !== fileNames.length) {
      // A name with no index in it means the set cannot be ordered reliably, so
      // NOTHING is returned rather than a guess. A half-ordered model is worse
      // than one that refuses to load.
      return [];
    }
    return Object.freeze(indexed.sort((a, b) => a.index - b.index).map((e) => e.name));
  }

  /** Whether every shard from 0 to n-1 is present. A gap loads silently and
   * produces a model missing a layer. */
  static isComplete(fileNames: readonly string[], expected: number): boolean {
    const indices = new Set(
      fileNames.map((n) => Number(/(\d+)(?=[^\d]*$)/.exec(n)?.[1] ?? NaN)),
    );
    for (let i = 0; i < expected; i++) if (!indices.has(i)) return false;
    return true;
  }
}

/**
 * Holds the LoRA adapters a device has, and which is active.
 *
 * ONE AT A TIME, because stacking adapters compounds their effects in ways
 * neither was trained for - and the result is not "both behaviours", it is a
 * model that behaves like neither.
 */
export class LoRAAdapterManager {
  private readonly adapters = new Map<string, { rank: number; sizeBytes: number }>();
  private active = "";

  register(id: string, rank: number, sizeBytes: number): boolean {
    if (!id.trim() || rank <= 0) return false;
    this.adapters.set(id, { rank, sizeBytes });
    return true;
  }

  get activeAdapter(): string {
    return this.active;
  }

  /** Activating one DEACTIVATES the previous. Not a stack. */
  activate(id: string): boolean {
    if (!this.adapters.has(id)) return false;
    this.active = id;
    return true;
  }

  deactivate(): void {
    this.active = "";
  }

  ids(): readonly string[] {
    return Object.freeze([...this.adapters.keys()].sort());
  }
}

/** Somewhere feedback waits to be used. */
export class FileBackedFeedbackTrainingQueue {
  private readonly pending: { text: string; label: string; atMs: number }[] = [];

  constructor(
    private readonly write?: (json: string) => void,
    /** A cap, because this queue exists on a phone. Beyond it the OLDEST goes,
     * since recent feedback describes the model as it is now. */
    private readonly maxEntries = 500,
  ) {}

  get count(): number {
    return this.pending.length;
  }

  enqueue(text: string, label: string, atMs: number): void {
    this.pending.push({ text, label, atMs });
    while (this.pending.length > this.maxEntries) this.pending.shift();
  }

  /** Flushes to disk and CLEARS only on a successful write. Clearing first
   * loses the queue when the write fails, which is exactly when it matters. */
  flush(): boolean {
    if (!this.write || this.pending.length === 0) return false;
    try {
      this.write(JSON.stringify(this.pending));
    } catch {
      return false;
    }
    this.pending.length = 0;
    return true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Offload

/** Another device that could do the work. */
export interface MeshPeer {
  readonly peerId: string;
  readonly displayName: string;
  /** Whether BOTH devices added each other. Offloading to a peer that has not
   * added us back is sending a prompt to a stranger. */
  readonly mutuallyAdded: boolean;
  readonly ramBytes: number;
  /** MEASURED, not advertised. A peer's own claim about its speed is a claim. */
  readonly measuredTokensPerSecond: number;
  readonly loadAverage: number;
}

/** When work may leave this device. */
export enum MeshOffloadStrategy {
  /** The default. Nothing leaves. */
  Never = "never",
  /** Only when this device genuinely cannot do it at all. */
  OnlyIfIncapable = "only-if-incapable",
  /** When a peer is meaningfully faster AND the person agreed to that peer. */
  PreferFasterPeer = "prefer-faster-peer",
}

/**
 * Whether to offload, and why.
 *
 * The REASON is mandatory. An offload decision without a reason is a decision
 * nobody can review, and this one moves somebody's words to another machine.
 */
export interface OffloadVerdict {
  readonly shouldOffload: boolean;
  readonly reason: string;
  readonly peer?: MeshPeer;
}

/** Applies the strategy. */
export class MeshOffloadPlanner {
  /**
   * A peer must be this much faster before moving work is worth it. Below this
   * the transfer costs more than it saves, and it has also told another device
   * what was asked.
   */
  static readonly SPEEDUP_THRESHOLD = 1.5;

  private readonly consented: ReadonlySet<string>;

  constructor(
    private readonly strategy: MeshOffloadStrategy = MeshOffloadStrategy.Never,
    consentedPeerIds: readonly string[] = [],
  ) {
    this.consented = new Set(consentedPeerIds.map((p) => p.trim()).filter(Boolean));
  }

  decide(
    peers: readonly MeshPeer[],
    localTokensPerSecond: number,
    canRunLocally = true,
  ): OffloadVerdict {
    if (this.strategy === MeshOffloadStrategy.Never) {
      return Object.freeze({
        shouldOffload: false,
        reason: "this device never sends work elsewhere",
      });
    }
    // Consent, then mutual, then capability - in that order, so a peer failing
    // the first test is never evaluated on speed.
    const eligible = peers.filter((p) => this.consented.has(p.peerId) && p.mutuallyAdded);
    if (eligible.length === 0) {
      return Object.freeze({
        shouldOffload: false,
        reason: "no peer has both been agreed to and added this device back",
      });
    }
    if (canRunLocally && this.strategy === MeshOffloadStrategy.OnlyIfIncapable) {
      return Object.freeze({ shouldOffload: false, reason: "this device can do it, so it will" });
    }
    const best = eligible.reduce((a, b) =>
      b.measuredTokensPerSecond > a.measuredTokensPerSecond ? b : a,
    );
    if (best.measuredTokensPerSecond <= 0) {
      return Object.freeze({
        shouldOffload: false,
        reason: "no peer has been measured, only claimed",
      });
    }
    if (!canRunLocally) {
      return Object.freeze({
        shouldOffload: true,
        reason: `this device cannot run it; ${best.peerId} can, and you agreed`,
        peer: best,
      });
    }
    if (localTokensPerSecond <= 0) {
      return Object.freeze({
        shouldOffload: false,
        reason: "this device's own speed is unmeasured",
      });
    }
    const speedup = best.measuredTokensPerSecond / localTokensPerSecond;
    if (speedup < MeshOffloadPlanner.SPEEDUP_THRESHOLD) {
      return Object.freeze({
        shouldOffload: false,
        reason: `${best.peerId} is only ${speedup.toFixed(1)}x faster, which is not worth sending your words to another device`,
      });
    }
    return Object.freeze({
      shouldOffload: true,
      reason: `${best.peerId} is ${speedup.toFixed(1)}x faster, and you agreed to it`,
      peer: best,
    });
  }
}

/**
 * A small model drafts, the big one checks.
 *
 * THE ACCEPTED PREFIX IS WHAT THE BIG MODEL WOULD HAVE PRODUCED ANYWAY, so this
 * is a speed change and not a quality one. That is the whole claim, and it holds
 * only if the check is exact: the moment a draft token is accepted without the
 * target agreeing, the output is the small model's and the claim is false.
 *
 * On the first disagreement the target's own token is taken and the rest of the
 * draft is DISCARDED - keeping any of it would be keeping tokens conditioned on
 * a prefix that did not happen.
 */
export class SpeculativeDecodingPipeline {
  constructor(
    /** Longer drafts win more when right and cost more when wrong. Four is
     * where the two roughly balance on a phone. */
    readonly draftLength = 4,
  ) {
    if (draftLength < 1) throw new Error("a draft must be at least one token");
  }

  /** Returns the accepted prefix and the index of the first disagreement. */
  accept(
    draftTokens: readonly number[],
    targetTokens: readonly number[],
  ): { accepted: number[]; rejectedAt: number } {
    const accepted: number[] = [];
    for (let i = 0; i < draftTokens.length; i++) {
      if (i >= targetTokens.length || targetTokens[i] !== draftTokens[i]) {
        return { accepted, rejectedAt: i };
      }
      accepted.push(draftTokens[i]);
    }
    return { accepted, rejectedAt: draftTokens.length };
  }

  /** One round. Always emits at least ONE token - the target's own at the point
   * of disagreement - so the loop cannot stall on a draft that is always wrong. */
  step(
    draft: (n: number) => number[],
    verify: (drafted: readonly number[]) => number[],
  ): number[] {
    const drafted = draft(this.draftLength);
    const checked = verify(drafted);
    const { accepted, rejectedAt } = this.accept(drafted, checked);
    if (rejectedAt < drafted.length && rejectedAt < checked.length) {
      accepted.push(checked[rejectedAt]);
    } else if (accepted.length === 0 && checked.length > 0) {
      accepted.push(checked[0]);
    }
    return accepted;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Bundles

/** How importing a handed-over model went. */
export enum SideloadOutcome {
  Imported = "imported",
  /** Already present and identical. NOT an error - handing somebody a model
   * they already have is the normal case in a room full of phones. */
  AlreadyPresent = "already-present",
  /** The bytes do not match the digest. The only correct response is refusal. */
  DigestMismatch = "digest-mismatch",
  UnsupportedFormat = "unsupported-format",
  NoSpace = "no-space",
  Refused = "refused",
}

/** What the import did. */
export interface SideloadResult {
  readonly outcome: SideloadOutcome;
  readonly modelId: string;
  readonly installedPath: string;
  readonly bytesWritten: number;
  readonly message: string;
}

const sideloadResult = (partial: Partial<SideloadResult> & { outcome: SideloadOutcome }): SideloadResult =>
  Object.freeze({
    outcome: partial.outcome,
    modelId: partial.modelId ?? "",
    installedPath: partial.installedPath ?? "",
    bytesWritten: partial.bytesWritten ?? 0,
    message: partial.message ?? "",
  });

export const sideloadSucceeded = (r: SideloadResult): boolean =>
  r.outcome === SideloadOutcome.Imported || r.outcome === SideloadOutcome.AlreadyPresent;

/**
 * Compares digests without leaking WHERE they differ.
 *
 * Overkill for a local file and correct anyway: the habit of comparing digests
 * in constant time is worth keeping, because the one place it is skipped is
 * always the place it mattered.
 */
export function constantTimeEquals(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return difference === 0;
}

/**
 * Imports a model handed over offline.
 *
 * THIS IS THE POINT OF THE WHOLE DESIGN: a model arrives on a phone from
 * another phone, over Wi-Fi Direct, with no internet involved. Which is also
 * why the digest check is not optional - a file from a peer is a file from
 * somebody else's device, and a model that has been altered is a model that
 * says what somebody else wanted it to say.
 */
export class SideloadedBundleImporter {
  static readonly SUPPORTED_SUFFIXES = Object.freeze([
    ".onnx", ".gguf", ".mnn", ".bin", ".safetensors",
  ]);

  constructor(
    private readonly installRoot = "",
    private readonly readFile?: (path: string) => Uint8Array,
    private readonly writeFile?: (path: string, bytes: Uint8Array) => void,
    private readonly digestOf?: (bytes: Uint8Array) => string,
    private readonly freeBytes?: () => number,
    private readonly exists: (path: string) => boolean = () => false,
  ) {}

  importBundle(modelId: string, sourcePath: string, expectedSha256: string): SideloadResult {
    if (!expectedSha256) {
      // No digest means no import. Accepting a file on trust because the sender
      // did not supply one is exactly the case this refuses.
      return sideloadResult({
        outcome: SideloadOutcome.Refused,
        modelId,
        message: "this file came with no checksum, so it cannot be trusted",
      });
    }
    const lower = sourcePath.toLowerCase();
    if (!SideloadedBundleImporter.SUPPORTED_SUFFIXES.some((s) => lower.endsWith(s))) {
      return sideloadResult({
        outcome: SideloadOutcome.UnsupportedFormat,
        modelId,
        message: "that is not a model file this build can load",
      });
    }
    if (!this.readFile || !this.digestOf) {
      return sideloadResult({
        outcome: SideloadOutcome.Refused,
        modelId,
        message: "no way to read or check the file",
      });
    }

    const data = this.readFile(sourcePath);
    if (!constantTimeEquals(this.digestOf(data).toLowerCase(), expectedSha256.trim().toLowerCase())) {
      return sideloadResult({
        outcome: SideloadOutcome.DigestMismatch,
        modelId,
        message: "this file does not match its checksum and was not installed",
      });
    }

    const name = sourcePath.split(/[/\\]/).pop() ?? "model.bin";
    const target = [this.installRoot, modelId, name].filter(Boolean).join("/");
    if (this.exists(target)) {
      return sideloadResult({
        outcome: SideloadOutcome.AlreadyPresent,
        modelId,
        installedPath: target,
        bytesWritten: data.length,
        message: "this device already has that model",
      });
    }
    if (this.freeBytes && this.freeBytes() < data.length) {
      return sideloadResult({
        outcome: SideloadOutcome.NoSpace,
        modelId,
        message: `this needs ${MeteredNetworkDownloadGate.describeSize(data.length)} and there is not that much room`,
      });
    }
    if (!this.writeFile) {
      return sideloadResult({
        outcome: SideloadOutcome.Refused,
        modelId,
        message: "no way to write the file",
      });
    }
    this.writeFile(target, data);
    return sideloadResult({
      outcome: SideloadOutcome.Imported,
      modelId,
      installedPath: target,
      bytesWritten: data.length,
      message: "installed from a file on this device",
    });
  }
}

/**
 * Loads a model from an installed bundle.
 *
 * Every file is checked against the manifest BEFORE anything is loaded. A
 * partial bundle that loads two of three files produces a model that runs and
 * is wrong, which is worse than one that does not run.
 */
export class BundleModelLoader {
  constructor(
    private readonly exists: (path: string) => boolean = () => false,
    private readonly sizeOf: (path: string) => number = () => 0,
  ) {}

  /** Reports EVERY problem, not the first. Fixing one missing file, re-running
   * and finding the next is three trips where one would do. */
  verify(root: string, expected: Readonly<Map<string, number>>): { complete: boolean; problems: string[] } {
    const problems: string[] = [];
    for (const name of Object.keys(expected).sort()) {
      const path = `${root}/${name}`;
      if (!this.exists(path)) problems.push(`${name} is missing`);
      else if (expected[name] > 0 && this.sizeOf(path) !== expected[name]) {
        problems.push(`${name} is ${this.sizeOf(path)} bytes, expected ${expected[name]}`);
      }
    }
    return { complete: problems.length === 0, problems };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Selection

/**
 * How good a match a selected model is.
 *
 * ORDERED, so a caller can compare - and the order is the meaning: a caller
 * decides whether to proceed by asking whether the quality is at least
 * something.
 */
export enum SelectionQuality {
  /** Nothing suitable. Not an error - a device that cannot do a thing should
   * say so rather than doing it badly. */
  None = 0,
  /** It will run and be poor. Offered only when the caller asked for anything
   * at all. */
  Degraded = 1,
  Acceptable = 2,
  Good = 3,
  /** Exactly what was asked for, on hardware that fits it. */
  Ideal = 4,
}

/**
 * What a power budget resolved to for one call.
 *
 * Separate from the budget itself because the budget is a REQUEST and this is
 * what the device decided - and on a hot phone at 8% battery those are not the
 * same thing.
 */
export enum Resolution {
  Honoured = "honoured",
  /** Lowered because of battery, heat or a foreground app. The caller is told,
   * so a shorter answer is explained rather than mysterious. */
  Throttled = "throttled",
  /** Raised because the device is charging and cool. */
  Relaxed = "relaxed",
  /** Refused outright. The device will not spend the power at all. */
  Declined = "declined",
}

/** Decides what a power budget actually means right now. */
export class PowerBudgetPolicy {
  /** Below this, everything is throttled whatever was asked for. */
  static readonly LOW_BATTERY_PERCENT = 15;
  /** Below this, generation is declined rather than throttled. */
  static readonly CRITICAL_BATTERY_PERCENT = 5;

  static resolve(
    requestedMaxTokens: number,
    batteryPercent: number | undefined,
    isCharging: boolean | undefined,
    thermalStatus: string,
  ): { resolution: Resolution; maxTokens: number; reason: string } {
    if (thermalStatus === "critical" || thermalStatus === "emergency") {
      return {
        resolution: Resolution.Declined,
        maxTokens: 0,
        reason: "this device is too hot to run a model right now",
      };
    }
    if (batteryPercent !== undefined && !isCharging) {
      if (batteryPercent <= PowerBudgetPolicy.CRITICAL_BATTERY_PERCENT) {
        return {
          resolution: Resolution.Declined,
          maxTokens: 0,
          reason: `${batteryPercent}% battery - this would not leave you a phone`,
        };
      }
      if (batteryPercent <= PowerBudgetPolicy.LOW_BATTERY_PERCENT) {
        return {
          resolution: Resolution.Throttled,
          maxTokens: Math.max(32, Math.floor(requestedMaxTokens / 4)),
          reason: `${batteryPercent}% battery, so this answer will be shorter`,
        };
      }
    }
    if (thermalStatus === "severe") {
      return {
        resolution: Resolution.Throttled,
        maxTokens: Math.max(64, Math.floor(requestedMaxTokens / 2)),
        reason: "this device is warm, so this answer will be shorter",
      };
    }
    // Charging and cool means the budget may be RAISED, which is the case
    // nobody implements and is exactly when a longer answer costs nothing.
    if (isCharging && thermalStatus === "none") {
      return {
        resolution: Resolution.Relaxed,
        maxTokens: Math.floor(requestedMaxTokens * 1.5),
        reason: "charging and cool, so there is room for a fuller answer",
      };
    }
    return { resolution: Resolution.Honoured, maxTokens: requestedMaxTokens, reason: "" };
  }
}

/**
 * Which engine handles which part of a request.
 *
 * A request can need several - speech in, text through, speech out - and each
 * may come from a different place. Held together so a caller can be told the
 * whole route before any of it runs.
 */
export interface ModalityPlan {
  readonly transcribeWith: string;
  readonly generateWith: string;
  readonly speakWith: string;
  readonly seeWith: string;
  readonly quality: SelectionQuality;
  /** Why this plan. Shown when a person asks why the assistant sounds different
   * today. */
  readonly reason: string;
}

/** Picks the speech models for a request. */
export interface SpeechModelSelectorContract {
  plan(language: string, needsSpeechIn: boolean, needsSpeechOut: boolean): ModalityPlan;
}

/**
 * The default selector.
 *
 * NO MODEL NAME IS HARDCODED. The catalogue supplies them, keyed by language,
 * because a hardcoded name is a model that cannot be replaced without a release
 * - and the catalogue is exactly where a device learns it now has a better
 * voice for a language it used to handle badly.
 */
export class SpeechModelSelector implements SpeechModelSelectorContract {
  private readonly transcribers: ReadonlyMap<string, string>;
  private readonly voices: ReadonlyMap<string, string>;

  constructor(
    transcribersByLanguage: Readonly<Map<string, string>> = {},
    voicesByLanguage: Readonly<Map<string, string>> = {},
    private readonly generatorId = "",
  ) {
    this.transcribers = new Map(
      Object.entries(transcribersByLanguage).map(([k, v]) => [k.toLowerCase(), v]),
    );
    this.voices = new Map(Object.entries(voicesByLanguage).map(([k, v]) => [k.toLowerCase(), v]));
  }

  /** `af-ZA` and `af` are the same language for choosing a model. Falling back
   * to the base tag is what makes a device with one Afrikaans voice usable by
   * somebody whose locale says af-NA. */
  private static base(tag: string): string {
    return tag.split(/[-_]/)[0].toLowerCase();
  }

  plan(language: string, needsSpeechIn: boolean, needsSpeechOut: boolean): ModalityPlan {
    const tag = language.trim().toLowerCase();
    const base = SpeechModelSelector.base(tag);
    const lookUp = (table: ReadonlyMap<string, string>) => table.get(tag) ?? table.get(base) ?? "";

    const transcribeWith = needsSpeechIn ? lookUp(this.transcribers) : "";
    const speakWith = needsSpeechOut ? lookUp(this.voices) : "";

    if (!this.generatorId) {
      return Object.freeze({
        transcribeWith,
        generateWith: "",
        speakWith,
        seeWith: "",
        quality: SelectionQuality.None,
        reason: "this device has no text model, so it cannot answer at all",
      });
    }

    const wanted = (needsSpeechIn ? 1 : 0) + (needsSpeechOut ? 1 : 0);
    const got = (transcribeWith ? 1 : 0) + (speakWith ? 1 : 0);
    let quality: SelectionQuality;
    let reason: string;
    if (wanted === 0) {
      quality = SelectionQuality.Ideal;
      reason = "text only, which this device does";
    } else if (got === wanted) {
      quality = SelectionQuality.Ideal;
      reason = `this device has everything needed for ${language}`;
    } else if (got === 0) {
      quality = SelectionQuality.Degraded;
      reason = `this device has no speech models for ${language}, so it will answer in text`;
    } else {
      quality = SelectionQuality.Acceptable;
      reason = `this device has some of what ${language} needs, but not all`;
    }
    return Object.freeze({
      transcribeWith,
      generateWith: this.generatorId,
      speakWith,
      seeWith: "",
      quality,
      reason,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Generators

/**
 * What the model-family generators share.
 *
 * They differ in their PROMPT FORMAT and nothing else that matters here.
 * Getting the format wrong does not fail - it produces a model that answers
 * slightly worse and nobody can say why, which is why each is written out
 * rather than approximated by a shared template.
 */
export abstract class ManagedTextGeneratorBase {
  constructor(
    readonly modelId = "",
    protected readonly generate?: (prompt: string) => Promise<string>,
  ) {}

  get isAvailable(): boolean {
    return this.modelId.length > 0 && this.generate !== undefined;
  }

  abstract formatPrompt(turns: readonly { role: string; content: string }[], system?: string): string;

  async run(turns: readonly { role: string; content: string }[], system = ""): Promise<string> {
    if (!this.generate) throw new Error(`${this.constructor.name} has no model loaded`);
    return this.generate(this.formatPrompt(turns, system));
  }
}

/** Qwen's ChatML format. */
export class QwenTextGenerator extends ManagedTextGeneratorBase {
  formatPrompt(turns: readonly { role: string; content: string }[], system = ""): string {
    const parts: string[] = [];
    if (system) parts.push(`<|im_start|>system\n${system}<|im_end|>`);
    for (const t of turns) parts.push(`<|im_start|>${t.role}\n${t.content}<|im_end|>`);
    // The trailing OPEN tag is what tells the model it is its turn. Leaving it
    // off makes the model continue the conversation as the user, which reads as
    // the assistant talking to itself.
    parts.push("<|im_start|>assistant\n");
    return parts.join("\n");
  }
}

/**
 * Kimi's vision-language format.
 *
 * Images are referenced by a PLACEHOLDER in the text, in order. The order is
 * the binding - an image list that does not match the placeholders describes
 * the wrong picture with complete confidence.
 */
export class KimiVlGenerator extends ManagedTextGeneratorBase {
  static readonly IMAGE_TOKEN = "<|media_start|>image<|media_content|><|media_end|>";

  constructor(
    modelId = "",
    generate?: (prompt: string) => Promise<string>,
    readonly imageCount = 0,
  ) {
    super(modelId, generate);
  }

  formatPrompt(turns: readonly { role: string; content: string }[], system = ""): string {
    const parts: string[] = [];
    if (system) parts.push(`<|im_system|>system<|im_middle|>${system}<|im_end|>`);
    turns.forEach((t, i) => {
      const prefix = i === 0 && this.imageCount > 0 ? KimiVlGenerator.IMAGE_TOKEN : "";
      parts.push(`<|im_user|>${t.role}<|im_middle|>${prefix}${t.content}<|im_end|>`);
    });
    parts.push("<|im_assistant|>assistant<|im_middle|>");
    return parts.join("");
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// The code agent

/** What the model asked for. */
export enum AgentActionKind {
  /** The reply could not be parsed. Kept as a VALUE so the loop can re-prompt
   * rather than fail. */
  Unknown = "unknown",
  ReadFile = "read_file",
  /** A character-range edit. Ranges rather than a diff because a diff that
   * fails to apply leaves the model guessing why; a range either is or is not
   * inside the file. */
  EditFile = "edit_file",
  RunCommand = "run_command",
  SearchCode = "search_code",
  Finish = "finish",
}

/** One parsed action. */
export interface AgentAction {
  readonly kind: AgentActionKind;
  readonly path: string;
  readonly rangeStart: number;
  readonly rangeEnd: number;
  readonly replacement: string;
  readonly command: string;
  readonly query: string;
  readonly topK: number;
  readonly summary: string;
  /** The source JSON, or the whole reply when it did not parse. Without it a
   * loop that goes wrong leaves no evidence of what the model actually said. */
  readonly raw: string;
}

const ACTION_ALIASES: Readonly<Map<string, AgentActionKind>> = Object.freeze({
  read_file: AgentActionKind.ReadFile,
  read: AgentActionKind.ReadFile,
  edit_file: AgentActionKind.EditFile,
  edit: AgentActionKind.EditFile,
  run_command: AgentActionKind.RunCommand,
  run: AgentActionKind.RunCommand,
  search_code: AgentActionKind.SearchCode,
  search: AgentActionKind.SearchCode,
  finish: AgentActionKind.Finish,
  done: AgentActionKind.Finish,
});

/** Turns a model reply into an action. */
export class AgentActionParser {
  /**
   * Finds the first balanced { } run, ignoring braces inside strings.
   *
   * BY BRACE DEPTH rather than by regex, because models routinely wrap the
   * object in prose, in a fenced block, or in both - and a regex that handles
   * two of those three quietly mis-parses the third.
   */
  static extractJsonObject(text: string): string {
    let depth = 0;
    let start = -1;
    let inString = false;
    let escaped = false;
    for (let i = 0; i < text.length; i++) {
      const ch = text[i];
      if (inString) {
        if (escaped) escaped = false;
        else if (ch === "\\") escaped = true;
        else if (ch === '"') inString = false;
        continue;
      }
      if (ch === '"') inString = true;
      else if (ch === "{") {
        if (depth === 0) start = i;
        depth += 1;
      } else if (ch === "}") {
        depth -= 1;
        if (depth === 0 && start >= 0) return text.slice(start, i + 1);
      }
    }
    return "";
  }

  /** NEVER fails - a reply it cannot understand becomes Unknown with `raw` set. */
  static parse(reply: string): AgentAction {
    const unknown = (raw: string): AgentAction =>
      Object.freeze({
        kind: AgentActionKind.Unknown,
        path: "",
        rangeStart: 0,
        rangeEnd: 0,
        replacement: "",
        command: "",
        query: "",
        topK: 10,
        summary: "",
        raw,
      });

    const object = AgentActionParser.extractJsonObject(reply);
    if (!object) return unknown(reply);
    let parsed: Map<string, unknown>;
    try {
      const value = JSON.parse(object);
      if (!value || typeof value !== "object" || Array.isArray(value)) return unknown(reply);
      parsed = value as Map<string, unknown>;
    } catch {
      return unknown(reply);
    }
    const kind = ACTION_ALIASES[String(parsed.action ?? "").trim().toLowerCase()];
    if (!kind) return unknown(reply);
    return Object.freeze({
      kind,
      path: String(parsed.path ?? ""),
      rangeStart: Number(parsed.range_start ?? 0) || 0,
      rangeEnd: Number(parsed.range_end ?? 0) || 0,
      replacement: String(parsed.replacement ?? ""),
      command: String(parsed.command ?? ""),
      query: String(parsed.query ?? ""),
      topK: Number(parsed.top_k ?? 0) || 10,
      summary: String(parsed.summary ?? ""),
      raw: object,
    });
  }
}

/** A command the agent wants to run. */
export interface CommandRequest {
  readonly executable: string;
  readonly arguments: readonly string[];
  readonly workingDirectory: string;
  readonly timeoutMs: number;
}

/** How it went. */
export interface CommandResult {
  /** Whether it ran AT ALL. False with exit code 0 is the shape of a refusal,
   * and a caller that only checks the exit code would read that as success. */
  readonly executed: boolean;
  readonly timedOut: boolean;
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  /** Why it did not run. Populated only when `executed` is false. */
  readonly refusal: string;
}

const commandResult = (partial: Partial<CommandResult> = {}): CommandResult =>
  Object.freeze({
    executed: partial.executed ?? false,
    timedOut: partial.timedOut ?? false,
    exitCode: partial.exitCode ?? 0,
    stdout: partial.stdout ?? "",
    stderr: partial.stderr ?? "",
    refusal: partial.refusal ?? "",
  });

export const commandSucceeded = (r: CommandResult): boolean =>
  r.executed && !r.timedOut && r.exitCode === 0;

/** Runs commands for the agent. */
export interface CommandRunner {
  run(request: CommandRequest): Promise<CommandResult>;
}

/**
 * Refuses everything, with a reason.
 *
 * THE DEFAULT: an agent that can run commands because nobody configured a
 * runner is an agent that can run commands by accident.
 */
export class DisabledCommandRunner implements CommandRunner {
  async run(): Promise<CommandResult> {
    return commandResult({ refusal: "command running is disabled on this device" });
  }
}

/**
 * Runs only what is on the list.
 *
 * An ALLOW-LIST, not a deny-list: a deny-list is a claim to have thought of
 * every dangerous command, and it is wrong the first time somebody pipes one
 * into another.
 */
export class ProcessCommandRunner implements CommandRunner {
  private readonly allowed: ReadonlySet<string>;

  constructor(
    allowedExecutables: readonly string[],
    private readonly spawn?: (
      executable: string,
      args: readonly string[],
      cwd: string,
      timeoutMs: number,
    ) => Promise<{ exitCode: number; stdout: string; stderr: string; timedOut: boolean }>,
    private readonly maxOutputChars = 64 * 1024,
  ) {
    if (allowedExecutables.length === 0) {
      throw new Error(
        "an allow-list is required: a runner with an empty list would run nothing, and one with no list would run everything",
      );
    }
    this.allowed = new Set(
      allowedExecutables.map((e) => (e.split(/[/\\]/).pop() ?? e).toLowerCase()),
    );
  }

  /**
   * Matching is on the RESOLVED base name, not the string the model wrote -
   * otherwise "./git", "git.exe" and a relative path through a symlink are
   * three different things to the check and one thing to the operating system.
   */
  async run(request: CommandRequest): Promise<CommandResult> {
    const base = (request.executable.split(/[/\\]/).pop() ?? request.executable).toLowerCase();
    if (!this.allowed.has(base)) {
      return commandResult({ refusal: `'${base}' is not on the allow-list` });
    }
    if (!this.spawn) return commandResult({ refusal: "no way to run a command on this host" });
    let outcome: { exitCode: number; stdout: string; stderr: string; timedOut: boolean };
    try {
      // NO SHELL. A shell would make the allow-list meaningless the first time
      // an argument contained a semicolon.
      outcome = await this.spawn(
        request.executable,
        request.arguments,
        request.workingDirectory,
        request.timeoutMs,
      );
    } catch (error) {
      return commandResult({ refusal: error instanceof Error ? error.message : String(error) });
    }
    // Output is truncated: a command that prints a hundred megabytes would
    // otherwise be handed to a model as context and cost more than the task.
    const clip = (text: string) =>
      text.length <= this.maxOutputChars ? text : `${text.slice(0, this.maxOutputChars)}\n… truncated`;
    return commandResult({
      executed: true,
      timedOut: outcome.timedOut,
      exitCode: outcome.exitCode,
      stdout: clip(outcome.stdout),
      stderr: clip(outcome.stderr),
    });
  }
}

/** Which class of device this is. */
export enum DeviceTier {
  Wearable = 0,
  LowPhone = 1,
  Phone = 2,
  Tablet = 3,
  Desktop = 4,
  Server = 5,
}

/** What a coding model must meet. */
export interface CodingModelRequirements {
  readonly minParametersBillion: number;
  readonly minRamGb: number;
  readonly minFreeStorageGb: number;
  readonly minDeviceTier: DeviceTier;
  readonly requiredCapabilities: readonly string[];
}

/**
 * The PROVISIONAL floor, labelled so.
 *
 * These are reasoned, not measured - the numbers to trust are the ones a bench
 * run produces on the actual device, and a default that pretends otherwise is a
 * threshold nobody ever revisits.
 */
export const defaultCodingModelRequirements = (): CodingModelRequirements =>
  Object.freeze({
    minParametersBillion: 3,
    minRamGb: 8,
    minFreeStorageGb: 6,
    minDeviceTier: DeviceTier.Tablet,
    requiredCapabilities: Object.freeze(["tools", "reasoning", "long-context"]),
  });

/** One candidate model. */
export interface CodingModelDescriptor {
  readonly modelId: string;
  readonly parametersBillion: number;
  readonly ramGb: number;
  readonly downloadGb: number;
  readonly capabilities: readonly string[];
  readonly note: string;
}

/** Lists coding models. */
export interface CodingModelCatalog {
  list(): readonly CodingModelDescriptor[];
  /** Undefined when nothing meets the floor. Returning the closest and letting
   * it fail on load is how a feature becomes a crash report. */
  bestFor(requirements: CodingModelRequirements): CodingModelDescriptor | undefined;
}

/** Knows about no models. */
export class EmptyCodingModelCatalog implements CodingModelCatalog {
  list(): readonly CodingModelDescriptor[] {
    return [];
  }
  bestFor(): CodingModelDescriptor | undefined {
    return undefined;
  }
}

/** Holds a list a host supplied. */
export class InMemoryCodingModelCatalog implements CodingModelCatalog {
  constructor(private readonly models: readonly CodingModelDescriptor[] = []) {}

  list(): readonly CodingModelDescriptor[] {
    return Object.freeze([...this.models]);
  }

  bestFor(requirements: CodingModelRequirements): CodingModelDescriptor | undefined {
    const wanted = new Set(requirements.requiredCapabilities.map((c) => c.toLowerCase()));
    const viable = this.models.filter(
      (m) =>
        m.parametersBillion >= requirements.minParametersBillion &&
        m.ramGb <= requirements.minRamGb &&
        [...wanted].every((c) => m.capabilities.some((h) => h.toLowerCase() === c)),
    );
    return viable.reduce<CodingModelDescriptor | undefined>(
      (best, m) => (!best || m.parametersBillion > best.parametersBillion ? m : best),
      undefined,
    );
  }
}

/** Decides whether this device can code at all. */
export interface CodingCapabilityPlannerContract {
  isCapable(): { capable: boolean; reason: string };
}

/** The default planner. */
export class CodingCapabilityPlanner implements CodingCapabilityPlannerContract {
  private static readonly GB = 1 << 30;

  constructor(
    private readonly catalog: CodingModelCatalog,
    private readonly ramBytes: number,
    private readonly freeStorageBytes: number,
    private readonly tier: DeviceTier,
  ) {}

  /** The reason names the SHORTFALL - "needs about 8 GB of memory" - rather
   * than a policy identifier, because it is shown to a person. */
  isCapable(): { capable: boolean; reason: string } {
    const req = defaultCodingModelRequirements();
    const ramGb = this.ramBytes / CodingCapabilityPlanner.GB;
    if (ramGb < req.minRamGb) {
      return {
        capable: false,
        reason: `this needs about ${req.minRamGb} GB of memory and this device has ${ramGb.toFixed(1)}`,
      };
    }
    const freeGb = this.freeStorageBytes / CodingCapabilityPlanner.GB;
    if (freeGb < req.minFreeStorageGb) {
      return {
        capable: false,
        reason: `this needs about ${req.minFreeStorageGb} GB free and this device has ${freeGb.toFixed(1)}`,
      };
    }
    if (this.tier < req.minDeviceTier) {
      return { capable: false, reason: "this device is below the class a coding model needs" };
    }
    if (!this.catalog.bestFor(req)) {
      return { capable: false, reason: "no catalogued model meets the floor" };
    }
    return { capable: true, reason: "" };
  }
}

/** Bounds one run. */
export interface CodeAgentOptions {
  /**
   * A TERMINATION GUARANTEE, not a tuning knob. A model that has lost the
   * thread does not stop - it reads the same file again, edits it back, and
   * reads it once more. Without a cap that costs money until somebody notices,
   * and on a phone it costs battery until it is flat.
   */
  readonly maxIterations: number;
  readonly workingDirectory: string;
  readonly maxObservationChars: number;
}

export const codeAgentOptions = (partial: Partial<CodeAgentOptions> = {}): CodeAgentOptions =>
  Object.freeze({
    maxIterations: partial.maxIterations ?? 24,
    workingDirectory: partial.workingDirectory ?? ".",
    maxObservationChars: partial.maxObservationChars ?? 16 * 1024,
  });

/** One turn of the loop. */
export interface CodeAgentStep {
  readonly index: number;
  readonly action: AgentAction;
  /** What came back. Truncated to the budget, and the truncation is MARKED so
   * the model knows it did not see everything. */
  readonly observation: string;
  readonly observationTruncated: boolean;
  readonly durationMs: number;
}

/** The whole run. */
export interface CodeAgentRunResult {
  readonly finished: boolean;
  readonly summary: string;
  readonly steps: readonly CodeAgentStep[];
  /**
   * Set when the loop stopped because it hit the cap rather than because the
   * model said finish. The two must NEVER be confused: one is a completed task
   * and the other is an abandoned one.
   */
  readonly exhaustedIterations: boolean;
  readonly error: string;
}

/** Runs a coding task. */
export interface CodeAgent {
  run(task: string): Promise<CodeAgentRunResult>;
}

/** Runs nothing. */
export class NullCodeAgent implements CodeAgent {
  async run(): Promise<CodeAgentRunResult> {
    return Object.freeze({
      finished: false,
      summary: "",
      steps: [],
      exhaustedIterations: false,
      error: "no code agent configured",
    });
  }
}

/** The default agent. */
export class CodeAgentLoop implements CodeAgent {
  constructor(
    private readonly runner: CommandRunner = new DisabledCommandRunner(),
    private readonly options: CodeAgentOptions = codeAgentOptions(),
    private readonly generate?: (transcript: string) => Promise<string>,
    private readonly readFile?: (path: string) => Promise<string>,
    private readonly now: () => number = () => 0,
  ) {}

  private truncate(text: string): { text: string; truncated: boolean } {
    const cap = this.options.maxObservationChars;
    if (cap <= 0 || text.length <= cap) return { text, truncated: false };
    return {
      text: `${text.slice(0, cap)}\n… truncated; you have not seen the whole thing`,
      truncated: true,
    };
  }

  async run(task: string): Promise<CodeAgentRunResult> {
    if (!this.generate) {
      return Object.freeze({
        finished: false,
        summary: "",
        steps: [],
        exhaustedIterations: false,
        error: "no generator configured",
      });
    }
    let transcript = task;
    const steps: CodeAgentStep[] = [];

    for (let i = 0; i < this.options.maxIterations; i++) {
      const started = this.now();
      let reply: string;
      try {
        reply = await this.generate(transcript);
      } catch (error) {
        return Object.freeze({
          finished: false,
          summary: "",
          steps: Object.freeze([...steps]),
          exhaustedIterations: false,
          error: error instanceof Error ? error.message : String(error),
        });
      }

      const action = AgentActionParser.parse(reply);
      let observation = "";
      let truncated = false;

      if (action.kind === AgentActionKind.Finish) {
        steps.push(
          Object.freeze({
            index: i,
            action,
            observation: "",
            observationTruncated: false,
            durationMs: this.now() - started,
          }),
        );
        return Object.freeze({
          finished: true,
          summary: action.summary,
          steps: Object.freeze([...steps]),
          exhaustedIterations: false,
          error: "",
        });
      }

      if (action.kind === AgentActionKind.ReadFile && this.readFile) {
        try {
          const clipped = this.truncate(
            await this.readFile(`${this.options.workingDirectory}/${action.path}`),
          );
          observation = clipped.text;
          truncated = clipped.truncated;
        } catch (error) {
          observation = `could not read ${action.path}: ${error instanceof Error ? error.message : String(error)}`;
        }
      } else if (action.kind === AgentActionKind.RunCommand) {
        const fields = action.command.split(/\s+/).filter(Boolean);
        if (fields.length > 0) {
          const result = await this.runner.run(
            Object.freeze({
              executable: fields[0],
              arguments: Object.freeze(fields.slice(1)),
              workingDirectory: this.options.workingDirectory,
              timeoutMs: 60_000,
            }),
          );
          if (!result.executed) observation = `refused: ${result.refusal}`;
          else {
            const clipped = this.truncate(result.stdout + result.stderr);
            observation = clipped.text;
            truncated = clipped.truncated;
          }
        }
      } else if (action.kind === AgentActionKind.Unknown) {
        // Re-prompt rather than fail. Answering in prose when asked for JSON is
        // the most common thing a model does.
        observation = "that reply could not be read as an action; answer with a single JSON object";
      }

      steps.push(
        Object.freeze({
          index: i,
          action,
          observation,
          observationTruncated: truncated,
          durationMs: this.now() - started,
        }),
      );
      transcript += `\n${reply}\n${observation}`;
    }

    return Object.freeze({
      finished: false,
      summary: "",
      steps: Object.freeze([...steps]),
      exhaustedIterations: true,
      error: "",
    });
  }
}

/** Wires the code agent. */
export class CodeAgentServiceCollectionExtensions {
  /** Returns a NullCodeAgent when the device cannot run one, rather than an
   * agent that will fail on its first command. */
  static addCodeAgent(
    planner: CodingCapabilityPlannerContract,
    runner: CommandRunner = new DisabledCommandRunner(),
    generate?: (transcript: string) => Promise<string>,
  ): CodeAgent {
    return planner.isCapable().capable && generate
      ? new CodeAgentLoop(runner, codeAgentOptions(), generate)
      : new NullCodeAgent();
  }
}

// The C# spellings, kept so the two trees line up.
export type IModelDownloadGate = ModelDownloadGate;
export type INetworkPreflight = NetworkPreflightContract;
export type ISpeechModelSelector = SpeechModelSelectorContract;
export type ICommandRunner = CommandRunner;
export type ICodeAgent = CodeAgent;
export type ICodingModelCatalog = CodingModelCatalog;
export type ICodingCapabilityPlanner = CodingCapabilityPlannerContract;
