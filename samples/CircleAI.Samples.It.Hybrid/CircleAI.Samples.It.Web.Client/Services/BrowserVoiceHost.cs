// BrowserVoiceHost.cs
//
// The browser cannot speak, and says so.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <summary>
/// The web head's voice: none. Declared rather than faked.
/// </summary>
/// <remarks>
/// A NO-OP THAT RETURNED SUCCESS WOULD BE THE WORST OPTION HERE. Synthesis on the
/// phone is a 60-150 MB ONNX graph, sometimes a 103 MB morphological dictionary,
/// and a native espeak provider reached over a content provider. None of that
/// exists in a WebAssembly sandbox.
/// <para>
/// Routing to a server instead was considered and rejected: the sample's claim is
/// that the work happens on your device and nothing leaves it. Quietly posting the
/// text to a server to make a button light up would break exactly the promise the
/// sample exists to demonstrate.
/// </para>
/// </remarks>
public sealed class BrowserVoiceHost : IVoiceHost
{
    /// <inheritdoc />
    public VoiceAvailability Availability => VoiceAvailability.Unavailable;

    /// <inheritdoc />
    public string Provenance =>
        "Voices run on the device, so this page lists them rather than speaking them. "
        + "Install the app to hear one.";

    /// <inheritdoc />
    /// <remarks>
    /// The name table, with no sizes. A browser has no model registry and no
    /// device to select against, and inventing a megabyte figure here would put a
    /// number on screen that nothing checked.
    /// </remarks>
    public Task<IReadOnlyList<VoiceRow>> CatalogueAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VoiceRow>>(
            SampleLanguages.All.Keys.Select(t => new VoiceRow(t, null)).ToList());

    /// <inheritdoc />
    public Task<SpeakOutcome> SpeakAsync(
        string tag, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new SpeakOutcome(false, Provenance));
}
