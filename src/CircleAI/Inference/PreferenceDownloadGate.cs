// PreferenceDownloadGate.cs
//
// THE PERSON'S TWO DOWNLOAD PREFERENCES, ENFORCED WHERE EVERY DOWNLOAD PASSES (Step 4).
//
// "Not over mobile data" and "no more than this much of my phone" were preferences Step 1 names and
// nothing held. The service now keeps them (ServicePreferences) and this is where they act: the
// loader asks a gate before any bytes move, and a download this refuses shows on the setup board as
// waiting for a network - with the person's own reason - rather than failing.
//
// THE PLATFORM SAYS WHETHER THE NETWORK IS METERED, because Core cannot see it: a phone's
// connectivity manager can, a desktop cannot and says null, and null is never treated as metered -
// a gate that cannot tell must not stop a desktop from ever downloading.

using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CircleAI.Inference;

/// <summary>Refuses a download the person's preferences do not allow: mobile data, or past their storage limit.</summary>
/// <param name="mobileDataAllowed">Whether the person allows downloads over a metered network.</param>
/// <param name="metered">Whether the active network is metered; null when the platform cannot say.</param>
/// <param name="storageCapGb">The most storage the models may take, in GB; 0 or less is no limit.</param>
/// <param name="storeBytes">How many bytes the models take now.</param>
public sealed class PreferenceDownloadGate(
    Func<bool> mobileDataAllowed, Func<bool?> metered, Func<double> storageCapGb, Func<long> storeBytes)
    : IModelDownloadGate
{
    /// <inheritdoc />
    public bool IsEnforceable => SafeMetered() is not null || SafeMobileDataAllowed();

    /// <inheritdoc />
    public string? BlockReason(long estimatedBytes)
    {
        if (!SafeMobileDataAllowed() && SafeMetered() == true)
        {
            var size = estimatedBytes > 0
                ? (estimatedBytes / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " MB"
                : "it";
            return $"Waiting for Wi-Fi: downloads over mobile data are switched off, and this is {size}.";
        }

        double cap;
        try { cap = storageCapGb(); }
        catch { cap = 0; }
        if (cap > 0)
        {
            long used;
            try { used = storeBytes(); }
            catch { used = 0; }
            var after = (used + Math.Max(0, estimatedBytes)) / 1_000_000_000d;
            if (after > cap + 0.0001)
                return string.Create(CultureInfo.InvariantCulture,
                    $"It would take the models to {after:0.0} GB, past the {cap:0.#} GB you set aside for them.");
        }

        return null;
    }

    /// <summary>The bytes under a model store, counted from disk; 0 when it cannot be read.</summary>
    public static long StoreBytes(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private bool SafeMobileDataAllowed()
    {
        try { return mobileDataAllowed(); }
        catch { return true; }
    }

    private bool? SafeMetered()
    {
        try { return metered(); }
        catch { return null; }
    }
}
