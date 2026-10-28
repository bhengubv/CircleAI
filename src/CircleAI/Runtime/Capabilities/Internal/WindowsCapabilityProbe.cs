// WindowsCapabilityProbe.cs
//
// Windows-specific probe. Strategy: prefer Environment + GlobalMemoryStatusEx
// (P/Invoke) for the values built into Win32, and shell out to wmic / pwsh
// for vendor identifiers. We deliberately do NOT take a dependency on
// System.Management (Microsoft.WindowsDesktop.App) so this package stays
// cross-platform compatible — the only Windows-specific surface is hidden
// behind P/Invoke that simply returns 0 on non-Windows OSes (the probe is
// instantiated only when ArchHelpers.ResolveOsKind() == Windows anyway).

using System.Runtime.InteropServices;

namespace CircleAI.Runtime.Capabilities.Internal;

// No [SupportedOSPlatform("windows")] attribute on purpose — CapabilityProbe
// gates instantiation by RuntimeInformation, so the attribute is redundant
// and would force CA1416 noise on every dispatch site.
internal sealed partial class WindowsCapabilityProbe : ICapabilityProbe
{
    public async Task<HostProfile> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var os    = OperatingSystemKind.Windows;
        var arch  = ArchHelpers.FromRuntime(RuntimeInformation.ProcessArchitecture);
        var osVer = Environment.OSVersion.Version.ToString();

        // ── RAM ────────────────────────────────────────────────────────────────
        long ramBytes = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms) != 0)
            ramBytes = (long)ms.ullTotalPhys;

        // ── CPU ────────────────────────────────────────────────────────────────
        var cpuModel   = await ReadCpuModelAsync(ct).ConfigureAwait(false);
        var logical    = Environment.ProcessorCount;
        var physical   = await ReadPhysicalCoreCountAsync(logical, ct).ConfigureAwait(false);

        // ── GPU ────────────────────────────────────────────────────────────────
        var gpu = await ReadGpuAsync(ct).ConfigureAwait(false);

        // ── NPU ────────────────────────────────────────────────────────────────
        // Intel Core Ultra and AMD Ryzen AI surface their NPU via WMI under
        // Win32_Processor.Caption; Qualcomm Snapdragon X surfaces under
        // Win32_PnPEntity. We sniff Win32_PnPEntity Name containing "NPU"
        // or "Neural" or "Hexagon" as a portable signal.
        var npu = await ReadNpuAsync(ct).ConfigureAwait(false);

        return new HostProfile(
            os, osVer, arch, cpuModel,
            logical, physical, ramBytes,
            gpu, npu,
            DateTimeOffset.UtcNow);
    }

    // ── RAM via Win32 P/Invoke ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // [LibraryImport], not [DllImport]. This assembly declares
    // DisableRuntimeMarshalling - MNN's parameter structs carry C++ bools the
    // runtime marshaller will not treat as blittable - and with that set a DllImport
    // cannot take `ref MEMORYSTATUSEX` at all: it fails at the call with "Cannot
    // marshal managed types when the runtime marshalling system is disabled", having
    // built perfectly. LibraryImport generates the stub at compile time instead,
    // which is the same mechanism Inference/LlamaInterop.cs uses and what that
    // AssemblyInfo was written for.
    //
    // Also gone, and nothing lost: SetLastError (forbidden alongside disabled
    // marshalling, and the error was never read), CharSet (no strings in this
    // signature) and [return: MarshalAs] (a Win32 BOOL is already a blittable
    // 4-byte int). MEMORYSTATUSEX is uint/ulong throughout.
    [LibraryImport("kernel32.dll")]
    private static partial int GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── CPU model via PowerShell CIM (no System.Management dep) ─────────────────

    private static async Task<string> ReadCpuModelAsync(CancellationToken ct)
    {
        // Pwsh-flavoured: prefers Get-CimInstance (modern) and falls back to
        // wmic. Either way we capture stdout-only and strip whitespace.
        var name = await HostExec.CaptureStdoutAsync(
            "powershell.exe",
            "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Select-Object -First 1).Name\"",
            timeoutMs: 3000, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(name))
        {
            // wmic is deprecated on Win11+ but still present on Server / older Win10.
            name = await HostExec.CaptureStdoutAsync(
                "wmic.exe", "cpu get Name /value",
                timeoutMs: 3000, ct).ConfigureAwait(false);
            // wmic emits "Name=<value>" — strip the prefix.
            var eq = name.IndexOf('=');
            if (eq >= 0 && eq + 1 < name.Length) name = name[(eq + 1)..];
        }

        return string.IsNullOrWhiteSpace(name) ? "Unknown CPU" : name.Trim();
    }

    private static async Task<int> ReadPhysicalCoreCountAsync(int logicalFallback, CancellationToken ct)
    {
        var s = await HostExec.CaptureStdoutAsync(
            "powershell.exe",
            "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Sum -Property NumberOfCores).Sum\"",
            timeoutMs: 3000, ct).ConfigureAwait(false);

        if (int.TryParse(s.Trim(), out var n) && n > 0)
            return n;

        return logicalFallback;
    }

    // ── GPU via PowerShell CIM ─────────────────────────────────────────────────

    private static async Task<GpuInfo?> ReadGpuAsync(CancellationToken ct)
    {
        // Returns "Name|AdapterRAM|DriverVersion" lines — we pick the first
        // entry whose AdapterRAM > 0 (skips Microsoft Basic Display Adapter).
        var s = await HostExec.CaptureStdoutAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-CimInstance Win32_VideoController | ForEach-Object { \\\"$($_.Name)|$($_.AdapterRAM)|$($_.DriverVersion)\\\" }\"",
            timeoutMs: 4000, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(s)) return null;

        // A VIRTUAL DISPLAY IS NOT A GPU, and this returned the FIRST adapter that was
        // not called "Basic". Measured on this box on 2026-10-27 the service reported its
        // GPU as "MS Idd Device" - a Microsoft Indirect Display Driver adapter, which is
        // what a virtual monitor, a remote-desktop session or a capture tool installs -
        // while an Iris Xe sat in the same list. A backend decision made from that reads
        // the wrong hardware, and a person reading the line is told the wrong thing about
        // their own machine.
        //
        // KNOWN VENDORS FIRST, then anything left. The real adapter is identifiable by
        // vendor; the pretenders are identifiable by name, and a name list alone would
        // need a new entry for every virtual driver anyone installs.
        var candidates = new List<GpuInfo>();
        foreach (var raw in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split('|');
            if (parts.Length < 3) continue;

            var name = parts[0].Trim();
            long vram = 0;
            long.TryParse(parts[1].Trim(), out vram);
            var driver = parts[2].Trim();

            // Skip Microsoft Basic Display Adapter (no real GPU acceleration).
            if (vram <= 0 && name.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsVirtualDisplay(name)) continue;

            candidates.Add(new GpuInfo(
                ClassifyVendor(name),
                name,
                vram,
                string.IsNullOrWhiteSpace(driver) ? null : driver));
        }

        // A named vendor beats Other, and more VRAM beats less - so a discrete card is
        // preferred over the integrated one it sits beside.
        return candidates
            .OrderByDescending(g => g.Vendor != GpuVendor.Other && g.Vendor != GpuVendor.None)
            .ThenByDescending(g => g.VramBytes)
            .FirstOrDefault();
    }

    /// <summary>Adapters that exist to carry pixels somewhere else, not to compute.</summary>
    private static bool IsVirtualDisplay(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("idd")                  // Microsoft Indirect Display Driver
            || n.Contains("indirect display")
            || n.Contains("virtual")
            || n.Contains("remote display")
            || n.Contains("meta ")                // Oculus/Meta virtual monitors
            || n.Contains("parsec")
            || n.Contains("spacedesk")
            || n.Contains("usb display")
            || n.Contains("displaylink");
    }

    private static GpuVendor ClassifyVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")) return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon")) return GpuVendor.Amd;
        if (n.Contains("intel") || n.Contains("arc ") || n.Contains("iris")) return GpuVendor.Intel;
        if (n.Contains("apple")) return GpuVendor.Apple;
        if (n.Contains("adreno") || n.Contains("qualcomm") || n.Contains("snapdragon")) return GpuVendor.Qualcomm;
        if (n.Contains("huawei") || n.Contains("kirin") || n.Contains("maleoon")) return GpuVendor.Huawei;
        if (n.Contains("mali") || n.Contains("arm")) return GpuVendor.Arm;
        return GpuVendor.Other;
    }

    // ── NPU via PowerShell Get-PnpDevice ───────────────────────────────────────

    // "NPU" AS A BARE SUBSTRING MATCHES THE WORD "INPUT". Measured on this box on
    // 2026-10-27, the service reported its NPU as "Microsoft Input Configuration
    // Device", because -match 'NPU' is case-insensitive and I-npu-t contains it. A
    // machine with no NPU therefore claimed one, and claimed an input device as it.
    //
    // \b ON EITHER SIDE for NPU, and the vendor words left alone: "Neural", "Hexagon"
    // and "Ascend" are not substrings of common device names, and anchoring them would
    // miss "Neural Processor" variants. Only the three-letter acronym needed the fence.
    private const string NpuNamePattern = @"\bNPU\b|Neural|Hexagon|Ascend";

    private static async Task<NpuInfo?> ReadNpuAsync(CancellationToken ct)
    {
        var s = await HostExec.CaptureStdoutAsync(
            "powershell.exe",
            "-NoProfile -Command \"Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match '" + NpuNamePattern + "' } | ForEach-Object { $_.FriendlyName }\"",
            timeoutMs: 3000, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(s)) return null;

        var first = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .FirstOrDefault()
                     ?.Trim();
        if (string.IsNullOrWhiteSpace(first)) return null;

        var lower = first.ToLowerInvariant();
        var vendor =
            lower.Contains("hexagon")   ? NpuVendor.QualcommHexagon :
            lower.Contains("ascend")    ? NpuVendor.HuaweiAscend    :
            lower.Contains("intel")     ? NpuVendor.IntelVpu        :
            lower.Contains("apple")     ? NpuVendor.AppleNeuralEngine :
            NpuVendor.Other;

        return new NpuInfo(vendor, first);
    }
}
