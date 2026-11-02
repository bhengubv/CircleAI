// PretendConditions.cs
//
// A BATTERY, A CHARGER AND A HEAT THAT ARE NOT THE PHONE'S, FOR A FEW MINUTES - FROM ADB ONLY.
//
// S9 has to see the brain re-planned when the battery runs low or the phone runs hot, and the
// only other ways to get there are a device setting (`dumpsys battery set`, which outlives the
// test if it is not undone) or waiting for the weather. So the three readers in this process -
// the brain plan's power probe, the answer budget and the balance loop - ask here first, and a
// DUMP-protected broadcast (SoakReceiver "pretend") sets what they are told until it runs out.
// Nothing on the phone changes; when the time is up, every reader is back on the real thing.

namespace CircleAI.Service.Droid;

/// <summary>A pretend battery, charger and heat for a while - a developer check, never a setting.</summary>
internal static class PretendConditions
{
    private static readonly object Gate = new();
    private static int? _battery;
    private static bool? _charging;
    private static int? _heat;
    private static DateTimeOffset _until;

    /// <summary>Pretends for <paramref name="span"/>; a null value leaves that reading the phone's.</summary>
    public static void Set(int? battery, bool? charging, int? heat, TimeSpan span)
    {
        lock (Gate)
        {
            _battery = battery is >= 0 and <= 100 ? battery : null;
            _charging = charging;
            _heat = heat is >= 0 and <= 2 ? heat : null;
            _until = DateTimeOffset.UtcNow + span;
        }
    }

    /// <summary>Back on the phone's own readings now.</summary>
    public static void Clear()
    {
        lock (Gate) _until = DateTimeOffset.MinValue;
    }

    /// <summary>One line for the log: what is pretended and until when, or that nothing is.</summary>
    public static string Describe()
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow >= _until) return "nothing pretended";
            return $"pretending battery {(_battery is { } b ? b + "%" : "as is")}, "
                   + $"{(_charging is { } c ? (c ? "charging" : "not charging") : "charger as is")}, "
                   + $"heat {(_heat is { } h ? h.ToString(System.Globalization.CultureInfo.InvariantCulture) : "as is")} "
                   + $"until {_until:HH:mm:ss} UTC";
        }
    }

    /// <summary>The battery to report: the pretend one while it lasts, else <paramref name="real"/>.</summary>
    public static int? Battery(int? real) => Now(out var b, out _, out _) && b is { } v ? v : real;

    /// <summary>The charger to report.</summary>
    public static bool? Charging(bool? real) => Now(out _, out var c, out _) && c is { } v ? v : real;

    /// <summary>The heat to report: 0 calm, 1 serious, 2 critical.</summary>
    public static int Heat(int real) => Now(out _, out _, out var h) && h is { } v ? v : real;

    /// <summary>Whether the phone is too hot to work hard.</summary>
    public static bool Hot(bool real) => Now(out _, out _, out var h) && h is { } v ? v > 0 : real;

    private static bool Now(out int? battery, out bool? charging, out int? heat)
    {
        lock (Gate)
        {
            var on = DateTimeOffset.UtcNow < _until;
            battery = on ? _battery : null;
            charging = on ? _charging : null;
            heat = on ? _heat : null;
            return on;
        }
    }
}
