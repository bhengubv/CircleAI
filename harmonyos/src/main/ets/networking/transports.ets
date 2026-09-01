/**
 * networking/transports
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// The transports, one per way two devices can reach each other.
//
// EACH OF THESE IS A DIFFERENT SET OF CONSTRAINTS, not a different spelling of
// "send bytes". Writing them behind one interface is right; pretending they
// behave alike is how a design that works over TCP fails over BLE and nobody
// can say why.
//
// The constraint that matters most, per transport:
//
//   BLE       one GATT operation in flight at a time, ~9 messages a second,
//             one way. Doing work before SendResponse deadlocks the peer.
//   Wi-Fi     ~50 messages a second both ways. Never stop discovery before
//             connect, and discoverServices is not discoverPeers.
//   MQTT      QoS is a promise about DELIVERY, not ordering, and a retained
//             message arrives to a subscriber that was not there.
//   DTN       the link may not exist yet. A bundle waits, possibly for days,
//             and custody is what stops it being lost when it does not.
//   HTTP      a cache key that ignores a header serves one caller another's
//             answer.

// ─────────────────────────────────────────────────────────────────────────────
// HTTP

/**
 * Which family a status code belongs to.
 *
 * The FAMILY is what decides behaviour: a 4xx must not be retried and a 5xx
 * usually should, and code that switches on individual codes gets the ones
 * nobody thought of wrong.
 */
export enum HttpStatusFamily {
  Informational = "1xx",
  Success = "2xx",
  Redirect = "3xx",
  /** The caller's fault. Retrying will not help. */
  ClientError = "4xx",
  /** The server's. Retrying might. */
  ServerError = "5xx",
  Unknown = "unknown",
}

export function statusFamily(status: number): HttpStatusFamily {
  if (status >= 100 && status < 200) return HttpStatusFamily.Informational;
  if (status >= 200 && status < 300) return HttpStatusFamily.Success;
  if (status >= 300 && status < 400) return HttpStatusFamily.Redirect;
  if (status >= 400 && status < 500) return HttpStatusFamily.ClientError;
  if (status >= 500 && status < 600) return HttpStatusFamily.ServerError;
  return HttpStatusFamily.Unknown;
}

/**
 * 429 is a 4xx that SHOULD be retried, and it is the exception that a
 * family-based rule has to name explicitly. 408 and 425 likewise.
 */
export const isRetryable = (status: number): boolean =>
  statusFamily(status) === HttpStatusFamily.ServerError ||
  status === 429 ||
  status === 408 ||
  status === 425;

/** Where a request goes. */
export interface HttpEndpointDescriptor {
  readonly method: string;
  readonly url: string;
  readonly timeoutMs: number;
  /** Whether a response may be cached. A response to a request carrying a
   * credential should say no however cacheable the body looks. */
  readonly cacheable: boolean;
}

/**
 * What identifies a cached response.
 *
 * THE VARY HEADERS ARE PART OF THE KEY. A cache keyed on the URL alone serves
 * an English answer to somebody who asked in Zulu, and serves one account's
 * data to another - which is the same bug wearing two hats.
 */
export class HttpCacheKey {
  constructor(
    readonly method: string,
    readonly url: string,
    readonly varyValues: Readonly<Record<string, string>> = {},
  ) {}

  toString(): string {
    const vary = Object.entries(this.varyValues)
      // SORTED, so the same request produces the same key whatever order the
      // headers arrived in.
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([k, v]) => `${k.toLowerCase()}=${v}`)
      .join("&");
    return `${this.method.toUpperCase()} ${this.url}${vary ? `|${vary}` : ""}`;
  }

  /** A request with an Authorization header is NEVER cached under a shared key.
   * Two people on one device is the normal case here. */
  static isSafeToCache(headers: Readonly<Record<string, string>>): boolean {
    const lowered = Object.keys(headers).map((k) => k.toLowerCase());
    return !lowered.includes("authorization") && !lowered.includes("cookie");
  }
}

/** What one request did. */
export interface HttpRequestSummary {
  readonly method: string;
  readonly url: string;
  readonly status: number;
  readonly durationMs: number;
  readonly bytes: number;
  readonly fromCache: boolean;
  readonly error: string;
}

/** Counts requests, on the device. */
export class InMemoryHttpRequestMetrics {
  private readonly summaries: HttpRequestSummary[] = [];

  /** Bounded, because this runs on a phone for weeks. The OLDEST goes, since
   * recent requests describe the current state of the network. */
  constructor(private readonly maxEntries = 500) {}

  record(summary: HttpRequestSummary): void {
    this.summaries.push(summary);
    while (this.summaries.length > this.maxEntries) this.summaries.shift();
  }

  get count(): number {
    return this.summaries.length;
  }

  /** The failure RATE, which is the number worth acting on - five failures out
   * of five is a broken network and five out of five hundred is normal. */
  failureRate(): number {
    if (this.summaries.length === 0) return 0;
    const failed = this.summaries.filter(
      (s) => s.error || statusFamily(s.status) === HttpStatusFamily.ServerError,
    ).length;
    return failed / this.summaries.length;
  }

  cacheHitRate(): number {
    if (this.summaries.length === 0) return 0;
    return this.summaries.filter((s) => s.fromCache).length / this.summaries.length;
  }
}

/** Sends HTTP requests. */
export class HttpNetworkTransport {
  private readonly cache = new Map<string, { body: Uint8Array; storedAtMs: number }>();

  constructor(
    private readonly send?: (
      descriptor: HttpEndpointDescriptor,
      headers: Record<string, string>,
      body?: Uint8Array,
    ) => Promise<{ status: number; body: Uint8Array }>,
    readonly metrics = new InMemoryHttpRequestMetrics(),
    private readonly now: () => number = () => 0,
    private readonly cacheTtlMs = 5 * 60_000,
  ) {}

  get isConnected(): boolean {
    return this.send !== undefined;
  }

  async request(
    descriptor: HttpEndpointDescriptor,
    headers: Record<string, string> = {},
    body?: Uint8Array,
  ): Promise<{ status: number; body: Uint8Array; fromCache: boolean; error: string }> {
    if (!this.send) {
      return { status: 0, body: new Uint8Array(0), fromCache: false, error: "no HTTP transport" };
    }
    const key = new HttpCacheKey(descriptor.method, descriptor.url, {
      "accept-language": headers["Accept-Language"] ?? headers["accept-language"] ?? "",
    }).toString();
    const cacheable = descriptor.cacheable && HttpCacheKey.isSafeToCache(headers);

    if (cacheable) {
      const hit = this.cache.get(key);
      if (hit && this.now() - hit.storedAtMs < this.cacheTtlMs) {
        this.metrics.record({
          method: descriptor.method, url: descriptor.url, status: 200,
          durationMs: 0, bytes: hit.body.length, fromCache: true, error: "",
        });
        return { status: 200, body: hit.body, fromCache: true, error: "" };
      }
    }

    const started = this.now();
    try {
      const response = await this.send(descriptor, headers, body);
      if (cacheable && statusFamily(response.status) === HttpStatusFamily.Success) {
        this.cache.set(key, { body: response.body, storedAtMs: this.now() });
      }
      this.metrics.record({
        method: descriptor.method, url: descriptor.url, status: response.status,
        durationMs: this.now() - started, bytes: response.body.length,
        fromCache: false, error: "",
      });
      return { ...response, fromCache: false, error: "" };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.metrics.record({
        method: descriptor.method, url: descriptor.url, status: 0,
        durationMs: this.now() - started, bytes: 0, fromCache: false, error: message,
      });
      return { status: 0, body: new Uint8Array(0), fromCache: false, error: message };
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// MQTT

/**
 * How hard a broker tries.
 *
 * QoS IS A PROMISE ABOUT DELIVERY, NOT ORDERING. AtLeastOnce means a subscriber
 * may see a message TWICE, and code that assumes otherwise double-counts.
 */
export enum MqttQos {
  /** Fire and forget. Right for telemetry nobody misses. */
  AtMostOnce = 0,
  /** Delivered at least once, possibly twice. The usual choice, and the one
   * that needs an idempotent handler. */
  AtLeastOnce = 1,
  /** Exactly once, at four round trips. Rarely worth it. */
  ExactlyOnce = 2,
}

/** One client on a broker. */
export interface MqttClientDescriptor {
  readonly clientId: string;
  /** Whether the broker forgets this client's subscriptions on disconnect. A
   * device that reconnects on a flaky link wants a PERSISTENT session, or it
   * misses everything sent while it was away. */
  readonly cleanSession: boolean;
  readonly keepAliveSeconds: number;
}

/** One topic. */
export interface MqttTopicDescriptor {
  readonly topic: string;
  readonly qos: MqttQos;
  /** Whether the broker keeps the last message for new subscribers. */
  readonly retain: boolean;
}

/**
 * The last message on a topic, kept for whoever subscribes next.
 *
 * A retained message arrives to a subscriber that WAS NOT THERE when it was
 * sent, which is what makes it useful for state and dangerous for events. A
 * retained "call incoming" reaches somebody an hour late.
 */
export interface MqttRetainedMessage {
  readonly topic: string;
  readonly payload: Uint8Array;
  readonly qos: MqttQos;
  readonly storedAtMs: number;
}

/** A broker in memory, for a harness and for a device relaying between two of
 * its own processes. */
export class InMemoryMqttBroker {
  private readonly subscribers = new Map<string, ((topic: string, payload: Uint8Array) => void)[]>();
  private readonly retained = new Map<string, MqttRetainedMessage>();

  /**
   * Matches MQTT wildcards: `+` for one level, `#` for the rest.
   *
   * `#` must be the LAST segment and matches zero or more levels - so `a/#`
   * matches `a` itself. An implementation that requires at least one level
   * silently drops the message to the topic somebody actually subscribed to.
   */
  static matches(filter: string, topic: string): boolean {
    const f = filter.split("/");
    const t = topic.split("/");
    for (let i = 0; i < f.length; i++) {
      if (f[i] === "#") return i === f.length - 1;
      if (i >= t.length) return false;
      if (f[i] !== "+" && f[i] !== t[i]) return false;
    }
    return f.length === t.length;
  }

  subscribe(filter: string, handler: (topic: string, payload: Uint8Array) => void): () => void {
    const list = this.subscribers.get(filter) ?? [];
    list.push(handler);
    this.subscribers.set(filter, list);
    // Retained messages are delivered ON SUBSCRIBE, which is the whole point of
    // retaining them - a new subscriber learns the current state without
    // waiting for the next change.
    for (const message of this.retained.values()) {
      if (InMemoryMqttBroker.matches(filter, message.topic)) handler(message.topic, message.payload);
    }
    return () => {
      const current = this.subscribers.get(filter) ?? [];
      this.subscribers.set(
        filter,
        current.filter((h) => h !== handler),
      );
    };
  }

  publish(descriptor: MqttTopicDescriptor, payload: Uint8Array, atMs = 0): number {
    if (descriptor.retain) {
      // A retained message with an EMPTY payload CLEARS the retention. That is
      // the protocol's own idiom and the only way to withdraw one.
      if (payload.length === 0) this.retained.delete(descriptor.topic);
      else {
        this.retained.set(descriptor.topic, {
          topic: descriptor.topic, payload, qos: descriptor.qos, storedAtMs: atMs,
        });
      }
    }
    let delivered = 0;
    for (const [filter, handlers] of this.subscribers) {
      if (!InMemoryMqttBroker.matches(filter, descriptor.topic)) continue;
      for (const handler of handlers) {
        // A throwing subscriber must not stop the others: on a broker, one bad
        // consumer would otherwise take down every other consumer's delivery.
        try {
          handler(descriptor.topic, payload);
          delivered += 1;
        } catch {
          continue;
        }
      }
    }
    return delivered;
  }
}

/** Sends over MQTT. */
export class MqttNetworkTransport {
  constructor(
    private readonly broker?: InMemoryMqttBroker,
    private readonly client: MqttClientDescriptor = {
      clientId: "", cleanSession: false, keepAliveSeconds: 60,
    },
  ) {}

  get isConnected(): boolean {
    return this.broker !== undefined && this.client.clientId.length > 0;
  }

  publish(topic: string, payload: Uint8Array, qos = MqttQos.AtLeastOnce, retain = false): number {
    if (!this.broker) return 0;
    return this.broker.publish({ topic, qos, retain }, payload);
  }

  subscribe(filter: string, handler: (topic: string, payload: Uint8Array) => void): () => void {
    return this.broker ? this.broker.subscribe(filter, handler) : () => undefined;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// TCP

/** Where a connection has got to. */
export enum TcpConnectionState {
  Idle = "idle",
  Connecting = "connecting",
  Connected = "connected",
  /** The peer closed its half. This device may still SEND, which is the half of
   * TCP that surprises people. */
  PeerClosed = "peer-closed",
  Closed = "closed",
  Failed = "failed",
}

/** Where to connect. */
export interface TcpEndpointDescriptor {
  readonly host: string;
  readonly port: number;
  readonly connectTimeoutMs: number;
  /** Nagle's algorithm batches small writes, which adds up to 200ms of latency
   * to an interactive message. Off for anything a person is waiting on. */
  readonly noDelay: boolean;
}

/** The ports this system uses, named once. */
export class TcpKnownPorts {
  /** The local inference server. Loopback only. */
  static readonly INFERENCE = 8317;
  /** The media host a television fetches from. */
  static readonly MEDIA = 8318;
  /** SSDP's port, which is not ours to choose. */
  static readonly SSDP = 1900;

  static readonly ALL = Object.freeze([
    TcpKnownPorts.INFERENCE, TcpKnownPorts.MEDIA, TcpKnownPorts.SSDP,
  ]);

  /** Below 1024 needs privilege on most systems, so choosing one is a decision
   * that will fail on a phone. */
  static isPrivileged(port: number): boolean {
    return port > 0 && port < 1024;
  }
}

/** One throughput measurement. */
export interface TcpThroughputSample {
  readonly bytes: number;
  readonly durationMs: number;
  readonly atMs: number;
}

/** Bytes per second from a sample, or 0 when the duration was too short to
 * mean anything - dividing by a sub-millisecond duration produces a number
 * that looks like a gigabit link. */
export const throughputBytesPerSecond = (s: TcpThroughputSample): number =>
  s.durationMs < 1 ? 0 : (s.bytes * 1000) / s.durationMs;

/** Which connections exist. */
export class InMemoryTcpConnectionRegistry {
  private readonly connections = new Map<string, TcpConnectionState>();
  private readonly samples: TcpThroughputSample[] = [];

  static key(endpoint: TcpEndpointDescriptor): string {
    return `${endpoint.host}:${endpoint.port}`;
  }

  set(endpoint: TcpEndpointDescriptor, state: TcpConnectionState): void {
    this.connections.set(InMemoryTcpConnectionRegistry.key(endpoint), state);
  }

  get(endpoint: TcpEndpointDescriptor): TcpConnectionState {
    return this.connections.get(InMemoryTcpConnectionRegistry.key(endpoint)) ?? TcpConnectionState.Idle;
  }

  record(sample: TcpThroughputSample): void {
    this.samples.push(sample);
    while (this.samples.length > 100) this.samples.shift();
  }

  /** The MEDIAN, not the mean. One stalled transfer drags a mean to uselessness
   * and leaves a median where it was. */
  medianBytesPerSecond(): number {
    const rates = this.samples.map(throughputBytesPerSecond).filter((r) => r > 0).sort((a, b) => a - b);
    if (rates.length === 0) return 0;
    const middle = Math.floor(rates.length / 2);
    return rates.length % 2 ? rates[middle] : (rates[middle - 1] + rates[middle]) / 2;
  }
}

/** Sends over TCP. */
export class TcpNetworkTransport {
  constructor(
    private readonly connect?: (endpoint: TcpEndpointDescriptor) => Promise<boolean>,
    private readonly write?: (payload: Uint8Array) => Promise<number>,
    readonly registry = new InMemoryTcpConnectionRegistry(),
  ) {}

  get isConnected(): boolean {
    return this.connect !== undefined && this.write !== undefined;
  }

  async open(endpoint: TcpEndpointDescriptor): Promise<boolean> {
    if (!this.connect) return false;
    this.registry.set(endpoint, TcpConnectionState.Connecting);
    try {
      const ok = await this.connect(endpoint);
      this.registry.set(endpoint, ok ? TcpConnectionState.Connected : TcpConnectionState.Failed);
      return ok;
    } catch {
      this.registry.set(endpoint, TcpConnectionState.Failed);
      return false;
    }
  }

  async send(payload: Uint8Array): Promise<number> {
    return this.write ? this.write(payload) : 0;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket

/** What a frame carries. */
export enum WebSocketMessageType {
  Text = "text",
  Binary = "binary",
  /** A ping must be answered with a pong carrying THE SAME PAYLOAD. A pong with
   * a different payload is not an answer and some peers close the connection. */
  Ping = "ping",
  Pong = "pong",
  Close = "close",
}

/** Where a link has got to. */
export enum WebSocketLinkState {
  Connecting = "connecting",
  Open = "open",
  /** The close handshake has begun. Frames sent now may or may not arrive. */
  Closing = "closing",
  Closed = "closed",
}

/** Where to connect. */
export interface WebSocketEndpointDescriptor {
  readonly url: string;
  readonly subprotocols: readonly string[];
  /** How often to ping. A link through a NAT dies silently after a few minutes
   * of quiet, and the first thing that notices is the next real message. */
  readonly pingIntervalMs: number;
}

/** One frame, for a log. */
export interface WebSocketFrameSummary {
  readonly type: WebSocketMessageType;
  readonly bytes: number;
  readonly outbound: boolean;
  readonly atMs: number;
}

/** Which sessions exist. */
export class InMemoryWebSocketSessionRegistry {
  private readonly sessions = new Map<string, WebSocketLinkState>();
  private readonly frames: WebSocketFrameSummary[] = [];

  open(sessionId: string): void {
    this.sessions.set(sessionId, WebSocketLinkState.Open);
  }

  set(sessionId: string, state: WebSocketLinkState): void {
    this.sessions.set(sessionId, state);
  }

  state(sessionId: string): WebSocketLinkState {
    return this.sessions.get(sessionId) ?? WebSocketLinkState.Closed;
  }

  record(frame: WebSocketFrameSummary): void {
    this.frames.push(frame);
    while (this.frames.length > 200) this.frames.shift();
  }

  /**
   * Whether the link has gone quiet in BOTH directions.
   *
   * One direction quiet is normal - a listener says nothing for minutes. Both
   * quiet past the ping interval means the link is dead and nothing has
   * noticed.
   */
  isSilent(nowMs: number, forMs: number): boolean {
    if (this.frames.length === 0) return false;
    return nowMs - this.frames[this.frames.length - 1].atMs > forMs;
  }
}

/** Sends over a websocket. */
export class WebSocketTransport {
  constructor(
    private readonly connect?: (endpoint: WebSocketEndpointDescriptor) => Promise<string>,
    private readonly write?: (sessionId: string, payload: Uint8Array | string) => Promise<boolean>,
    readonly registry = new InMemoryWebSocketSessionRegistry(),
    private readonly now: () => number = () => 0,
  ) {}

  get isConnected(): boolean {
    return this.connect !== undefined && this.write !== undefined;
  }

  async open(endpoint: WebSocketEndpointDescriptor): Promise<string> {
    if (!this.connect) return "";
    const sessionId = await this.connect(endpoint);
    if (sessionId) this.registry.open(sessionId);
    return sessionId;
  }

  async send(sessionId: string, payload: Uint8Array | string): Promise<boolean> {
    // Refuses on a closed link rather than throwing. Frames arrive from a
    // capture thread that has not noticed the close, and throwing there kills
    // the capture.
    if (!this.write || this.registry.state(sessionId) !== WebSocketLinkState.Open) return false;
    const ok = await this.write(sessionId, payload);
    this.registry.record({
      type: typeof payload === "string" ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
      bytes: typeof payload === "string" ? payload.length : payload.length,
      outbound: true,
      atMs: this.now(),
    });
    return ok;
  }

  /** A pong carries the ping's payload back, unchanged. */
  async pong(sessionId: string, pingPayload: Uint8Array): Promise<boolean> {
    return this.send(sessionId, pingPayload);
  }
}

/** Sends over gRPC. */
export class GrpcNetworkTransport {
  constructor(
    private readonly call?: (
      service: string,
      method: string,
      payload: Uint8Array,
      deadlineMs: number,
    ) => Promise<Uint8Array>,
    /**
     * A DEADLINE, not a timeout, and it is absolute for the whole call chain.
     * A deadline set per hop lets a three-hop call take three times as long as
     * anybody asked for.
     */
    private readonly defaultDeadlineMs = 30_000,
  ) {}

  get isConnected(): boolean {
    return this.call !== undefined;
  }

  async invoke(
    service: string,
    method: string,
    payload: Uint8Array,
    deadlineMs = 0,
  ): Promise<{ payload: Uint8Array; error: string }> {
    if (!this.call) return { payload: new Uint8Array(0), error: "no gRPC transport" };
    try {
      return {
        payload: await this.call(service, method, payload, deadlineMs || this.defaultDeadlineMs),
        error: "",
      };
    } catch (error) {
      return {
        payload: new Uint8Array(0),
        error: error instanceof Error ? error.message : String(error),
      };
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Delay-tolerant

/**
 * How urgent a bundle is.
 *
 * On a delay-tolerant link the queue may hold days of traffic, so priority is
 * what decides which of a hundred waiting bundles goes when a contact opens.
 */
export enum DtnPriority {
  Bulk = 0,
  Normal = 1,
  Expedited = 2,
}

/**
 * Something waiting for a link that does not exist yet.
 *
 * A LIFETIME, NOT A TIMEOUT. A bundle is not failing while it waits - waiting
 * is the design - but a message that is no longer worth delivering must expire
 * rather than arriving days late and confusing somebody.
 */
export interface DtnBundle {
  readonly bundleId: string;
  readonly destination: string;
  readonly payload: Uint8Array;
  readonly priority: DtnPriority;
  readonly createdAtMs: number;
  readonly lifetimeMs: number;
}

export const isExpired = (b: DtnBundle, nowMs: number): boolean =>
  nowMs - b.createdAtMs > b.lifetimeMs;

/**
 * A record that somebody else has taken responsibility for a bundle.
 *
 * CUSTODY IS THE POINT of a delay-tolerant network: this device may delete its
 * copy only once another has accepted custody, because there is no end-to-end
 * acknowledgement to wait for.
 */
export interface DtnCustodyRecord {
  readonly bundleId: string;
  readonly custodianId: string;
  readonly acceptedAtMs: number;
}

/** Bundles waiting to go. */
export class InMemoryDtnBundleStore {
  private readonly bundles = new Map<string, DtnBundle>();
  private readonly custody = new Map<string, DtnCustodyRecord>();

  put(bundle: DtnBundle): void {
    this.bundles.set(bundle.bundleId, bundle);
  }

  /** Expedited first, then oldest first WITHIN a priority - so a long-waiting
   * bulk bundle is not starved forever by a stream of normal ones. */
  ready(nowMs: number): readonly DtnBundle[] {
    return Object.freeze(
      [...this.bundles.values()]
        .filter((b) => !isExpired(b, nowMs))
        .sort((a, b) => b.priority - a.priority || a.createdAtMs - b.createdAtMs),
    );
  }

  acceptCustody(record: DtnCustodyRecord): void {
    this.custody.set(record.bundleId, record);
  }

  /** A bundle may be dropped only once ANOTHER node has custody, or once it has
   * expired. Dropping on send loses it when the send did not arrive. */
  mayDelete(bundleId: string, nowMs: number): boolean {
    const bundle = this.bundles.get(bundleId);
    if (!bundle) return false;
    return this.custody.has(bundleId) || isExpired(bundle, nowMs);
  }

  delete(bundleId: string, nowMs: number): boolean {
    if (!this.mayDelete(bundleId, nowMs)) return false;
    this.bundles.delete(bundleId);
    this.custody.delete(bundleId);
    return true;
  }

  /** Removes what has expired and returns how many. Separate from `ready` so an
   * expiry sweep is a decision rather than a side effect of reading. */
  sweep(nowMs: number): number {
    let dropped = 0;
    for (const [id, bundle] of [...this.bundles]) {
      if (isExpired(bundle, nowMs)) {
        this.bundles.delete(id);
        this.custody.delete(id);
        dropped += 1;
      }
    }
    return dropped;
  }
}

/** Moves bundles when a contact opens. */
export class DtnSyncChannel {
  constructor(
    private readonly store: InMemoryDtnBundleStore,
    private readonly send?: (bundle: DtnBundle) => Promise<DtnCustodyRecord | undefined>,
  ) {}

  /**
   * A contact is a WINDOW, not a connection: it opens, some bundles move, and
   * it closes without warning. So this sends until the window closes and leaves
   * everything else in the store, rather than failing the batch.
   */
  async onContact(nowMs: number, maxBundles = 32): Promise<{ sent: number; kept: number }> {
    if (!this.send) return { sent: 0, kept: this.store.ready(nowMs).length };
    let sent = 0;
    for (const bundle of this.store.ready(nowMs).slice(0, maxBundles)) {
      let custody: DtnCustodyRecord | undefined;
      try {
        custody = await this.send(bundle);
      } catch {
        // The window closed. Everything left stays in the store, which is what
        // a delay-tolerant network is for.
        break;
      }
      if (!custody) break;
      this.store.acceptCustody(custody);
      this.store.delete(bundle.bundleId, nowMs);
      sent += 1;
    }
    return { sent, kept: this.store.ready(nowMs).length };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Radios

/** What a GATT adapter must do. */
export interface BleGattAdapter {
  readonly isAvailable: boolean;
  write(peerId: string, characteristic: string, payload: Uint8Array): Promise<boolean>;
  onNotify(handler: (peerId: string, payload: Uint8Array) => void): () => void;
}

/**
 * Bluetooth Low Energy.
 *
 * ONE GATT OPERATION IN FLIGHT AT A TIME. The stack has a single queue, and
 * doing work before answering a write DEADLOCKS the peer - it is waiting for a
 * response that is waiting for the work. Every handler here answers first and
 * works afterwards.
 *
 * ~9 messages a second, one way. Signalling only.
 */
export class BluetoothNetworkTransport {
  /** The usable payload after the ATT header. Anything larger must be
   * fragmented, and fragment reassembly is where the interesting bugs live -
   * an unguarded reassembly buffer splices two messages together and the
   * failure looks like a broken ratchet. */
  static readonly MAX_PAYLOAD = 512;

  private sending = false;
  private readonly queue: { peerId: string; payload: Uint8Array }[] = [];

  constructor(private readonly adapter?: BleGattAdapter) {}

  get isConnected(): boolean {
    return this.adapter?.isAvailable ?? false;
  }

  /**
   * Queued and drained one at a time.
   *
   * Not a nicety: two concurrent writes on one connection is a stack error on
   * every platform, and the error arrives long after the second write.
   */
  async send(peerId: string, payload: Uint8Array): Promise<boolean> {
    if (!this.adapter?.isAvailable) return false;
    if (payload.length > BluetoothNetworkTransport.MAX_PAYLOAD) return false;
    this.queue.push({ peerId, payload });
    if (this.sending) return true;

    this.sending = true;
    try {
      while (this.queue.length > 0) {
        const next = this.queue.shift()!;
        try {
          await this.adapter.write(next.peerId, "circle-tx", next.payload);
        } catch {
          // One failed write does not abandon the queue. On BLE a write fails
          // routinely and the next usually succeeds.
          continue;
        }
      }
    } finally {
      this.sending = false;
    }
    return true;
  }
}

/** A short-range link that is not Bluetooth. */
export interface NearLinkAdapter {
  readonly isAvailable: boolean;
  send(peerId: string, payload: Uint8Array): Promise<boolean>;
}

/**
 * NearLink, where the hardware has it.
 *
 * NOT ASSUMED PRESENT. No device in this project's fleet has it, so the
 * transport reports unavailable rather than failing at send time - which is the
 * same discipline as Wi-Fi Aware, and for the same reason.
 */
export class NearLinkTransport {
  constructor(private readonly adapter?: NearLinkAdapter) {}

  get isConnected(): boolean {
    return this.adapter?.isAvailable ?? false;
  }

  async send(peerId: string, payload: Uint8Array): Promise<boolean> {
    return this.adapter?.isAvailable ? this.adapter.send(peerId, payload) : false;
  }
}

/**
 * Wi-Fi Direct.
 *
 * ~50 messages a second, both ways. The only transport here that carries voice.
 *
 * TWO RULES LEARNED THE HARD WAY:
 *
 *   * Never stop discovery before connecting. `stopPeerDiscovery()` followed by
 *     `connect()` drops the peer, and the connect then fails with an error that
 *     names neither call.
 *
 *   * `discoverServices` is not `discoverPeers`. They look interchangeable and
 *     only one of them finds a device that is not advertising a service.
 */
export class WiFiNetworkTransport {
  constructor(
    private readonly connect?: (peerId: string) => Promise<boolean>,
    private readonly write?: (peerId: string, payload: Uint8Array) => Promise<boolean>,
  ) {}

  get isConnected(): boolean {
    return this.connect !== undefined && this.write !== undefined;
  }

  async open(peerId: string): Promise<boolean> {
    // Discovery is deliberately NOT stopped here. See the class comment.
    return this.connect ? this.connect(peerId) : false;
  }

  async send(peerId: string, payload: Uint8Array): Promise<boolean> {
    return this.write ? this.write(peerId, payload) : false;
  }
}

/**
 * Finds peers over Wi-Fi Direct.
 *
 * THE GROUP NAME AND PASSPHRASE ARE DERIVED FROM THE HOST'S PUBLIC KEY, not
 * discovered and not exchanged over the link. Discovery by DNS-SD was measured
 * dead on this hardware, and passing credentials over the link deadlocks - both
 * were tried. Deriving them means two devices that know each other's key can
 * form a group with no exchange at all.
 */
export class WiFiPeerDiscovery {
  private discovering = false;

  constructor(
    private readonly start?: () => Promise<boolean>,
    private readonly stop?: () => Promise<void>,
    private readonly listPeers?: () => readonly { peerId: string; canHost: boolean }[],
  ) {}

  get isDiscovering(): boolean {
    return this.discovering;
  }

  async startDiscovery(): Promise<boolean> {
    if (!this.start) return false;
    this.discovering = await this.start();
    return this.discovering;
  }

  async stopDiscovery(): Promise<void> {
    this.discovering = false;
    await this.stop?.();
  }

  peers(): readonly { peerId: string; canHost: boolean }[] {
    return this.listPeers?.() ?? [];
  }

  /**
   * ROLE FOLLOWS CAPABILITY, not tag order.
   *
   * A peer that cannot host is never chosen as group owner, however the ids
   * sort - assigning by tag order deadlocks on a device that cannot advertise,
   * and the deadlock looks like a peer that is simply not responding.
   */
  static chooseHost(
    peers: readonly { peerId: string; canHost: boolean }[],
    selfCanHost: boolean,
    selfId: string,
  ): string {
    const candidates = peers.filter((p) => p.canHost).map((p) => p.peerId);
    if (selfCanHost) candidates.push(selfId);
    if (candidates.length === 0) return "";
    // Among peers that CAN host, the id order is a fine tiebreak - it is
    // deterministic and both sides compute the same answer.
    return candidates.sort()[0];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNet

/** What kind of thing a peer is. */
export enum AetherPeerKind {
  /** A phone somebody carries. */
  Device = "device",
  /** A device relaying for two peers that have both added it. NOT a server. */
  Relay = "relay",
  /** Reachable, not added. Nothing is sent to one. */
  Observed = "observed",
}

/** Another device on the mesh. */
export interface AetherPeer {
  readonly aetherTag: string;
  readonly kind: AetherPeerKind;
  /** Whether BOTH devices added each other. Nothing is sent otherwise. */
  readonly mutuallyAdded: boolean;
  readonly lastSeenAtMs: number;
  readonly transports: readonly string[];
}

/**
 * What one hop cost.
 *
 * PER HOP, not end to end, because on a relayed path the interesting number is
 * which hop was slow - and an end-to-end figure hides a relay that is failing.
 */
export interface AetherHopTelemetry {
  readonly fromTag: string;
  readonly toTag: string;
  readonly latencyMs: number;
  readonly bytes: number;
  readonly succeeded: boolean;
}

/**
 * What a packet was, for a log.
 *
 * NO PAYLOAD AND NO CONTENT TYPE. A log of who talked to whom and when is
 * already a lot; a log that also says what about would be a record of
 * somebody's conversations on their own device.
 */
export interface AetherPacketSummary {
  readonly fromTag: string;
  readonly toTag: string;
  readonly bytes: number;
  readonly hops: number;
  readonly atMs: number;
}

/** Who is on the mesh. */
export class InMemoryAetherNetRegistry {
  private readonly peers = new Map<string, AetherPeer>();
  private readonly telemetry: AetherHopTelemetry[] = [];

  add(peer: AetherPeer): void {
    this.peers.set(peer.aetherTag, peer);
  }

  get(tag: string): AetherPeer | undefined {
    return this.peers.get(tag);
  }

  /** Only peers that have added this device back. Everything else in the system
   * asks this rather than the full list, so a stranger cannot be sent to by
   * forgetting a check. */
  reachable(): readonly AetherPeer[] {
    return Object.freeze([...this.peers.values()].filter((p) => p.mutuallyAdded));
  }

  record(hop: AetherHopTelemetry): void {
    this.telemetry.push(hop);
    while (this.telemetry.length > 200) this.telemetry.shift();
  }

  /** Which hop is failing, which is the question a relayed path raises. */
  worstHop(): AetherHopTelemetry | undefined {
    const failures = this.telemetry.filter((h) => !h.succeeded);
    if (failures.length > 0) return failures[failures.length - 1];
    return this.telemetry.reduce<AetherHopTelemetry | undefined>(
      (worst, h) => (!worst || h.latencyMs > worst.latencyMs ? h : worst),
      undefined,
    );
  }
}

/** Sends over the mesh. */
export class AetherNetworkTransport {
  constructor(
    readonly registry: InMemoryAetherNetRegistry,
    private readonly write?: (tag: string, payload: Uint8Array) => Promise<boolean>,
    private readonly now: () => number = () => 0,
  ) {}

  get isConnected(): boolean {
    return this.write !== undefined;
  }

  async send(tag: string, payload: Uint8Array): Promise<{ sent: boolean; reason: string }> {
    const peer = this.registry.get(tag);
    if (!peer || !peer.mutuallyAdded) {
      return { sent: false, reason: `${tag} has not added this device back` };
    }
    if (!this.write) return { sent: false, reason: "no mesh transport" };
    const started = this.now();
    let ok = false;
    try {
      ok = await this.write(tag, payload);
    } catch {
      ok = false;
    }
    this.registry.record({
      fromTag: "self", toTag: tag, latencyMs: this.now() - started,
      bytes: payload.length, succeeded: ok,
    });
    return { sent: ok, reason: ok ? "" : "the peer did not accept it" };
  }
}

/** Finds peers on the mesh. */
export class AetherPeerDiscovery {
  constructor(
    readonly registry: InMemoryAetherNetRegistry,
    private readonly scan?: () => Promise<readonly AetherPeer[]>,
  ) {}

  async discover(): Promise<readonly AetherPeer[]> {
    if (!this.scan) return [];
    const found = await this.scan();
    // Everything found is RECORDED, including peers that have not added this
    // device - a UI needs to show them so somebody can choose to add them. What
    // changes is that nothing is sent to them.
    for (const peer of found) this.registry.add(peer);
    return found;
  }
}

/**
 * Keeps two devices in step over the mesh.
 *
 * BOTH SIDES RE-HANDSHAKING ON LINK-UP DIVERGES THE RATCHET. So repair is led
 * by the side that CANNOT read - the one with the problem asks, rather than
 * both announcing.
 */
export class AetherSyncChannel {
  private lastSyncedAtMs = 0;

  constructor(
    private readonly transport: AetherNetworkTransport,
    private readonly now: () => number = () => 0,
  ) {}

  /** True only when this side has something it cannot open. The side that CAN
   * read stays quiet. */
  shouldRequestRepair(canDecryptLatest: boolean): boolean {
    return !canDecryptLatest;
  }

  async sync(tag: string, payload: Uint8Array): Promise<boolean> {
    const result = await this.transport.send(tag, payload);
    if (result.sent) this.lastSyncedAtMs = this.now();
    return result.sent;
  }

  get lastSyncedAt(): number {
    return this.lastSyncedAtMs;
  }
}

/** Carries what the companion knows to another of the person's devices. */
export class AetherNetCompanionStateChannel {
  constructor(private readonly channel: AetherSyncChannel) {}

  /** State goes only to a device the SAME PERSON owns. A companion's state is a
   * record of somebody's life, and mutual addition is not the same as being
   * the same person - so this takes an explicit own-device list. */
  async push(ownDeviceTags: readonly string[], state: Uint8Array): Promise<number> {
    let sent = 0;
    for (const tag of ownDeviceTags) {
      if (await this.channel.sync(tag, state)) sent += 1;
    }
    return sent;
  }
}

/** Turns mesh context into something the companion can use. */
export class AetherNetContextAdapter {
  constructor(private readonly registry: InMemoryAetherNetRegistry) {}

  /** Named for what it is - a count and a capability list, never who. A
   * companion that says "Thabo's phone is nearby" has disclosed a location. */
  describe(): string {
    const reachable = this.registry.reachable();
    if (reachable.length === 0) return "no other devices are reachable";
    return `${reachable.length} device${reachable.length === 1 ? "" : "s"} reachable`;
  }
}

/** Where a directive goes. */
export class AetherNetDirectiveSink {
  private readonly received: { fromTag: string; directive: string; atMs: number }[] = [];

  constructor(private readonly handle?: (fromTag: string, directive: string) => void) {}

  /**
   * A directive from a peer is DATA, not a command.
   *
   * It is recorded and handed to a handler that decides. Executing it because
   * it arrived would let anything that reaches the mesh drive this device.
   */
  accept(fromTag: string, directive: string, atMs = 0): boolean {
    this.received.push({ fromTag, directive, atMs });
    this.handle?.(fromTag, directive);
    return true;
  }

  get count(): number {
    return this.received.length;
  }
}

/** Brings directives in from the mesh. */
export class AetherNetInboundDirectiveBridge {
  constructor(
    private readonly registry: InMemoryAetherNetRegistry,
    private readonly sink: AetherNetDirectiveSink,
  ) {}

  /** Refuses a directive from a peer that has not been added. The check is here
   * rather than in the sink because the sink cannot see the registry, and a
   * check nobody can see is a check somebody removes. */
  onDirective(fromTag: string, directive: string, atMs = 0): { accepted: boolean; reason: string } {
    const peer = this.registry.get(fromTag);
    if (!peer?.mutuallyAdded) {
      return { accepted: false, reason: `${fromTag} is not a device you have added` };
    }
    this.sink.accept(fromTag, directive, atMs);
    return { accepted: true, reason: "" };
  }
}

/** Sends telemetry over the mesh. */
export class AetherNetTelemetryAdapter {
  constructor(private readonly registry: InMemoryAetherNetRegistry) {}

  /** Summaries only, never payloads - see `AetherPacketSummary`. */
  summarise(packets: readonly AetherPacketSummary[]): Readonly<Record<string, number>> {
    const byPeer: Record<string, number> = {};
    for (const packet of packets) byPeer[packet.toTag] = (byPeer[packet.toTag] ?? 0) + packet.bytes;
    return Object.freeze(byPeer);
  }
}

/** Lets the mesh answer a prompt for a device that cannot. */
export class CircleAiAetherNetAiProvider {
  constructor(
    private readonly transport: AetherNetworkTransport,
    private readonly consentedTag = "",
  ) {}

  get isAvailable(): boolean {
    return this.transport.isConnected && this.consentedTag.length > 0;
  }

  /** The consented tag is checked HERE as well as in the transport. Two checks
   * because this one is about consent and the other is about mutual addition,
   * and they are different questions with the same shape. */
  async ask(prompt: string): Promise<{ answer: string; servedBy: string; reason: string }> {
    if (!this.consentedTag) {
      return { answer: "", servedBy: "", reason: "no peer has been agreed to" };
    }
    const result = await this.transport.send(this.consentedTag, new TextEncoder().encode(prompt));
    return result.sent
      ? { answer: "", servedBy: this.consentedTag, reason: "" }
      : { answer: "", servedBy: "", reason: result.reason };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Sync

/**
 * Who has seen what.
 *
 * A VERSION VECTOR RATHER THAN A TIMESTAMP. Two phones' clocks disagree by
 * seconds, and last-write-wins on a wrong clock silently discards the newer
 * edit. A vector says what each device has seen, which no clock can contradict.
 */
export class VersionVector {
  private readonly counters = new Map<string, number>();

  constructor(seed: Readonly<Record<string, number>> = {}) {
    for (const [k, v] of Object.entries(seed)) this.counters.set(k, v);
  }

  get(deviceId: string): number {
    return this.counters.get(deviceId) ?? 0;
  }

  increment(deviceId: string): number {
    const next = this.get(deviceId) + 1;
    this.counters.set(deviceId, next);
    return next;
  }

  /** The pointwise MAXIMUM, which is what merging two vectors means. */
  merge(other: VersionVector): VersionVector {
    const merged = new VersionVector(this.toRecord());
    for (const [device, count] of other.counters) {
      merged.counters.set(device, Math.max(merged.get(device), count));
    }
    return merged;
  }

  /** True when this vector has seen everything the other has. */
  dominates(other: VersionVector): boolean {
    for (const [device, count] of other.counters) {
      if (this.get(device) < count) return false;
    }
    return true;
  }

  /**
   * CONCURRENT means neither has seen everything the other has - a genuine
   * conflict, and the only case a person needs to resolve. Everything else is
   * one side being behind, which merges without a decision.
   */
  isConcurrentWith(other: VersionVector): boolean {
    return !this.dominates(other) && !other.dominates(this);
  }

  toRecord(): Record<string, number> {
    return Object.fromEntries(this.counters);
  }
}

/** How a merge went. */
export class SyncReconciliation {
  /**
   * Reconciles two versions, KEEPING BOTH on a genuine conflict.
   *
   * Two devices that learned different things have both learned something, and
   * picking one by timestamp discards a real change because a clock was wrong.
   * A conflict is surfaced, not resolved.
   */
  static reconcile<T>(
    mine: { value: T; vector: VersionVector },
    theirs: { value: T; vector: VersionVector },
  ): { value: T; vector: VersionVector; conflict: boolean; alternative?: T } {
    if (mine.vector.dominates(theirs.vector)) {
      return { value: mine.value, vector: mine.vector, conflict: false };
    }
    if (theirs.vector.dominates(mine.vector)) {
      return { value: theirs.value, vector: theirs.vector, conflict: false };
    }
    return {
      value: mine.value,
      vector: mine.vector.merge(theirs.vector),
      conflict: true,
      alternative: theirs.value,
    };
  }
}

// The C# spellings, kept so the two trees line up.
export type IBleGattAdapter = BleGattAdapter;
export type INearLinkAdapter = NearLinkAdapter;
