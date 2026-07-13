// games/index.ts
// Full-parity port of CircleAI.Games (C#). C# is the exact spec.
//
// Game-runtime contracts + deterministic in-memory implementations + null
// implementations: a timer-driven game loop, an in-memory input map, and an
// in-memory scene graph.
//
// Type mappings (C# → TS):
//   record                                   → readonly interface (+ factory)
//   TimeSpan Elapsed                         → number of milliseconds
//   IReadOnlyDictionary<string,string>? Payload → ReadonlyMap<string,string> | null
//   double X / Y / Z                         → number
//   IGameLoop : IAsyncDisposable             → IGameLoop with disposeAsync()
//   ValueTask / Task                         → Promise
//   IDisposable Subscribe(...)               → IGameDisposable (idempotent dispose)
//   Func<GameTick, ValueTask> handler        → (t: GameTick) => Promise<void> | void
//   System.Threading.Timer                   → setInterval (unref'd)
//   Interlocked.Increment(ref _frame)        → ++frame (JS is single-threaded)
//   ConcurrentDictionary (Ordinal)           → Map<string,T>
//
// CONCURRENCY PARITY: The C# fan-out snapshots the subscriber list under the lock
// and then invokes handlers OUTSIDE the lock (so a handler that unsubscribes does
// not deadlock and does not disturb iteration). JS is single-threaded, but we
// preserve the snapshot-before-iterate so a handler that disposes its own token
// mid-tick still sees a stable iteration. Handler exceptions are swallowed
// (logged), matching the C# try/catch around each invocation.

/** The TypeScript analogue of C# `IDisposable` for subscriptions. */
export interface IGameDisposable {
  /** Idempotent unsubscribe. */
  dispose(): void;
}

/** A single game-loop tick. Mirrors C# `GameTick` record. */
export interface GameTick {
  readonly frame: number;
  /** Elapsed time since loop start, in milliseconds (C# `TimeSpan Elapsed`). */
  readonly elapsedMs: number;
}

/** Constructs a {@link GameTick}. */
export function gameTick(frame: number, elapsedMs: number): GameTick {
  return { frame, elapsedMs };
}

/** An input event. Mirrors C# `InputEvent` record. */
export interface InputEvent {
  readonly action: string;
  /** Optional payload (C# `IReadOnlyDictionary<string,string>? Payload`). */
  readonly payload: ReadonlyMap<string, string> | null;
}

/** Constructs an {@link InputEvent}. */
export function inputEvent(action: string, payload: ReadonlyMap<string, string> | null = null): InputEvent {
  return { action, payload };
}

/** A scene-graph node. Mirrors C# `SceneNode` record. */
export interface SceneNode {
  readonly nodeId: string;
  readonly kind: string;
  readonly x: number;
  readonly y: number;
  readonly z: number;
}

/** Constructs a {@link SceneNode}. */
export function sceneNode(nodeId: string, kind: string, x: number, y: number, z: number): SceneNode {
  return { nodeId, kind, x, y, z };
}

/** A game loop. Mirrors C# `IGameLoop : IAsyncDisposable`. */
export interface IGameLoop {
  readonly backendId: string;
  startAsync(targetFps?: number): Promise<void>;
  stopAsync(): Promise<void>;
  subscribe(handler: (tick: GameTick) => Promise<void> | void): IGameDisposable;
  disposeAsync(): Promise<void>;
}

/** An input map. Mirrors C# `IInputMap`. */
export interface IInputMap {
  readonly backendId: string;
  subscribe(handler: (ev: InputEvent) => Promise<void> | void): IGameDisposable;
}

/** A scene graph. Mirrors C# `ISceneGraph`. */
export interface ISceneGraph {
  readonly backendId: string;
  addAsync(node: SceneNode): Promise<void>;
  removeAsync(nodeId: string): Promise<void>;
  snapshotAsync(): Promise<readonly SceneNode[]>;
}

/** An already-disposed no-op handle, shared by the null implementations. */
const EMPTY_DISPOSABLE: IGameDisposable = Object.freeze({ dispose(): void {} });

/**
 * A timer-driven game loop: `startAsync` arms a repeating timer at the requested
 * FPS; `stopAsync` clears it. Each tick increments the frame counter and fans a
 * {@link GameTick} out to subscribers. Mirrors C# `TimerGameLoop`.
 */
export class TimerGameLoop implements IGameLoop {
  private readonly subs: ((tick: GameTick) => Promise<void> | void)[] = [];
  private timer: ReturnType<typeof setInterval> | null = null;
  private frame = 0;
  private startMs = 0;

  get backendId(): string {
    return "timer";
  }

  startAsync(targetFps = 60): Promise<void> {
    if (targetFps <= 0) throw new Error("targetFps");
    if (this.timer !== null) throw new Error("already started");
    const ms = Math.max(1, Math.trunc(1000.0 / targetFps));
    this.startMs = Date.now();
    this.timer = setInterval(() => this.onTick(), ms);
    if (typeof this.timer.unref === "function") this.timer.unref();
    return Promise.resolve();
  }

  stopAsync(): Promise<void> {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
    return Promise.resolve();
  }

  subscribe(handler: (tick: GameTick) => Promise<void> | void): IGameDisposable {
    if (handler == null) throw new Error("handler required");
    this.subs.push(handler);
    let disposed = false;
    return {
      dispose: (): void => {
        if (disposed) return;
        disposed = true;
        const i = this.subs.indexOf(handler);
        if (i >= 0) this.subs.splice(i, 1);
      },
    };
  }

  async disposeAsync(): Promise<void> {
    await this.stopAsync();
  }

  private onTick(): void {
    const frame = ++this.frame;
    const tick: GameTick = { frame, elapsedMs: Date.now() - this.startMs };
    // Snapshot the subscriber list before invoking (C# copies under lock, invokes outside).
    const snap = [...this.subs];
    for (const s of snap) {
      try {
        void s(tick);
      } catch (ex) {
        // eslint-disable-next-line no-console
        console.debug(`[CircleAI.Games] tick subscriber threw: ${(ex as Error).message}`);
      }
    }
  }
}

/**
 * An in-memory input map: `raise` fans an {@link InputEvent} out to subscribers.
 * Mirrors C# `InMemoryInputMap`.
 */
export class InMemoryInputMap implements IInputMap {
  private readonly subs: ((ev: InputEvent) => Promise<void> | void)[] = [];

  get backendId(): string {
    return "in-memory";
  }

  raise(ev: InputEvent): void {
    if (ev == null) throw new Error("ev required");
    const snap = [...this.subs];
    for (const s of snap) {
      try {
        void s(ev);
      } catch (ex) {
        // eslint-disable-next-line no-console
        console.debug(`[CircleAI.Games] input subscriber threw: ${(ex as Error).message}`);
      }
    }
  }

  subscribe(handler: (ev: InputEvent) => Promise<void> | void): IGameDisposable {
    if (handler == null) throw new Error("handler required");
    this.subs.push(handler);
    let disposed = false;
    return {
      dispose: (): void => {
        if (disposed) return;
        disposed = true;
        const i = this.subs.indexOf(handler);
        if (i >= 0) this.subs.splice(i, 1);
      },
    };
  }
}

/**
 * An in-memory scene graph backed by a keyed node map. Mirrors C#
 * `InMemorySceneGraph`.
 */
export class InMemorySceneGraph implements ISceneGraph {
  private readonly nodes = new Map<string, SceneNode>();

  get backendId(): string {
    return "in-memory";
  }

  async addAsync(node: SceneNode): Promise<void> {
    if (node == null) throw new Error("node required");
    if (node.nodeId == null || node.nodeId.trim() === "") throw new Error("NodeId required");
    this.nodes.set(node.nodeId, node);
  }

  async removeAsync(nodeId: string): Promise<void> {
    if (nodeId == null || nodeId.trim() === "") throw new Error("nodeId required");
    this.nodes.delete(nodeId);
  }

  snapshotAsync(): Promise<readonly SceneNode[]> {
    return Promise.resolve([...this.nodes.values()]);
  }
}

/** A no-op game loop. Mirrors C# `NullGameLoop`. */
export class NullGameLoop implements IGameLoop {
  get backendId(): string {
    return "null";
  }
  startAsync(_targetFps = 60): Promise<void> {
    return Promise.resolve();
  }
  stopAsync(): Promise<void> {
    return Promise.resolve();
  }
  subscribe(_handler: (tick: GameTick) => Promise<void> | void): IGameDisposable {
    return EMPTY_DISPOSABLE;
  }
  disposeAsync(): Promise<void> {
    return Promise.resolve();
  }
}

/** A no-op input map. Mirrors C# `NullInputMap`. */
export class NullInputMap implements IInputMap {
  static readonly instance: NullInputMap = new NullInputMap();
  get backendId(): string {
    return "null";
  }
  subscribe(_handler: (ev: InputEvent) => Promise<void> | void): IGameDisposable {
    return EMPTY_DISPOSABLE;
  }
}

/** A no-op scene graph. Mirrors C# `NullSceneGraph`. */
export class NullSceneGraph implements ISceneGraph {
  static readonly instance: NullSceneGraph = new NullSceneGraph();
  get backendId(): string {
    return "null";
  }
  addAsync(_node: SceneNode): Promise<void> {
    return Promise.resolve();
  }
  removeAsync(_nodeId: string): Promise<void> {
    return Promise.resolve();
  }
  snapshotAsync(): Promise<readonly SceneNode[]> {
    return Promise.resolve([]);
  }
}
