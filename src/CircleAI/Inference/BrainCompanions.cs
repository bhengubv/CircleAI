// BrainCompanions.cs
//
// WHAT GOES WITH A BRAIN: ITS VISION TOWER AND ITS DRAFTER (Step 4, "everything on").
//
// Bonsai 2 is a vision-language model whose eyes are a separate file - the mmproj tower, about
// 365 MB at Q8_0 for the 2B - and whose speed-up is a separate file too, the DFlash 2 drafter.
// Neither is any use without its own brain: a 2B's tower beside a 4B is 365 MB of nothing. So
// they are not abilities to be ranked against the catalogue the way a voice is; they are fetched
// for the brain this device runs, when the two together still fit, and never on their own.
//
// THE CONTRACT A CATALOGUE ROW MEETS TO BE TAKEN, written down because the rows are added by S5
// and this plan must be ready for them:
//
//   a tower    Modality Vision, and its file (or Architecture) says "mmproj" - upstream's own
//              name for the projector, the same word the loader already excludes when it looks
//              for a brain's GGUF;
//   a drafter  a "Draft" capability, or an Architecture or name naming DFlash or a draft model;
//   its brain  the same Repo as the brain (upstream publishes a brain's tower beside it), or a
//              name that begins with the brain's name less its packing ("Ternary-Bonsai-2-4B");
//   usable     the engine can use it (Usable): a drafter by the engine's own pack rule, so one
//              catalogued before its code lands waits rather than costing the person's data for
//              nothing; a tower on the C# engine whenever its brain is, because the brain finds it
//              in the folder beside its own that the store names after the tower's row (S8).
//
// A row that meets none of it is an ordinary model and is planned as one.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>What a companion does for its brain.</summary>
public enum CompanionKind
{
    /// <summary>The vision tower (mmproj): lets the brain see.</summary>
    VisionTower,

    /// <summary>A drafter for speculative decoding: lets the brain answer faster.</summary>
    Drafter,
}

/// <summary>A catalogue row that goes with a brain.</summary>
/// <param name="Entry">The row.</param>
/// <param name="Kind">What it does for the brain.</param>
public sealed record BrainCompanion(ModelEntry Entry, CompanionKind Kind);

/// <summary>Finds a brain's vision tower and drafter, and says whether they fit beside it.</summary>
public static class BrainCompanions
{
    /// <summary>Whether a row is a vision tower - the projector half of a vision-language brain.</summary>
    public static bool IsTower(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Modality != ModelModality.Vision) return false;
        return Names(entry.Architecture, "mmproj", "projector")
               || (entry.BundleFiles?.Any(f => Names(f.Name, "mmproj")) ?? false);
    }

    /// <summary>Whether a row is a drafter - a small model that proposes tokens its brain checks.</summary>
    public static bool IsDrafter(ModelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.Capabilities?.Any(c => string.Equals(c, "Draft", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(c, "Drafter", StringComparison.OrdinalIgnoreCase)) ?? false)
               || Names(entry.Architecture, "dflash", "draft")
               || Names(entry.Name, "dflash");
    }

    /// <summary>Whether a row only makes sense beside a brain: a tower or a drafter.</summary>
    public static bool IsCompanion(ModelEntry entry) => IsTower(entry) || IsDrafter(entry);

    /// <summary>Whether <paramref name="companion"/> belongs to <paramref name="brain"/>.</summary>
    public static bool Belongs(ModelEntry companion, ModelEntry brain)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(brain);
        if (string.Equals(companion.Name, brain.Name, StringComparison.OrdinalIgnoreCase)) return false;

        if (!string.IsNullOrWhiteSpace(companion.Repo) && !string.IsNullOrWhiteSpace(brain.Repo)
            && string.Equals(companion.Repo, brain.Repo, StringComparison.OrdinalIgnoreCase))
            return true;

        var family = Family(brain);
        return family.Length > 0
               && companion.Name.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every companion the catalogue holds for <paramref name="brain"/>, towers first.</summary>
    public static IReadOnlyList<BrainCompanion> Of(ModelEntry brain, IEnumerable<ModelEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(catalogue);

        return catalogue
            .Where(e => !e.Retired && Belongs(e, brain))
            .Select(e => IsTower(e) ? new BrainCompanion(e, CompanionKind.VisionTower)
                       : IsDrafter(e) ? new BrainCompanion(e, CompanionKind.Drafter)
                       : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderBy(c => c.Kind)          // the tower before the drafter: seeing is an ability, speed a nicety
            .ThenBy(c => c.Entry.TotalBytes) // and the smaller of two towers (Q8_0 before BF16)
            .ToList();
    }

    /// <summary>
    /// The companions this device can hold beside <paramref name="brain"/>: at most one of each
    /// kind, each added only while the brain and everything chosen so far still fit in the most
    /// one model may ever claim here, only where there is room on the disk, and only once the
    /// engine can read it.
    /// </summary>
    /// <param name="brain">The brain they go with.</param>
    /// <param name="catalogue">Every row known.</param>
    /// <param name="probe">The device.</param>
    /// <param name="declined">Rows the person or this device refused; null refuses none.</param>
    /// <param name="engines">The engines this build ships; null asks the build.</param>
    /// <remarks>
    /// NOT BEFORE THE ENGINE CAN USE IT. The Bonsai 2 fleet's drafters ("dflash") are catalogued
    /// before the C# engine has code for them, and a file nobody can load is hundreds of megabytes of
    /// the person's data for nothing - <see cref="EngineChoice.CanRun"/> is the engine's own answer,
    /// so each arrives in the plan the day its code lands, with no catalogue edit. A TOWER ON THE C#
    /// ENGINE IS THE EXCEPTION THAT PROVES IT: it is never opened as a model of its own; the brain
    /// finds it in the folder the store names after the tower's row, beside its own (S8,
    /// <c>ManagedChatGenerator.FindTower</c>), so it is usable exactly when its brain is.
    /// </remarks>
    public static IReadOnlyList<BrainCompanion> ThatFit(
        ModelEntry brain, IEnumerable<ModelEntry> catalogue, DeviceProbe probe, Func<string, bool>? declined = null,
        IReadOnlyCollection<ModelEngine>? engines = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var chosen = new List<BrainCompanion>();
        var residentGb = ModelFit.EagerGb(brain);
        foreach (var kind in new[] { CompanionKind.VisionTower, CompanionKind.Drafter })
        {
            foreach (var candidate in Of(brain, catalogue).Where(c => c.Kind == kind))
            {
                if (declined is not null && declined(candidate.Entry.Name)) continue;
                if (!Usable(candidate, brain, engines)) continue;
                if (!ModelFit.FitsStorage(candidate.Entry, probe.StorageFreeGb)) continue;

                // THE WHOLE RESIDENT COST, NOT THE COMPANION'S ALONE: a tower that fits on its own
                // and not beside its brain is a tower the brain can never use here.
                var together = residentGb + ModelFit.EagerGb(candidate.Entry);
                var combined = brain with
                {
                    Name = brain.Name + "+" + candidate.Entry.Name,
                    MinRamGb = together,
                    TotalBytes = 0,
                    MmapResidentGb = null,
                };
                if (!ModelFit.CouldEverHold(combined, probe)) continue;

                chosen.Add(candidate);
                residentGb = together;
                break;
            }
        }
        return chosen;
    }

    /// <summary>
    /// Whether this build can use <paramref name="companion"/> beside <paramref name="brain"/>: a
    /// C# engine tower beside a C# engine brain the engine reads (the brain finds it, S8); anything
    /// else by the engine's own pack rule.
    /// </summary>
    public static bool Usable(BrainCompanion companion, ModelEntry brain, IReadOnlyCollection<ModelEngine>? engines = null)
    {
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(brain);
        return companion.Kind == CompanionKind.VisionTower
               && companion.Entry.Engine == ModelEngine.Managed && brain.Engine == ModelEngine.Managed
            ? EngineChoice.CanRun(brain, engines)
            : EngineChoice.CanRun(companion.Entry, engines);
    }

    /// <summary>A brain's name less its packing: "Ternary-Bonsai-2-4B-PQ2_0" is family "Ternary-Bonsai-2-4B".</summary>
    internal static string Family(ModelEntry brain)
    {
        var name = brain.Name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(brain.Quantization)
            && name.EndsWith("-" + brain.Quantization, StringComparison.OrdinalIgnoreCase))
            return name[..^(brain.Quantization.Length + 1)];
        return name;
    }

    private static bool Names(string? text, params string[] words)
        => !string.IsNullOrWhiteSpace(text)
           && words.Any(w => text!.Contains(w, StringComparison.OrdinalIgnoreCase));
}
