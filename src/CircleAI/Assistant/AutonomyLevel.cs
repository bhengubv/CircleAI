// AutonomyLevel.cs
//
// How much the app may heal itself without asking. The person owns this dial; it
// lives in the browser-safe layer because the Wolverine dashboard both shows and
// sets it. The product side never widens it — the only thing that ever runs
// automatically is a safe, reversible fix (never code, money, or security).

namespace CircleAI.Assistant;

/// <summary>How autonomously the app heals its own failures.</summary>
public enum AutonomyLevel
{
    /// <summary>Do nothing automatically. Failures are still recorded, nothing is analysed or fixed.</summary>
    Off,

    /// <summary>Analyse and recommend, but never act. Every fix waits for a person.</summary>
    SuggestOnly,

    /// <summary>Auto-run the safe, reversible fixes; escalate everything else. The default.</summary>
    AutoFixSafe,
}

/// <summary>The wire form of <see cref="AutonomyLevel"/> — how it is stored in settings.</summary>
public static class AutonomyLevels
{
    /// <summary>Stored value for <see cref="AutonomyLevel.Off"/>.</summary>
    public const string Off = "off";
    /// <summary>Stored value for <see cref="AutonomyLevel.SuggestOnly"/>.</summary>
    public const string SuggestOnly = "suggest-only";
    /// <summary>Stored value for <see cref="AutonomyLevel.AutoFixSafe"/>.</summary>
    public const string AutoFixSafe = "auto-fix-safe";

    /// <summary>The stored value for a level.</summary>
    public static string ToWire(AutonomyLevel level) => level switch
    {
        AutonomyLevel.Off => Off,
        AutonomyLevel.SuggestOnly => SuggestOnly,
        _ => AutoFixSafe,
    };

    /// <summary>A stored value back to a level. Anything unrecognised defaults to
    /// <see cref="AutonomyLevel.AutoFixSafe"/> — the app heals itself by default.</summary>
    public static AutonomyLevel Parse(string? wire) => wire switch
    {
        Off => AutonomyLevel.Off,
        SuggestOnly => AutonomyLevel.SuggestOnly,
        _ => AutonomyLevel.AutoFixSafe,
    };
}
