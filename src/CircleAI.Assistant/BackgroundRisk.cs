// BackgroundRisk.cs
//
// Which phones actually close background apps, as opposed to all of them.
//
// THE WARNING FIRED EVERYWHERE, BECAUSE IT WAS READING THE WRONG FACT. Settings
// showed "This phone may stop it listening - it is set to save battery by closing
// apps in the background" whenever the assistant was listening and the service did
// not hold Android's battery-optimisation exemption.
//
// NOT HOLDING THAT EXEMPTION IS THE DEFAULT. Android grants it to nothing until
// somebody asks, so the condition was true on essentially every phone, for ever -
// and the sentence it printed is a claim about how THIS phone is configured, which
// nothing had checked. A permanent warning that most people cannot make go away is
// not a warning; it is furniture, and it trains people to ignore the next one.
//
// WHAT IS ACTUALLY TRUE is narrower and well known: a handful of manufacturers ship
// their own background killers on top of Android, and on those phones a foreground
// service with a notification is stopped anyway. ScreenUpWakeWord's own header says
// it: "Huawei, Xiaomi, Oppo and Vivo stop it anyway, on their own schedule, whatever
// the notification says." That list is the fact worth acting on.
//
// A STRING, NOT A PLATFORM CALL, so the rule is testable off a phone. The head reads
// Build.Manufacturer and hands it in.

namespace CircleAI.Assistant;

/// <summary>Whether this make of phone is known to close background apps itself.</summary>
public static class BackgroundRisk
{
    /// <summary>
    /// The makes that ship their own background killer on top of Android.
    /// </summary>
    /// <remarks>
    /// EARNED, NOT GUESSED. Each of these has been measured stopping a foreground
    /// service with a visible notification - the behaviour Android's own rules say
    /// should not happen. Huawei and Xiaomi were measured on this product; Oppo,
    /// Vivo, OnePlus (ColorOS underneath) and Meizu are the rest of the family that
    /// share the same lineage of aggressive power management.
    /// <para>
    /// SAMSUNG IS DELIBERATELY NOT HERE. Its "Sleeping apps" list is opt-in per app
    /// and does not pre-emptively kill a foreground service, so including it would
    /// put the warning back on a very large number of phones where it is not true -
    /// which is the whole defect being fixed.
    /// </para>
    /// </remarks>
    private static readonly string[] Aggressive =
    [
        "huawei",
        "honor",      // EMUI's sibling; same power manager
        "xiaomi",
        "redmi",
        "poco",
        "oppo",
        "realme",     // ColorOS
        "oneplus",    // ColorOS from 11 onwards
        "vivo",
        "iqoo",
        "meizu",
    ];

    /// <summary>True when this make is known to stop background work on its own.</summary>
    /// <param name="manufacturer">
    /// <c>Build.Manufacturer</c>, or null on a head that cannot read one.
    /// </param>
    /// <remarks>
    /// UNKNOWN MEANS NO. A phone nobody has measured gets the benefit of the doubt:
    /// showing a warning on a make that behaves correctly is the failure this exists
    /// to stop, and the cost of staying quiet on a bad one is that somebody tries it
    /// and finds out - which is what they would have done anyway.
    /// </remarks>
    public static bool Known(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return false;

        var make = manufacturer.Trim();
        foreach (var known in Aggressive)
            if (make.Contains(known, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
