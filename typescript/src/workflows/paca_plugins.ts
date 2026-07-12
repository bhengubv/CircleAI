// workflows/paca_plugins.ts
//
// (3.3.0) Plugin runtime + manifest + lifecycle ported from paca
// (PacaPlugins.cs): plugin manifest validation, semver upgrade detection,
// reverse-DNS naming, marketplace install/upgrade/uninstall, frontend module
// surface, extension points, artifact + migration management, per-plugin
// resource limits + WASI snapshot preview-1 support.
//
// The wazero / WASM execution layer is host-supplied via IPluginRuntimeHost;
// this module owns the lifecycle. Uri → string | null.

/** (3.3.0) Plugin extension points supported by the marketplace. Mirrors C# `PluginExtensionPoint`. */
export enum PluginExtensionPoint {
  Sidebar = 0,
  TaskDetail = 1,
  Settings = 2,
  CustomView = 3,
  Route = 4,
  Event = 5,
  McpTool = 6,
}

/**
 * (3.3.0) Per-plugin resource limits. Mirrors C# `PluginResourceLimits`.
 * @param callTimeoutMs Max wall-clock time for one host call. Default 5000ms.
 * @param memoryCeilingBytes Max memory the WASM instance may allocate. Default 64MB.
 */
export interface PluginResourceLimits {
  readonly callTimeoutMs: number;
  readonly memoryCeilingBytes: number;
}

/** Constructs {@link PluginResourceLimits} with the C# record defaults. */
export function pluginResourceLimits(callTimeoutMs = 5000, memoryCeilingBytes = 64 * 1024 * 1024): PluginResourceLimits {
  return { callTimeoutMs, memoryCeilingBytes };
}

/** (3.3.0) Plugin manifest from `plugin.json`. Mirrors C# `PluginManifest`. */
export interface PluginManifest {
  /** reverse-DNS, e.g. "com.paca.bdd". */
  readonly name: string;
  readonly displayName: string;
  /** SemVer. */
  readonly version: string;
  readonly description: string;
  readonly artifactWasmUrl: string | null;
  readonly frontendModuleUrl: string | null;
  readonly extensionPoints: readonly PluginExtensionPoint[];
  readonly mcpTools: readonly string[];
  readonly sqlMigrationFiles: readonly string[];
  readonly limits: PluginResourceLimits;
}

/** (3.3.0) Installed instance. Mirrors C# `InstalledPlugin`. */
export interface InstalledPlugin {
  /** matches manifest.name. */
  readonly id: string;
  readonly manifest: PluginManifest;
  readonly installedFromCatalog: string;
  readonly installedAtUtc: Date;
  readonly enabled: boolean;
}

/** (3.3.0) Plugin runtime host (wazero-style). Provided by the deploy. Mirrors C# `IPluginRuntimeHost`. */
export interface IPluginRuntimeHost {
  /** Install + initialise. Run SQL migrations + cache the WASM artifact. */
  installAsync(plugin: InstalledPlugin, signal?: AbortSignal): Promise<void>;
  /** Uninstall — drop WASM + clean artifacts; do NOT roll back data unless asked. */
  uninstallAsync(pluginId: string, dropArtifacts: boolean, signal?: AbortSignal): Promise<void>;
  /** Hot-swap to a new version (semver upgrade). */
  upgradeAsync(from: InstalledPlugin, to: InstalledPlugin, signal?: AbortSignal): Promise<void>;
}

// ^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$ — mirrors the C# compiled ReverseDnsPattern.
const REVERSE_DNS_PATTERN = /^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+$/;

/** (3.3.0) Plugin lifecycle manager. Mirrors C# `PacaPluginRegistry`. */
export class PacaPluginRegistry {
  private readonly installed = new Map<string, InstalledPlugin>();
  private readonly runtime: IPluginRuntimeHost;
  private readonly clock: () => Date;

  constructor(runtime: IPluginRuntimeHost, clock?: (() => Date) | null) {
    if (runtime == null) throw new Error("runtime required");
    this.runtime = runtime;
    this.clock = clock ?? ((): Date => new Date());
  }

  listInstalled(): readonly InstalledPlugin[] {
    return [...this.installed.values()];
  }

  get(id: string): InstalledPlugin | null {
    return this.installed.get(id) ?? null;
  }

  /** (3.3.0) Validate a manifest before install / upgrade. */
  static validateManifest(manifest: PluginManifest): void {
    if (manifest == null) throw new Error("manifest required");
    if (!REVERSE_DNS_PATTERN.test(manifest.name)) {
      throw new Error(`Plugin name '${manifest.name}' must be reverse-DNS (e.g. com.paca.bdd).`);
    }
    if (parseVersion(stripPrerelease(manifest.version)) === null) {
      throw new Error(`Plugin version '${manifest.version}' is not parseable SemVer.`);
    }
    if (manifest.limits.callTimeoutMs <= 0) throw new Error("CallTimeoutMs must be positive.");
    if (manifest.limits.memoryCeilingBytes <= 0) throw new Error("MemoryCeilingBytes must be positive.");
  }

  /** (3.3.0) Install plugin from the supplied manifest. */
  async installAsync(manifest: PluginManifest, catalog: string, signal?: AbortSignal): Promise<InstalledPlugin> {
    PacaPluginRegistry.validateManifest(manifest);
    if (this.installed.has(manifest.name)) {
      throw new Error(`Plugin '${manifest.name}' is already installed; use upgradeAsync.`);
    }
    const installed: InstalledPlugin = {
      id: manifest.name,
      manifest,
      installedFromCatalog: catalog,
      installedAtUtc: this.clock(),
      enabled: true,
    };
    await this.runtime.installAsync(installed, signal);
    this.installed.set(manifest.name, installed);
    return installed;
  }

  /** (3.3.0) Upgrade if `newManifest`'s SemVer is strictly newer. */
  async upgradeAsync(newManifest: PluginManifest, catalog: string, signal?: AbortSignal): Promise<InstalledPlugin> {
    PacaPluginRegistry.validateManifest(newManifest);
    const current = this.installed.get(newManifest.name);
    if (current === undefined) {
      throw new Error(`Plugin '${newManifest.name}' is not installed.`);
    }
    if (PacaPluginRegistry.compareSemver(newManifest.version, current.manifest.version) <= 0) {
      throw new Error(`Version ${newManifest.version} is not newer than ${current.manifest.version}.`);
    }
    const next: InstalledPlugin = {
      id: newManifest.name,
      manifest: newManifest,
      installedFromCatalog: catalog,
      installedAtUtc: this.clock(),
      enabled: current.enabled,
    };
    await this.runtime.upgradeAsync(current, next, signal);
    this.installed.set(newManifest.name, next);
    return next;
  }

  async uninstallAsync(id: string, dropArtifacts = true, signal?: AbortSignal): Promise<void> {
    if (!this.installed.has(id)) return;
    this.installed.delete(id);
    await this.runtime.uninstallAsync(id, dropArtifacts, signal);
  }

  setEnabled(id: string, enabled: boolean): void {
    const current = this.installed.get(id);
    if (current !== undefined) {
      this.installed.set(id, { ...current, enabled });
    }
  }

  /** (3.3.0) Compare SemVer-ish strings: returns <0 / 0 / >0. Mirrors C# `CompareSemver` (System.Version). */
  static compareSemver(a: string, b: string): number {
    const va = parseVersion(stripPrerelease(a));
    const vb = parseVersion(stripPrerelease(b));
    if (va === null) throw new Error(`Version '${a}' is not parseable.`);
    if (vb === null) throw new Error(`Version '${b}' is not parseable.`);
    return compareVersionParts(va, vb);
  }
}

/**
 * Parse a dotted numeric version like System.Version (2..4 numeric components,
 * each non-negative). Returns null when unparseable. Missing minor/build/revision
 * default to -1 the way System.Version leaves them, but we normalise to 0 for a
 * total order that matches Version.CompareTo.
 */
function parseVersion(v: string): [number, number, number, number] | null {
  const raw = v.split(".");
  if (raw.length < 2 || raw.length > 4) return null;
  const nums: number[] = [];
  for (const part of raw) {
    if (!/^\d+$/.test(part)) return null;
    nums.push(Number.parseInt(part, 10));
  }
  return [nums[0], nums[1], nums[2] ?? 0, nums[3] ?? 0];
}

function compareVersionParts(a: [number, number, number, number], b: [number, number, number, number]): number {
  for (let i = 0; i < 4; i++) {
    if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  }
  return 0;
}

function stripPrerelease(v: string): string {
  return v.split(/[-+]/)[0];
}
