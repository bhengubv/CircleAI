// collaboration/index.ts
// Full-parity port of CircleAI.Collaboration (C#). C# is the exact spec.
//
// Team-collaboration primitives: Channel / Message / PresenceState records, the
// IChannelStore / IMessageStore / IPresence contracts, deterministic in-memory
// stores (channels indexed by team, messages kept per-channel newest-first,
// presence with online + last-seen), and fail-closed Null* defaults.
//
// Type mappings (C# → TS):
//   DateTimeOffset AtUtc / LastSeenUtc  → Date
//   ValueTask / ValueTask<T>            → Promise<void> / Promise<T>
//   ConcurrentDictionary (Ordinal)      → Map<string, T>

/** A collaboration channel. Mirrors C# `Channel` record. */
export interface Channel {
  readonly channelId: string;
  readonly name: string;
  readonly teamId: string;
}

/** Constructs a {@link Channel}. */
export function channel(channelId: string, name: string, teamId: string): Channel {
  return { channelId, name, teamId };
}

/** A posted message. Mirrors C# `Message` record. */
export interface Message {
  readonly messageId: string;
  readonly channelId: string;
  readonly authorId: string;
  readonly body: string;
  readonly atUtc: Date;
}

/** Constructs a {@link Message}. */
export function message(
  messageId: string,
  channelId: string,
  authorId: string,
  body: string,
  atUtc: Date,
): Message {
  return { messageId, channelId, authorId, body, atUtc };
}

/** A user's presence. Mirrors C# `PresenceState` record. */
export interface PresenceState {
  readonly userId: string;
  readonly online: boolean;
  readonly lastSeenUtc: Date;
}

/** Constructs a {@link PresenceState}. */
export function presenceState(userId: string, online: boolean, lastSeenUtc: Date): PresenceState {
  return { userId, online, lastSeenUtc };
}

/** Reads channels. Mirrors C# `IChannelStore`. */
export interface IChannelStore {
  readonly backendId: string;
  getAsync(id: string, signal?: AbortSignal): Promise<Channel | null>;
  listForTeamAsync(teamId: string, signal?: AbortSignal): Promise<readonly Channel[]>;
}

/** Posts + reads messages. Mirrors C# `IMessageStore`. */
export interface IMessageStore {
  readonly backendId: string;
  postAsync(msg: Message, signal?: AbortSignal): Promise<Message>;
  readAsync(channelId: string, limit?: number, signal?: AbortSignal): Promise<readonly Message[]>;
}

/** Reads presence. Mirrors C# `IPresence`. */
export interface IPresence {
  readonly backendId: string;
  getAsync(userId: string, signal?: AbortSignal): Promise<PresenceState | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IChannelStore}. Mirrors C# `InMemoryChannelStore`. */
export class InMemoryChannelStore implements IChannelStore {
  private readonly items = new Map<string, Channel>();
  readonly backendId = "in-memory";

  /** Insert or replace a channel. Mirrors C# `Upsert`. */
  upsert(c: Channel): void {
    if (c == null) throw new Error("channel required");
    this.items.set(c.channelId, c);
  }

  getAsync(id: string, _signal?: AbortSignal): Promise<Channel | null> {
    if (isBlank(id)) throw new Error("id required");
    return Promise.resolve(this.items.get(id) ?? null);
  }

  listForTeamAsync(teamId: string, _signal?: AbortSignal): Promise<readonly Channel[]> {
    if (isBlank(teamId)) throw new Error("teamId required");
    return Promise.resolve(
      [...this.items.values()]
        .filter((c) => c.teamId === teamId)
        .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)),
    );
  }
}

/** In-memory {@link IMessageStore} kept per-channel newest-first. Mirrors C# `InMemoryMessageStore`. */
export class InMemoryMessageStore implements IMessageStore {
  private readonly byChannel = new Map<string, Message[]>();
  readonly backendId = "in-memory";

  postAsync(msg: Message, _signal?: AbortSignal): Promise<Message> {
    if (msg == null) throw new Error("message required");
    if (isBlank(msg.channelId)) throw new Error("ChannelId required");
    let list = this.byChannel.get(msg.channelId);
    if (list === undefined) {
      list = [];
      this.byChannel.set(msg.channelId, list);
    }
    list.push(msg);
    return Promise.resolve(msg);
  }

  readAsync(channelId: string, limit = 100, _signal?: AbortSignal): Promise<readonly Message[]> {
    if (isBlank(channelId)) throw new Error("channelId required");
    const list = this.byChannel.get(channelId);
    if (list === undefined) return Promise.resolve([]);
    return Promise.resolve(
      [...list].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit),
    );
  }
}

/** In-memory {@link IPresence}. Mirrors C# `InMemoryPresence`. */
export class InMemoryPresence implements IPresence {
  private readonly states = new Map<string, PresenceState>();
  readonly backendId = "in-memory";

  /** Set a user's presence. Mirrors C# `Set`. */
  set(s: PresenceState): void {
    if (s == null) throw new Error("state required");
    this.states.set(s.userId, s);
  }

  getAsync(userId: string, _signal?: AbortSignal): Promise<PresenceState | null> {
    if (isBlank(userId)) throw new Error("userId required");
    return Promise.resolve(this.states.get(userId) ?? null);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IChannelStore}. Mirrors C# `NullChannelStore`. */
export class NullChannelStore implements IChannelStore {
  static readonly instance = new NullChannelStore();
  readonly backendId = "null";
  getAsync(_id: string, _signal?: AbortSignal): Promise<Channel | null> {
    return Promise.resolve(null);
  }
  listForTeamAsync(_teamId: string, _signal?: AbortSignal): Promise<readonly Channel[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IMessageStore}. Mirrors C# `NullMessageStore`. */
export class NullMessageStore implements IMessageStore {
  static readonly instance = new NullMessageStore();
  readonly backendId = "null";
  postAsync(msg: Message, _signal?: AbortSignal): Promise<Message> {
    return Promise.resolve(msg);
  }
  readAsync(_channelId: string, _limit = 100, _signal?: AbortSignal): Promise<readonly Message[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IPresence}. Mirrors C# `NullPresence`. */
export class NullPresence implements IPresence {
  static readonly instance = new NullPresence();
  readonly backendId = "null";
  getAsync(_userId: string, _signal?: AbortSignal): Promise<PresenceState | null> {
    return Promise.resolve(null);
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
