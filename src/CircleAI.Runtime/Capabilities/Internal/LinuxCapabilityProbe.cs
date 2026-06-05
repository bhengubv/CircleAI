// LinuxCapabilityProbe.cs
//
// Linux probe. Reads /proc/cpuinfo + /proc/meminfo for CPU/RAM and falls
// back to lspci / nvidia-smi for GPU. Pure file IO + shell exec — no
// platform-restricted .NET package.

using System.Runtime.InteropServices;

namespace CircleAI.Runtime.Capabilities.Internal;

// No [SupportedOSPlatform("linux")] attribute on purpose — CapabilityProbe
// gates instantiation by RuntimeInformation, so the attribute is redundant
// and would force CA1416 noise on every dispatch site (including the
// Android probe which composes with Linux's /proc readers).
internal sealed class LinuxCapabilityProbe : ICapabilityProbe
{
    public async Task<HostProfile> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var os    = OperatingSystemKind.Linux;
        var arch  = ArchHelpers.FromRuntime(RuntimeInformation.ProcessArchitecture);
        var osVer = await ReadOsVersionAsync(ct).ConfigureAwait(false);

        var ramBytes = ReadTotalMemoryBytes();
        var cpuModel = ReadCpuModel();
        var logical  = Environment.ProcessorCount;
        var physical = ReadPhysicalCoreCount(logical);

        var gpu = await ReadGpuAsync(ct).ConfigureAwait(false);
        var npu = await ReadNpuAsync(ct).ConfigureAwait(false);

        return new HostProfile(
            os, osVer, arch, cpuModel,
            logical, physical, ramBytes,
            gpu, npu,
            DateTimeOffset.UtcNow);
    }

    // ── OS release ─────────────────────────────────────────────────────────────

    private static async Task<string> ReadOsVersionAsync(CancellationToken ct)
    {
        try
        {
            const string path = "/etc/os-release";
            if (!File.Exists(path)) return Environment.OSVersion.Version.ToString();

            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            var pretty = lines.FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            if (pretty is not null)
                return pretty[("PRETTY_NAME=".Length)..].Trim('"', ' ');
        }
        catch { /* best effort */ }
        return Environment.OSVersion.Version.ToString();
    }

    // ── RAM via /proc/meminfo ──────────────────────────────────────────────────

    private static long ReadTotalMemoryBytes()
    {
        try
        {
            const string path = "/proc/meminfo";
            if (!File.Exists(path)) return 0;

            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                // "MemTotal:       16384000 kB"
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                    return kb * 1024L;
            }
        }
        catch { /* best effort */ }
        return 0;
    }

    // ── CPU model + core count via /proc/cpuinfo ───────────────────────────────

    private static string ReadCpuModel()
    {
        try
        {
            const string path = "/proc/cpuinfo";
            if (!File.Exists(path)) return "Unknown CPU";

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && i + 1 < line.Length) return line[(i + 1)..].Trim();
                }
                // ARM systems frequently use "Hardware" or "Processor" instead.
                if (line.StartsWith("Hardware", StringComparison.Ordinal)
                 || line.StartsWith("Processor", StringComparison.Ordinal))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && i + 1 < line.Length) return line[(i + 1)..].Trim();
                }
            }
        }
        catch { /* best effort */ }
        return "Unknown CPU";
    }

    private static int ReadPhysicalCoreCount(int logicalFallback)
    {
        try
        {
            const string path = "/proc/cpuinfo";
            if (!File.Exists(path)) return logicalFallback;

            var coreIds = new HashSet<string>();
            string? currentPhysicalId = null;

            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("physical id", StringComparison.Ordinal))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && i + 1 < line.Length) currentPhysicalId = line[(i + 1)..].Trim();
                }
                else if (line.StartsWith("core id", StringComparison.Ordinal))
                {
                    var i = line.IndexOf(':');
                    if (i > 0 && i + 1 < line.Length)
                    {
                        var coreId = line[(i + 1)..].Trim();
                        coreIds.Add($"{currentPhysicalId}:{coreId}");
                    }
                }
            }

            return coreIds.Count > 0 ? coreIds.Count : logicalFallback;
        }
        catch { return logicalFallback; }
    }

    // ── GPU via nvidia-smi (preferred) then lspci ──────────────────────────────

    private static async Task<GpuInfo?> ReadGpuAsync(CancellationToken ct)
    {
        // Strongly prefer nvidia-smi when present — it gives us VRAM and driver.
        var nvidia = await HostExec.CaptureStdoutAsync(
            "nvidia-smi",
            "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
            timeoutMs: 3000, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(nvidia))
        {
            var first = nvidia.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault()
                              ?.Trim();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var parts = first.Split(',');
                if (parts.Length >= 3)
                {
                    var name   = parts[0].Trim();
                    long.TryParse(parts[1].Trim(), out var vramMib);
                    var driver = parts[2].Trim();
                    return new GpuInfo(GpuVendor.Nvidia, name, vramMib * 1024L * 1024L, driver);
                }
            }
        }

        // Fall back to lspci — covers AMD / Intel / Mali / Adreno on Linux.
        var lspci = await HostExec.CaptureStdoutAsync(
            "lspci",
            "-mm | grep -i 'VGA\\|3D\\|Display'",
            timeoutMs: 3000, ct).ConfigureAwait(false);

        // lspci -mm output: 'NN:NN.N "VGA compatible controller" "NVIDIA Corp." "GA104 [GeForce RTX 3070]" ...'
        // We don't get VRAM out of lspci — leave it 0.
        if (!string.IsNullOrWhiteSpace(lspci))
        {
            var first = lspci.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                // Find quoted vendor and device strings.
                var pieces = SplitQuoted(first);
                if (pieces.Count >= 4)
                {
                    var vendor = pieces[2];
                    var device = pieces[3];
                    var fullName = $"{vendor} {device}".Trim();
                    return new GpuInfo(ClassifyVendor(fullName), fullName, 0, null);
                }
            }
        }
        return null;
    }

    private static List<string> SplitQuoted(string line)
    {
        var result = new List<string>();
        var start  = 0;
        var inQ    = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (inQ) { result.Add(line.Substring(start, i - start)); inQ = false; }
                else     { start = i + 1; inQ = true; }
            }
        }
        return result;
    }

    private static GpuVendor ClassifyVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")) return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("advanced micro devices")) return GpuVendor.Amd;
        if (n.Contains("intel")) return GpuVendor.Intel;
        if (n.Contains("apple")) return GpuVendor.Apple;
        if (n.Contains("adreno") || n.Contains("qualcomm")) return GpuVendor.Qualcomm;
        if (n.Contains("huawei") || n.Contains("kirin") || n.Contains("hisilicon")) return GpuVendor.Huawei;
        if (n.Contains("mali") || n.Contains("arm holdings") || n.Contains("arm ltd")) return GpuVendor.Arm;
        return GpuVendor.Other;
    }

    // ── NPU via /sys/class/devfreq or known device nodes ───────────────────────

    private static Task<NpuInfo?> ReadNpuAsync(CancellationToken ct)
    {
        // Huawei Ascend devices register as /dev/davinci*. Intel VPU as /dev/accel/accel0.
        // Cambricon as /dev/cambricon_dev*. These are reliable presence signals.
        if (Directory.Exists("/dev"))
        {
            try
            {
                var dev = Directory.EnumerateFileSystemEntries("/dev").ToList();
                if (dev.Any(p => p.Contains("davinci", StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult<NpuInfo?>(new NpuInfo(NpuVendor.HuaweiAscend, "Huawei Ascend"));
                if (dev.Any(p => p.Contains("cambricon", StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult<NpuInfo?>(new NpuInfo(NpuVendor.CambriconMlu, "Cambricon MLU"));
                if (Directory.Exists("/dev/accel"))
                    return Task.FromResult<NpuInfo?>(new NpuInfo(NpuVendor.IntelVpu, "Intel VPU"));
            }
            catch { /* permissions, best effort */ }
        }
        return Task.FromResult<NpuInfo?>(null);
    }
}
