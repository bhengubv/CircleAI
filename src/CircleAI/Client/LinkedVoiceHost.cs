// LinkedVoiceHost.cs
//
// Speaking, with the voice in the other process.
//
// WHAT THIS TAKES OUT OF A CLIENT APK. DeviceVoiceHost catalogues TTS models,
// selects one for the device and loads it through BundleModelLoader; VoiceWiring
// unpacks espeak's data and installs the phonemiser. Between them that is
// libespeak-ng.so, a 12 MB data zip, the ONNX runtime and whatever voice the person
// downloaded — in EVERY app that wants to talk. The service already owns all of it,
// so a client only needs bytes back and something to play them with.
//
// THE PHONEMISER GOES TOO, which is the part that is easy to miss. espeak-ng is
// GPL-3.0 and lives out-of-process precisely so linking it does not relicense the
// app; a client that no longer synthesises does not need that arrangement at all.
//
// WHAT STAYS HERE: playback. An audio track is cheap and belongs to the app somebody
// is looking at — it knows when to duck, when to stop, and when the screen went off.
// Only the VOICE crosses.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>An <see cref="IVoiceHost"/> that asks the CircleAI service to speak.</summary>
/// <param name="brain">The linked brain; its connection is reused rather than a second bind.</param>
/// <param name="play">
/// Plays PCM in <see cref="LinkAudioFormat"/>. Supplied by the head, because playback
/// is platform work and this assembly should not pick an audio API for it.
/// </param>
public sealed class LinkedVoiceHost(LinkedBrain brain, Func<byte[], CancellationToken, Task>? play = null)
    : IVoiceHost
{
    private readonly LinkedBrain _brain = brain ?? throw new ArgumentNullException(nameof(brain));

    /// <inheritdoc />
    /// <remarks>
    /// Says where the sound came from, which matters when two apps on one phone can
    /// sound different: this one has no voice of its own and never did.
    /// </remarks>
    public string Provenance => "the shared CircleAI voice, over the cross-app link";

    /// <inheritdoc />
    /// <remarks>
    /// THE CONTRACT ASKS "CAN THIS HEAD SPEAK", not "does it own a voice", and the
    /// answer here is yes when CircleAI is installed. Its own header says why this
    /// must be truthful: a head that quietly returns silence gives somebody a
    /// "Hear it" button identical to a working one, which is worse than a button that
    /// explains itself.
    /// <para>
    /// A SYNCHRONOUS PROPERTY CAN ONLY ASK A SYNCHRONOUS QUESTION, so this checks
    /// whether the service is INSTALLED rather than whether the link is approved —
    /// binding is asynchronous and needs consent. An installed-but-unapproved service
    /// therefore reads OnDevice and the first SayAsync explains what to do, which is
    /// the better failure: the capability genuinely exists and one tap away.
    /// </para>
    /// </remarks>
    public VoiceAvailability Availability =>
        _brain.ServiceInstalled ? VoiceAvailability.OnDevice : VoiceAvailability.Unavailable;

    /// <inheritdoc />
    /// <remarks>
    /// EMPTY, AND HONESTLY SO. The catalogue is a list of what is INSTALLED, and a
    /// client installs nothing — the service holds the voices. Returning a plausible
    /// list here would offer somebody a download this app cannot perform and does not
    /// own. A screen that wants to manage voices belongs in the service's own app.
    /// </remarks>
    public Task<IReadOnlyList<VoiceRow>> CatalogueAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VoiceRow>>(Array.Empty<VoiceRow>());

    /// <inheritdoc />
    public Task<SpeakOutcome> SpeakAsync(string tag, IProgress<string>? progress = null,
                                         CancellationToken ct = default)
        // The no-text overload exists to speak a voice's own sample line. There is no
        // verb for "say your sample" on the wire, and inventing a sentence here would
        // be this app's words in the service's voice, so it declines rather than
        // guessing at one.
        => Task.FromResult(new SpeakOutcome(
            false, "Ask for specific words: the shared voice has no sample line over the link."));

    /// <inheritdoc />
    /// <remarks>
    /// The tag is the requested VOICE, passed through as a language hint. The service
    /// decides what it can actually run — it owns the catalogue and the device fit —
    /// so a client asking for a voice that is not installed gets the service's choice
    /// rather than a failure it cannot act on.
    /// </remarks>
    public async Task<SpeakOutcome> SayAsync(string tag, string text,
                                             IProgress<string>? progress = null,
                                             CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SpeakOutcome(false, "There were no words to say.");

        var clock = Stopwatch.StartNew();
        progress?.Report("asking CircleAI to speak…");

        var audio = await _brain.SpeakAsync(text, tag, ct).ConfigureAwait(false);
        var synth = clock.ElapsedMilliseconds;

        if (audio.Length == 0)
            return new SpeakOutcome(false,
                "CircleAI could not speak that — it may not be linked, or the text may be "
                + "longer than one link call carries.", synth);

        // Duration from the agreed format rather than from the player: it is known
        // before a single sample is played, so a caller can show it immediately.
        var audioMs = (long)(audio.Length * 1000L / LinkAudioFormat.BytesPerSecond);

        if (play is null)
            return new SpeakOutcome(true,
                $"{audio.Length / 1024} kB of speech came back; this head has no player wired.",
                synth, audioMs);

        progress?.Report("playing…");
        try
        {
            await play(audio, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service DID speak; playback is this app's failure and saying so
            // points at the right half. Blaming the link would send somebody to
            // reinstall CircleAI over a broken audio track.
            return new SpeakOutcome(false, $"CircleAI spoke, but playback failed here: {ex.Message}",
                                    synth, audioMs);
        }

        return new SpeakOutcome(true, Provenance, clock.ElapsedMilliseconds, audioMs);
    }
}
