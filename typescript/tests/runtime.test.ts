// runtime.test.ts
// Verifies the CircleAI.Runtime port: HostProfile helpers, BackendSelector table
// routing + tier clamping, the capability probes, NativeRuntimeRegistry parsing +
// newest-version lookup, and the deterministic in-memory native-runtime fetcher.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  OperatingSystemKind,
  ArchitectureKind,
  GpuVendor,
  NpuVendor,
  BackendKind,
  CapabilityTier,
  BackendSelector,
  StaticCapabilityProbe,
  UnknownCapabilityProbe,
  CapabilityProbe,
  NativeRuntimeRegistry,
  InMemoryNativeRuntimeFetcher,
  gpuInfo,
  npuInfo,
  hostProfile,
  hasUsableGpu,
  is64Bit,
  type HostProfile,
} from "../src/runtime/index";

const GIB = 1024 * 1024 * 1024;

function profile(overrides: Partial<HostProfile> = {}): HostProfile {
  return hostProfile(
    overrides.os ?? OperatingSystemKind.Linux,
    overrides.osVersion ?? "6.1",
    overrides.arch ?? ArchitectureKind.X64,
    overrides.cpuModel ?? "Test CPU",
    overrides.logicalCoreCount ?? 8,
    overrides.physicalCoreCount ?? 4,
    overrides.totalPhysicalMemoryBytes ?? 16 * GIB,
    overrides.gpu ?? null,
    overrides.npu ?? null,
    overrides.probedAt ?? new Date("2026-01-01T00:00:00Z"),
  );
}

describe("HostProfile helpers", () => {
  it("hasUsableGpu respects the VRAM floor", () => {
    assert.equal(hasUsableGpu(profile({ gpu: gpuInfo(GpuVendor.Nvidia, "x", 1 * GIB, null) })), false);
    assert.equal(hasUsableGpu(profile({ gpu: gpuInfo(GpuVendor.Nvidia, "x", 4 * GIB, null) })), true);
    assert.equal(hasUsableGpu(profile()), false);
  });

  it("is64Bit for X64/Arm64/Loong64", () => {
    assert.equal(is64Bit(profile({ arch: ArchitectureKind.X64 })), true);
    assert.equal(is64Bit(profile({ arch: ArchitectureKind.Arm64 })), true);
    assert.equal(is64Bit(profile({ arch: ArchitectureKind.X86 })), false);
  });
});

describe("BackendSelector", () => {
  const sel = new BackendSelector();

  it("routes Apple Silicon to Metal capped by unified RAM", () => {
    const p = profile({
      os: OperatingSystemKind.MacOS,
      arch: ArchitectureKind.Arm64,
      gpu: gpuInfo(GpuVendor.Apple, "M2", 0, null),
      totalPhysicalMemoryBytes: 16 * GIB,
    });
    const r = sel.select(p, CapabilityTier.Tier4_Frontier);
    assert.equal(r.backend, BackendKind.Metal);
    assert.equal(r.actualTier, CapabilityTier.Tier2_Medium); // 16 GiB unified → Tier2
  });

  it("routes NVIDIA to CUDA capped by VRAM", () => {
    const p = profile({ gpu: gpuInfo(GpuVendor.Nvidia, "RTX", 12 * GIB, "550") });
    const r = sel.select(p, CapabilityTier.Tier4_Frontier);
    assert.equal(r.backend, BackendKind.Cuda);
    assert.equal(r.actualTier, CapabilityTier.Tier3_Large); // 12 GiB VRAM → Tier3
  });

  it("routes Ascend NPU", () => {
    const p = profile({ npu: npuInfo(NpuVendor.HuaweiAscend, "910") });
    const r = sel.select(p, CapabilityTier.Tier4_Frontier);
    assert.equal(r.backend, BackendKind.Ascend);
    assert.equal(r.actualTier, CapabilityTier.Tier3_Large);
  });

  it("routes Qualcomm to OpenCL capped to Tier1", () => {
    const p = profile({ gpu: gpuInfo(GpuVendor.Qualcomm, "Adreno", 0, null) });
    const r = sel.select(p, CapabilityTier.Tier4_Frontier);
    assert.equal(r.backend, BackendKind.OpenCL);
    assert.equal(r.actualTier, CapabilityTier.Tier1_Small);
  });

  it("falls back to CPU when no accelerator is present", () => {
    const r = sel.select(profile({ totalPhysicalMemoryBytes: 16 * GIB }), CapabilityTier.Tier4_Frontier);
    assert.equal(r.backend, BackendKind.Cpu);
    assert.equal(r.actualTier, CapabilityTier.Tier1_Small); // 16 GiB CPU RAM → Tier1
    assert.match(r.rationale, /CPU SIMD backend/);
  });

  it("never upgrades above the requested tier", () => {
    const p = profile({ gpu: gpuInfo(GpuVendor.Nvidia, "RTX", 24 * GIB, null) });
    const r = sel.select(p, CapabilityTier.Tier1_Small);
    assert.equal(r.actualTier, CapabilityTier.Tier1_Small);
  });
});

describe("Capability probes", () => {
  it("StaticCapabilityProbe returns its fixed profile", async () => {
    const p = profile({ cpuModel: "Fixed" });
    assert.equal((await new StaticCapabilityProbe(p).probeAsync()).cpuModel, "Fixed");
  });

  it("UnknownCapabilityProbe returns an Unknown profile", async () => {
    const p = await new UnknownCapabilityProbe().probeAsync();
    assert.equal(p.os, OperatingSystemKind.Unknown);
    assert.equal(p.arch, ArchitectureKind.Unknown);
  });

  it("CapabilityProbe delegates to the injected inner probe", async () => {
    const p = profile({ cpuModel: "Inner" });
    assert.equal((await new CapabilityProbe(new StaticCapabilityProbe(p)).probeAsync()).cpuModel, "Inner");
  });
});

describe("NativeRuntimeRegistry", () => {
  const doc = {
    mnn_versions: [
      {
        version: "3.4.0",
        bundles: [{ os: "Windows", arch: "X64", backend: "Cpu", url: "https://example.com/a.tar.gz" }],
      },
      {
        version: "3.5.0",
        bundles: [
          {
            os: "windows",
            arch: "x64",
            backend: "cpu",
            url: "https://example.com/b.tar.gz",
            sha256: "abc",
            mnn_lib: "MNN.dll",
          },
          { os: "not-an-os", arch: "x64", backend: "cpu", url: "https://example.com/c.tar.gz" },
        ],
      },
    ],
  };

  it("parses valid bundles and skips malformed ones", () => {
    const reg = NativeRuntimeRegistry.fromJson(doc);
    assert.equal(reg.all.length, 2); // the "not-an-os" bundle is skipped
  });

  it("find returns the newest version for a tuple", () => {
    const reg = NativeRuntimeRegistry.fromJson(doc);
    const b = reg.find(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu);
    assert.equal(b?.mnnVersion, "3.5.0");
    assert.equal(b?.archiveSha256Hex, "abc");
  });

  it("returns an empty registry for a doc with no versions", () => {
    assert.equal(NativeRuntimeRegistry.fromJson({}).all.length, 0);
    assert.equal(NativeRuntimeRegistry.fromJson(null).all.length, 0);
  });
});

describe("InMemoryNativeRuntimeFetcher", () => {
  const reg = NativeRuntimeRegistry.fromJson({
    mnn_versions: [
      { version: "3.5.0", bundles: [{ os: "Linux", arch: "X64", backend: "Cpu", url: "https://x/a.tgz" }] },
    ],
  });

  it("resolves and caches an install for a known tuple", async () => {
    const f = new InMemoryNativeRuntimeFetcher(reg);
    assert.equal(await f.isRuntimeCachedAsync(OperatingSystemKind.Linux, ArchitectureKind.X64, BackendKind.Cpu), false);
    const install = await f.ensureRuntimeAsync(OperatingSystemKind.Linux, ArchitectureKind.X64, BackendKind.Cpu);
    assert.equal(install.bundle.mnnVersion, "3.5.0");
    assert.match(install.mnnCorePath, /libMNN\.so$/);
    assert.equal(await f.isRuntimeCachedAsync(OperatingSystemKind.Linux, ArchitectureKind.X64, BackendKind.Cpu), true);
  });

  it("rejects an unknown tuple", async () => {
    const f = new InMemoryNativeRuntimeFetcher(reg);
    await assert.rejects(() =>
      f.ensureRuntimeAsync(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cpu),
    );
  });

  it("lists the registry's bundles", () => {
    assert.equal(new InMemoryNativeRuntimeFetcher(reg).listAvailableBundles().length, 1);
  });
});
