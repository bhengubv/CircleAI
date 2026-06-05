// MacOSCapabilityProbe.cs
//
// macOS probe. Uses sysctl for CPU + RAM + arch and system_profiler for GPU.
// Apple Silicon (M-series) always reports an Apple GPU and the Apple Neural
// Engine NPU. Intel macs report Intel/AMD GPU and no NPU.

using System.Runtime.InteropServices;

namespace CircleAI.Runtime.Capabilities.Internal;

// No [SupportedOSPlatform("macos")] attribute on purpose — CapabilityProbe
// gates instantiation by RuntimeInformation, so the attribute is redundant
// and would force CA1416 noise on every dispatch site.
internal sealed class MacOSCapabilityProbe : ICapabilityProbe
{
    public async Task<HostProfile> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var os    = OperatingSystemKind.MacOS;
        var arch  = ArchHelpers.FromRuntime(RuntimeInformation.ProcessArchitecture);
        var osVer = await ReadSysctlAsync("kern.osproductversion", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(osVer))
            osVer = Environment.OSVersion.Version.ToString();

        // ── CPU ────────────────────────────────────────────────────────────────
        var cpuModel = await ReadSysctlAsync("machdep.cpu.brand_string", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cpuModel)) cpuModel = "Apple Silicon";

        var logical = await ReadSysctlIntAsync("hw.logicalcpu", Environment.ProcessorCount, ct)
                            .ConfigureAwait(false);
        var physical = await ReadSysctlIntAsync("hw.physicalcpu", logical, ct).ConfigureAwait(false);

        // ── RAM ────────────────────────────────────────────────────────────────
        var ramBytes = await ReadSysctlLongAsync("hw.memsize", 0L, ct).ConfigureAwait(false);

        // ── GPU + NPU (Apple Silicon implies both) ─────────────────────────────
        GpuInfo? gpu;
        NpuInfo? npu;
        if (arch == ArchitectureKind.Arm64)
        {
            // Apple Silicon: integrated GPU, unified memory equals the system RAM
            // surface so we report the full RAM as "VRAM" available to the GPU.
            // This is what Metal sees.
            gpu = new GpuInfo(GpuVendor.Apple, ExtractAppleGpuName(cpuModel), ramBytes, null);
            npu = new NpuInfo(NpuVendor.AppleNeuralEngine, "Apple Neural Engine");
        }
        else
        {
            // Intel mac: parse system_profiler for the discrete GPU.
            gpu = await ReadGpuViaSystemProfilerAsync(ct).ConfigureAwait(false);
            npu = null;
        }

        return new HostProfile(
            os, osVer.Trim(), arch, cpuModel,
            logical, physical, ramBytes,
            gpu, npu,
            DateTimeOffset.UtcNow);
    }

    // ── sysctl helpers ─────────────────────────────────────────────────────────

    private static Task<string> ReadSysctlAsync(string key, CancellationToken ct) =>
        HostExec.CaptureStdoutAsync("sysctl", $"-n {key}", timeoutMs: 2000, ct);

    private static async Task<int> ReadSysctlIntAsync(string key, int fallback, CancellationToken ct)
    {
        var s = await ReadSysctlAsync(key, ct).ConfigureAwait(false);
        return int.TryParse(s.Trim(), out var n) && n > 0 ? n : fallback;
    }

    private static async Task<long> ReadSysctlLongAsync(string key, long fallback, CancellationToken ct)
    {
        var s = await ReadSysctlAsync(key, ct).ConfigureAwait(false);
        return long.TryParse(s.Trim(), out var n) && n > 0 ? n : fallback;
    }

    private static async Task<GpuInfo?> ReadGpuViaSystemProfilerAsync(CancellationToken ct)
    {
        var s = await HostExec.CaptureStdoutAsync(
            "system_profiler", "SPDisplaysDataType",
            timeoutMs: 4000, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(s)) return null;

        string? chipsetModel = null;
        long vram = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Chipset Model:", StringComparison.Ordinal))
                chipsetModel = line["Chipset Model:".Length..].Trim();
            else if (line.StartsWith("VRAM (Total):", StringComparison.Ordinal)
                  || line.StartsWith("VRAM (Dynamic, Max):", StringComparison.Ordinal))
            {
                // "VRAM (Total): 8 GB"
                var idx = line.IndexOf(':');
                if (idx > 0 && idx + 1 < line.Length)
                {
                    var val = line[(idx + 1)..].Trim();
                    var parts = val.Split(' ');
                    if (parts.Length >= 2 && double.TryParse(parts[0], out var n))
                    {
                        vram = parts[1].ToUpperInvariant() switch
                        {
                            "GB" => (long)(n * 1024 * 1024 * 1024),
                            "MB" => (long)(n * 1024 * 1024),
                            _ => 0L
                        };
                    }
                }
            }
        }

        if (chipsetModel is null) return null;
        return new GpuInfo(ClassifyVendor(chipsetModel), chipsetModel, vram, null);
    }

    private static string ExtractAppleGpuName(string cpuBrand)
    {
        // CPU brand on Apple Silicon is e.g. "Apple M2 Pro"; the integrated GPU
        // is conventionally named after the chip ("Apple M2 Pro GPU").
        return string.IsNullOrWhiteSpace(cpuBrand) ? "Apple GPU" : $"{cpuBrand.Trim()} GPU";
    }

    private static GpuVendor ClassifyVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("apple")) return GpuVendor.Apple;
        if (n.Contains("amd") || n.Contains("radeon")) return GpuVendor.Amd;
        if (n.Contains("intel")) return GpuVendor.Intel;
        if (n.Contains("nvidia") || n.Contains("geforce")) return GpuVendor.Nvidia;
        return GpuVendor.Other;
    }
}
