/**
 * plugins/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// plugins/index.ts
// Full-parity port of the scoped CircleAI.Plugins surface (C#). C# is the exact
// spec.
//
// Plugin contract surface in scope: IPlugin, IPluginContext, IPluginEvents (+
// the thread-safe PluginEvents bus and PluginEventNames), the default
// PluginContext, and the permission-gated PermissionedPluginContext, plus the
// IWorkspacePathProvider host hook. The C# PluginLoader / PluginRegistry /
// PluginLifecycleService rely on .NET AssemblyLoadContext, IHostedService,
// IConfiguration, and the filesystem — those host/reflection concerns have no
// in-process TS analogue and are outside this work unit.
//
// Type mappings (C# → TS):
//   IDisposable Subscribe(...)          → returns { dispose(): void }
//   Action<object?> handler             → (payload: Object) => void
//   ILogger (Microsoft.Extensions)      → the minimal ILogger seam below
//   ConcurrentDictionary<string, List>  → Map<string, Array<...>>

/** A disposable subscription handle. Mirrors C# `IDisposable`. */
export interface Disposable {
  dispose(): void;
}

/**
 * Minimal structured-logging sink — the injectable seam standing in for the C#
 * `Microsoft.Extensions.Logging.ILogger` dependency that {@link IPluginContext}
 * exposes. Consumers wire their own; the null logger is the default.
 */
export interface ILogger {
  logInformation(message: string): void;
  logWarning(message: string): void;
  logError(message: string): void;
}

/** A logger that drops everything. Mirrors `NullLogger.Instance`. */
export const NullLogger: ILogger = {
  logInformation(): void {},
  logWarning(): void {},
  logError(): void {},
};

/**
 * String-keyed event bus. The host raises events via {@link raise}; plugins
 * subscribe with {@link subscribe}. Payload is opaque. Mirrors C# `IPluginEvents`.
 */
export interface IPluginEvents {
  subscribe(eventName: string, handler: (payload: Object) => void): Disposable;
  raise(eventName: string, payload: Object): void;
}

/** Thread-safe default {@link IPluginEvents}. Mirrors C# `PluginEvents`. */
export class PluginEvents implements IPluginEvents {
  private readonly handlers = new Map<string, Array<(payload: Object) => void>>();

  subscribe(eventName: string, handler: (payload: Object) => void): Disposable {
    if (isEmpty(eventName)) throw new Error("eventName required");
    if (handler == null) throw new Error("handler required");
    let list = this.handlers.get(eventName);
    if (list === undefined) {
      list = [];
      this.handlers.set(eventName, list);
    }
    list.push(handler);
    return new PluginSubscription(this, eventName, handler);
  }

  raise(eventName: string, payload: Object): void {
    const list = this.handlers.get(eventName);
    if (list === undefined) return;
    // Snapshot before invoking so mutation during dispatch is safe.
    for (const h of [...list]) {
      try {
        h(payload);
      } catch {
        /* an unhealthy plugin must not corrupt the host */
      }
    }
  }

  /** @internal */
  unsubscribe(eventName: string, handler: (payload: Object) => void): void {
    const list = this.handlers.get(eventName);
    if (list === undefined) return;
    const idx = list.indexOf(handler);
    if (idx >= 0) list.splice(idx, 1);
  }
}

class PluginSubscription implements Disposable {
  private disposed = false;
  constructor(
    private readonly owner: PluginEvents,
    private readonly name: string,
    private readonly handler: (payload: Object) => void,
  ) {}
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.owner.unsubscribe(this.name, this.handler);
  }
}

/** Well-known event names. Mirrors C# `PluginEventNames`. */
export const PluginEventNames = {
  WorkspaceLoaded: "workspace.loaded",
  ChatMessage: "chat.message",
  ModelLoaded: "model.loaded",
  ModelUnloaded: "model.unloaded",
} as const;

/** Stable surface plugins are allowed to use. Mirrors C# `IPluginContext`. */
export interface IPluginContext {
  readonly workspacePath: string | null;
  readonly events: IPluginEvents;
  readonly logger: ILogger;
}

/** The contract every CircleAI plugin implements. Mirrors C# `IPlugin`. */
export interface IPlugin {
  readonly id: string;
  readonly displayName: string;
  readonly version: string;
  initializeAsync(context: IPluginContext, signal?: AbortSignal): Promise<void>;
  shutdownAsync(signal?: AbortSignal): Promise<void>;
}

/** Optional host hook for the workspace path plugins see. Mirrors C# `IWorkspacePathProvider`. */
export interface IWorkspacePathProvider {
  readonly workspacePath: string | null;
}

/** Default {@link IPluginContext}. Mirrors C# `PluginContext`. */
export class PluginContext implements IPluginContext {
  private readonly workspacePathAccessor: () => string | null;
  readonly events: IPluginEvents;
  readonly logger: ILogger;

  constructor(
    workspacePathAccessor: (() => string | null) | null,
    events: IPluginEvents,
    logger: ILogger,
  ) {
    this.workspacePathAccessor = workspacePathAccessor ?? ((): string | null => null);
    if (events == null) throw new Error("events required");
    if (logger == null) throw new Error("logger required");
    this.events = events;
    this.logger = logger;
  }

  get workspacePath(): string | null {
    return this.workspacePathAccessor();
  }
}

/** Permission tokens for {@link PermissionedPluginContext}. Mirrors C# `PermissionedPluginContext.Permissions`. */
export const PermissionedPluginContextPermissions = {
  WorkspaceRead: "workspace.read",
  WorkspaceWrite: "workspace.write",
  EventsSubscribe: "events.subscribe",
} as const;

/**
 * Wraps an inner context and gates capabilities by a granted-permission set.
 * Mirrors C# `PermissionedPluginContext`. Permission checks are case-insensitive.
 */
export class PermissionedPluginContext implements IPluginContext {
  /** Permission tokens (mirrors the C# nested `Permissions` class). */
  static readonly Permissions = PermissionedPluginContextPermissions;

  private readonly inner: IPluginContext;
  private readonly granted: Set<string>;
  private readonly eventsBus: IPluginEvents;

  constructor(inner: IPluginContext, grantedPermissions: Iterable<string> | null) {
    if (inner == null) throw new Error("inner required");
    this.inner = inner;
    this.granted = new Set([...(grantedPermissions ?? [])].map((p) => p.toLowerCase()));
    this.eventsBus = this.granted.has(PermissionedPluginContextPermissions.EventsSubscribe)
      ? inner.events
      : new SilentEvents();
  }

  get workspacePath(): string | null {
    return this.granted.has(PermissionedPluginContextPermissions.WorkspaceRead) ||
      this.granted.has(PermissionedPluginContextPermissions.WorkspaceWrite)
      ? this.inner.workspacePath
      : null;
  }

  get events(): IPluginEvents {
    return this.eventsBus;
  }

  get logger(): ILogger {
    return this.inner.logger;
  }
}

/** Drop-on-the-floor event bus for permission-denied plugins. Mirrors the C# `SilentEvents`. */
class SilentEvents implements IPluginEvents {
  subscribe(_eventName: string, _handler: (payload: Object) => void): Disposable {
    return { dispose(): void {} };
  }
  raise(_eventName: string, _payload: Object): void {}
}

function isEmpty(s: string | null | undefined): boolean {
  return s == null || s.length === 0;
}
