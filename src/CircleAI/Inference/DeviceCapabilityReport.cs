// DeviceCapabilityReport.cs
//
// "Best we can do with your device is this."
//
// The honest answer to a person who has just installed the app on whatever phone
// they own, computed rather than promised. Every modality is asked the same
// question the selector already answers -- ISpeechModelSelector.PlanFor returns a
// ModalityPlan whose Reason is documented as safe to show a user -- and the answers
// are gathered into one statement instead of each screen discovering its own gap
// at the moment somebody taps it.
//
// WHY THIS IS THE PRODUCT'S JOB AND NOT THE APP'S. The thin client renders; it does
// not decide. A screen that works out for itself whether vision is available is a
// second owner of that fact, and this repo has the scar: the abilities screen once
// offered a 311 MB vision download to a screen that did not exist. One computation,
// one answer, every head reads it.
//
// RAM IS PER MODEL, NEVER A SUM. Models are tagged in and out of memory as needed --
// CircleNeuronService keeps the generalist warm and hot-swaps the specialist -- so a
// device does NOT need to hold every modality at once. Each plan is judged against
// usable RAM on its own, and only DISK has to hold the whole set. On the reference
// P30 that is 3.59 GB against an 11.40 GB budget, so a phone that can serve one
// modality at a time can serve all of them, one at a time.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>What one capability can do on this device, in words a person can read.</summary>
/// <param name="Modality">The capability asked about.</param>
/// <param name="Plan">The selector's decision, including its own reason string.</param>
public sealed record CapabilityLine(ModelModality Modality, ModalityPlan Plan)
{
    /// <summary>The capability can be served at all — by a model or a built-in.</summary>
    public bool Available => Plan.IsAvailable;

    /// <summary>Served without loading anything (the procedural built-ins).</summary>
    public bool NeedsNoModel => Plan.UsesBuiltIn;

    /// <summary>The model this device would use, or <c>null</c>.</summary>
    public string? ModelId => Plan.Model?.ModelId;

    /// <summary>Download size for that model, 0 when nothing is needed.</summary>
    public long Bytes => Plan.Model?.EstimatedBytes ?? 0;
}

/// <summary>
/// Everything this device can and cannot do, computed once and shown to the person.
/// </summary>
/// <param name="Tier">The device class the probe reported.</param>
/// <param name="MeetsFloor">Whether the device clears the supported floor.</param>
/// <param name="Lines">One line per capability asked about.</param>
/// <param name="TotalBytes">Disk the offered set would occupy in total.</param>
/// <param name="BudgetBytes">Disk the models may claim between them.</param>
public sealed record DeviceCapabilityReport(
    DeviceTier Tier,
    bool MeetsFloor,
    IReadOnlyList<CapabilityLine> Lines,
    long TotalBytes,
    double BudgetBytes)
{
    /// <summary>Capabilities this device can serve.</summary>
    public IReadOnlyList<CapabilityLine> Available =>
        Lines.Where(l => l.Available).ToList();

    /// <summary>Capabilities it cannot, which is the half worth saying out loud.</summary>
    public IReadOnlyList<CapabilityLine> Unavailable =>
        Lines.Where(l => !l.Available).ToList();

    /// <summary>Whether the offered set fits the disk budget.</summary>
    public bool FitsBudget => TotalBytes <= BudgetBytes;
}

/// <summary>
/// The floor this product supports, and the report that tells a person where they
/// stand against it.
/// </summary>
public static class DeviceCapability
{
    /// <summary>
    /// The reference device: <b>Huawei P30 Lite</b> — 3.6 GB RAM, 116 GB storage.
    /// </summary>
    /// <remarks>
    /// NOT AN ARBITRARY FLOOR. A P30 Lite is a seven-year-old handset, and that is
    /// precisely why it is the floor: it is what a great many people actually hold,
    /// so supporting down to it is a decision about who the product is for rather
    /// than a convenience. Measured, not taken off a spec sheet — 3.6 GB
    /// (3,776,516 kB), 1.19 GB available, 115.9 GB storage with 29.8 GB free, Kirin
    /// 710, no GPU, EMUI, no GMS. RAM and availability read off the device on
    /// 2026-09-28; see <c>docs/HARDWARE_FINDINGS_HUAWEI_P30.md</c>, whose "38 GB" was
    /// FREE SPACE on the day it was written rather than the size of the device — and
    /// free space is precisely what a budget must not be computed from.
    /// <para>
    /// Below it, the honest answer is to say so. Above it, every capability the
    /// device can serve must be served — the quantisation moves, the capability list
    /// does not.
    /// </para>
    /// </remarks>
    public const double FloorRamTotalGb = 3.5;

    /// <summary>Storage floor, from the same measured device.</summary>
    public const double FloorStorageTotalGb = 32.0;

    /// <summary>The capabilities a device is asked about, in the order a person reads them.</summary>
    public static readonly IReadOnlyList<ModelModality> Asked = new[]
    {
        ModelModality.Chat,
        ModelModality.Vision,
        ModelModality.Asr,
        ModelModality.Tts,
        ModelModality.WakeWord,
        ModelModality.ImageGeneration,
    };

    /// <summary>Whether this device clears the supported floor.</summary>
    /// <remarks>
    /// Judged on TOTAL RAM and TOTAL storage, never on what happens to be free: a
    /// phone is not unsupported because its gallery is full this morning. A device
    /// that reports neither figure is given the benefit of the doubt rather than
    /// turned away on missing data — a desktop build reports 0 for both.
    /// </remarks>
    public static bool MeetsFloor(DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var ramGb = probe.RamTotalBytes > 0 ? probe.RamTotalBytes / DeviceProbe.BytesPerGb : 0.0;
        var diskGb = probe.StorageTotalGb;
        if (ramGb <= 0 && diskGb <= 0) return true;                 // unknown: do not refuse
        if (ramGb > 0 && ramGb < FloorRamTotalGb - 0.2) return false; // 0.2 slack: 3.6 reports as 3.5x
        if (diskGb > 0 && diskGb < FloorStorageTotalGb) return false;
        return true;
    }

    /// <summary>Build the report for this device.</summary>
    /// <remarks>
    /// TWO SELECTORS, because the product genuinely has two and pretending otherwise
    /// would put a second owner on the question. Chat and vision go through
    /// <see cref="IModelSelector.BestFit"/> — the speech selector refuses them
    /// outright — and everything else through <see cref="ISpeechModelSelector"/>.
    /// Vision rides the chat selector as a <see cref="ChatCapability"/> flag because
    /// a VLM may be catalogued either as Vision or as a Chat model that declares the
    /// capability, and asking the speech selector would miss the second kind.
    /// </remarks>
    public static DeviceCapabilityReport For(IModelSelector chat, ISpeechModelSelector speech,
                                             DeviceProbe probe,
                                             IReadOnlyList<ModelModality>? asked = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(probe);

        var lines = (asked ?? Asked).Select(m => new CapabilityLine(m, PlanFor(chat, speech, probe, m)))
                                    .ToList();

        // Disk holds the whole set; RAM never has to, because models are tagged in
        // and out as needed. Summing bytes is therefore the right total, and summing
        // RAM would not be.
        var total = lines.Sum(l => l.Bytes);

        return new DeviceCapabilityReport(
            probe.Classify(), MeetsFloor(probe), lines, total,
            DeviceModelAssessor.BudgetBytesFor(probe));
    }

    /// <summary>Ask the selector that owns this modality, and never throw.</summary>
    /// <remarks>
    /// <see cref="IModelSelector.BestFit"/> THROWS when nothing satisfies the request
    /// — it is written for a caller that has already decided to load something. This
    /// one is written for a person reading a list, where "cannot" is an answer rather
    /// than an error, so the throw becomes an Unavailable line carrying its message.
    /// A report that fell over on the first unsupported capability would be useless
    /// on exactly the cheap devices it exists to describe.
    /// </remarks>
    private static ModalityPlan PlanFor(IModelSelector chat, ISpeechModelSelector speech,
                                        DeviceProbe probe, ModelModality modality)
    {
        if (modality is not (ModelModality.Chat or ModelModality.Vision))
            return speech.PlanFor(probe, modality);

        var required = modality == ModelModality.Vision
            ? ChatCapability.Vision
            : ChatCapability.Default;
        try
        {
            var pick = chat.BestFit(probe, required);
            return new ModalityPlan(pick.Quality, pick, $"{pick.ModelId} ({pick.Quality})");
        }
        catch (Exception ex)
        {
            return new ModalityPlan(SelectionQuality.Unavailable, null, ex.Message);
        }
    }

    /// <summary>
    /// The disclaimer, as a person reads it.
    /// </summary>
    /// <remarks>
    /// Says what the device CAN do first and what it cannot second, because a list
    /// of absences is not a product. Never apologises and never hedges: a capability
    /// is either offered or it is not, and the reason is the selector's own words.
    /// <para>
    /// THE BELOW-FLOOR BRANCH IS A SEAM, not a dead end. Telling somebody their phone
    /// is too old and stopping there is the least useful true thing that can be said,
    /// and a device recommendation belongs exactly here — the report already carries
    /// what the device fell short on, which is what a recommendation would need. Left
    /// as a string for now rather than half-wired to something that does not exist:
    /// <see cref="DeviceCapabilityReport.MeetsFloor"/> is the flag a head branches on,
    /// so the seam is reachable without this method knowing about a shop.
    /// </para>
    /// </remarks>
    public static string Disclaimer(DeviceCapabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.MeetsFloor)
            return "This device is below what CircleAI supports. " +
                   "It needs about 3.5 GB of memory and 32 GB of storage — " +
                   "roughly a 2019 handset. Nothing here will run well on it.";

        var can = report.Available.Select(l => Name(l.Modality)).ToList();
        var cannot = report.Unavailable.ToList();

        var head = can.Count == 0
            ? "This device cannot run any of CircleAI's capabilities."
            : $"On this device CircleAI can {Join(can)}.";

        if (cannot.Count == 0) return head;

        var tail = string.Join("; ", cannot.Select(l => $"{Name(l.Modality)} — {l.Plan.Reason}"));
        return $"{head} Not yet: {tail}.";
    }

    private static string Name(ModelModality m) => m switch
    {
        ModelModality.Chat            => "hold a conversation",
        ModelModality.Vision          => "look at a picture",
        ModelModality.Asr             => "write down what it hears",
        ModelModality.Tts             => "speak",
        ModelModality.WakeWord        => "listen for its name",
        ModelModality.ImageGeneration => "draw a picture",
        ModelModality.Music           => "make a music bed",
        ModelModality.Video           => "put a clip together",
        ModelModality.Coding          => "write code",
        _                             => m.ToString(),
    };

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
}
