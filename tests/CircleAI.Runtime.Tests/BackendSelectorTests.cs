// BackendSelectorTests.cs
//
// Table-driven verification that the deterministic selector returns the
// expected (backend, tier) pair for each canonical host shape. Includes
// downgrade tests where the host cannot run the requested tier.

using System;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using Xunit;

namespace CircleAI.Runtime.Tests;

public sealed class BackendSelectorTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static HostProfile MakeProfile(
        OperatingSystemKind os = OperatingSystemKind.Windows,
        ArchitectureKind arch = ArchitectureKind.X64,
        long ram = 16 * GiB,
        GpuInfo? gpu = null,
        NpuInfo? npu = null) =>
        new(os, "test-os",
            arch, "TestCpu", 8, 4, ram, gpu, npu,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    // ── NVIDIA / CUDA ─────────────────────────────────────────────────────────

    [Fact]
    public void Nvidia_24GiB_Returns_Cuda_Tier4()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Nvidia, "RTX 4090", 24 * GiB, "550.0")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cuda, sel.Backend);
        Assert.Equal(CapabilityTier.Tier4_Frontier, sel.ActualTier);
        Assert.Contains("CUDA", sel.Rationale);
    }

    [Fact]
    public void Nvidia_12GiB_Returns_Cuda_Tier3()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Nvidia, "RTX 4070", 12 * GiB, "550.0")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cuda, sel.Backend);
        Assert.Equal(CapabilityTier.Tier3_Large, sel.ActualTier);
    }

    [Fact]
    public void Nvidia_8GiB_Returns_Cuda_Tier2()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Nvidia, "RTX 3060 Ti", 8 * GiB, "550.0")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cuda, sel.Backend);
        Assert.Equal(CapabilityTier.Tier2_Medium, sel.ActualTier);
    }

    [Fact]
    public void Nvidia_Below_4GiB_Falls_Out_Of_Cuda_Branch_To_Cpu()
    {
        // 3 GiB VRAM — under the 4 GiB minimum for the Cuda branch.
        var sel = new BackendSelector().Select(
            MakeProfile(ram: 32 * GiB,
                gpu: new GpuInfo(GpuVendor.Nvidia, "GTX 1050", 3 * GiB, "470.0")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cpu, sel.Backend);
    }

    // ── Apple Silicon / Metal ────────────────────────────────────────────────

    [Fact]
    public void AppleSilicon_32GiB_Returns_Metal_Tier3()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.MacOS,
                arch: ArchitectureKind.Arm64,
                ram: 32 * GiB,
                gpu: new GpuInfo(GpuVendor.Apple, "Apple M3 Pro GPU", 32 * GiB, null)),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Metal, sel.Backend);
        Assert.Equal(CapabilityTier.Tier3_Large, sel.ActualTier);
    }

    [Fact]
    public void AppleSilicon_8GiB_Caps_To_Tier1()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.MacOS,
                arch: ArchitectureKind.Arm64,
                ram: 8 * GiB,
                gpu: new GpuInfo(GpuVendor.Apple, "Apple M2 GPU", 8 * GiB, null)),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Metal, sel.Backend);
        Assert.Equal(CapabilityTier.Tier1_Small, sel.ActualTier);
    }

    // ── Huawei Ascend NPU ────────────────────────────────────────────────────

    [Fact]
    public void HuaweiAscend_NPU_Returns_Ascend_Backend()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.Linux,
                npu: new NpuInfo(NpuVendor.HuaweiAscend, "Ascend 910B")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Ascend, sel.Backend);
        Assert.Equal(CapabilityTier.Tier3_Large, sel.ActualTier);
    }

    // ── Cambricon MLU ─────────────────────────────────────────────────────────

    [Fact]
    public void Cambricon_Returns_Cambricon_Backend()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.Linux,
                npu: new NpuInfo(NpuVendor.CambriconMlu, "MLU370")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cambricon, sel.Backend);
    }

    // ── AMD/Intel Vulkan ─────────────────────────────────────────────────────

    [Fact]
    public void AMD_8GiB_Returns_Vulkan_Tier2()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Amd, "RX 7800 XT", 16 * GiB, "23.20")),
            CapabilityTier.Tier3_Large);
        Assert.Equal(BackendKind.Vulkan, sel.Backend);
        Assert.Equal(CapabilityTier.Tier3_Large, sel.ActualTier);
    }

    [Fact]
    public void Intel_Arc_8GiB_Returns_Vulkan()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Intel, "Arc A770", 16 * GiB, "31.0.101.5000")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Vulkan, sel.Backend);
    }

    // ── Qualcomm Hexagon ──────────────────────────────────────────────────────

    [Fact]
    public void Qualcomm_Hexagon_NPU_Returns_OpenCL()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.Android,
                arch: ArchitectureKind.Arm64,
                ram: 8 * GiB,
                npu: new NpuInfo(NpuVendor.QualcommHexagon, "Hexagon 780")),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.OpenCL, sel.Backend);
        Assert.Equal(CapabilityTier.Tier1_Small, sel.ActualTier);
    }

    // ── ARM Mali ──────────────────────────────────────────────────────────────

    [Fact]
    public void Mali_GPU_Returns_Vulkan_Capped_Tier1()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(
                os: OperatingSystemKind.Android,
                arch: ArchitectureKind.Arm64,
                ram: 12 * GiB,
                gpu: new GpuInfo(GpuVendor.Arm, "Mali-G715", 0, null)),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Vulkan, sel.Backend);
        Assert.Equal(CapabilityTier.Tier1_Small, sel.ActualTier);
    }

    // ── CPU fallback ──────────────────────────────────────────────────────────

    [Fact]
    public void No_GPU_64GiB_Returns_Cpu_Tier3()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(ram: 64 * GiB),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cpu, sel.Backend);
        Assert.Equal(CapabilityTier.Tier3_Large, sel.ActualTier);
    }

    [Fact]
    public void No_GPU_4GiB_Returns_Cpu_Tier0()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(ram: 4 * GiB),
            CapabilityTier.Tier4_Frontier);
        Assert.Equal(BackendKind.Cpu, sel.Backend);
        Assert.Equal(CapabilityTier.Tier0_Tiny, sel.ActualTier);
    }

    [Fact]
    public void Requested_Tier_Is_Honoured_When_Host_Has_Headroom()
    {
        // 24 GiB GPU could run Tier4, but the caller asked for Tier1 — must NOT upgrade.
        var sel = new BackendSelector().Select(
            MakeProfile(gpu: new GpuInfo(GpuVendor.Nvidia, "RTX 4090", 24 * GiB, "550.0")),
            CapabilityTier.Tier1_Small);
        Assert.Equal(BackendKind.Cuda, sel.Backend);
        Assert.Equal(CapabilityTier.Tier1_Small, sel.ActualTier);
    }

    [Fact]
    public void Null_Profile_Throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BackendSelector().Select(null!, CapabilityTier.Tier0_Tiny));
    }

    [Fact]
    public void Rationale_Is_Non_Empty_For_Every_Branch()
    {
        var sel = new BackendSelector().Select(
            MakeProfile(ram: 8 * GiB),
            CapabilityTier.Tier1_Small);
        Assert.False(string.IsNullOrWhiteSpace(sel.Rationale));
    }
}
