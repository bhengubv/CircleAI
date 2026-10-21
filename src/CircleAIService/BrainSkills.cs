// BrainSkills.cs
//
// WHAT THE BRAIN IS HANDED AS NOTES, ON BOTH FRONT DOORS. The phone's inference process
// composed the capability manifest and the consumer pack; the Windows service composed
// nothing. Run for real on a PC on 2026-10-21, Qwen3.5-4B answered "How do I plant maize?"
// with seed depths and no planting months, and asked how many people a meal plan was for -
// the notes the phone gets had never been offered. One owner now, read by both heads.

using CircleAI.Skills;

namespace CircleAI.Service;

/// <summary>The skill stores the brain's notes come from, the same on a phone and on a desktop.</summary>
internal static class BrainSkills
{
    /// <summary>The embedded stores: the capability manifest first, then the consumer pack.</summary>
    /// <remarks>
    /// THE EMBEDDED STORES ONLY. The 20 MB library is unpacked by the phone's main process,
    /// and opening it in the inference process could race that unpack; the manifest - the
    /// assistant's honesty about itself - is embedded and is what matters most.
    /// </remarks>
    public static ISkillStore Embedded() => new CompositeSkillStore(
    [
        CapabilityManifestSkillStore.Default,
        ConsumerSkillPack.Shared,
    ]);
}
