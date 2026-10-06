// IAppPairing.cs
//
// What the "you are missing half of this" screen needs, and only a head can answer.
//
// Circle AI is two packages and the thin app holds no model: every question travels
// app -> link -> the service. Installed on its own the app is a set of screens that
// cannot answer anything, and whether the other half is present is a question about
// Android's package manager - which does not exist in a browser tab and does not
// belong in a Razor component. The screen renders rows; it does not decide what a
// row says.
//
// AND THE WAY OUT IS A FILE, NOT A LINK. A phone with nothing on it cannot reach a
// store or a mesh, so the route that works is somebody who already has Circle AI
// handing over the installer through Android's own share sheet. That too is a head
// question: a browser has no APK to give.

namespace CircleAI.Assistant;

/// <summary>What is missing before this phone can think, and whether it can be fixed here.</summary>
/// <param name="Complete">Is the other half of Circle AI installed?</param>
/// <param name="MayInstall">
/// Will this phone permit this app to install a package? From Android 8 that is a
/// Settings switch somebody has to visit, not a dialog that can be raised.
/// </param>
/// <param name="CanOffer">Has this phone got installers it could hand to somebody else?</param>
public readonly record struct PairingFacts(bool Complete, bool MayInstall, bool CanOffer);

/// <summary>One installer this phone could pass on.</summary>
/// <param name="Label">What to call it to a person.</param>
/// <param name="Bytes">How big it is, so a screen can say so before sending.</param>
public readonly record struct OfferableApp(string Label, long Bytes);

/// <summary>The missing half of Circle AI, and how a person gets it.</summary>
public interface IAppPairing
{
    /// <summary>Where this phone stands. Cheap enough to ask whenever a screen appears.</summary>
    PairingFacts Facts { get; }

    /// <summary>What this phone could hand to somebody else, largest need first.</summary>
    IReadOnlyList<OfferableApp> Offerable { get; }

    /// <summary>
    /// Open Android's share sheet with the installers on it.
    /// </summary>
    /// <returns>False when there was nothing to share or the sheet could not open.</returns>
    /// <remarks>
    /// IT IS THE SHEET THAT DOES THE WORK, not us. Bluetooth, Nearby, Wi-Fi Direct
    /// and the mesh are all routes it offers when the phone has them, and Bluetooth
    /// needs no store, no account, no signal and no other app of ours - which is the
    /// whole reason this is the route rather than a download.
    /// </remarks>
    Task<bool> OfferAsync(CancellationToken ct = default);

    /// <summary>
    /// Send somebody to the Settings switch that permits installing packages.
    /// </summary>
    /// <returns>False when there is no such screen - below Android 8 there is not.</returns>
    bool OpenInstallPermission();
}
