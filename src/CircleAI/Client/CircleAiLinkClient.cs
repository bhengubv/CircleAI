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

    // THE BINDER IS NOT A ONE-SHOT, AND TREATING IT AS ONE IS WHY A PERSON WAS
    // ASKED TO SWITCH THE BRAIN ON.
    //
    // Measured on the P30 on 2026-10-09:
    //
    //   19:03:49  Killing 8610:…circleai.service (adj 100):
    //             iAwareK[abnormProc](adj:-10000,type:service)
    //   19:04:21  …circleai.service back up as pid 11233, brain loaded, 593 MB
    //   19:05:25  W/CircleAI.Link(8465): transact failed: DeadObjectException
    //   19:06:03  W/CircleAI.Link(8465): transact failed: DeadObjectException
    //
    // The service was ALIVE from 19:04:21 and the app went on transacting with the
    // corpse. _bound was a single TaskCompletionSource: once it carried a binder,
    // OnServiceDisconnected's TrySetResult(null) was a no-op, and so was the
    // TrySetResult on the RECONNECT. Bind.AutoCreate had done its job and handed
    // back a fresh binder; this class threw it away and kept the dead one forever.
    //
    // So every ask failed with "Circle AI stopped - ask again and it will start up",
    // asking again did exactly the same thing, and under it sat a button asking a
    // person to go and start the brain themselves. You do not ask someone to turn
    // their brain on before you speak to them.
    //
    // The app being open IS the service running - the bind is AutoCreate and lives
    // as long as the app does. These three fields make the client believe that:
    // _live is the binder currently thought good, _bound is a waiter that is
    // REPLACED on every death so the next caller waits for the restart instead of
    // being handed a corpse, and _gate keeps the swap atomic against the binder
    // threadpool, which delivers the callbacks.
    private readonly object _gate = new();
    private TaskCompletionSource<IBinder?> _bound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IBinder? _live;
    private bool _isBound;

    /// <summary>
    /// How long a call waits for the service to come back before giving up.
    /// </summary>
    /// <remarks>
    /// Measured: iAware killed it at 19:03:49 and Android had it serving again by
    /// 19:04:21 - 32 seconds, on a phone with its swap exhausted. The old code gave
    /// it none at all. This is deliberately longer than that worst case, because
    /// the alternative to waiting is the screen that started all this.
    /// </remarks>
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(45);

    private CircleAiLinkClient(Context context) => _context = context;

    /// <summary>
    /// The binder to use right now, waiting out a restart if the service has died.
    /// </summary>
    private async Task<IBinder?> LiveBinderAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task<IBinder?> waiter;
        lock (_gate)
        {
            if (_live is not null) return _live;
            waiter = _bound.Task;
        }

        return await WaitOrNull(waiter, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops a binder that has just thrown <see cref="DeadObjectException"/>, so the
    /// next caller waits for the reconnect rather than reusing it.
    /// </summary>
    /// <remarks>
    /// DONE FROM THE FAILURE, NOT ONLY FROM OnServiceDisconnected, because the
    /// callback can arrive after the transact that discovered the death - and a
    /// caller that has already been handed the dead binder would otherwise burn its
    /// one retry on the same corpse.
    /// <para>
    /// The reference check matters: by the time a slow caller reports its failure
    /// the service may already be back and _live may hold the NEW binder. Clearing
    /// it then would throw away a good connection on the strength of stale news.
    /// </para>
    /// </remarks>
    private void Invalidate(IBinder dead)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_live, dead)) return;
            _live = null;
            if (_bound.Task.IsCompleted)
                _bound = new TaskCompletionSource<IBinder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

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

        // THROUGH THE SAME HELPER AS EVERY OTHER CALLER, so the field is read under
        // the lock. OnServiceConnected arrives on the binder threadpool and can land
        // before this line does.
        var binder = await client.LiveBinderAsync(timeout ?? TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        if (binder is null) { client.Dispose(); return null; }
        return client;
    }

    /// <summary>Ask the shared brain one turn. Blocks internally until it answers,
    /// off the calling thread.</summary>
    public Task<LinkTurnReply> AskAsync(
        string sessionId, string message, bool agentic = false, CancellationToken ct = default)
        => TransactAsync(
            LinkIpc.TransactAsk,
            data => WriteMap(data, LinkTurnCodec.Encode(new LinkTurnRequest(sessionId, message, agentic))),
            reply => LinkTurnCodec.DecodeReply(ReadMap(reply)),
            LinkTurnReply.Failure,
            ct);

    /// <summary>
    /// One transaction, surviving the service being killed underneath it.
    /// </summary>
    /// <remarks>
    /// ALL THREE TRANSACTS GO THROUGH HERE so the recovery cannot be applied to two
    /// of them and forgotten on the third - which is exactly the shape of defect
    /// this repo keeps finding.
    /// <para>
    /// ONE RETRY, NOT A LOOP. A second DeadObjectException after a fresh bind means
    /// the service is dying on this specific work rather than being evicted, and
    /// retrying that forever is how a phone ends up in a kill-restart cycle with a
    /// person watching a spinner. One retry covers the eviction, which is the case
    /// that actually happens.
    /// </para>
    /// <para>
    /// The write happens INSIDE the retry, not once outside it: a Parcel is consumed
    /// by a transact and cannot be sent twice.
    /// </para>
    /// </remarks>
    private async Task<T> TransactAsync<T>(
        int code,
        Action<Parcel> write,
        Func<Parcel, T> read,
        Func<string, T> failure,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var binder = await LiveBinderAsync(RestartGrace, ct).ConfigureAwait(false);
            if (binder is null)
                return failure("Circle AI is still starting up. Ask again in a moment.");

            var outcome = await Task.Run(() =>
            {
                var data = Parcel.Obtain();
                var reply = Parcel.Obtain();
                try
                {
                    data!.WriteInterfaceToken(LinkIpc.Descriptor);
                    write(data);
                    binder.Transact(code, data, reply, (TransactionFlags)0);
                    reply!.ReadException();
                    return (Value: read(reply), Dead: false);
                }
                catch (global::Android.OS.DeadObjectException)
                {
                    // Not reported yet - the caller gets an answer from the retry.
                    return (Value: default(T)!, Dead: true);
                }
                catch (Exception ex) { return (Value: failure(Why(ex)), Dead: false); }
                finally { data?.Recycle(); reply?.Recycle(); }
            }, ct).ConfigureAwait(false);

            if (!outcome.Dead) return outcome.Value;

            Invalidate(binder);

            if (attempt > 0)
            {
                global::Android.Util.Log.Warn("CircleAI.Link",
                    "the service died twice on the same call; not retrying again");
                return failure("Circle AI could not answer just now. Ask again.");
            }

            global::Android.Util.Log.Info("CircleAI.Link",
                "the service was closed by the phone; waiting for it to come back and asking again");
        }
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
            // THIS SHOULD NO LONGER REACH A PERSON. TransactAsync catches
            // DeadObjectException before here, drops the dead binder, waits for
            // Bind.AutoCreate to bring the service back and asks again - so a
            // kill is absorbed rather than reported.
            //
            // The sentence that used to live here was
            // "Circle AI stopped — this phone closed it. Ask again and it will
            // start up." It was WRONG TWICE: the client never rebound, so asking
            // again did the same thing forever; and it put the restart on the
            // person, who then got a "Turn it on" button under it. Nobody asks
            // someone to turn their brain on before speaking to them.
            global::Android.OS.DeadObjectException =>
                "Circle AI is still starting up. Ask again in a moment.",

            // Over the binder's ~1 MB budget, which is shared across the process.
            global::Android.OS.TransactionTooLargeException =>
                "That was too big to send to Circle AI. Try a shorter one.",

            _ => "Circle AI could not answer just now. Ask again.",
        };
    }

    /// <summary>Transacts one structured verb. Mirrors <see cref="AskAsync"/>: blocks
    /// internally, off the calling thread.</summary>
    private Task<LinkRowsReply> VerbAsync(LinkVerbRequest req, CancellationToken ct)
        => TransactAsync(
            LinkIpc.TransactVerb,
            data => WriteMap(data, LinkVerbCodec.Encode(req)),
            reply => LinkVerbCodec.DecodeReply(ReadMap(reply)),
            LinkRowsReply.Failure,
            ct);

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

        // A WRITE THAT REFUSES MUST NOT LOOK LIKE A TRANSACT THAT FAILED. The codec
        // can decline before anything is sent; that is this caller's mistake and is
        // not retryable, so it is carried out as a value rather than an exception.
        string? writeRefusal = null;

        var result = await TransactAsync(
            LinkIpc.TransactAudio,
            data =>
            {
                if (!LinkAudioCodec.TryWriteRequest(new ParcelWriter(data), req, out var why))
                    writeRefusal = why;
            },
            reply => LinkAudioCodec.ReadReply(new ParcelReader(reply)),
            LinkAudioReply.Failure,
            ct).ConfigureAwait(false);

        return writeRefusal is not null ? LinkAudioReply.Failure(writeRefusal) : result;
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

    /// <remarks>
    /// CALLED AGAIN ON EVERY RESTART, which is the whole point. Bind.AutoCreate
    /// keeps the binding alive across the service process dying, so Android brings
    /// it back and calls this a second, third, nth time. The old body was
    /// <c>_bound.TrySetResult(service)</c> - a no-op once the first binder had
    /// landed - so every one of those restarts was discarded.
    /// </remarks>
    public void OnServiceConnected(ComponentName? name, IBinder? service)
    {
        lock (_gate)
        {
            _live = service;

            // A completed waiter cannot carry the new binder, so it is replaced
            // before being completed. Anyone already awaiting the old one is
            // released by the TrySetResult below it.
            if (_bound.Task.IsCompleted)
                _bound = new TaskCompletionSource<IBinder?>(TaskCreationOptions.RunContinuationsAsynchronously);

            _bound.TrySetResult(service);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A FRESH, UNCOMPLETED WAITER - not a null result. Completing with null would
    /// make every later call fail fast with "not connected" during the second or
    /// two before Android restarts the service, which is the same unhelpful answer
    /// in different words. An uncompleted waiter makes the next caller WAIT for the
    /// restart, up to <see cref="RestartGrace"/>, and then answer normally.
    /// </remarks>
    public void OnServiceDisconnected(ComponentName? name)
    {
        lock (_gate)
        {
            _live = null;
            _bound = new TaskCompletionSource<IBinder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

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
