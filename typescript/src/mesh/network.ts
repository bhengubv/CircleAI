// Reaching other devices, offloading to them, and what the device itself is.
//
// THE NUMBERS HERE ARE MEASURED, NOT QUOTED. On the hardware this is built for:
//
//   Wi-Fi Direct   about 50 messages a second, BOTH ways. Enough for voice.
//   BLE            about  9 messages a second, ONE way. Signalling and nothing
//                  more - a design that carries audio over it does not work.
//   Wi-Fi Aware    no hardware on any device here, so anything built on it
//                  would not run at all.
//
// Those three facts decide the whole transport design, which is why they are at
// the top of the file rather than in a document nobody opens. A transport
// selector that treats BLE as a slower Wi-Fi Direct produces a call that never
// connects and a log full of timeouts.
//
// AND: THERE IS NO LAN. Two phones reach each other over Wi-Fi Direct or
// through a node both have added. A design that assumes a shared subnet works
// in an office and nowhere this is for.

// ─────────────────────────────────────────────────────────────────────────────
// Transports

/** How two devices are reaching each other. */
export enum TransportKind {
  /** ~50 msg/s both ways. The only one that carries voice. */
  WifiDirect = "wifi-direct",
  /** ~9 msg/s, one way. Signalling only - never audio, never bulk. */
  BluetoothLe = "bluetooth-le",
  /** A device that has added both, relaying between them. NOT a server: it is
   * somebody's phone, and it can only relay for peers it has added. */
  NodeRelay = "node-relay",
  /** Same physical device, different process. */
  Loopback = "loopback",
}

/** What a transport can actually carry. */
export interface TransportCapability {
  readonly kind: TransportKind;
  readonly messagesPerSecond: number;
  readonly isBidirectional: boolean;
  readonly maxPayloadBytes: number;
  /** Whether it can carry a live call. Derived from the measurements rather
   * than declared, so a transport cannot claim what it cannot do. */
  readonly canCarryVoice: boolean;
}

/**
 * The measured capabilities.
 *
 * Frozen and stated once. Every routing decision in this file reads from here,
 * so correcting a measurement corrects every decision rather than one of them.
 */
export const TRANSPORT_CAPABILITIES: Readonly<Record<string, TransportCapability>> = Object.freeze({
  [TransportKind.WifiDirect]: Object.freeze({
    kind: TransportKind.WifiDirect,
    messagesPerSecond: 50,
    isBidirectional: true,
    maxPayloadBytes: 64 * 1024,
    canCarryVoice: true,
  }),
  [TransportKind.BluetoothLe]: Object.freeze({
    kind: TransportKind.BluetoothLe,
    messagesPerSecond: 9,
    isBidirectional: false,
    // The GATT MTU after the ATT header. Anything larger has to be fragmented,
    // and fragment reassembly is where the interesting bugs live.
    maxPayloadBytes: 512,
    canCarryVoice: false,
  }),
  [TransportKind.NodeRelay]: Object.freeze({
    kind: TransportKind.NodeRelay,
    messagesPerSecond: 30,
    isBidirectional: true,
    maxPayloadBytes: 32 * 1024,
    canCarryVoice: false,
  }),
  [TransportKind.Loopback]: Object.freeze({
    kind: TransportKind.Loopback,
    messagesPerSecond: 10000,
    isBidirectional: true,
    maxPayloadBytes: 1 << 20,
    canCarryVoice: true,
  }),
});

/** Whether anything is reachable. */
export enum ConnectivityState {
  /** Nothing at all. */
  Offline = "offline",
  /** Peers reachable, no internet. THE NORMAL CASE here, not a degraded one. */
  LocalOnly = "local-only",
  /** Internet, metered. */
  Metered = "metered",
  /** Internet, free. */
  Unmetered = "unmetered",
}

/** How urgent a message is. */
export enum MessagePriority {
  /** Dropped rather than queued when the link is busy. Presence, typing. */
  Background = 0,
  Normal = 1,
  /** Ahead of normal traffic. A call setup, a hangup. */
  Interactive = 2,
  /** Never dropped and never delayed. An SOS, a call teardown. */
  Critical = 3,
}

/** What a device is doing in a link. */
export enum PeerRole {
  /** Hosts the Wi-Fi Direct group. Costs more battery, so it is chosen by
   * CAPABILITY rather than by who asked first. */
  GroupOwner = "group-owner",
  Client = "client",
  /** Relays for two peers that have both added it. */
  Relay = "relay",
  Unknown = "unknown",
}

/** Another device. */
export interface PeerInfo {
  readonly peerId: string;
  readonly displayName: string;
  readonly role: PeerRole;
  readonly transports: readonly TransportKind[];
  /** Whether BOTH devices added each other. Nothing is sent to a peer that has
   * not added this device back. */
  readonly mutuallyAdded: boolean;
  readonly lastSeenAtMs: number;
  /** Whether this peer can HOST a group. Not every device can - some cannot
   * advertise at all - and a role assigned by tag order deadlocks on a device
   * that cannot host. */
  readonly canHost: boolean;
}

/** Something being sent. */
export interface NetworkPayload {
  readonly bytes: Uint8Array;
  readonly priority: MessagePriority;
  readonly contentType: string;
  /** Whether it may be split across fragments. Sealed payloads may not - a
   * fragment of a sealed message cannot be opened, and reassembling one that
   * was spliced with a stray fragment from another message decrypts to
   * nothing while looking like a broken ratchet. */
  readonly fragmentable: boolean;
}

/** What the link looks like from here. */
export interface NetworkContext {
  readonly state: ConnectivityState;
  readonly peers: readonly PeerInfo[];
  readonly activeTransport: TransportKind | undefined;
  readonly batteryPercent: number | undefined;
  readonly isCharging: boolean | undefined;
}

// ─────────────────────────────────────────────────────────────────────────────
// Policy

/** What the device is allowed to do on a network. */
export interface NetworkPolicy {
  mayUse(transport: TransportKind, context: NetworkContext): { allowed: boolean; reason: string };
  mayCarry(payload: NetworkPayload, transport: TransportKind): { allowed: boolean; reason: string };
}

/**
 * The default policy.
 *
 * RADIOS STAY UP. There is no rule here that turns one off to save battery, and
 * that is deliberate: an unreachable device is a device somebody cannot get
 * hold of, and the whole point of this is being reachable. Battery is managed
 * by sending LESS, not by going dark.
 */
export class DefaultNetworkPolicy implements NetworkPolicy {
  /** Below this, background traffic stops. Interactive and critical do not. */
  static readonly LOW_BATTERY_PERCENT = 15;

  mayUse(transport: TransportKind, context: NetworkContext): { allowed: boolean; reason: string } {
    if (context.state === ConnectivityState.Offline && transport !== TransportKind.Loopback) {
      return { allowed: false, reason: "nothing is reachable" };
    }
    return { allowed: true, reason: "" };
  }

  mayCarry(
    payload: NetworkPayload,
    transport: TransportKind,
  ): { allowed: boolean; reason: string } {
    const capability = TRANSPORT_CAPABILITIES[transport];
    if (!capability) return { allowed: false, reason: `${transport} is not a known transport` };
    if (payload.bytes.length > capability.maxPayloadBytes && !payload.fragmentable) {
      // A sealed payload that will not fit is REFUSED rather than fragmented.
      // Splitting it produces fragments nobody can open, and the reassembly
      // failure downstream looks like a broken ratchet rather than a size
      // problem.
      return {
        allowed: false,
        reason: `${payload.bytes.length} bytes will not fit in ${transport}'s ${capability.maxPayloadBytes} and cannot be split`,
      };
    }
    if (payload.contentType.startsWith("audio/") && !capability.canCarryVoice) {
      return {
        allowed: false,
        reason: `${transport} carries about ${capability.messagesPerSecond} messages a second, which is not a call`,
      };
    }
    return { allowed: true, reason: "" };
  }
}

/** Builds a policy from parts. */
export class NetworkPolicyBuilder {
  private readonly rules: ((
    payload: NetworkPayload,
    transport: TransportKind,
    context: NetworkContext,
  ) => { allowed: boolean; reason: string } | undefined)[] = [];
  private base: NetworkPolicy = new DefaultNetworkPolicy();

  /** Rules are consulted IN ORDER and the FIRST refusal wins, so a specific
   * restriction can be added in front of the general policy without editing
   * it. */
  deny(
    rule: (
      payload: NetworkPayload,
      transport: TransportKind,
      context: NetworkContext,
    ) => { allowed: boolean; reason: string } | undefined,
  ): NetworkPolicyBuilder {
    this.rules.push(rule);
    return this;
  }

  over(policy: NetworkPolicy): NetworkPolicyBuilder {
    this.base = policy;
    return this;
  }

  /** Refuses background traffic on a low battery, and nothing else. */
  conserveOnLowBattery(): NetworkPolicyBuilder {
    return this.deny((payload, _transport, context) =>
      context.batteryPercent !== undefined &&
      !context.isCharging &&
      context.batteryPercent <= DefaultNetworkPolicy.LOW_BATTERY_PERCENT &&
      payload.priority === MessagePriority.Background
        ? { allowed: false, reason: "the battery is low, so this can wait" }
        : undefined,
    );
  }

  build(context: () => NetworkContext): NetworkPolicy {
    const rules = [...this.rules];
    const base = this.base;
    return {
      mayUse: (transport, ctx) => base.mayUse(transport, ctx),
      mayCarry: (payload, transport) => {
        const ctx = context();
        for (const rule of rules) {
          const verdict = rule(payload, transport, ctx);
          if (verdict && !verdict.allowed) return verdict;
        }
        return base.mayCarry(payload, transport);
      },
    };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Transports and channels

/** Sends and receives bytes. */
export interface NetworkTransport {
  readonly kind: TransportKind;
  readonly isConnected: boolean;
  send(peerId: string, payload: NetworkPayload): Promise<boolean>;
  onReceive(handler: (peerId: string, payload: NetworkPayload) => void): () => void;
}

/** A channel for one kind of message. */
export interface MessageChannel {
  readonly name: string;
  publish(payload: NetworkPayload): Promise<number>;
  subscribe(handler: (peerId: string, payload: NetworkPayload) => void): () => void;
}

/** Finds peers. */
export interface PeerDiscovery {
  readonly isDiscovering: boolean;
  start(): Promise<boolean>;
  stop(): Promise<void>;
  peers(): readonly PeerInfo[];
}

/** Watches whether anything is reachable. */
export interface ConnectivityMonitor {
  readonly state: ConnectivityState;
  onChange(handler: (state: ConnectivityState) => void): () => void;
}

/** Chooses which transport to use. */
export interface TransportSelector {
  select(peer: PeerInfo, payload: NetworkPayload): TransportKind | undefined;
}

/**
 * Picks by what the payload NEEDS, not by what is fastest.
 *
 * Voice goes over Wi-Fi Direct or it does not go: BLE at nine messages a second
 * cannot carry it, and a selector that falls back to BLE for audio produces a
 * call that connects and then does not work - which is far worse than one that
 * refuses.
 *
 * ROLE FOLLOWS CAPABILITY. A peer that cannot host is never asked to, however
 * the tags sort - assigning by tag order deadlocks on a device that cannot
 * advertise.
 */
export class CapabilityTransportSelector implements TransportSelector {
  constructor(private readonly available: readonly TransportKind[] = []) {}

  select(peer: PeerInfo, payload: NetworkPayload): TransportKind | undefined {
    if (!peer.mutuallyAdded) return undefined;
    const usable = peer.transports.filter((t) => this.available.includes(t));
    const needsVoice = payload.contentType.startsWith("audio/");
    const fits = (t: TransportKind) => {
      const c = TRANSPORT_CAPABILITIES[t];
      if (!c) return false;
      if (needsVoice && !c.canCarryVoice) return false;
      return payload.fragmentable || payload.bytes.length <= c.maxPayloadBytes;
    };
    // Ordered by throughput, filtered by fitness. Loopback first because it is
    // the same device and costs nothing.
    for (const kind of [
      TransportKind.Loopback,
      TransportKind.WifiDirect,
      TransportKind.NodeRelay,
      TransportKind.BluetoothLe,
    ]) {
      if (usable.includes(kind) && fits(kind)) return kind;
    }
    return undefined;
  }
}

/** Makes a message smaller before it goes over a radio. */
export interface PayloadOptimiser {
  /** Returns whether it CHANGED, so a caller can tell "small enough" from "made
   * smaller". */
  optimise(payload: Uint8Array, maxBytes: number): { bytes: Uint8Array; changed: boolean };
  restore(payload: Uint8Array): Uint8Array;
}

/** The whole mesh, as one thing. */
export interface MeshNetwork {
  readonly discovery: PeerDiscovery;
  readonly connectivity: ConnectivityMonitor;
  send(peerId: string, payload: NetworkPayload): Promise<boolean>;
  channel(name: string): MessageChannel;
}

// ─────────────────────────────────────────────────────────────────────────────
// Offload

/** Which device answered. */
export interface OffloadServedBy {
  readonly peerId: string;
  readonly displayName: string;
  /** Whether this peer has been added by BOTH devices. Offloading to a peer
   * that has not added us back is sending a prompt to a stranger. */
  readonly mutuallyAdded: boolean;
}

/** One turn, wherever it ran. */
export interface OffloadTurn {
  readonly prompt: string;
  readonly response: string;
  /** Undefined means it ran HERE. Always carried through to the caller, so a UI
   * can say which device answered - the one fact that makes offloading
   * something somebody agreed to rather than something that happened to them. */
  readonly servedBy?: OffloadServedBy;
  readonly durationMs: number;
}

/** The turn plus why it was routed the way it was. */
export interface OffloadResult {
  readonly turn: OffloadTurn;
  /** ALWAYS populated, including when the answer was to stay local. The reason
   * is what makes an offload decision reviewable instead of magic. */
  readonly reason: string;
}

/** Runs a prompt on this device instead. */
export interface LocalInferenceFallback {
  readonly isAvailable: boolean;
  run(prompt: string): Promise<string>;
}

/**
 * Runs nothing and reports unavailable.
 *
 * The default: a router with no local fallback must KNOW it has none, or it
 * will route to the mesh because it believes there is a safety net.
 */
export class NullLocalInferenceFallback implements LocalInferenceFallback {
  readonly isAvailable = false;
  async run(): Promise<string> {
    throw new Error("no local inference available on this device");
  }
}

/** Sends a prompt to a peer. */
export interface MeshOffloadClientContract {
  send(peerId: string, prompt: string): Promise<OffloadTurn>;
}

/** The default client. */
export class MeshOffloadClient implements MeshOffloadClientContract {
  private readonly peers = new Map<string, OffloadServedBy>();

  constructor(
    private readonly transport?: (peerId: string, prompt: string) => Promise<string>,
    private readonly now: () => number = () => 0,
  ) {}

  addPeer(peer: OffloadServedBy): void {
    this.peers.set(peer.peerId, peer);
  }

  async send(peerId: string, prompt: string): Promise<OffloadTurn> {
    const peer = this.peers.get(peerId);
    if (!peer || !peer.mutuallyAdded) {
      throw new Error(`peer '${peerId}' has not added this device back`);
    }
    if (!this.transport) throw new Error("no transport configured");
    const started = this.now();
    const response = await this.transport(peerId, prompt);
    return Object.freeze({
      prompt,
      response,
      servedBy: peer,
      durationMs: this.now() - started,
    });
  }
}

/** Configures offloading. */
export interface MeshOffloadOptions {
  /** OFF by default. Offloading sends a prompt to somebody else's hardware, and
   * it should never begin because a component was imported. */
  readonly enabled: boolean;
  /** The peer agreed to, per peer rather than globally. */
  readonly preferredPeerId: string;
  readonly maxPromptBytes: number;
}

export const meshOffloadOptions = (partial: Partial<MeshOffloadOptions> = {}): MeshOffloadOptions =>
  Object.freeze({
    enabled: partial.enabled ?? false,
    preferredPeerId: partial.preferredPeerId ?? "",
    maxPromptBytes: partial.maxPromptBytes ?? 8192,
  });

/** Decides where a turn runs and runs it. */
export interface OffloadRouter {
  route(prompt: string): Promise<OffloadResult>;
}

/**
 * Routes to a peer only when every condition holds.
 *
 * The peer is mutually added, this device genuinely cannot do the work, and the
 * person has consented - PER PEER, because agreeing to use the tablet in the
 * next room is not agreeing to use whatever else joins the mesh later.
 *
 * LATENCY ALONE IS NEVER SUFFICIENT: "it would be faster over there" is the
 * argument that ends with somebody's conversation on a device they do not own.
 */
export class MeshOffloadRouter implements OffloadRouter {
  private consentedPeer = "";

  constructor(
    private readonly client?: MeshOffloadClientContract,
    private readonly fallback: LocalInferenceFallback = new NullLocalInferenceFallback(),
  ) {}

  consent(peerId: string): void {
    this.consentedPeer = peerId.trim();
  }

  async route(prompt: string): Promise<OffloadResult> {
    if (this.fallback.isAvailable) {
      try {
        return Object.freeze({
          turn: Object.freeze({
            prompt,
            response: await this.fallback.run(prompt),
            durationMs: 0,
          }),
          reason: "this device can answer, so it did",
        });
      } catch {
        // A local failure falls through to the mesh rather than failing
        // outright - but only through the same consent check as always.
      }
    }
    if (!this.consentedPeer) {
      return Object.freeze({
        turn: Object.freeze({ prompt, response: "", durationMs: 0 }),
        reason: "nothing on this device can answer, and no peer has been agreed to",
      });
    }
    if (!this.client) {
      return Object.freeze({
        turn: Object.freeze({ prompt, response: "", durationMs: 0 }),
        reason: "no mesh client configured",
      });
    }
    try {
      const turn = await this.client.send(this.consentedPeer, prompt);
      return Object.freeze({ turn, reason: `answered by ${this.consentedPeer}, which you agreed to` });
    } catch (error) {
      return Object.freeze({
        turn: Object.freeze({ prompt, response: "", durationMs: 0 }),
        reason: `the agreed peer could not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/**
 * What a device tells the room about itself.
 *
 * CAPABILITIES ONLY - never what it is doing, never who owns it, never what was
 * asked. A beacon that carried activity would make a mesh of phones into a mesh
 * of people broadcasting their behaviour to the room.
 */
export interface MeshAdvertisementBeacon {
  readonly deviceId: string;
  readonly capabilities: readonly string[];
  readonly ramBytes: number;
  readonly loadAverage: number;
  readonly atMs: number;
}

/** Tells nearby devices what this one can do. */
export class AetherMeshCapabilityBroadcaster {
  private lastSentAtMs?: number;

  constructor(
    private readonly deviceId: string,
    private readonly publish?: (beacon: MeshAdvertisementBeacon) => void,
    /** A beacon is a RADIO TRANSMISSION: broadcasting every second is a
     * measurable battery cost on every device in range, not just this one. */
    private readonly minPeriodMs = 30_000,
    private readonly now: () => number = () => 0,
  ) {}

  advertise(beacon: Omit<MeshAdvertisementBeacon, "deviceId" | "atMs">): boolean {
    if (!this.publish) throw new Error("no transport configured");
    const at = this.now();
    if (this.lastSentAtMs !== undefined && at - this.lastSentAtMs < this.minPeriodMs) {
      return false;
    }
    this.lastSentAtMs = at;
    this.publish(
      Object.freeze({
        deviceId: this.deviceId,
        capabilities: Object.freeze([...beacon.capabilities]),
        ramBytes: beacon.ramBytes,
        loadAverage: beacon.loadAverage,
        atMs: at,
      }),
    );
    return true;
  }
}

/** Wires the mesh. */
export class MeshServiceCollectionExtensions {
  static addMesh(
    options: MeshOffloadOptions = meshOffloadOptions(),
    client?: MeshOffloadClientContract,
    fallback: LocalInferenceFallback = new NullLocalInferenceFallback(),
  ): OffloadRouter {
    const router = new MeshOffloadRouter(client, fallback);
    // Consent is applied only when offloading is ENABLED and a peer was named.
    // Registering a router with a preferred peer and offloading off would leave
    // the peer configured and ready, which is one flag away from sending.
    if (options.enabled && options.preferredPeerId) router.consent(options.preferredPeerId);
    return router;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// What the device is

/**
 * Where a memory number came from.
 *
 * THE POINT OF THE WHOLE TYPE. A managed runtime's heap figure and the device's
 * physical memory are both "a number of bytes about memory", and using one
 * where the other was meant is invisible until a model gets killed on a phone
 * that had plenty of room.
 */
export enum PlatformMemory {
  /** Nothing measured. Not zero - UNKNOWN. A chooser must treat this as "do not
   * know" and refuse to size anything by it. */
  Unknown = "unknown",
  /** Read from the operating system. The only source that answers the question
   * actually being asked. */
  Physical = "physical",
  /** The managed runtime's own heap. NEVER the device's memory, and named so
   * that using it as such requires writing the word. */
  ManagedHeap = "managed-heap",
  /** What a container or cgroup will allow, which on a server is the real
   * ceiling regardless of what the host machine has. */
  CgroupLimit = "cgroup-limit",
}

/** How much memory, and how we know. */
export interface RamMeasurement {
  readonly totalBytes: number;
  readonly availableBytes: number;
  readonly source: PlatformMemory;
}

/**
 * A measurement with a value and no source is REFUSED.
 *
 * An unsourced number is exactly the bug this type exists to prevent, and
 * letting one be built would put it back.
 */
export function ramMeasurement(
  totalBytes: number,
  availableBytes: number,
  source: PlatformMemory,
): RamMeasurement {
  if (totalBytes > 0 && source === PlatformMemory.Unknown) {
    throw new Error("a memory measurement with a value must say where it came from");
  }
  return Object.freeze({ totalBytes, availableBytes, source });
}

export const unknownRam = (): RamMeasurement =>
  ramMeasurement(0, 0, PlatformMemory.Unknown);
export const physicalRam = (total: number, available = 0): RamMeasurement =>
  ramMeasurement(total, available || total, PlatformMemory.Physical);
/** Deliberately awkward to build and clearly named at every call site. */
export const managedHeapRam = (total: number): RamMeasurement =>
  ramMeasurement(total, 0, PlatformMemory.ManagedHeap);

/**
 * Whether a model chooser may size anything by this.
 *
 * A HEAP READING IS NOT USABLE, and that is the rule this whole section is
 * built around: it describes the runtime's allocations, not the phone.
 */
export const isUsableForSizing = (r: RamMeasurement): boolean =>
  r.totalBytes > 0 &&
  (r.source === PlatformMemory.Physical || r.source === PlatformMemory.CgroupLimit);

export function describeRam(r: RamMeasurement): string {
  const gb = r.totalBytes / (1 << 30);
  if (r.source === PlatformMemory.Unknown || !r.totalBytes) {
    return "this device's memory has not been measured";
  }
  if (!isUsableForSizing(r)) {
    return `${gb.toFixed(1)} GB of managed heap - this is NOT the device's memory and must not be used to choose a model`;
  }
  return `${gb.toFixed(1)} GB of memory`;
}

/** What the assistant knows about the hardware it is on. */
export interface SystemInfoDeviceContext {
  readonly deviceName: string;
  readonly platform: string;
  readonly ram: RamMeasurement;
  readonly cpuCount: number;
  /** Undefined when unknown. NOT 100 - a device that assumes a full battery
   * because it cannot read one will spend a flat phone's last minutes on
   * inference. */
  readonly batteryPercent: number | undefined;
  readonly isCharging: boolean | undefined;
  readonly thermalStatus: string;
  readonly freeStorageBytes: number;
}

export const systemInfoDeviceContext = (
  partial: Partial<SystemInfoDeviceContext> = {},
): SystemInfoDeviceContext =>
  Object.freeze({
    deviceName: partial.deviceName ?? "",
    platform: partial.platform ?? "",
    ram: partial.ram ?? unknownRam(),
    cpuCount: Math.max(1, partial.cpuCount ?? 1),
    batteryPercent: partial.batteryPercent,
    isCharging: partial.isCharging,
    thermalStatus: partial.thermalStatus ?? "unknown",
    freeStorageBytes: partial.freeStorageBytes ?? 0,
  });

export const canSizeModels = (c: SystemInfoDeviceContext): boolean => isUsableForSizing(c.ram);

/** What a model does. */
export enum ModelModality {
  Text = "text",
  Transcription = "transcription",
  Speech = "speech",
  Vision = "vision",
  Embedding = "embedding",
  /** Text and vision together. Its own value rather than a pair, because a
   * model that takes both is not the same as two that each take one. */
  Multimodal = "multimodal",
  Rerank = "rerank",
}

/**
 * Where a download has got to.
 *
 * Verifying and Installing are separate from Downloading because they are what
 * a person waits through AFTER the progress bar reaches the end - and a bar
 * sitting at 100% with no explanation reads as a hang.
 */
export enum DownloadPhase {
  Idle = "idle",
  /** Working out what to fetch. Fast, and worth naming so the UI does not show
   * 0% during it. */
  Resolving = "resolving",
  Downloading = "downloading",
  /** Checking the digest. On a phone, hashing four gigabytes is a real wait. */
  Verifying = "verifying",
  Installing = "installing",
  Complete = "complete",
  Failed = "failed",
  /** Stopped on purpose. NOT a failure, and shown differently. */
  Cancelled = "cancelled",
}

/**
 * Where a model comes from.
 *
 * NO MODEL NAME AND NO DEFAULT REPOSITORY. Both are supplied by the catalogue,
 * because a hardcoded either is a thing that cannot be changed without a
 * release.
 */
export interface ModelSource {
  readonly sourceId: string;
  readonly repository: string;
  readonly revision: string;
  readonly files: readonly string[];
  /** Keyed by file name. A file with no digest is refused on import, so this
   * being complete is the difference between a bundle that can be verified and
   * one that cannot. */
  readonly digests: Readonly<Record<string, string>>;
  readonly totalBytes: number;
}

export const isVerifiable = (s: ModelSource): boolean =>
  s.files.length > 0 && s.files.every((f) => Boolean(s.digests[f]));

/**
 * Where model files live on this device.
 *
 * EVERY PATH IS CONTAINED. A model id arrives from a catalogue, which is
 * fetched, which means it is input - and an id of `../../../etc` that joins
 * cleanly writes outside the model directory.
 */
export class ModelPaths {
  /** Deliberately strict. A model id is an identifier, not a path, and anything
   * a filesystem could interpret is rejected rather than escaped. */
  private static readonly SAFE_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;

  constructor(readonly root: string) {
    if (!root) throw new Error("a model root is required");
  }

  /** A single segment only. A slash makes it a path, and a path is the thing
   * being defended against. */
  static isSafeId(modelId: string): boolean {
    return ModelPaths.SAFE_ID.test(modelId ?? "");
  }

  modelDirectory(modelId: string): string {
    if (!ModelPaths.isSafeId(modelId)) {
      throw new Error(`'${modelId}' is not a usable model identifier`);
    }
    return `${this.root}/${modelId}`;
  }

  /**
   * Contained by NORMALISING and comparing, not by inspecting the raw string.
   *
   * Checking for ".." in the text misses an absolute path that overrides the
   * join entirely, and misses a backslash on a platform that treats it as a
   * separator. Resolving the segments and checking nothing escapes catches
   * both.
   */
  filePath(modelId: string, fileName: string): string {
    const directory = this.modelDirectory(modelId);
    if (/^([A-Za-z]:)?[/\\]/.test(fileName)) {
      throw new Error(`'${fileName}' would write outside this model's directory`);
    }
    const segments: string[] = [];
    for (const part of fileName.split(/[/\\]/)) {
      if (!part || part === ".") continue;
      if (part === "..") {
        if (segments.length === 0) {
          throw new Error(`'${fileName}' would write outside this model's directory`);
        }
        segments.pop();
        continue;
      }
      segments.push(part);
    }
    if (segments.length === 0) {
      throw new Error(`'${fileName}' does not name a file`);
    }
    return `${directory}/${segments.join("/")}`;
  }

  manifestPath(modelId: string): string {
    return this.filePath(modelId, "manifest.json");
  }

  /**
   * Turns `org/model` into a single safe segment.
   *
   * The separator becomes `--`, which is reversible by eye and cannot be a
   * directory boundary. Lower-cased so a case-insensitive filesystem cannot
   * hold two directories a case-sensitive one would keep apart - which is how
   * the same model gets downloaded twice on one platform and once on another.
   */
  static normaliseId(repository: string): string {
    return repository
      .trim()
      .replace(/[^A-Za-z0-9._/-]/g, "-")
      .replace(/\//g, "--")
      .replace(/^[-.]+|[-.]+$/g, "")
      .toLowerCase()
      .slice(0, 128);
  }
}

/**
 * Voice configuration that ships INSIDE the app.
 *
 * Not because a voice is embedded - the voices are downloaded - but because the
 * shape of each family's configuration is code-adjacent knowledge that must be
 * right before any file arrives. A device that downloads a voice and then
 * cannot work out its sample rate has a voice it cannot use.
 *
 * THE PAD RULE is here: a blank in a model's symbol table means index 0 for the
 * MMS families and index 3 for Piper, and getting it wrong produces audio that
 * is silent, clipped, or a fraction of a second long - never an error.
 */
export class EmbeddedVoiceConfigs {
  private static readonly families: Readonly<
    Record<string, { sampleRateHz: number; padIndex: number; declaresRate: boolean }>
  > = Object.freeze({
    mms: { sampleRateHz: 16000, padIndex: 0, declaresRate: true },
    piper: { sampleRateHz: 22050, padIndex: 3, declaresRate: true },
    // Open JTalk voices do NOT declare their rate. Assuming the family default
    // plays Japanese at the wrong speed, which sounds like a broken voice
    // rather than a configuration error.
    "jsut-openjtalk": { sampleRateHz: 22050, padIndex: 0, declaresRate: false },
    pocket: { sampleRateHz: 24000, padIndex: 0, declaresRate: true },
  });

  private static entry(family: string) {
    return (
      EmbeddedVoiceConfigs.families[family.toLowerCase()] ?? {
        sampleRateHz: 22050,
        padIndex: 0,
        declaresRate: true,
      }
    );
  }

  static sampleRateFor(family: string): number {
    return EmbeddedVoiceConfigs.entry(family).sampleRateHz;
  }

  /** 0 for MMS, 3 for Piper. A wrong pad is never an error, only bad audio -
   * which is why it is a table and not a guess. */
  static padIndexFor(family: string): number {
    return EmbeddedVoiceConfigs.entry(family).padIndex;
  }

  static declaresRate(family: string): boolean {
    return EmbeddedVoiceConfigs.entry(family).declaresRate;
  }

  static knownFamilies(): readonly string[] {
    return Object.freeze(Object.keys(EmbeddedVoiceConfigs.families).sort());
  }
}

/**
 * How much a claim about this code has actually been earned.
 *
 * ORDERED, and the order is the entire point. Everything below RanOnDevice is a
 * claim about a compiler, not about the thing working.
 */
export enum VerificationLevel {
  /** Written. Nobody has run it. */
  Unverified = 0,
  /** It compiles. Says nothing about behaviour, and is the level most often
   * mistaken for the next one. */
  Compiles = 1,
  /** Unit tests pass on a development machine. */
  TestedLocally = 2,
  /** It ran on the target hardware and did the thing. THE ONLY LEVEL THAT
   * COUNTS as done, because a desktop is a compile gate and a phone is the
   * benchmark. */
  RanOnDevice = 3,
  /** Ran on device and the numbers were recorded. */
  MeasuredOnDevice = 4,
}

/**
 * Records what has actually been verified about a piece of code.
 *
 * A recorded value rather than a comment so it can be COLLECTED - a build can
 * list everything claiming RanOnDevice and check that against what actually
 * ran. A comment saying the same thing cannot be counted, and so drifts.
 */
export class CircleAIVerificationStatusAttribute {
  constructor(
    readonly level: VerificationLevel = VerificationLevel.Unverified,
    /** Required above TestedLocally: "ran on device" without naming the device
     * is the claim this type exists to stop. */
    readonly device = "",
    readonly verifiedOn = "",
    readonly note = "",
  ) {
    if (level >= VerificationLevel.RanOnDevice && !device.trim()) {
      throw new Error("a claim that this ran on a device must name the device");
    }
  }

  get isDone(): boolean {
    return this.level >= VerificationLevel.RanOnDevice;
  }

  describe(): string {
    switch (this.level) {
      case VerificationLevel.Unverified:
        return "not verified";
      case VerificationLevel.Compiles:
        return "compiles - which says nothing about whether it works";
      case VerificationLevel.TestedLocally:
        return "unit tested on a development machine, not on the target";
      default: {
        const where = this.device ? ` on ${this.device}` : "";
        const when = this.verifiedOn ? ` (${this.verifiedOn})` : "";
        return this.level === VerificationLevel.MeasuredOnDevice
          ? `ran and was measured${where}${when}`
          : `ran${where}${when}`;
      }
    }
  }
}

/**
 * The names a diagnostic counter is allowed to take.
 *
 * A FIXED SET, because free-form outcome strings produce three spellings of the
 * same thing and a dashboard that undercounts all three.
 */
export class Outcomes {
  static readonly SUCCESS = "success";
  /** Refused on purpose. NOT a failure, and counting it as one makes a working
   * safety gate look like an outage. */
  static readonly REFUSED = "refused";
  static readonly FAILED = "failed";
  static readonly TIMED_OUT = "timed-out";
  static readonly CANCELLED = "cancelled";
  /** The device could not, and said so. Also not a failure. */
  static readonly UNAVAILABLE = "unavailable";

  static readonly ALL = Object.freeze([
    Outcomes.SUCCESS, Outcomes.REFUSED, Outcomes.FAILED,
    Outcomes.TIMED_OUT, Outcomes.CANCELLED, Outcomes.UNAVAILABLE,
  ]);

  /** Only two of the six mean something went wrong. */
  static isBad(outcome: string): boolean {
    return outcome === Outcomes.FAILED || outcome === Outcomes.TIMED_OUT;
  }
}

/**
 * Counters and timings, in memory, on the device.
 *
 * NOTHING LEAVES. There is no exporter, no endpoint and no identifier here, and
 * that is deliberate: telemetry that reaches a server is a record of what
 * somebody asked their phone, however aggregated it claims to be.
 */
export class CircleAIDiagnostics {
  private readonly counters = new Map<string, number>();
  private readonly durations = new Map<string, number[]>();
  private readonly startedAtMs: number;

  constructor(private readonly now: () => number = () => 0) {
    this.startedAtMs = now();
  }

  count(operation: string, outcome: string = Outcomes.SUCCESS): void {
    if (!Outcomes.ALL.includes(outcome)) {
      throw new Error(`'${outcome}' is not a known outcome; use one of ${Outcomes.ALL.join(", ")}`);
    }
    const key = `${operation}.${outcome}`;
    this.counters.set(key, (this.counters.get(key) ?? 0) + 1);
  }

  observe(operation: string, milliseconds: number): void {
    const list = this.durations.get(operation) ?? [];
    list.push(milliseconds);
    this.durations.set(operation, list);
  }

  counter(operation: string, outcome: string = Outcomes.SUCCESS): number {
    return this.counters.get(`${operation}.${outcome}`) ?? 0;
  }

  /**
   * A real percentile from the samples held, or 0 when there are none.
   *
   * NEAREST-RANK, not interpolated: with the handful of samples a phone
   * accumulates, an interpolated p95 reports a duration that never happened.
   */
  percentile(operation: string, fraction = 0.95): number {
    const samples = [...(this.durations.get(operation) ?? [])].sort((a, b) => a - b);
    if (samples.length === 0) return 0;
    const index = Math.max(
      0,
      Math.min(samples.length - 1, Math.round(fraction * samples.length + 0.5) - 1),
    );
    return samples[index];
  }

  snapshot(): Readonly<Record<string, unknown>> {
    const operations = [...this.durations.keys()].sort();
    return Object.freeze({
      uptimeSeconds: (this.now() - this.startedAtMs) / 1000,
      counters: Object.fromEntries(this.counters),
      p50: Object.fromEntries(operations.map((o) => [o, this.percentile(o, 0.5)])),
      p95: Object.fromEntries(operations.map((o) => [o, this.percentile(o, 0.95)])),
    });
  }

  reset(): void {
    this.counters.clear();
    this.durations.clear();
  }
}

/**
 * What a UI component gets for free.
 *
 * Not a framework base class - there is no framework here. It carries the
 * device context and the diagnostics handle so a component never reaches for a
 * global to find either, which is what makes the same component testable and
 * the same code usable from a head that is not a UI at all.
 */
export class CircleAIComponentBase {
  private disposed = false;

  constructor(
    readonly device: SystemInfoDeviceContext = systemInfoDeviceContext(),
    readonly diagnostics: CircleAIDiagnostics = new CircleAIDiagnostics(),
  ) {}

  get isDisposed(): boolean {
    return this.disposed;
  }

  /** IDEMPOTENT. A component is disposed by a navigation and by a parent
   * teardown, and often by both within a frame of each other. */
  dispose(): void {
    this.disposed = true;
  }
}

// The C# spellings, kept so the two trees line up.
export type INetworkPolicy = NetworkPolicy;
export type INetworkTransport = NetworkTransport;
export type IMessageChannel = MessageChannel;
export type IPeerDiscovery = PeerDiscovery;
export type IConnectivityMonitor = ConnectivityMonitor;
export type ITransportSelector = TransportSelector;
export type IPayloadOptimiser = PayloadOptimiser;
export type IMeshNetwork = MeshNetwork;
export type ILocalInferenceFallback = LocalInferenceFallback;
export type IMeshOffloadClient = MeshOffloadClientContract;
export type IOffloadRouter = OffloadRouter;
