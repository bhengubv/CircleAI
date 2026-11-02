// DeviceContext.cs
//
// STEP 1 OF THE FRAMEWORK: READ THE ROOM BEFORE CHOOSING A BRAIN.
//
// DeviceProbe answers how much memory and storage there is, which is what fit needs.
// Choosing and shaping a brain needs more: how many cores are fast, which vector
// instructions the runtime really has, whether the device is hot, low or charging, and
// what kind of device it is. This grows out of the probe rather than beside it - Read
// takes the probe's snapshot and adds to it - so there is still one place that measures
// the device.
//
// THE PLATFORM SAYS WHAT CORE CANNOT SEE. Battery, heat and form factor are hooks a head
// installs at start-up, the same arrangement DeviceProbe uses for memory: Core cannot
// read a phone's battery, and a guess presented as a reading is worse than "unknown".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CircleAI.Core;

/// <summary>What kind of device this is.</summary>
public enum FormFactor
{
    /// <summary>Nothing said and nothing to infer it from.</summary>
    Unknown = 0,

    /// <summary>A watch or band.</summary>
    Watch,

    /// <summary>A phone.</summary>
    Phone,

    /// <summary>A tablet.</summary>
    Tablet,

    /// <summary>A laptop: a computer on a battery.</summary>
    Laptop,

    /// <summary>A desktop computer.</summary>
    Desktop,

    /// <summary>A machine with nobody in front of it.</summary>
    Server,
}

/// <summary>The vector instructions this runtime actually exposes - not what the chip has.</summary>
/// <remarks>
/// THE RUNTIME DECIDES, NOT THE CHIP. On the P30 the shipped runtime is Mono, which exposes
/// no ARM vector intrinsics at all (S0, 2026-10-30), so a Kirin 710 that has NEON reports
/// none of it here - and that is the true answer for every kernel this process can run.
/// </remarks>
public sealed record CpuFeatures(bool AdvSimd, bool ArmDotProduct, bool Avx2, bool AvxVnni, bool Avx512F)
{
    /// <summary>What this process's runtime reports.</summary>
    public static CpuFeatures Read() => new(
        System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported,
        System.Runtime.Intrinsics.Arm.Dp.IsSupported,
        System.Runtime.Intrinsics.X86.Avx2.IsSupported,
        System.Runtime.Intrinsics.X86.AvxVnni.IsSupported,
        System.Runtime.Intrinsics.X86.Avx512F.IsSupported);

    /// <inheritdoc />
    public override string ToString()
    {
        var names = new List<string>();
        if (AdvSimd) names.Add("AdvSimd");
        if (ArmDotProduct) names.Add("Dp");
        if (Avx2) names.Add("Avx2");
        if (AvxVnni) names.Add("AvxVnni");
        if (Avx512F) names.Add("Avx512F");
        return names.Count == 0 ? "scalar only" : string.Join(", ", names);
    }
}

/// <summary>The device's power and heat, as the platform reports them.</summary>
/// <param name="BatteryPercent">0 to 100, or null when the platform does not say.</param>
/// <param name="Charging">Whether it is plugged in, or null when unknown.</param>
/// <param name="Hot">Whether the platform says it is too hot to work hard.</param>
public sealed record PowerState(int? BatteryPercent, bool? Charging, bool Hot)
{
    /// <summary>Nothing known: no battery figure, no charging state, not hot.</summary>
    public static PowerState Unknown { get; } = new(null, null, false);

    /// <summary>Low enough that a brain should do less: under 20% and not charging.</summary>
    public bool Low => BatteryPercent is < 20 && Charging != true;
}

/// <summary>Everything Step 1 reads about this device, the memory probe inside it.</summary>
/// <param name="Probe">Memory, storage, connectivity and the device tier.</param>
/// <param name="Cpu">The vector instructions this runtime exposes.</param>
/// <param name="PerformanceCores">The fast cores.</param>
/// <param name="EfficiencyCores">The slow cores, on a big/little chip; 0 elsewhere.</param>
/// <param name="Power">Battery, charging and heat.</param>
/// <param name="FormFactor">What kind of device it is.</param>
public sealed record DeviceContext(
    DeviceProbe Probe,
    CpuFeatures Cpu,
    int PerformanceCores,
    int EfficiencyCores,
    PowerState Power,
    FormFactor FormFactor)
{
    /// <summary>Battery and heat, installed by a platform head. Null reads as unknown.</summary>
    public static Func<PowerState>? PowerProbe { get; set; }

    /// <summary>The form factor, installed by a platform head. Null infers it from the device tier.</summary>
    public static Func<FormFactor>? FormFactorProbe { get; set; }

    /// <summary>Where the kernel lists CPUs. A setting so a test can point it at a fake tree.</summary>
    internal static string CpuRoot { get; set; } = "/sys/devices/system/cpu";

    /// <summary>
    /// A core counts as fast when its top frequency is at least this share of the fastest
    /// core's.
    /// </summary>
    /// <remarks>
    /// 0.8 BECAUSE OF THE TWO PHONES ON THE BENCH. The P30's Kirin 710 is 4 x A73 at 2.2 GHz
    /// and 4 x A53 at 1.71 GHz (0.78), and its little cores made the C# kernels slower, not
    /// faster - three threads beat four and eight (2026-10-30). A Pixel 7a's Tensor G2 has
    /// 2 x X1 at 2.85 GHz and 2 x A78 at 2.35 GHz (0.82): those are both fast, and its four
    /// A55 at 1.8 GHz (0.63) are not.
    /// </remarks>
    internal const double FastCoreShare = 0.8;

    /// <summary>Reads this device now.</summary>
    /// <param name="probe">An existing snapshot, or null to take one.</param>
    public static DeviceContext Read(DeviceProbe? probe = null)
    {
        probe ??= DeviceProbe.Snapshot();
        var (fast, slow) = Cores(CpuRoot, Environment.ProcessorCount);

        PowerState power;
        try { power = PowerProbe?.Invoke() ?? PowerState.Unknown; }
        catch { power = PowerState.Unknown; }

        FormFactor form;
        try { form = FormFactorProbe?.Invoke() ?? FormFactor.Unknown; }
        catch { form = FormFactor.Unknown; }
        if (form == FormFactor.Unknown) form = FromTier(probe.Classify());

        return new DeviceContext(probe, CpuFeatures.Read(), fast, slow, power, form);
    }

    /// <summary>How many threads a brain should run on here.</summary>
    /// <remarks>
    /// THE FAST CORES, ONE LEFT FOR THE PERSON. A brain spread over the little cores waits
    /// on the slowest of them, and one that takes every fast core leaves the screen and the
    /// microphone fighting it. On the P30 that is three - the count that measured fastest.
    /// </remarks>
    public int BrainThreads => PerformanceCores > 1 ? PerformanceCores - 1 : 1;

    /// <summary>One line for a log: what this device is, as a brain plan sees it.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{FormFactor}, {Probe.Classify()} tier, {PerformanceCores} fast + {EfficiencyCores} slow cores, {Cpu}, " +
        $"{Probe.RamTotalBytes / DeviceProbe.BytesPerGb:F1} GB memory ({Probe.UsableRamGb:F2} GB usable now), " +
        $"battery {(Power.BatteryPercent is { } b ? b + "%" : "unknown")}" +
        $"{(Power.Charging == true ? " charging" : string.Empty)}{(Power.Hot ? ", hot" : string.Empty)}");

    /// <summary>The fast and slow cores, from each core's top frequency.</summary>
    /// <remarks>
    /// The kernel's cpufreq tree where there is one (Android, Linux). Anywhere it cannot be
    /// read - Windows, a sandbox that hides it - every core counts as fast, which is true of
    /// the machines that have no such tree.
    /// </remarks>
    internal static (int Fast, int Slow) Cores(string root, int logicalCores)
    {
        var logical = Math.Max(1, logicalCores);
        try
        {
            if (!Directory.Exists(root)) return (logical, 0);

            var tops = new List<long>();
            foreach (var dir in Directory.EnumerateDirectories(root, "cpu*"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length <= 3 || !name[3..].All(char.IsDigit)) continue;
                var file = Path.Combine(dir, "cpufreq", "cpuinfo_max_freq");
                if (!File.Exists(file)) continue;
                if (long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var khz) && khz > 0)
                    tops.Add(khz);
            }

            if (tops.Count == 0) return (logical, 0);
            var fastest = tops.Max();
            var fast = tops.Count(khz => khz >= fastest * FastCoreShare);
            return (fast, tops.Count - fast);
        }
        catch
        {
            return (logical, 0);
        }
    }

    private static FormFactor FromTier(DeviceTier tier) => tier switch
    {
        DeviceTier.Wearable    => FormFactor.Watch,
        DeviceTier.Phone       => FormFactor.Phone,
        DeviceTier.Tablet      => FormFactor.Tablet,
        DeviceTier.Desktop     => FormFactor.Desktop,
        DeviceTier.Workstation => FormFactor.Desktop,
        _                      => FormFactor.Unknown,
    };
}
