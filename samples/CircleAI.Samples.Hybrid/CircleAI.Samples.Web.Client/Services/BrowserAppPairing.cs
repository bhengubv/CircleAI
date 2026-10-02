// BrowserAppPairing.cs
//
// A browser tab has no packages and no APK to give.
//
// COMPLETE RATHER THAN MISSING, and that is the careful bit. The pairing prompt
// exists to tell somebody their phone is short of the half that thinks - on the web
// there is no second half to be short of, because the web build never links to a
// service at all. Reporting "incomplete" here would put an instruction to install an
// Android package in front of somebody sitting at a desk.

using CircleAI.Assistant;

namespace CircleAI.Samples.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserAppPairing : IAppPairing
{
    /// <inheritdoc />
    public PairingFacts Facts => new(Complete: true, MayInstall: false, CanOffer: false);

    /// <inheritdoc />
    public IReadOnlyList<OfferableApp> Offerable => [];

    /// <inheritdoc />
    public Task<bool> OfferAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public bool OpenInstallPermission() => false;
}
