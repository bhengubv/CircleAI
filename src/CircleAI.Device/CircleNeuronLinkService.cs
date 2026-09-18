// CircleNeuronLinkService.cs
//
// The cross-app door to the shared brain.
//
// CircleNeuronService owns the models in one process; this exported service is
// the only thing that lets a DIFFERENT app reach them. Exported is not trust:
// every incoming turn is put through the LinkGate/LinkAuthorizer, which
// authorizes the caller by the OS-reported package + signing certificate
// (first-party auto, third-party device-auth) before a single token is served.
//
// LOW-LEVEL BINDER, ON PURPOSE. A Messenger is easier but delivers the message
// AFTER the binder transaction returns, so Binder.CallingUid is gone by the time
// the handler runs and the caller cannot be identified. Binder.OnTransact runs
// INSIDE the transaction, where the uid is reliable. The wire is a hand-marshalled
// string map (LinkTurnCodec) behind an enforced interface token (LinkIpc.Descriptor).

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using CircleAI.Aether;
using CircleAI.Hosting.Chat;
using CircleAI.Linking;
using Java.Security;

namespace CircleAI.Device;

/// <summary>
/// Exported bound service that lets another app use the shared brain, gated by a
/// link grant approved with device auth. Delegates the actual turn to the
/// resident <see cref="CircleNeuronService"/>.
/// </summary>
[Service(Name = "ai.circle.CircleNeuronLinkService", Exported = true)]
[IntentFilter(new[] { LinkIpc.BindAction })]
public sealed class CircleNeuronLinkService : Service
{
    private const string Tag = "CircleAI.Link";

    /// <summary>Where standing grants live. The host sets this before first bind.</summary>
    public static ILinkGrantStore? Grants { get; set; }

    /// <summary>Signing digests trusted without a prompt (our own apps, same key).</summary>
    public static IReadOnlySet<string>? FirstPartySignatures { get; set; }

    /// <summary>The device-auth gate (biometric / PIN). The host sets this.</summary>
    public static IAuthChallenge? Auth { get; set; }

    /// <summary>How long a minted grant lives. Zero = does not expire.</summary>
    public static TimeSpan GrantLifetime { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public override IBinder OnBind(Intent? intent) => new LinkBinder(this);

    // ── the transaction ──────────────────────────────────────────────────────

    private sealed class LinkBinder : Binder
    {
        private readonly CircleNeuronLinkService _service;
        public LinkBinder(CircleNeuronLinkService service) => _service = service;

        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            if (code != LinkIpc.TransactAsk || data is null)
                return base.OnTransact(code, data, reply, flags);

            // Refuse a transact meant for another interface.
            data.EnforceInterface(LinkIpc.Descriptor);

            // Reliable HERE, inside the transaction: who is calling us.
            var uid = Binder.CallingUid;
            var request = ReadMap(data);

            LinkTurnReply result;
            try { result = _service.ServeAsync(uid, request).GetAwaiter().GetResult(); }
            catch (Exception ex) { result = LinkTurnReply.Failure(ex.Message); }

            reply?.WriteNoException();
            WriteMap(reply, LinkTurnCodec.Encode(result));
            return true;
        }
    }

    // ── authorize, then serve ────────────────────────────────────────────────

    private async Task<LinkTurnReply> ServeAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var turn = LinkTurnCodec.TryDecodeRequest(requestMap);
        if (turn is null) return LinkTurnReply.Failure("no message");

        var identity = CallerIdentity(uid);
        if (identity is null) return LinkTurnReply.Failure("unknown caller");

        var grants = Grants;
        if (grants is null) return LinkTurnReply.Failure("linking not available");

        var gate = new LinkGate(grants, FirstPartySignatures);
        var authorizer = new LinkAuthorizer(grants, gate, GrantLifetime);

        var request = new LinkRequest(identity.Value.Package, identity.Value.Signature, LinkScope.Chat);
        var grant = await authorizer
            .AuthorizeAsync(request, RunDeviceAuthAsync, DateTimeOffset.UtcNow)
            .ConfigureAwait(false);
        if (grant is null) return LinkTurnReply.Failure("not authorized");

        // Make sure the resident brain is up. A cross-app caller may be the first
        // thing to touch it, and a cold model load is seconds long, so if it is
        // not ready yet we say so rather than block the binder for half a minute.
        try { CircleNeuronService.Start(this); } catch (Exception ex) { Log.Warn(Tag, "start: " + ex.Message); }

        var node = CircleNeuronService.Node;
        if (node is null || !node.IsReady)
            return LinkTurnReply.Failure("brain warming up, try again shortly");

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", turn.Message) })
                           .ConfigureAwait(false))
            sb.Append(chunk);

        return LinkTurnReply.Success(sb.ToString().Trim());
    }

    private static async Task<bool> RunDeviceAuthAsync(CancellationToken ct)
    {
        var auth = Auth;
        if (auth is null) return false;   // no gate wired => nothing may be approved
        var result = await auth.ChallengeAsync(
            AuthChallengeReason.AppLinkRequest,
            AuthMethod.BiometricAndDeviceAdmin,
            "Allow this app to use your Circle AI?",
            ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    // ── caller identity from the OS, never from the caller ───────────────────

    private (string Package, string Signature)? CallerIdentity(int uid)
    {
        var pm = PackageManager;
        var packages = pm?.GetPackagesForUid(uid);
        if (pm is null || packages is null || packages.Length == 0) return null;

        var package = packages[0]!;
        var signature = SignatureDigest(pm, package);
        return signature is null ? null : (package, signature);
    }

    private static string? SignatureDigest(PackageManager pm, string package)
    {
        try
        {
            byte[]? first = null;
            if ((int)Build.VERSION.SdkInt >= 28)
            {
                var info = pm.GetPackageInfo(package, PackageInfoFlags.SigningCertificates);
                var signers = info?.SigningInfo?.GetApkContentsSigners();
                if (signers is { Length: > 0 }) first = signers[0].ToByteArray();
            }
            else
            {
#pragma warning disable CS0618 // GetSignatures/Signatures deprecated; still the only path < API 28
                var info = pm.GetPackageInfo(package, PackageInfoFlags.Signatures);
                var sigs = info?.Signatures;
#pragma warning restore CS0618
                if (sigs is { Count: > 0 }) first = sigs[0].ToByteArray();
            }

            if (first is null) return null;
            var sha = MessageDigest.GetInstance("SHA-256")!;
            return Convert.ToHexString(sha.Digest(first)!);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "signature: " + ex.Message);
            return null;
        }
    }

    // ── parcel <-> string map (mirror of the client's writer) ────────────────

    private static IReadOnlyDictionary<string, string> ReadMap(Parcel data)
    {
        var count = data.ReadInt();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = data.ReadString();
            var value = data.ReadString();
            if (key is not null) map[key] = value ?? string.Empty;
        }
        return map;
    }

    private static void WriteMap(Parcel? reply, IReadOnlyDictionary<string, string> map)
    {
        if (reply is null) return;
        reply.WriteInt(map.Count);
        foreach (var pair in map)
        {
            reply.WriteString(pair.Key);
            reply.WriteString(pair.Value);
        }
    }
}
