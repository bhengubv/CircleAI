// CircleAiLinkClient.cs
//
// The host-app side of the cross-app link: find the shared brain, bind it, ask.
//
// The mirror of CircleNeuronLinkService. It writes the same interface token and
// the same flat string map (LinkTurnCodec) into a Parcel, transacts, and reads
// the reply back. The transaction BLOCKS until the brain answers — the 0.6B is
// slow — so AskAsync runs it off the calling thread; never call the underlying
// binder from the UI thread.
//
// PACKAGE VISIBILITY. On Android 11+ a host app cannot see or bind the CircleAI
// service unless its OWN manifest declares a <queries> entry for the CircleAI
// package (or the link action). Without it IsInstalled reads false and the bind
// is refused, even though the app is installed. See the sample LinkDemo manifest.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>Binds the CircleAI standalone service and asks the shared brain a turn.</summary>
public sealed class CircleAiLinkClient : Java.Lang.Object, IServiceConnection, IDisposable
{
    private readonly Context _context;
    private readonly TaskCompletionSource<IBinder?> _bound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isBound;

    private CircleAiLinkClient(Context context) => _context = context;

    /// <summary>Is the CircleAI standalone installed on this device?</summary>
    /// <remarks>Requires the host manifest's &lt;queries&gt; entry (Android 11+),
    /// or this reads false even when it is installed.</remarks>
    public static bool IsInstalled(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            context.PackageManager!.GetPackageInfo(LinkIpc.HostPackage, (PackageInfoFlags)0);
            return true;
        }
        catch (PackageManager.NameNotFoundException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The intent to launch Circle AI's link-approval screen. Start it with
    /// <c>StartActivityForResult</c> from a foreground Activity (the biometric
    /// sheet needs one) and treat <c>Result.Ok</c> as approved; then call
    /// <see cref="AskAsync"/>. On a later ask the grant already exists.
    /// </summary>
    public static Intent ConsentIntent(LinkScope scope = LinkScope.Chat)
    {
        var intent = new Intent(LinkIpc.ConsentAction);
        intent.SetPackage(LinkIpc.HostPackage);
        intent.PutExtra(LinkIpc.ScopeExtra, (int)scope);
        return intent;
    }

    /// <summary>Bind the CircleAI link service. Null when it is not installed or
    /// the bind is refused.</summary>
    public static async Task<CircleAiLinkClient?> ConnectAsync(
        Context context, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;

        var client = new CircleAiLinkClient(app);
        var intent = new Intent(LinkIpc.BindAction);
        intent.SetPackage(LinkIpc.HostPackage);

        client._isBound = app.BindService(intent, client, Bind.AutoCreate);
        if (!client._isBound) { client.Dispose(); return null; }

        var binder = await WaitOrNull(client._bound.Task, timeout ?? TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        if (binder is null) { client.Dispose(); return null; }
        return client;
    }

    /// <summary>Ask the shared brain one turn. Blocks internally until it answers,
    /// off the calling thread.</summary>
    public async Task<LinkTurnReply> AskAsync(
        string sessionId, string message, bool agentic = false, CancellationToken ct = default)
    {
        var binder = await _bound.Task.ConfigureAwait(false);
        if (binder is null) return LinkTurnReply.Failure("not connected to Circle AI");

        return await Task.Run(() =>
        {
            var data = Parcel.Obtain();
            var reply = Parcel.Obtain();
            try
            {
                data!.WriteInterfaceToken(LinkIpc.Descriptor);
                WriteMap(data, LinkTurnCodec.Encode(new LinkTurnRequest(sessionId, message, agentic)));
                binder.Transact(LinkIpc.TransactAsk, data, reply, (TransactionFlags)0);
                reply!.ReadException();
                return LinkTurnCodec.DecodeReply(ReadMap(reply));
            }
            catch (Exception ex) { return LinkTurnReply.Failure(Why(ex)); }
            finally { data?.Recycle(); reply?.Recycle(); }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Recall the person's memory for a situation. Needs a <c>Memory</c> grant.
    /// Each row is <c>[text]</c>.</summary>
    public Task<LinkRowsReply> RecallAsync(
        string query, int limit = 5, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Recall, Query: query, Limit: limit), ct);

    /// <summary>Write a fact into the person's memory. Needs a <c>Memory</c> grant.
    /// Returns an ok reply with no rows.</summary>
    public Task<LinkRowsReply> RememberAsync(
        string text, string? subject = null, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Remember, Text: text, Subject: subject), ct);

    /// <summary>Search or list the skill library. Needs a <c>Skills</c> grant. Each row
    /// is <c>[id, name]</c>. A null or empty query lists the pack.</summary>
    public Task<LinkRowsReply> SkillsAsync(
        string? query = null, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Skills, Query: query), ct);

    /// <summary>List what Circle AI can do, from its honest manifest. The chat floor is
    /// enough. Each row is <c>[id, status, summary]</c>.</summary>
    public Task<LinkRowsReply> CapabilitiesAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Capabilities), ct);

    /// <summary>Whether the service can hold a conversation yet, and what it waits on.</summary>
    /// <remarks>
    /// THE FIVE SETUP VERBS ARE HOW A SERVICE WITH NO SCREENS GETS CONFIGURED. Every
    /// one carries the chat scope, because the person driving them is in an app they
    /// have already approved and is asking for this by name.
    /// </remarks>
    public Task<LinkRowsReply> SetupReadinessAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.SetupReadiness), ct);

    /// <summary>What the service still needs fetching, each row [title, bytes].</summary>
    public Task<LinkRowsReply> SetupPlanAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.SetupPlan), ct);

    /// <summary>What the service already holds, each row [title, present, bytes, detail].</summary>
    public Task<LinkRowsReply> SetupCensusAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.SetupCensus), ct);

    /// <summary>Begin fetching the plan. Returns at once; follow it with <see cref="SetupProgressAsync"/>.</summary>
    public Task<LinkRowsReply> SetupStartAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.SetupStart), ct);

    /// <summary>Where the run has got to: one row of [index, count, title, fraction, seconds, phase].</summary>
    public Task<LinkRowsReply> SetupProgressAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.SetupProgress), ct);

    /// <summary>The wake phrases for a language: [text, chosen, builtIn, quality, advice].</summary>
    /// <remarks>
    /// THE JUDGEMENT TRAVELS, NOT THE MODEL. Deciding whether a typed phrase can
    /// survive a room needs the keyword spotter's own tokeniser, which is hundreds of
    /// megabytes and lives with the service. These five verbs let a client ask for the
    /// verdict instead of carrying the thing that produces it.
    /// </remarks>
    public Task<LinkRowsReply> WakePhrasesForAsync(string language, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.WakePhrasesFor, Query: language), ct);

    /// <summary>Judge a phrase without adding it: [added, quality, advice].</summary>
    public Task<LinkRowsReply> WakePhraseCheckAsync(string language, string phrase, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.WakePhraseCheck, Query: language, Text: phrase), ct);

    /// <summary>Add a phrase, unless it cannot work at all.</summary>
    public Task<LinkRowsReply> WakePhraseAddAsync(string language, string phrase, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.WakePhraseAdd, Query: language, Text: phrase), ct);

    /// <summary>Listen for this phrase from now on.</summary>
    public Task<LinkRowsReply> WakePhraseChooseAsync(string language, string phrase, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.WakePhraseChoose, Query: language, Text: phrase), ct);

    /// <summary>Remove a phrase the person added.</summary>
    public Task<LinkRowsReply> WakePhraseRemoveAsync(string language, string phrase, CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.WakePhraseRemove, Query: language, Text: phrase), ct);

    /// <summary>What the resident listener is doing: [state, status, hint].</summary>
    public Task<LinkRowsReply> ResidentStatusAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.ResidentStatus), ct);

    /// <summary>Start listening for the wake phrase.</summary>
    public Task<LinkRowsReply> ResidentStartAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.ResidentStart), ct);

    /// <summary>Stop listening.</summary>
    public Task<LinkRowsReply> ResidentStopAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.ResidentStop), ct);

    /// <summary>What this device can actually do: [title, blurb, state, bytes].</summary>
    public Task<LinkRowsReply> AbilitiesAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Abilities), ct);

    /// <summary>What CircleAI is allowed to do here: [title, why, granted, runtime].</summary>
    public Task<LinkRowsReply> PermissionsAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Permissions), ct);

    /// <summary>What Circle AI holds here: lines, then total and freeable as the last two rows.</summary>
    public Task<LinkRowsReply> FootprintAsync(CancellationToken ct = default)
        => VerbAsync(new LinkVerbRequest(LinkVerb.Footprint), ct);

    /// <summary>What to tell a person when the transaction itself failed.</summary>
    /// <remarks>
    /// ex.Message IS NOT A SENTENCE WHEN THE OTHER APP HAS DIED. All three transacts
    /// handed it straight to the caller, and the caller renders it in a chat bubble -
    /// so when Android killed the service under memory pressure on a P30 on
    /// 2026-10-02, what a person saw was:
    ///
    ///     Exception_WasThrown, Android.Util.AndroidException
    ///
    /// which is a resource key and a type name. The binder's own failures are the
    /// ones most likely to be seen, because they happen exactly when the phone is
    /// struggling and somebody is most likely to be asking why.
    /// <para>
    /// THE REAL ONE IS STILL LOGGED. The sentence is for the person; the exception is
    /// for whoever reads logcat afterwards, and losing it would trade one unreadable
    /// failure for an invisible one.
    /// </para>
    /// </remarks>
    private static string Why(Exception ex)
    {
        global::Android.Util.Log.Warn("CircleAI.Link", "transact failed: " + ex);

        return ex switch
        {
            // The host process is gone - killed, crashed, or updated underneath us.
            // It restarts on the next bind, so "ask again" is true rather than kind.
            global::Android.OS.DeadObjectException =>
                "Circle AI stopped — this phone closed it. Ask again and it will start up.",

            // Over the binder's ~1 MB budget, which is shared across the process.
            global::Android.OS.TransactionTooLargeException =>
                "That was too big to send to Circle AI. Try a shorter one.",

            _ => "Circle AI could not answer just now. Ask again.",
        };
    }

    /// <summary>Transacts one structured verb. Mirrors <see cref="AskAsync"/>: blocks
    /// internally, off the calling thread.</summary>
    private async Task<LinkRowsReply> VerbAsync(LinkVerbRequest req, CancellationToken ct)
    {
        var binder = await _bound.Task.ConfigureAwait(false);
        if (binder is null) return LinkRowsReply.Failure("not connected to Circle AI");

        return await Task.Run(() =>
        {
            var data = Parcel.Obtain();
            var reply = Parcel.Obtain();
            try
            {
                data!.WriteInterfaceToken(LinkIpc.Descriptor);
                WriteMap(data, LinkVerbCodec.Encode(req));
                binder.Transact(LinkIpc.TransactVerb, data, reply, (TransactionFlags)0);
                reply!.ReadException();
                return LinkVerbCodec.DecodeReply(ReadMap(reply));
            }
            catch (Exception ex) { return LinkRowsReply.Failure(Why(ex)); }
            finally { data?.Recycle(); reply?.Recycle(); }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <summary>Transcribe audio this app recorded, using the service's recogniser.</summary>
    /// <param name="pcm16">Audio in <see cref="LinkAudioFormat"/> — 16 kHz mono 16-bit LE.</param>
    /// <param name="language">BCP-47 tag, or null to let the service choose.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// THE MICROPHONE STAYS HERE. This app records — it holds the permission and knows
    /// when the screen is up — and sends the samples to the one process that owns a
    /// recogniser. The size is checked BEFORE the transact, because exceeding the
    /// binder budget raises TransactionTooLargeException, which names neither the size
    /// nor the caller and can land on an unrelated call that was in flight.
    /// </remarks>
    public Task<LinkAudioReply> TranscribeAsync(byte[] pcm16, string? language = null,
                                                CancellationToken ct = default)
        => AudioAsync(new LinkAudioRequest(LinkAudioVerb.Transcribe, pcm16 ?? Array.Empty<byte>(),
                                           Language: language), ct);

    /// <summary>Say these words with the device voice, and get the audio back to play.</summary>
    /// <remarks>
    /// Playback stays here too, for the same reason recording does: an audio track is
    /// cheap and belongs to the app the person is looking at. Only the VOICE — the
    /// model — lives in the service.
    /// </remarks>
    public Task<LinkAudioReply> SpeakAsync(string text, string? language = null,
                                           CancellationToken ct = default)
        => AudioAsync(new LinkAudioRequest(LinkAudioVerb.Speak, Array.Empty<byte>(),
                                           text ?? string.Empty, language), ct);

    private async Task<LinkAudioReply> AudioAsync(LinkAudioRequest req, CancellationToken ct)
    {
        // CHECKED HERE, BEFORE A BINDER IS EVEN TOUCHED. The codec checks again when
        // writing, and the service checks a third time on read — a caller that skips
        // it gets a crash rather than an answer, and a service that trusts callers on
        // sizes is one any app can bring down.
        if (!LinkAudio.Fits(req.Audio.Length, out var refusal))
            return LinkAudioReply.Failure(refusal!);

        var binder = await _bound.Task.ConfigureAwait(false);
        if (binder is null) return LinkAudioReply.Failure("not connected to Circle AI");

        return await Task.Run(() =>
        {
            var data = Parcel.Obtain();
            var reply = Parcel.Obtain();
            try
            {
                data!.WriteInterfaceToken(LinkIpc.Descriptor);
                if (!LinkAudioCodec.TryWriteRequest(new ParcelWriter(data), req, out var why))
                    return LinkAudioReply.Failure(why!);

                binder.Transact(LinkIpc.TransactAudio, data, reply, (TransactionFlags)0);
                reply!.ReadException();
                return LinkAudioCodec.ReadReply(new ParcelReader(reply));
            }
            catch (Exception ex) { return LinkAudioReply.Failure(Why(ex)); }
            finally { data?.Recycle(); reply?.Recycle(); }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Adapts a <see cref="Parcel"/> to the codec's writer.</summary>
    /// <remarks>
    /// MUST MIRROR THE SERVICE'S PAIR EXACTLY. The codec owns field ORDER; these two
    /// own only the primitive calls. A Parcel is unframed, so a difference here does
    /// not fail — it reads structurally valid rubbish.
    /// </remarks>
    private sealed class ParcelWriter(Parcel parcel) : LinkAudioCodec.IWriter
    {
        public void WriteInt(int value) => parcel.WriteInt(value);
        public void WriteString(string? value) => parcel.WriteString(value);
        public void WriteBytes(byte[] value) => parcel.WriteByteArray(value);
    }

    /// <summary>Adapts a <see cref="Parcel"/> to the codec's reader.</summary>
    private sealed class ParcelReader(Parcel parcel) : LinkAudioCodec.IReader
    {
        public int ReadInt() => parcel.ReadInt();
        public string? ReadString() => parcel.ReadString();
        public byte[] ReadBytes() => parcel.CreateByteArray() ?? Array.Empty<byte>();
    }

    public void OnServiceConnected(ComponentName? name, IBinder? service) => _bound.TrySetResult(service);

    /// <inheritdoc/>
    public void OnServiceDisconnected(ComponentName? name) => _bound.TrySetResult(null);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _isBound)
        {
            _isBound = false;
            try { _context.UnbindService(this); } catch { /* already gone */ }
        }
        base.Dispose(disposing);
    }

    // ── parcel <-> string map (mirror of the service's writer/reader) ────────

    private static void WriteMap(Parcel parcel, IReadOnlyDictionary<string, string> map)
    {
        parcel.WriteInt(map.Count);
        foreach (var pair in map)
        {
            parcel.WriteString(pair.Key);
            parcel.WriteString(pair.Value);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadMap(Parcel parcel)
    {
        var count = parcel.ReadInt();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = parcel.ReadString();
            var value = parcel.ReadString();
            if (key is not null) map[key] = value ?? string.Empty;
        }
        return map;
    }

    private static async Task<T?> WaitOrNull<T>(Task<T?> task, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return finished == task ? await task.ConfigureAwait(false) : null;
    }
}
