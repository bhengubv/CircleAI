// CacheChoices.cs
//
// The handful of settings a person may pick for the self-managing cache - the
// Telegram controls, sized for a phone whose cache is kilobytes, not a media
// library. Two questions, four answers each: how long to keep untouched scratch,
// and how large to let the whole cache grow.
//
// Pure: enums in, a TimeSpan / a byte count / a label out. The default answers map
// to CacheEviction's own defaults, so the number "30 days / 256 MB" still lives in
// exactly one place and the picker's default cannot drift from what the engine
// does when nobody has picked anything.

namespace CircleAI.Assistant;

/// <summary>How long untouched scratch is kept before it ages out.</summary>
public enum KeepChoice
{
    /// <summary>Three days - the most eager sweep on offer.</summary>
    ThreeDays,

    /// <summary>One week.</summary>
    OneWeek,

    /// <summary>One month. The default, and CacheEviction's own.</summary>
    OneMonth,

    /// <summary>Never age anything out - keep it until the cap says otherwise.</summary>
    Forever,
}

/// <summary>How large the whole cache may grow before the oldest is evicted.</summary>
public enum CapChoice
{
    /// <summary>64 MB.</summary>
    Mb64,

    /// <summary>256 MB. The default, and CacheEviction's own.</summary>
    Mb256,

    /// <summary>One gigabyte - for a phone with room to spare.</summary>
    Gb1,

    /// <summary>No ceiling at all - only the age window trims.</summary>
    NoLimit,
}

/// <summary>The discrete cache settings, and what each one means to the engine.</summary>
public static class CacheChoices
{
    /// <summary>The answer used when nobody has picked one.</summary>
    public const KeepChoice DefaultKeep = KeepChoice.OneMonth;

    /// <summary>The answer used when nobody has picked one.</summary>
    public const CapChoice DefaultCap = CapChoice.Mb256;

    /// <summary>Every keep option, in the order a picker should show them.</summary>
    public static readonly IReadOnlyList<KeepChoice> AllKeep =
        [KeepChoice.ThreeDays, KeepChoice.OneWeek, KeepChoice.OneMonth, KeepChoice.Forever];

    /// <summary>Every cap option, in the order a picker should show them.</summary>
    public static readonly IReadOnlyList<CapChoice> AllCap =
        [CapChoice.Mb64, CapChoice.Mb256, CapChoice.Gb1, CapChoice.NoLimit];

    /// <summary>The keep window this choice means, for <see cref="CacheEviction"/>.</summary>
    /// <remarks>
    /// OneMonth returns CacheEviction's own default, so the number lives once.
    /// Forever is <see cref="System.TimeSpan.MaxValue"/> - the "never age out" switch.
    /// </remarks>
    public static TimeSpan Keep(KeepChoice choice) => choice switch
    {
        KeepChoice.ThreeDays => TimeSpan.FromDays(3),
        KeepChoice.OneWeek => TimeSpan.FromDays(7),
        KeepChoice.OneMonth => CacheEviction.KeepDefault,
        KeepChoice.Forever => TimeSpan.MaxValue,
        _ => CacheEviction.KeepDefault,
    };

    /// <summary>The cap this choice means, in bytes; 0 is "no limit".</summary>
    /// <remarks>Mb256 returns CacheEviction's own default, so the number lives once.</remarks>
    public static long Cap(CapChoice choice) => choice switch
    {
        CapChoice.Mb64 => 64L * 1024 * 1024,
        CapChoice.Mb256 => CacheEviction.MaxCacheDefaultBytes,
        CapChoice.Gb1 => 1024L * 1024 * 1024,
        CapChoice.NoLimit => 0,
        _ => CacheEviction.MaxCacheDefaultBytes,
    };

    /// <summary>The keep option in a person's words.</summary>
    public static string Label(KeepChoice choice) => choice switch
    {
        KeepChoice.ThreeDays => "3 days",
        KeepChoice.OneWeek => "1 week",
        KeepChoice.OneMonth => "1 month",
        KeepChoice.Forever => "Forever",
        _ => "1 month",
    };

    /// <summary>The cap option in a person's words.</summary>
    public static string Label(CapChoice choice) => choice switch
    {
        CapChoice.Mb64 => "64 MB",
        CapChoice.Mb256 => "256 MB",
        CapChoice.Gb1 => "1 GB",
        CapChoice.NoLimit => "No limit",
        _ => "256 MB",
    };
}
