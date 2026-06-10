// agents.ts — AgentMessage with auto-synth 32-char hex correlation ID.

export enum AgentMessageKind {
  Discover = 0,
  Greet = 1,
  CapabilityQuery = 2,
  Invoke = 3,
  Response = 4,
  Decline = 5,
  Heartbeat = 6,
}

export interface AgentMessage {
  readonly id: string;            // UUID v4
  readonly kind: AgentMessageKind;
  readonly fromUhid: string;
  readonly toUhid: string;
  readonly contentType: string;
  readonly payload: Uint8Array;
  readonly signature: Uint8Array;
  /** ISO-8601 UTC. */
  readonly sentAt: string;
  /** 32-char hex when caller passes null/undefined. */
  readonly correlationId: string;
}

function randomHex(bytes: number): string {
  let hex = '';
  try {
    // Node + ArkTS both expose globalThis.crypto.getRandomValues in modern runtimes.
    const out = new Uint8Array(bytes);
    if (typeof globalThis !== 'undefined' && typeof (globalThis as { crypto?: { getRandomValues?: (a: Uint8Array) => Uint8Array } }).crypto?.getRandomValues === 'function') {
      (globalThis as { crypto: { getRandomValues: (a: Uint8Array) => Uint8Array } }).crypto.getRandomValues(out);
    } else {
      // best-effort fallback
      for (let i = 0; i < bytes; i++) out[i] = Math.floor(Math.random() * 256);
    }
    for (const b of out) hex += b.toString(16).padStart(2, '0');
  } catch {
    for (let i = 0; i < bytes; i++) hex += Math.floor(Math.random() * 256).toString(16).padStart(2, '0');
  }
  return hex;
}

function randomUuidV4(): string {
  const b = new Uint8Array(16);
  if (typeof globalThis !== 'undefined' && typeof (globalThis as { crypto?: { getRandomValues?: (a: Uint8Array) => Uint8Array } }).crypto?.getRandomValues === 'function') {
    (globalThis as { crypto: { getRandomValues: (a: Uint8Array) => Uint8Array } }).crypto.getRandomValues(b);
  } else {
    for (let i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
  }
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export function createAgentMessage(args: {
  kind: AgentMessageKind;
  fromUhid: string;
  toUhid: string;
  contentType: string;
  payload: Uint8Array;
  signature: Uint8Array;
  correlationId?: string | null;
}): AgentMessage {
  const cid = (args.correlationId && args.correlationId.length > 0) ? args.correlationId : randomHex(16);
  return {
    id: randomUuidV4(),
    kind: args.kind,
    fromUhid: args.fromUhid,
    toUhid: args.toUhid,
    contentType: args.contentType,
    payload: args.payload,
    signature: args.signature,
    sentAt: new Date().toISOString(),
    correlationId: cid,
  };
}
