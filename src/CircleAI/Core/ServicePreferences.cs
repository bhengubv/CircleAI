// ServicePreferences.cs
//
// WHAT THE PERSON HAS SAID ABOUT THE SERVICE, KEPT WHERE THE SERVICE CAN READ IT (Step 4).
//
// The service has no screens, so every choice a person makes about it - whether gigabytes may come
// over mobile data, how much of the phone the models may take, whether it may shrink its brain to
// spare the battery, which abilities are off - arrives over the link from an app that has one. Step
// 1 lists these as "the person: permissions and preferences (metered downloads, storage cap,
// abilities switched off)", and nothing held them. This does: a small file in the service's own
// storage, read on every use, written on every change.
//
// ON BY DEFAULT, AND SAID. Every preference starts where the owner's rule puts it - everything on,
// downloads on any network, no storage limit, balance on - and the SettingsGet verb lists each one
// with its default beside it, so an app can show what changed and what did not.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CircleAI.Core;

/// <summary>What kind of value a preference holds, so a screen can draw the right control.</summary>
public enum PreferenceKind
{
    /// <summary>On or off: "1" or "0".</summary>
    Flag = 0,

    /// <summary>A number, written invariantly ("2.5").</summary>
    Number = 1,

    /// <summary>Free text.</summary>
    Text = 2,
}

/// <summary>One preference the service knows.</summary>
/// <param name="Key">Its key, as the link carries it.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Default">Its value when the person has said nothing.</param>
/// <param name="Title">What it is, in a person's words.</param>
/// <param name="Description">What it does, in one sentence.</param>
public sealed record PreferenceInfo(string Key, PreferenceKind Kind, string Default, string Title, string Description);

/// <summary>One preference as an app sees it: what it is, what it is now, and what it would be.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Value">Its value now.</param>
/// <param name="Default">Its value when the person has said nothing.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Title">What it is, in a person's words.</param>
/// <param name="Description">What it does, in one sentence.</param>
public sealed record SettingRow(string Key, string Value, string Default, PreferenceKind Kind, string Title, string Description);

/// <summary>The person's preferences for this service, kept in one small file.</summary>
public sealed class ServicePreferences
{
    /// <summary>Whether models may download over a metered (mobile) network.</summary>
    public const string MobileData = "downloads.mobileData";

    /// <summary>The most storage the models may take, in gigabytes; 0 is no limit.</summary>
    public const string StorageCapGb = "downloads.storageCapGb";

    /// <summary>Whether the service may shrink its brain under heat, low battery or low memory.</summary>
    public const string Balance = "balance.on";

    /// <summary>Log levels, "Area=Level" pairs, applied as the service runs.</summary>
    public const string LogLevels = "log.levels";

    // Kept beside the preferences, set through the AbilitySet verb rather than SettingsSet, because
    // switching an ability off also refuses its downloads - one verb has to do both.
    private const string AbilitiesOffKey = "abilities.off";

    /// <summary>Every preference the service knows, in the order a screen shows them.</summary>
    public static IReadOnlyList<PreferenceInfo> Known { get; } =
    [
        new(MobileData, PreferenceKind.Flag, "1", "Download over mobile data",
            "Models download over mobile data as well as Wi-Fi; off, they wait for Wi-Fi."),
        new(StorageCapGb, PreferenceKind.Number, "0", "Storage for models",
            "The most space the models may take, in GB; 0 means no limit."),
        new(Balance, PreferenceKind.Flag, "1", "Balance with the phone",
            "When the phone is hot, low on battery or short of memory, the brain shrinks first and grows back when it is calm."),
        new(LogLevels, PreferenceKind.Text, "", "Log levels",
            "Area=Level pairs such as Engine=Verbose,Voice=Debug; empty keeps the defaults."),
    ];

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Preferences kept in <paramref name="path"/>; null keeps them in memory only.</summary>
    public ServicePreferences(string? path)
    {
        _path = path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (stored is null) return;
            foreach (var (key, value) in stored)
                if (key == AbilitiesOffKey || Info(key) is not null) _values[key] = value ?? string.Empty;
        }
        catch
        {
            // A file that will not read is a person's choices lost, not a service that will not
            // start: the defaults stand, and the next change writes a good file.
        }
    }

    /// <summary>The service's preferences, once the host has opened them. Null reads every default.</summary>
    public static ServicePreferences? Current { get; set; }

    /// <summary>Raised after a preference changes: its key and new value.</summary>
    public event Action<string, string>? Changed;

    /// <summary>What is known about a key, or null for a key this service does not have.</summary>
    public static PreferenceInfo? Info(string key) => Known.FirstOrDefault(p => p.Key == key);

    /// <summary>A preference's value now: the person's, or its default.</summary>
    public string Get(string key)
    {
        lock (_gate)
            return _values.TryGetValue(key, out var value) ? value : Info(key)?.Default ?? string.Empty;
    }

    /// <summary>A flag preference.</summary>
    public bool Flag(string key) => Get(key) == "1";

    /// <summary>A number preference; 0 when it does not parse.</summary>
    public double Number(string key)
        => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Every preference as an app sees it.</summary>
    public IReadOnlyList<SettingRow> Rows()
        => Known.Select(p => new SettingRow(p.Key, Get(p.Key), p.Default, p.Kind, p.Title, p.Description)).ToList();

    /// <summary>
    /// Sets a preference after checking it means something, and says why when it does not. The
    /// value is stored the way it will be read back ("on" becomes "1").
    /// </summary>
    public bool TrySet(string key, string? value, out SettingRow? row, out string? why)
    {
        row = null;
        var info = Info(key ?? string.Empty);
        if (info is null)
        {
            why = $"There is no setting called '{key}'. Known: {string.Join(", ", Known.Select(k => k.Key))}.";
            return false;
        }

        if (!Normalise(info, value ?? string.Empty, out var normal, out why)) return false;

        Store(info.Key, normal);
        row = new SettingRow(info.Key, normal, info.Default, info.Kind, info.Title, info.Description);
        return true;
    }

    /// <summary>The abilities the person switched off, by title.</summary>
    public IReadOnlySet<string> AbilitiesOff
    {
        get
        {
            lock (_gate)
            {
                var raw = _values.TryGetValue(AbilitiesOffKey, out var v) ? v : string.Empty;
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Records an ability switched on or off by the person.</summary>
    public void SetAbility(string title, bool on)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var off = new HashSet<string>(AbilitiesOff, StringComparer.Ordinal);
        var changed = on ? off.Remove(title) : off.Add(title);
        if (changed) Store(AbilitiesOffKey, string.Join(",", off.OrderBy(t => t, StringComparer.Ordinal)));
    }

    private static bool Normalise(PreferenceInfo info, string value, out string normal, out string? why)
    {
        normal = value.Trim();
        why = null;
        switch (info.Kind)
        {
            case PreferenceKind.Flag:
                switch (normal.ToLowerInvariant())
                {
                    case "1" or "on" or "true" or "yes": normal = "1"; return true;
                    case "0" or "off" or "false" or "no": normal = "0"; return true;
                    default: why = $"{info.Title} is on or off (1 or 0), not '{value}'."; return false;
                }

            case PreferenceKind.Number:
                if (!double.TryParse(normal, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    || double.IsNaN(n) || double.IsInfinity(n) || n < 0)
                {
                    why = $"{info.Title} is a number of 0 or more, not '{value}'.";
                    return false;
                }
                normal = n.ToString("0.###", CultureInfo.InvariantCulture);
                return true;

            default:
                if (normal.Length > 500)
                {
                    why = $"{info.Title} is at most 500 characters.";
                    return false;
                }
                return true;
        }
    }

    private void Store(string key, string value)
    {
        Dictionary<string, string> copy;
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var current) && current == value) return;
            _values[key] = value;
            copy = new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(_path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var writing = _path + ".writing";
                File.WriteAllText(writing, JsonSerializer.Serialize(copy));
                File.Move(writing, _path, overwrite: true);
            }
            catch
            {
                // Kept in memory for this run; a file that will not write is not a reason to
                // refuse the choice somebody just made.
            }
        }

        try { Changed?.Invoke(key, value); }
        catch { /* a listener's failure is not the setting's */ }
    }
}
