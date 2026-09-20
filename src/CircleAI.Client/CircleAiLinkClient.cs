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
            catch (Exception ex) { return LinkTurnReply.Failure(ex.Message); }
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
            catch (Exception ex) { return LinkRowsReply.Failure(ex.Message); }
            finally { data?.Recycle(); reply?.Recycle(); }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
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
