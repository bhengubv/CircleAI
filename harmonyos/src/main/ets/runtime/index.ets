/**
 * runtime/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// runtime/index.ts
// Full-parity port of CircleAI.Runtime (C#). C# is the exact spec.
//
// Two sub-areas the host wires around a native MNN runtime:
//   • Capabilities — OperatingSystemKind / ArchitectureKind / GpuVendor /
//     NpuVendor enums, GpuInfo / NpuInfo / HostProfile records, ICapabilityProbe
//     + deterministic probes.
//   • Backends — BackendKind / CapabilityTier enums, BackendSelection, the
//     IBackendSelector contract, and the deterministic table-style BackendSelector.
//   • NativeRuntimes — NativeRuntimeBundle / NativeRuntimeInstall records, the
//     INativeRuntimeFetcher contract, a NativeRuntimeRegistry (pure lookup), and a
//     deterministic in-memory fetcher (cache injected; no network).
//
// Type mappings (C# → TS):
//   long (bytes / VRAM / RAM)       → number
//   DateTimeOffset ProbedAt         → Date
//   Uri PrimaryUri                  → string
//   Task<HostProfile>               → Promise<HostProfile>
//   record + helper methods         → readonly interface + free functions

// ═════════════════════════════════════════════════════════════════════════════
// Capabilities
// ═════════════════════════════════════════════════════════════════════════════

/** OS family the probe recognised. Mirrors C# `OperatingSystemKind`. */
export enum OperatingSystemKind {
  Unknown = 0,
  Windows = 1,
  Linux = 2,
  MacOS = 3,
  Android = 4,
  IOS = 5,
  HarmonyOS = 6,
}

/** CPU architecture family. Mirrors C# `ArchitectureKind`. */
export enum ArchitectureKind {
  Unknown = 0,
  X86 = 1,
  X64 = 2,
  Arm = 3,
  Arm64 = 4,
  Loong64 = 5,
}

/** GPU vendor identifier. Mirrors C# `GpuVendor`. */
export enum GpuVendor {
  None = 0,
  Nvidia = 1,
  Amd = 2,
  Intel = 3,
  Apple = 4,
  Qualcomm = 5,
  Huawei = 6,
  Arm = 7,
  Other = 99,
}

/** NPU / neural accelerator vendor identifier. Mirrors C# `NpuVendor`. */
export enum NpuVendor {
  None = 0,
  AppleNeuralEngine = 1,
  QualcommHexagon = 2,
  HuaweiAscend = 3,
  IntelVpu = 4,
  CambriconMlu = 5,
  Other = 99,
}

/** Discovered GPU details. Mirrors C# `GpuInfo` record. */
export interface GpuInfo {
  readonly vendor: GpuVendor;
  readonly model: string;
  /** Dedicated video memory in bytes; 0 when the probe could not determine it. */
  readonly vramBytes: number;
  readonly driverVersion: string | null;
}

/** Constructs a {@link GpuInfo}. */
export function gpuInfo(
  vendor: GpuVendor,
  model: string,
  vramBytes: number,
  driverVersion: string | null,
): GpuInfo {
  return { vendor, model, vramBytes, driverVersion };
}

/** Discovered NPU details. Mirrors C# `NpuInfo` record. */
export interface NpuInfo {
  readonly vendor: NpuVendor;
  readonly model: string;
}

/** Constructs a {@link NpuInfo}. */
export function npuInfo(vendor: NpuVendor, model: string): NpuInfo {
  return { vendor, model };
}

/**
 * Full host capability snapshot — the result of an {@link ICapabilityProbe.probeAsync}
 * call. Mirrors C# `HostProfile` record. `gpu`/`npu` are `null` when absent.
 */
export interface HostProfile {
  readonly os: OperatingSystemKind;
  readonly osVersion: string;
  readonly arch: ArchitectureKind;
  readonly cpuModel: string;
  readonly logicalCoreCount: number;
  readonly physicalCoreCount: number;
  readonly totalPhysicalMemoryBytes: number;
  readonly gpu: GpuInfo | null;
  readonly npu: NpuInfo | null;
  readonly probedAt: Date;
}

/** Constructs a {@link HostProfile}. */
export function hostProfile(
  os: OperatingSystemKind,
  osVersion: string,
  arch: ArchitectureKind,
  cpuModel: string,
  logicalCoreCount: number,
  physicalCoreCount: number,
  totalPhysicalMemoryBytes: number,
  gpu: GpuInfo | null,
  npu: NpuInfo | null,
  probedAt: Date,
): HostProfile {
  return {
    os,
    osVersion,
    arch,
    cpuModel,
    logicalCoreCount,
    physicalCoreCount,
    totalPhysicalMemoryBytes,
    gpu,
    npu,
    probedAt,
  };
}

/**
 * True when {@link HostProfile.gpu} is present and has at least `minimumVramBytes`
 * of dedicated VRAM. Mirrors C# `HostProfile.HasUsableGpu` (default 2 GiB).
 */
export function hasUsableGpu(p: HostProfile, minimumVramBytes = 2 * 1024 * 1024 * 1024): boolean {
  return p.gpu !== null && p.gpu.vramBytes >= minimumVramBytes;
}

/** True when the host runs on a 64-bit architecture. Mirrors C# `HostProfile.Is64Bit`. */
export function is64Bit(p: HostProfile): boolean {
  return (
    p.arch === ArchitectureKind.X64 ||
    p.arch === ArchitectureKind.Arm64 ||
    p.arch === ArchitectureKind.Loong64
  );
}

/**
 * Discovers the host's hardware capabilities. Implementations MUST NOT throw on
 * probe failure — unresolved fields come back as `Unknown`/`null`/`0`. Mirrors
 * C# `ICapabilityProbe`.
 */
export interface ICapabilityProbe {
  probeAsync(signal?: AbortSignal): Promise<HostProfile>;
}

/**
 * Deterministic probe that returns a fixed {@link HostProfile}. This is the
 * injectable form used by hosts, tests, and platform ports (which pass the
 * profile their platform-specific detection produced). Mirrors the injected
 * inner-probe path of C# `CapabilityProbe`.
 */
export class StaticCapabilityProbe implements ICapabilityProbe {
  private readonly profile: HostProfile;

  constructor(profile: HostProfile) {
    if (profile == null) throw new Error("profile required");
    this.profile = profile;
  }

  probeAsync(signal?: AbortSignal): Promise<HostProfile> {
    if (signal?.aborted) return Promise.reject(new Error("probe aborted"));
    return Promise.resolve(this.profile);
  }
}

/**
 * Returned on platforms where no in-process probe is registered. All fields
 * fall back to `Unknown` / `0` / `null`. Mirrors C# `UnknownCapabilityProbe`.
 */
export class UnknownCapabilityProbe implements ICapabilityProbe {
  probeAsync(signal?: AbortSignal): Promise<HostProfile> {
    if (signal?.aborted) return Promise.reject(new Error("probe aborted"));
    return Promise.resolve(
      hostProfile(
        OperatingSystemKind.Unknown,
        "0.0.0.0",
        ArchitectureKind.Unknown,
        "Unknown CPU",
        1,
        1,
        0,
        null,
        null,
        new Date(),
      ),
    );
  }
}

/**
 * Default {@link ICapabilityProbe} — delegates to an injected inner probe. In C#
 * the parameterless constructor dispatches to a native OS-specific probe; that
 * native path has no in-process TS analogue, so the host supplies the inner
 * probe (mirroring the C# `CapabilityProbe(ICapabilityProbe inner)` constructor).
 */
export class CapabilityProbe implements ICapabilityProbe {
  private readonly inner: ICapabilityProbe;

  constructor(inner?: ICapabilityProbe) {
    this.inner = inner ?? new UnknownCapabilityProbe();
  }

  probeAsync(signal?: AbortSignal): Promise<HostProfile> {
    return this.inner.probeAsync(signal);
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Backends
// ═════════════════════════════════════════════════════════════════════════════

/** MNN execution backend. Mirrors C# `BackendKind`. */
export enum BackendKind {
  Cpu = 0,
  Cuda = 1,
  Vulkan = 2,
  OpenCL = 3,
  Metal = 4,
  Ascend = 5,
  Cambricon = 6,
  CoreML = 7,
}

/**
 * Capability tier mapping to a Qwen / DeepSeek / GLM / Kimi model size band.
 * Mirrors C# `CapabilityTier` (explicit values 0..4).
 */
export enum CapabilityTier {
  Tier0_Tiny = 0,
  Tier1_Small = 1,
  Tier2_Medium = 2,
  Tier3_Large = 3,
  Tier4_Frontier = 4,
}

/** Result of an {@link IBackendSelector.select} call. Mirrors C# `BackendSelection`. */
export interface BackendSelection {
  readonly backend: BackendKind;
  readonly actualTier: CapabilityTier;
  readonly rationale: string;
}

/** Constructs a {@link BackendSelection}. */
export function backendSelection(
  backend: BackendKind,
  actualTier: CapabilityTier,
  rationale: string,
): BackendSelection {
  return { backend, actualTier, rationale };
}

/**
 * Picks the MNN backend and model tier for a given host. Implementations must
 * NEVER throw and NEVER return null. Mirrors C# `IBackendSelector`.
 */
export interface IBackendSelector {
  select(profile: HostProfile, requestedTier: CapabilityTier): BackendSelection;
}

const GIB = 1024 * 1024 * 1024;

/**
 * Default {@link IBackendSelector}. Deterministic, no I/O. The selection logic is
 * an explicit table so operators can predict routing. Mirrors C# `BackendSelector`.
 */
export class BackendSelector implements IBackendSelector {
  select(profile: HostProfile, requestedTier: CapabilityTier): BackendSelection {
    if (profile == null) throw new Error("profile required");

    // 1. Apple Silicon — Metal + ANE coexist via unified memory.
    if (
      profile.os === OperatingSystemKind.MacOS &&
      profile.arch === ArchitectureKind.Arm64 &&
      profile.gpu?.vendor === GpuVendor.Apple
    ) {
      const tier = clampTier(requestedTier, tierForUnifiedMemory(profile.totalPhysicalMemoryBytes));
      return backendSelection(
        BackendKind.Metal,
        tier,
        `Apple Silicon (${profile.cpuModel}); Metal over unified-memory GPU; tier capped to ${CapabilityTier[tier]} by ${Math.floor(profile.totalPhysicalMemoryBytes / GIB)} GiB unified RAM.`,
      );
    }

    // 2. NVIDIA + CUDA.
    if (profile.gpu?.vendor === GpuVendor.Nvidia && profile.gpu.vramBytes >= 4 * GIB) {
      const tier = clampTier(requestedTier, tierForVram(profile.gpu.vramBytes));
      return backendSelection(
        BackendKind.Cuda,
        tier,
        `NVIDIA ${profile.gpu.model} with ${Math.floor(profile.gpu.vramBytes / GIB)} GiB VRAM; CUDA backend; tier capped to ${CapabilityTier[tier]} by VRAM.`,
      );
    }

    // 3. Huawei Ascend NPU.
    if (profile.npu?.vendor === NpuVendor.HuaweiAscend) {
      const tier = clampTier(requestedTier, CapabilityTier.Tier3_Large);
      return backendSelection(
        BackendKind.Ascend,
        tier,
        `Huawei Ascend NPU detected (${profile.npu.model}); Ascend (CANN) backend; tier capped to ${CapabilityTier[tier]}.`,
      );
    }

    // 4. Cambricon MLU.
    if (profile.npu?.vendor === NpuVendor.CambriconMlu) {
      const tier = clampTier(requestedTier, CapabilityTier.Tier3_Large);
      return backendSelection(
        BackendKind.Cambricon,
        tier,
        `Cambricon MLU detected; Cambricon backend; tier capped to ${CapabilityTier[tier]}.`,
      );
    }

    // 5. AMD / Intel discrete GPU — Vulkan.
    const g = profile.gpu;
    if (
      g !== null &&
      (g.vendor === GpuVendor.Amd || g.vendor === GpuVendor.Intel) &&
      g.vramBytes >= 4 * GIB
    ) {
      const tier = clampTier(requestedTier, tierForVram(g.vramBytes));
      return backendSelection(
        BackendKind.Vulkan,
        tier,
        `${GpuVendor[g.vendor]} ${g.model} with ${Math.floor(g.vramBytes / GIB)} GiB VRAM; Vulkan backend; tier capped to ${CapabilityTier[tier]} by VRAM.`,
      );
    }

    // 6. Qualcomm Hexagon NPU / Adreno GPU — OpenCL.
    if (
      profile.npu?.vendor === NpuVendor.QualcommHexagon ||
      profile.gpu?.vendor === GpuVendor.Qualcomm
    ) {
      const tier = clampTier(requestedTier, CapabilityTier.Tier1_Small);
      return backendSelection(
        BackendKind.OpenCL,
        tier,
        `Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to ${CapabilityTier[tier]}.`,
      );
    }

    // 7. ARM Mali via Vulkan (MediaTek, Exynos, Tensor).
    if (profile.gpu?.vendor === GpuVendor.Arm || profile.gpu?.vendor === GpuVendor.Huawei) {
      const tier = clampTier(requestedTier, CapabilityTier.Tier1_Small);
      return backendSelection(
        BackendKind.Vulkan,
        tier,
        `ARM/Mali class GPU (${profile.gpu.model}); Vulkan backend; tier capped to ${CapabilityTier[tier]}.`,
      );
    }

    // 8. CPU fallback — always selectable.
    const cpuTier = clampTier(requestedTier, tierForCpuRam(profile.totalPhysicalMemoryBytes));
    return backendSelection(
      BackendKind.Cpu,
      cpuTier,
      `No usable accelerator detected; CPU SIMD backend on ${profile.cpuModel} ` +
        `(${profile.logicalCoreCount} logical cores, ${Math.floor(profile.totalPhysicalMemoryBytes / GIB)} GiB RAM); ` +
        `tier capped to ${CapabilityTier[cpuTier]} by available RAM.`,
    );
  }
}

function clampTier(requested: CapabilityTier, ceiling: CapabilityTier): CapabilityTier {
  return requested <= ceiling ? requested : ceiling;
}

function tierForVram(vramBytes: number): CapabilityTier {
  if (vramBytes >= 24 * GIB) return CapabilityTier.Tier4_Frontier;
  if (vramBytes >= 12 * GIB) return CapabilityTier.Tier3_Large;
  if (vramBytes >= 8 * GIB) return CapabilityTier.Tier2_Medium;
  if (vramBytes >= 4 * GIB) return CapabilityTier.Tier1_Small;
  return CapabilityTier.Tier0_Tiny;
}

function tierForUnifiedMemory(ramBytes: number): CapabilityTier {
  if (ramBytes >= 64 * GIB) return CapabilityTier.Tier4_Frontier;
  if (ramBytes >= 32 * GIB) return CapabilityTier.Tier3_Large;
  if (ramBytes >= 16 * GIB) return CapabilityTier.Tier2_Medium;
  if (ramBytes >= 8 * GIB) return CapabilityTier.Tier1_Small;
  return CapabilityTier.Tier0_Tiny;
}

function tierForCpuRam(ramBytes: number): CapabilityTier {
  if (ramBytes >= 64 * GIB) return CapabilityTier.Tier3_Large;
  if (ramBytes >= 32 * GIB) return CapabilityTier.Tier2_Medium;
  if (ramBytes >= 16 * GIB) return CapabilityTier.Tier1_Small;
  if (ramBytes >= 8 * GIB) return CapabilityTier.Tier1_Small;
  return CapabilityTier.Tier0_Tiny;
}

// ═════════════════════════════════════════════════════════════════════════════
// NativeRuntimes
// ═════════════════════════════════════════════════════════════════════════════

/**
 * A single fetchable MNN runtime bundle for one (OS, arch, backend) tuple.
 * Mirrors C# `NativeRuntimeBundle` record. `Uri` fields are absolute URL strings.
 */
export interface NativeRuntimeBundle {
  readonly mnnVersion: string;
  readonly os: OperatingSystemKind;
  readonly arch: ArchitectureKind;
  readonly backend: BackendKind;
  readonly primaryUri: string;
  readonly fallbackUri: string | null;
  readonly archiveSha256Hex: string | null;
  readonly mnnCoreLibraryName: string;
}

/** Constructs a {@link NativeRuntimeBundle}. */
export function nativeRuntimeBundle(
  mnnVersion: string,
  os: OperatingSystemKind,
  arch: ArchitectureKind,
  backend: BackendKind,
  primaryUri: string,
  fallbackUri: string | null,
  archiveSha256Hex: string | null,
  mnnCoreLibraryName: string,
): NativeRuntimeBundle {
  return {
    mnnVersion,
    os,
    arch,
    backend,
    primaryUri,
    fallbackUri,
    archiveSha256Hex,
    mnnCoreLibraryName,
  };
}

/**
 * Result of a successful {@link INativeRuntimeFetcher.ensureRuntimeAsync} call.
 * Mirrors C# `NativeRuntimeInstall` record.
 */
export interface NativeRuntimeInstall {
  readonly bundle: NativeRuntimeBundle;
  readonly extractedRoot: string;
  readonly mnnCorePath: string;
}

/** Constructs a {@link NativeRuntimeInstall}. */
export function nativeRuntimeInstall(
  bundle: NativeRuntimeBundle,
  extractedRoot: string,
  mnnCorePath: string,
): NativeRuntimeInstall {
  return { bundle, extractedRoot, mnnCorePath };
}

/**
 * Pre-built MNN native runtime fetcher — single source of truth for where the
 * on-disk runtime tree lives and how to bring it up to date. Mirrors C#
 * `INativeRuntimeFetcher`.
 */
export interface INativeRuntimeFetcher {
  ensureRuntimeAsync(
    os: OperatingSystemKind,
    arch: ArchitectureKind,
    backend: BackendKind,
    progress?: ((fraction: number) => void) | null,
    signal?: AbortSignal,
  ): Promise<NativeRuntimeInstall>;

  isRuntimeCachedAsync(
    os: OperatingSystemKind,
    arch: ArchitectureKind,
    backend: BackendKind,
    signal?: AbortSignal,
  ): Promise<boolean>;

  listAvailableBundles(): readonly NativeRuntimeBundle[];
}

const OS_BY_NAME: ReadonlyMap<string, OperatingSystemKind> = new Map(
  Object.entries(OperatingSystemKind)
    .filter(([k]) => Number.isNaN(Number(k)))
    .map(([k, v]) => [k.toLowerCase(), v as OperatingSystemKind]),
);
const ARCH_BY_NAME: ReadonlyMap<string, ArchitectureKind> = new Map(
  Object.entries(ArchitectureKind)
    .filter(([k]) => Number.isNaN(Number(k)))
    .map(([k, v]) => [k.toLowerCase(), v as ArchitectureKind]),
);
const BACKEND_BY_NAME: ReadonlyMap<string, BackendKind> = new Map(
  Object.entries(BackendKind)
    .filter(([k]) => Number.isNaN(Number(k)))
    .map(([k, v]) => [k.toLowerCase(), v as BackendKind]),
);

/** One version entry in the native-runtime registry JSON. */
export interface NativeRegistryVersionEntry {
  readonly version?: string;
  readonly bundles?: readonly NativeRegistryBundleEntry[];
}

/** One bundle entry in the native-runtime registry JSON. */
export interface NativeRegistryBundleEntry {
  readonly os?: string;
  readonly arch?: string;
  readonly backend?: string;
  readonly url?: string;
  readonly fallback_url?: string;
  readonly sha256?: string;
  readonly mnn_lib?: string;
}

/** Root shape of the native-runtime registry JSON. */
export interface NativeRegistryDocument {
  readonly mnn_versions?: readonly NativeRegistryVersionEntry[];
}

function defaultCoreLibName(os: OperatingSystemKind): string {
  switch (os) {
    case OperatingSystemKind.Windows:
      return "MNN.dll";
    case OperatingSystemKind.MacOS:
    case OperatingSystemKind.IOS:
      return "MNN";
    default:
      return "libMNN.so";
  }
}

/**
 * In-process registry of pre-built MNN runtime bundles. Non-object / malformed
 * entries are tolerated to keep the source file human-editable. Mirrors C#
 * `NativeRuntimeRegistry` (the pure lookup; the C# embedded-resource loader has
 * no TS analogue — load the parsed JSON via {@link NativeRuntimeRegistry.fromJson}).
 */
export class NativeRuntimeRegistry {
  private readonly bundles: readonly NativeRuntimeBundle[];

  private constructor(bundles: readonly NativeRuntimeBundle[]) {
    this.bundles = bundles;
  }

  /** Build a registry from a parsed registry document. Mirrors C# `LoadFromStream`. */
  static fromJson(doc: NativeRegistryDocument | null): NativeRuntimeRegistry {
    const list: NativeRuntimeBundle[] = [];
    const versions = doc?.mnn_versions;
    if (!Array.isArray(versions)) return new NativeRuntimeRegistry(list);

    for (const versionEntry of versions) {
      if (versionEntry == null || typeof versionEntry !== "object") continue;
      const mnnVersion = versionEntry.version;
      const bundlesArr = versionEntry.bundles;
      if (typeof mnnVersion !== "string" || !Array.isArray(bundlesArr)) continue;

      for (const b of bundlesArr) {
        const bundle = NativeRuntimeRegistry.tryParseBundle(mnnVersion, b);
        if (bundle !== null) list.push(bundle);
      }
    }
    return new NativeRuntimeRegistry(list);
  }

  /** Build a registry directly from a list of bundles. */
  static fromBundles(bundles: readonly NativeRuntimeBundle[]): NativeRuntimeRegistry {
    return new NativeRuntimeRegistry([...bundles]);
  }

  private static tryParseBundle(
    mnnVersion: string,
    b: NativeRegistryBundleEntry,
  ): NativeRuntimeBundle | null {
    if (b == null || typeof b !== "object") return null;
    if (typeof b.os !== "string" || typeof b.arch !== "string") return null;
    if (typeof b.backend !== "string" || typeof b.url !== "string") return null;

    const os = OS_BY_NAME.get(b.os.toLowerCase());
    const arch = ARCH_BY_NAME.get(b.arch.toLowerCase());
    const backend = BACKEND_BY_NAME.get(b.backend.toLowerCase());
    if (os === undefined || arch === undefined || backend === undefined) return null;
    if (!isAbsoluteUri(b.url)) return null;

    const fallback =
      typeof b.fallback_url === "string" && isAbsoluteUri(b.fallback_url) ? b.fallback_url : null;
    const sha = typeof b.sha256 === "string" ? b.sha256 : null;
    const coreLib =
      typeof b.mnn_lib === "string" && b.mnn_lib.length > 0
        ? b.mnn_lib
        : defaultCoreLibName(os);

    return nativeRuntimeBundle(mnnVersion, os, arch, backend, b.url, fallback, sha, coreLib);
  }

  /** All loaded bundles. Mirrors C# `All`. */
  get all(): readonly NativeRuntimeBundle[] {
    return this.bundles;
  }

  /**
   * Look up the newest bundle matching the tuple. When several MNN versions are
   * registered for the same tuple, the highest version string wins (ordinal
   * sort). Mirrors C# `Find`.
   */
  find(
    os: OperatingSystemKind,
    arch: ArchitectureKind,
    backend: BackendKind,
  ): NativeRuntimeBundle | null {
    const matches = this.bundles.filter(
      (b) => b.os === os && b.arch === arch && b.backend === backend,
    );
    if (matches.length === 0) return null;
    matches.sort((a, b) => (a.mnnVersion < b.mnnVersion ? 1 : a.mnnVersion > b.mnnVersion ? -1 : 0));
    return matches[0];
  }
}

function isAbsoluteUri(s: string): boolean {
  try {
    // eslint-disable-next-line no-new
    new URL(s);
    return true;
  } catch {
    return false;
  }
}

/**
 * Deterministic in-memory {@link INativeRuntimeFetcher}. Resolves the bundle for a
 * tuple from a {@link NativeRuntimeRegistry}, then materialises the install from an
 * injected cache map (tuple key → extracted root). No network I/O — the host's
 * real fetcher (HTTP + tar + SHA verify) is swapped in behind the same contract.
 */
export class InMemoryNativeRuntimeFetcher implements INativeRuntimeFetcher {
  private readonly registry: NativeRuntimeRegistry;
  private readonly cache: Map<string, string>;

  /**
   * @param registry Bundle registry used to resolve tuples.
   * @param cache Map of tuple key → already-extracted root directory. Present
   *   entries are treated as cached; missing entries are "materialised" on
   *   ensure and added to the cache. The default cache root is derived from the
   *   tuple so the fetcher is fully deterministic and offline.
   */
  constructor(registry: NativeRuntimeRegistry, cache?: Map<string, string>) {
    if (registry == null) throw new Error("registry required");
    this.registry = registry;
    this.cache = cache ?? new Map<string, string>();
  }

  private static key(os: OperatingSystemKind, arch: ArchitectureKind, backend: BackendKind): string {
    return `${OperatingSystemKind[os]}/${ArchitectureKind[arch]}/${BackendKind[backend]}`;
  }

  ensureRuntimeAsync(
    os: OperatingSystemKind,
    arch: ArchitectureKind,
    backend: BackendKind,
    progress?: ((fraction: number) => void) | null,
    signal?: AbortSignal,
  ): Promise<NativeRuntimeInstall> {
    if (signal?.aborted) return Promise.reject(new Error("ensureRuntime aborted"));

    const bundle = this.registry.find(os, arch, backend);
    if (bundle === null) {
      return Promise.reject(
        new Error(
          `No native-runtime registry entry for (${OperatingSystemKind[os]}, ${ArchitectureKind[arch]}, ${BackendKind[backend]}).`,
        ),
      );
    }

    const key = InMemoryNativeRuntimeFetcher.key(os, arch, backend);
    let extractedRoot = this.cache.get(key);
    if (extractedRoot === undefined) {
      // "Materialise" deterministically — no bytes are moved; the in-memory
      // fetcher records where a real fetcher would have extracted the archive.
      extractedRoot = `mem://native-runtime/${key}/${bundle.mnnVersion}`;
      this.cache.set(key, extractedRoot);
    }
    progress?.(1.0);

    const mnnCorePath = `${extractedRoot}/${bundle.mnnCoreLibraryName}`;
    return Promise.resolve(nativeRuntimeInstall(bundle, extractedRoot, mnnCorePath));
  }

  isRuntimeCachedAsync(
    os: OperatingSystemKind,
    arch: ArchitectureKind,
    backend: BackendKind,
    signal?: AbortSignal,
  ): Promise<boolean> {
    if (signal?.aborted) return Promise.reject(new Error("isRuntimeCached aborted"));
    return Promise.resolve(this.cache.has(InMemoryNativeRuntimeFetcher.key(os, arch, backend)));
  }

  listAvailableBundles(): readonly NativeRuntimeBundle[] {
    return this.registry.all;
  }
}
