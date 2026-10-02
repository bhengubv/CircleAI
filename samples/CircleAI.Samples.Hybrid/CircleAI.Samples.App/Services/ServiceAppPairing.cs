// ServiceAppPairing.cs
//
// The head's answer to "is the other half here, and how does somebody get it".
//
// THIN, LIKE EVERY OTHER SERVICE IN THIS FOLDER. ServicePairing and AppShare are in
// the product and do the thinking - which package is missing, whether this phone
// permits an install, where an installed APK actually lives and how to make a copy
// of it somebody else may read. What is here is the three things only an app can do:
// hold the Android context, open the share sheet, and start an activity.

using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceAppPairing : IAppPairing
{
#if ANDROID
    private static global::Android.Content.Context Context
        => global::Android.App.Application.Context;
#endif

    /// <inheritdoc />
    public PairingFacts Facts
    {
#if ANDROID
        get
        {
            var state = ServicePairing.Check(Context);
            return new PairingFacts(
                Complete:    state.IsPaired,
                MayInstall:  state.MayInstall,
                CanOffer:    AppShare.Available(Context).Count > 0);
        }
#else
        get => new(Complete: false, MayInstall: false, CanOffer: false);
#endif
    }

    /// <inheritdoc />
    public IReadOnlyList<OfferableApp> Offerable
    {
#if ANDROID
        get => [.. AppShare.Available(Context)
                           .Select(a => new OfferableApp(a.Label, a.Bytes))];
#else
        get => [];
#endif
    }

    /// <inheritdoc />
    public async Task<bool> OfferAsync(CancellationToken ct = default)
    {
#if ANDROID
        // COPIED FRESH EVERY TIME, because what travels has to be what this phone is
        // actually running - see AppShare. The copies are cleaned afterwards; two
        // APKs is tens of megabytes on a phone chosen for being cheap.
        var apps = AppShare.Available(Context);
        if (apps.Count == 0) return false;

        try
        {
            await Share.Default.RequestAsync(new ShareMultipleFilesRequest
            {
                Title = "Send Circle AI",
                Files = [.. apps.Select(a => new ShareFile(a.Path))],
            }).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            Trouble.Say(ex);
            return false;
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
        return false;
#endif
    }

    /// <inheritdoc />
    public bool OpenInstallPermission()
    {
#if ANDROID
        var intent = ServicePairing.InstallPermissionSettings(Context);
        if (intent is null) return false;

        try { Context.StartActivity(intent); return true; }
        catch (Exception ex) { Trouble.Say(ex); return false; }
#else
        return false;
#endif
    }
}
