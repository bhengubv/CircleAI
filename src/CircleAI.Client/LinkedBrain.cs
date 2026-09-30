// LinkedBrain.cs
//
// An IBrain that asks the CircleAI service instead of loading a model.
//
// WHAT MAKES A CLIENT THIN. Not fewer screens — fewer DEPENDENCIES. The hybrid
// sample referenced CircleAI.Inference and CircleAI.Device, which is how a "sample"
// came to carry the engines, the model catalogue and a 2.2 GB download; and because
// LinkIpc.HostPackage named that sample, the shared brain on a device was something
// a person got by installing a demo. Swapping DeviceBrain for this one is the whole
// change: the app keeps its screens and loses the model.
//
// IBrain WAS ALREADY THE SEAM, which is why this is small. The web head has proved
// a non-local brain works for a while — BrowserBrain is one. This is the third
// implementation of a contract that already existed, not a new abstraction.
//
// IT LIVES IN THE PRODUCT, NOT THE APP. A thin client renders and wires DI; deciding
// what to do when the service is missing, or how a turn maps onto the wire, is
// logic, and logic belongs where every client can get it.

using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>
/// The shared on-device brain, reached over the cross-app link.
/// </summary>
/// <remarks>
/// Binds lazily and keeps the connection: binding costs a round trip and the
/// consent check, and a brain that re-bound per question would pay both on every
/// turn. A dropped service simply fails the next call and rebinds on the one after.
/// </remarks>
public sealed class LinkedBrain : IBrain, IDisposable
{
    private readonly Context _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CircleAiLinkClient? _client;
    private string _session = Guid.NewGuid().ToString("n");

    /// <param name="context">An Android context — the application context is right here.</param>
    public LinkedBrain(Context context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Whether the CircleAI service is installed on this device at all.</summary>
    /// <remarks>
    /// A client must also declare a <c>&lt;queries&gt;</c> entry for the service package,
    /// or Android 11+ package visibility hides it and this reads false on a device
    /// where the service is installed and running — with no error anywhere.
    /// </remarks>
    public bool ServiceInstalled => CircleAiLinkClient.IsInstalled(_context);

    /// <summary>The intent that asks the person to approve this app's link.</summary>
    /// <remarks>
    /// Launched FOR RESULT by the foreground client, because a background service
    /// cannot raise a biometric sheet and because starting it for result is how the
    /// OS tells the consent screen which package is really asking.
    /// </remarks>
    public Intent ConsentIntent(LinkScope scope = LinkScope.Chat)
        => CircleAiLinkClient.ConsentIntent(scope);

    /// <inheritdoc />
    public async Task<BrainState> StateAsync(CancellationToken ct = default)
    {
        // THREE STATES, NOT TWO, because they need different things from a person:
        // not installed (install CircleAI), installed but not approved (approve the
        // link), and ready. Collapsing them into "not ready" leaves somebody with no
        // idea which of the two actions to take.
        if (!ServiceInstalled)
            return new BrainState(false, "CircleAI is not installed on this device.");

        var client = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new BrainState(false, "CircleAI is installed but this app is not linked to it yet.");

        // BOUND IS NOT APPROVED, AND SAYING OTHERWISE IS A LIE THE SCREEN REPEATS.
        // ConnectAsync only binds; the grant is checked per transaction, inside the
        // service. So this used to report "Using the shared CircleAI brain" on a link
        // nobody had approved, and the first question then came back refused - which
        // is exactly the state the three-way answer below exists to prevent.
        //
        // Capabilities is the probe because it is the cheapest verb that goes through
        // the same authorisation: its scope is Chat, and the manifest is not private,
        // so a granted link answers it immediately and an ungranted one refuses.
        var probe = await client.CapabilitiesAsync(ct).ConfigureAwait(false);
        return probe.Ok
            ? new BrainState(true, "Using the shared CircleAI brain.")
            : new BrainState(false, probe.Error ?? "This app is not linked to CircleAI yet.");
    }

    /// <inheritdoc />
    public async Task<string> AskAsync(string prompt, Action<string>? token = null,
                                       CancellationToken ct = default)
    {
        // TRACED AT EVERY STEP, because the first end-to-end run of this produced a
        // question on screen with no reply, no error and not one line anywhere. A
        // seam that can fail silently has to say where it got to.
        Trace("ask: connecting");
        var client = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null) { Trace("ask: NOT CONNECTED"); return NotLinked(); }

        Trace("ask: transacting");
        var reply = await client.AskAsync(_session, prompt, agentic: false, ct)
                                .ConfigureAwait(false);
        Trace($"ask: replied ok={reply.Ok} error={reply.Error ?? "-"} len={(reply.Reply ?? string.Empty).Length}");

        // A REFUSAL IS AN ANSWER AND MUST TRAVEL THE SAME ROAD. Returning it without
        // calling `token` looks harmless and is not: the typed screen builds its
        // bubble ONLY from streamed fragments, so an unstreamed refusal left the
        // bubble empty, and that screen then deletes an empty bubble as "the model
        // produced nothing". The service said "not linked for Chat - approve in
        // Circle AI first", the app received it in under a second, and a person saw
        // their question sitting there with no reply, no error and nothing in logcat.
        // Every caller that can render an answer can render this.
        if (!reply.Ok)
        {
            var why = reply.Error ?? "CircleAI could not answer.";
            token?.Invoke(why);
            return why;
        }

        var text = reply.Reply ?? string.Empty;

        // NO STREAMING OVER THE LINK, and pretending otherwise would be worse than
        // saying so. A binder transaction is request/response: the whole answer
        // arrives at once, so the token callback fires once with all of it. A caller
        // rendering token-by-token still works, it simply does not animate. Streaming
        // would need a second transaction code and a callback binder, which is a wire
        // change rather than something to fake here.
        token?.Invoke(text);
        return text;
    }

    /// <inheritdoc />
    public async Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default)
    {
        var client = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null) return NotLinked();

        var reply = await client.AskAsync(_session, prompt, agentic: true, ct)
                                .ConfigureAwait(false);
        return reply.Ok ? reply.Reply ?? string.Empty
                        : reply.Error ?? "CircleAI could not answer.";
    }

    /// <inheritdoc />
    /// <remarks>
    /// ZERO, which the contract defines as "do not resize", because there is nothing
    /// to resize FOR: the link carries text only. A screen that asks this gets the
    /// honest answer rather than a plausible 1024 that would have it shrink a photo
    /// before sending it somewhere that cannot take one.
    /// </remarks>
    public int MaxImageEdge => 0;

    /// <inheritdoc />
    public Task<string> SeeAsync(string question, byte[] image, Action<string>? token = null,
                                 CancellationToken ct = default)
    {
        // THE WIRE HAS NO IMAGE VERB. LinkVerb covers recall, remember, skills and
        // capabilities; a turn carries a string. So a linked client cannot ask about
        // a picture, and this says so rather than returning something that reads like
        // an answer about an image it never saw — which is exactly the failure this
        // repo has already had once, when a throw was swallowed and the TEXT model
        // answered about a photograph politely and wrongly.
        //
        // Closing it is a protocol change: a transaction that carries bytes, with its
        // own scope so a person approves picture-reading separately from chat.
        _ = question; _ = image; _ = token; _ = ct;
        return Task.FromResult(
            "Reading pictures is not available over the CircleAI link yet — "
            + "the link carries text only.");
    }

    /// <summary>Transcribe audio this app recorded, using the service's recogniser.</summary>
    /// <remarks>
    /// THE SPLIT, IN ONE METHOD. This app keeps the microphone — the permission, the
    /// capture, knowing when the screen is up — and the service keeps the recogniser,
    /// which is the hundreds of megabytes nobody can duplicate per app. The bytes
    /// cross; the model does not.
    /// <para>
    /// Roughly 25 seconds fits in one call (see <see cref="LinkAudio.MaxSeconds"/>),
    /// which is a spoken question rather than a recording. Longer audio is refused
    /// with a sentence measured in seconds, not a crash.
    /// </para>
    /// </remarks>
    public async Task<string> TranscribeAsync(byte[] pcm16, string? language = null,
                                              CancellationToken ct = default)
    {
        var client = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null) return NotLinked();

        var reply = await client.TranscribeAsync(pcm16, language, ct).ConfigureAwait(false);
        // An empty transcript is a RESULT — silence, a cough, an empty room — so it
        // comes back as an empty string rather than an error a screen would show.
        return reply.Ok ? reply.Text : reply.Error ?? "CircleAI could not hear that.";
    }

    /// <summary>Say these words with the device voice; the caller plays the audio.</summary>
    /// <returns>PCM in <see cref="LinkAudioFormat"/>, or empty when it could not.</returns>
    public async Task<byte[]> SpeakAsync(string text, string? language = null,
                                         CancellationToken ct = default)
    {
        var client = await ConnectAsync(ct).ConfigureAwait(false);
        if (client is null) return Array.Empty<byte>();

        var reply = await client.SpeakAsync(text, language, ct).ConfigureAwait(false);
        return reply.Ok ? reply.Audio : Array.Empty<byte>();
    }

    /// <summary>The bound client, for the siblings that ride the same link.</summary>
    /// <remarks>
    /// ONE BIND PER APP, not one per contract. LinkedMemory needs the same service,
    /// the same consent and the same connection; giving it its own would cost a second
    /// round trip and a second grant check for nothing, and would leave two objects
    /// disagreeing about whether the link is up.
    /// </remarks>
    internal Task<CircleAiLinkClient?> LinkAsync(CancellationToken ct) => ConnectAsync(ct);

    /// <summary>One line per step of a turn, to logcat.</summary>
    /// <remarks>
    /// Android.Util.Log directly: ILogger reaches nothing on Android, which is how a
    /// whole failing path came to produce no output at all.
    /// </remarks>
    private static void Trace(string line)
    {
        try { Android.Util.Log.Info("CircleAI.Link", line); } catch { }
    }

    private static string NotLinked() =>
        "CircleAI is not linked to this app yet. Approve the link to use the shared brain.";

    private async Task<CircleAiLinkClient?> ConnectAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: several screens can ask at once on a cold
            // start, and binding twice wastes a round trip and a consent check.
            if (_client is not null) return _client;
            // Default bind timeout (10 s). A cold service has to start before it can
            // answer, and a shorter wait would report "not linked" for a service that
            // is merely waking up.
            _client = await CircleAiLinkClient.ConnectAsync(_context, timeout: null, ct)
                                              .ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Start a fresh conversation; the service keeps history per session id.</summary>
    public void NewSession() => _session = Guid.NewGuid().ToString("n");

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _gate.Dispose();
    }
}
