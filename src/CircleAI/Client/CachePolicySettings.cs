// CachePolicySettings.cs
//
// The person's cache choices, in the one app store, and the numbers they resolve
// to. The KEYS live here and nowhere else: DeviceSettings persists AppSettings
// through them, and the launch and pressure trims read them back through Resolve.
// Keeping both on the same two constants is what stops a saved setting and an
// enforced one drifting apart.

namespace CircleAI.Assistant.Device;

/// <summary>Reads and writes the cache keep/cap choices in the app's KV store.</summary>
public static class CachePolicySettings
{
    /// <summary>The store key for the keep-window choice.</summary>
    public const string KeepKey = "app.cache.keep";

    /// <summary>The store key for the size-cap choice.</summary>
    public const string CapKey = "app.cache.cap";

    /// <summary>The stored keep choice, or the default when unset or unreadable.</summary>
    public static KeepChoice ReadKeep(SqliteAppStore store)
        => Enum.TryParse<KeepChoice>(store.Get(KeepKey, CacheChoices.DefaultKeep.ToString()), out var k)
            ? k : CacheChoices.DefaultKeep;

    /// <summary>The stored cap choice, or the default when unset or unreadable.</summary>
    public static CapChoice ReadCap(SqliteAppStore store)
        => Enum.TryParse<CapChoice>(store.Get(CapKey, CacheChoices.DefaultCap.ToString()), out var c)
            ? c : CacheChoices.DefaultCap;

    /// <summary>Persist the keep choice.</summary>
    public static void WriteKeep(SqliteAppStore store, KeepChoice keep)
        => store.Set(KeepKey, keep.ToString());

    /// <summary>Persist the cap choice.</summary>
    public static void WriteCap(SqliteAppStore store, CapChoice cap)
        => store.Set(CapKey, cap.ToString());

    /// <summary>
    /// The keep window and cap the trim should run with right now - the two choices
    /// turned into the values <see cref="MemoryManager.TrimCache"/> takes.
    /// </summary>
    public static (TimeSpan Keep, long MaxBytes) Resolve(SqliteAppStore store)
        => (CacheChoices.Keep(ReadKeep(store)), CacheChoices.Cap(ReadCap(store)));
}
