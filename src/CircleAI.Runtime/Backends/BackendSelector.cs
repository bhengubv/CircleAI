// BackendSelector.cs
//
// Deterministic, table-style selector. Each branch documents which host
// shape it claims, so failures during operator-facing routing are
// debuggable from the rationale string alone.

using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.Backends;

/// <summary>
/// Default <see cref="IBackendSelector"/>. Deterministic; no I/O; safe to
/// call on hot paths. The selection logic is intentionally explicit so
/// operators can predict routing without running the code.
/// </summary>
public sealed class BackendSelector : IBackendSelector
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <inheritdoc/>
    public BackendSelection Select(HostProfile profile, ModelTier requestedTier)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // ── 1. Apple Silicon — Metal + ANE coexist via unified memory ──────────
        if (profile.Os == OperatingSystemKind.MacOS &&
            profile.Arch == ArchitectureKind.Arm64 &&
            profile.Gpu?.Vendor == GpuVendor.Apple)
        {
            var tier = ClampTier(requestedTier,
                ceiling: TierForUnifiedMemory(profile.TotalPhysicalMemoryBytes));
            return new BackendSelection(
                BackendKind.Metal, tier,
                $"Apple Silicon ({profile.CpuModel}); Metal over unified-memory GPU; tier capped to {tier} by {profile.TotalPhysicalMemoryBytes / GiB} GiB unified RAM.");
        }

        // ── 2. NVIDIA + CUDA — best on Linux + Windows ─────────────────────────
        if (profile.Gpu?.Vendor == GpuVendor.Nvidia && profile.Gpu.VramBytes >= 4 * GiB)
        {
            var tier = ClampTier(requestedTier,
                ceiling: TierForVram(profile.Gpu.VramBytes));
            return new BackendSelection(
                BackendKind.Cuda, tier,
                $"NVIDIA {profile.Gpu.Model} with {profile.Gpu.VramBytes / GiB} GiB VRAM; CUDA backend; tier capped to {tier} by VRAM.");
        }

        // ── 3. Huawei Ascend NPU — Chinese data-centre + Kirin laptops ─────────
        if (profile.Npu?.Vendor == NpuVendor.HuaweiAscend)
        {
            var tier = ClampTier(requestedTier, ceiling: ModelTier.Tier3_Large);
            return new BackendSelection(
                BackendKind.Ascend, tier,
                $"Huawei Ascend NPU detected ({profile.Npu.Model}); Ascend (CANN) backend; tier capped to {tier}.");
        }

        // ── 4. Cambricon MLU — Chinese accelerator ─────────────────────────────
        if (profile.Npu?.Vendor == NpuVendor.CambriconMlu)
        {
            var tier = ClampTier(requestedTier, ceiling: ModelTier.Tier3_Large);
            return new BackendSelection(
                BackendKind.Cambricon, tier,
                $"Cambricon MLU detected; Cambricon backend; tier capped to {tier}.");
        }

        // ── 5. AMD / Intel discrete GPU — Vulkan ───────────────────────────────
        if (profile.Gpu is { } g &&
            (g.Vendor == GpuVendor.Amd || g.Vendor == GpuVendor.Intel) &&
            g.VramBytes >= 4 * GiB)
        {
            var tier = ClampTier(requestedTier,
                ceiling: TierForVram(g.VramBytes));
            return new BackendSelection(
                BackendKind.Vulkan, tier,
                $"{g.Vendor} {g.Model} with {g.VramBytes / GiB} GiB VRAM; Vulkan backend; tier capped to {tier} by VRAM.");
        }

        // ── 6. Qualcomm Hexagon NPU on Android / Snapdragon X — OpenCL ────────
        // Hexagon is most reliable via OpenCL on Android; CoreML-equivalent
        // bindings on Windows-on-Snapdragon are still maturing.
        if (profile.Npu?.Vendor == NpuVendor.QualcommHexagon ||
            profile.Gpu?.Vendor == GpuVendor.Qualcomm)
        {
            var tier = ClampTier(requestedTier, ceiling: ModelTier.Tier1_Small);
            return new BackendSelection(
                BackendKind.OpenCL, tier,
                $"Qualcomm Snapdragon platform; OpenCL backend (Adreno/Hexagon shared compute); tier capped to {tier}.");
        }

        // ── 7. ARM Mali via Vulkan (MediaTek, Exynos, Tensor) ──────────────────
        if (profile.Gpu?.Vendor is GpuVendor.Arm or GpuVendor.Huawei)
        {
            var tier = ClampTier(requestedTier, ceiling: ModelTier.Tier1_Small);
            return new BackendSelection(
                BackendKind.Vulkan, tier,
                $"ARM/Mali class GPU ({profile.Gpu.Model}); Vulkan backend; tier capped to {tier}.");
        }

        // ── 8. CPU fallback — always selectable ────────────────────────────────
        var cpuTier = ClampTier(requestedTier, ceiling: TierForCpuRam(profile.TotalPhysicalMemoryBytes));
        return new BackendSelection(
            BackendKind.Cpu, cpuTier,
            $"No usable accelerator detected; CPU SIMD backend on {profile.CpuModel} " +
            $"({profile.LogicalCoreCount} logical cores, {profile.TotalPhysicalMemoryBytes / GiB} GiB RAM); " +
            $"tier capped to {cpuTier} by available RAM.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ModelTier ClampTier(ModelTier requested, ModelTier ceiling) =>
        requested <= ceiling ? requested : ceiling;

    private static ModelTier TierForVram(long vramBytes) =>
        vramBytes switch
        {
            >= 24L * GiB => ModelTier.Tier4_Frontier,
            >= 12L * GiB => ModelTier.Tier3_Large,
            >= 8L  * GiB => ModelTier.Tier2_Medium,
            >= 4L  * GiB => ModelTier.Tier1_Small,
            _            => ModelTier.Tier0_Tiny,
        };

    private static ModelTier TierForUnifiedMemory(long ramBytes) =>
        // Apple Silicon shares one pool — be more conservative because the OS,
        // app, and graphics surface all consume from the same RAM.
        ramBytes switch
        {
            >= 64L * GiB => ModelTier.Tier4_Frontier,
            >= 32L * GiB => ModelTier.Tier3_Large,
            >= 16L * GiB => ModelTier.Tier2_Medium,
            >= 8L  * GiB => ModelTier.Tier1_Small,
            _            => ModelTier.Tier0_Tiny,
        };

    private static ModelTier TierForCpuRam(long ramBytes) =>
        ramBytes switch
        {
            >= 64L * GiB => ModelTier.Tier3_Large,    // Server CPU with lots of RAM
            >= 32L * GiB => ModelTier.Tier2_Medium,
            >= 16L * GiB => ModelTier.Tier1_Small,
            >= 8L  * GiB => ModelTier.Tier1_Small,
            _            => ModelTier.Tier0_Tiny,
        };
}
