// AndroidCapabilityProbe.cs
//
// Android probe. We rely on the same /proc surfaces Linux exposes (Bionic
// keeps these portable) plus a few Android-specific signals: ro.* system
// properties via the `getprop` binary for SoC marketing name.

using System.Runtime.InteropServices;

namespace CircleAI.Runtime.Capabilities.Internal;

// No [SupportedOSPlatform("android")] attribute on purpose — CapabilityProbe
// gates instantiation by RuntimeInformation, so the attribute is redundant
// and would force CA1416 noise on every dispatch site.
internal sealed class AndroidCapabilityProbe : ICapabilityProbe
{
    public async Task<HostProfile> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Reuse the LinuxCapabilityProbe for /proc-based values and overlay
        // Android-specific surface where it differs from generic Linux.
        var linuxProbe = new LinuxCapabilityProbe();
        var baseProfile = await linuxProbe.ProbeAsync(ct).ConfigureAwait(false);

        // Try to surface the SoC marketing name (Snapdragon, Kirin, Tensor, MediaTek).
        var soc = await HostExec.CaptureStdoutAsync(
            "getprop", "ro.soc.model",
            timeoutMs: 1500, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(soc))
            soc = await HostExec.CaptureStdoutAsync(
                "getprop", "ro.board.platform",
                timeoutMs: 1500, ct).ConfigureAwait(false);

        var cpuModel = string.IsNullOrWhiteSpace(soc) ? baseProfile.CpuModel : soc.Trim();

        // Android version
        var osVer = await HostExec.CaptureStdoutAsync(
            "getprop", "ro.build.version.release",
            timeoutMs: 1500, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(osVer)) osVer = baseProfile.OsVersion;

        // Infer GPU + NPU vendor from SoC name when /proc didn't yield one.
        var gpu = baseProfile.Gpu ?? InferGpuFromSoc(cpuModel);
        var npu = baseProfile.Npu ?? InferNpuFromSoc(cpuModel);

        return baseProfile with
        {
            Os       = OperatingSystemKind.Android,
            OsVersion = osVer.Trim(),
            CpuModel = cpuModel,
            Gpu      = gpu,
            Npu      = npu,
        };
    }

    private static GpuInfo? InferGpuFromSoc(string soc)
    {
        var s = soc.ToLowerInvariant();
        if (s.Contains("snapdragon") || s.Contains("qcom") || s.Contains("sm8"))
            return new GpuInfo(GpuVendor.Qualcomm, "Adreno (Snapdragon)", 0, null);
        if (s.Contains("kirin") || s.Contains("hisi"))
            return new GpuInfo(GpuVendor.Huawei, "Maleoon/Mali (Kirin)", 0, null);
        if (s.Contains("tensor"))
            return new GpuInfo(GpuVendor.Arm, "Mali (Google Tensor)", 0, null);
        if (s.Contains("mt") || s.Contains("mediatek") || s.Contains("dimensity"))
            return new GpuInfo(GpuVendor.Arm, "Mali (MediaTek)", 0, null);
        if (s.Contains("exynos"))
            return new GpuInfo(GpuVendor.Arm, "Mali (Exynos)", 0, null);
        return null;
    }

    private static NpuInfo? InferNpuFromSoc(string soc)
    {
        var s = soc.ToLowerInvariant();
        if (s.Contains("snapdragon") || s.Contains("qcom"))
            return new NpuInfo(NpuVendor.QualcommHexagon, "Qualcomm Hexagon NPU");
        if (s.Contains("kirin") || s.Contains("hisi"))
            return new NpuInfo(NpuVendor.HuaweiAscend, "Huawei NPU (Kirin)");
        return null;
    }
}
