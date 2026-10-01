// ResidentListeningControl.cs
//
// "Answer to its name" — the switch, and the thing behind it.
//
// THERE WAS NOTHING BEHIND IT ON ANY HEAD. The only IResidentAssistant
// implementations in the tree were a browser one saying "impossible" and a client
// one saying "open CircleAI" — about an app with no launcher icon. Meanwhile every
// part a wake word needs had been built and left unreachable:
//
//   ScreenUpWakeWord          complete, ZERO callers in the repo
//   ZipformerKwsSpotter       complete, reached only by the above
//   DeviceWakePhrases         writes the keyword file, and its KeywordFile is
//                             documented "Public: the resident wake service reads
//                             the same file the app writes" — a service nobody wrote
//   KWS-Zipformer-HeyB        6.4 MB, downloaded, never opened
//   RECORD_AUDIO              declared in the manifest, never granted, because a
//                             service with no Activity cannot raise the dialog
//
// Five finished pieces and no line joining them. This is that line.
//
// THE NOTIFICATION IS NOT OPTIONAL. CircleNeuronService is started alongside the
// spotter because it owns the foreground notification the microphone is disclosed
// through — "listening for Hey Circle AI, nothing is kept or sent". A phone holding
// an open microphone with nothing on the shade is not disclosure, it is the absence
// of it, and that is the one thing this feature must never get wrong.

using System.Linq;
using Android.Content;
using CircleAI.Assistant;
using CircleAI.Device;

namespace CircleAI.Assistant.Device;

/// <summary>Turns the wake word on and off, and says what it is doing.</summary>
/// <param name="context">Any context; the application context is right here.</param>
public sealed class ResidentListeningControl(Context context) : IResidentAssistant
{
    private const string Tag = "CircleAI.Wake";

    /// <summary>The directory that actually holds the spotter's graphs.</summary>
    /// <remarks>
    /// THE BUNDLE UNPACKS A LEVEL DOWN, and assuming otherwise fails late and
    /// obscurely: ZipformerKwsSpotter throws "no *encoder*.onnx in ..." from inside
    /// its constructor, which surfaces as the wake loop dying rather than as a model
    /// that is not where it was expected. On the P30 the entry is
    /// KWS-Zipformer-HeyB/ and the graphs are in KWS-Zipformer-HeyB/kws-hey-b/.
    /// <para>
    /// So the encoder is what is searched for, not a path that is guessed. One level
    /// down is enough for every bundle shape shipped so far, and a bundle that nests
    /// deeper should say so rather than be found by a recursive walk of the model
    /// store.
    /// </para>
    /// </remarks>
    private static string? BundleDir()
    {
        var root = Path.Combine(ModelStore.Path, "KWS-Zipformer-HeyB");
        if (!Directory.Exists(root)) return null;
        if (HasEncoder(root)) return root;

        foreach (var child in Directory.EnumerateDirectories(root))
            if (HasEncoder(child)) return child;

        return null;
    }

    private static bool HasEncoder(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*encoder*.onnx").Any();
        }
        catch
        {
            return false;
        }
    }

    private ScreenUpWakeWord? _ears;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public bool IsListening => _ears?.IsListening == true;

    /// <inheritdoc />
    /// <remarks>Forwarded from the spotter, so a host can act on the phrase.</remarks>
    public event EventHandler<string>? Woke;

    /// <inheritdoc />
    public async Task<ResidentStatus> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ears is { IsListening: true }) return Listening();

            // THE MODEL FIRST, because the honest refusal is specific. Without the
            // bundle the spotter throws deep inside its first audio chunk, which
            // surfaces as "it stopped" rather than "you have not downloaded it yet".
            var bundle = BundleDir();
            if (bundle is null)
                return new ResidentStatus(ResidentState.Unsupported,
                    "The wake word is not downloaded",
                    "Set CircleAI up and it will fetch what it needs to hear its name.");

            // The microphone is a runtime permission and this app cannot prompt — it
            // has no Activity. LinkConsentActivity asks for it when a client is
            // approved for Voice; until then, say so rather than opening a recorder
            // that returns silence for ever.
            if (context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio)
                != Android.Content.PM.Permission.Granted)
                return new ResidentStatus(ResidentState.NeedsPermission,
                    "CircleAI cannot use the microphone",
                    "Approve the link again from the app and allow the microphone when asked.");

            // THE DISCLOSURE, BEFORE THE MICROPHONE. CircleNeuronService owns the
            // ongoing notification; starting the spotter first would open a recorder
            // with nothing on the shade to say so.
            try { CircleNeuronService.Start(context); }
            catch (Exception ex)
            {
                Android.Util.Log.Warn(Tag, "foreground service did not start: " + ex.Message);
            }

            var keywords = DeviceWakePhrases.KeywordFile("en");
            var ears = new ScreenUpWakeWord(bundle, File.Exists(keywords) ? keywords : null);
            ears.Woke += (_, phrase) =>
            {
                Android.Util.Log.Info(Tag, $"woke on \"{phrase}\"");
                Woke?.Invoke(this, phrase);
            };

            // THROUGH THE SERVICE, NOT PAST IT, AND THE SCREEN SHOWED WHY.
            //
            // This used to call ears.Start() directly and keep the spotter to
            // itself. CircleNeuronService.IsListening - the one static three other
            // places read to answer "is the microphone open" - therefore stayed
            // false while the microphone was open, and Settings printed both
            // answers at once on a P30 on 2026-10-01: "Answer to its name" ticked,
            // because that reads THIS object, and the Waking ability still offering
            // "Turn on", because that reads the static. Same screen, same second.
            //
            // AND TWO THINGS THE PRIVATE LOOP COULD NOT HAVE. StartListeningAsync
            // takes the PARTIAL WAKE LOCK, without which "it answers with the screen
            // off" lasts until the CPU suspends and then silently does not - the
            // failure measured on a P30 on 2026-09-05, eleven minutes of somebody
            // talking to a phone that was not scheduled to hear them. And it writes
            // the microphone disclosure on the shade, naming the phrase, which is
            // the one thing this feature must never get wrong.
            CircleNeuronService.Listener = ears;
            _ears = ears;

            var up = await CircleNeuronService.StartListeningAsync(ct).ConfigureAwait(false);
            if (!up)
            {
                CircleNeuronService.Listener = null;
                _ears = null;
                return new ResidentStatus(ResidentState.Failed,
                    "It could not start listening",
                    "The microphone did not open. Something else on this phone may be holding it.");
            }

            Android.Util.Log.Info(Tag, $"listening, keywords={(File.Exists(keywords) ? keywords : "built-in")}");
            return Listening();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(Tag, "could not start listening: " + ex);
            return new ResidentStatus(ResidentState.Failed, "It could not start listening", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ResidentStatus> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ears is not null)
            {
                // THE SERVICE STOPS IT, so the wake lock is released and the shade
                // stops saying the microphone is open in the same breath. Stopping
                // the spotter behind the service's back left both behind.
                await CircleNeuronService.StopListeningAsync(ct).ConfigureAwait(false);
                CircleNeuronService.Listener = null;

                await _ears.DisposeAsync().ConfigureAwait(false);
                _ears = null;
                Android.Util.Log.Info(Tag, "stopped listening");
            }

            // THE SERVICE STAYS UP. It hosts the brain as well as the microphone, and
            // stopping the wake word is not a reason to unload a model every other
            // app on the link is using.
            return Off();
        }
        catch (Exception ex)
        {
            return new ResidentStatus(ResidentState.Failed, "It could not stop listening", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Resuming is starting; Start is a no-op when it is already listening.</remarks>
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default) => StartAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// ASKED, NOT REMEMBERED. The loop can end without anyone calling Stop — the
    /// microphone is revoked, the OS reclaims the service, the spotter throws — so
    /// this reads the loop rather than a flag set when it was started.
    /// </remarks>
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
        => Task.FromResult(IsListening ? Listening() : Off());

    private static ResidentStatus Listening() => new(
        ResidentState.Listening,
        "Listening",
        "It answers to its name with the screen off. Nothing is kept or sent.");

    private static ResidentStatus Off() => new(
        ResidentState.Off,
        "Not listening",
        "Turn it on to wake it by name.");
}
