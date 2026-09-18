// CircleNeuronLinkService.cs
//
// The cross-app door to the shared brain.
//
// CircleNeuronService owns the models in one process; this exported service is
// the only thing that lets a DIFFERENT app reach them. Exported is not trust:
// every incoming turn is judged by the LinkGate/LinkAuthorizer against the
// OS-reported package + signing certificate before a single token is served.
//
// THIS SERVICE NEVER PROMPTS. A biometric sheet cannot be shown from a background
// service, so the approval happens in LinkConsentActivity — launched by the
// foreground client — which mints the grant into the same store this reads. Here,
// a caller either already has a grant (serve) or does not (told to link first).
//
// LOW-LEVEL BINDER, ON PURPOSE. A Messenger delivers the message AFTER the binder
// transaction returns, so Binder.CallingUid is gone by the time the handler runs.
// Binder.OnTransact runs INSIDE the transaction, where the uid is reliable. The
// wire is a hand-marshalled string map (LinkTurnCodec) behind an enforced token.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using CircleAI.Hosting.Chat;
using CircleAI.Linking;
using Java.Security;

namespace CircleAI.Device;

/// <summary>Exported bound service that serves the shared brain to a linked app.</summary>
[Service(Name = "ai.circle.CircleNeuronLinkService", Exported = true)]
[IntentFilter(new[] { LinkIpc.BindAction })]
public sealed class CircleNeuronLinkService : Service
{
    private const string Tag = "CircleAI.Link";

    /// <summary>Where standing grants live. The host sets this before first bind.</summary>
    public static ILinkGrantStore? Grants { get; set; }

    /// <summary>Signing digests trusted without a prompt (our own apps, same key).</summary>
    public static IReadOnlySet<string>? FirstPartySignatures { get; set; }

    /// <summary>How long a minted grant lives. Zero = does not expire.</summary>
    public static TimeSpan GrantLifetime { get; set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public override IBinder OnBind(Intent? intent) => new LinkBinder(this);

    private sealed class LinkBinder : Binder
    {
        private readonly CircleNeuronLinkService _service;
        public LinkBinder(CircleNeuronLinkService service) => _service = service;

        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            if (code != LinkIpc.TransactAsk || data is null)
                return base.OnTransact(code, data, reply, flags);

            data.EnforceInterface(LinkIpc.Descriptor);

            var uid = Binder.CallingUid;   // reliable inside the transaction
            var request = ReadMap(data);

            LinkTurnReply result;
            try { result = _service.ServeAsync(uid, request).GetAwaiter().GetResult(); }
            catch (Exception ex) { result = LinkTurnReply.Failure(ex.Message); }

            reply?.WriteNoException();
            WriteMap(reply, LinkTurnCodec.Encode(result));
            return true;
        }
    }

    private async Task<LinkTurnReply> ServeAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var turn = LinkTurnCodec.TryDecodeRequest(requestMap);
        if (turn is null) return LinkTurnReply.Failure("no message");

        var identity = CallerIdentity(uid);
        if (identity is null) return LinkTurnReply.Failure("unknown caller");

        var grants = Grants;
        if (grants is null) return LinkTurnReply.Failure("linking not available");

        // NEVER PROMPT HERE. First-party callers auto-mint; everyone else must have
        // approved the link already via LinkConsentActivity, so the auth callback
        // just says no and an un-approved caller is told to link first.
        var gate = new LinkGate(grants, FirstPartySignatures);
        var authorizer = new LinkAuthorizer(grants, gate, GrantLifetime);
        var grant = await authorizer.AuthorizeAsync(
            new LinkRequest(identity.Value.Package, identity.Value.Signature, LinkScope.Chat),
            static _ => Task.FromResult(false),
            DateTimeOffset.UtcNow).ConfigureAwait(false);
        if (grant is null) return LinkTurnReply.Failure("not linked — approve in Circle AI first");

        // Make sure the resident brain is up; a cold model load is seconds long.
        try { CircleNeuronService.Start(this); }
        catch (Exception ex) { Log.Warn(Tag, "start: " + ex.Message); }

        var node = CircleNeuronService.Node;
        if (node is null || !node.IsReady)
            return LinkTurnReply.Failure("brain warming up, try again shortly");

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", turn.Message) })
                           .ConfigureAwait(false))
            sb.Append(chunk);

        return LinkTurnReply.Success(sb.ToString().Trim());
    }

    private (string Package, string Signature)? CallerIdentity(int uid)
    {
        var packages = PackageManager?.GetPackagesForUid(uid);
        if (packages is null || packages.Length == 0) return null;

        var package = packages[0]!;
        var signature = SignatureDigestOf(this, package);
        return signature is null ? null : (package, signature);
    }

    /// <summary>
    /// SHA-256 of a package's first signing certificate, or null. Shared with
    /// LinkConsentActivity so the service and the consent screen judge a caller's
    /// identity the same way.
    /// </summary>
    internal static string? SignatureDigestOf(Context context, string package)
    {
        try
        {
            var pm = context.PackageManager;
            if (pm is null) return null;

            byte[]? first = null;
            if ((int)Build.VERSION.SdkInt >= 28)
            {
                var info = pm.GetPackageInfo(package, PackageInfoFlags.SigningCertificates);
                var signers = info?.SigningInfo?.GetApkContentsSigners();
                if (signers is { Length: > 0 }) first = signers[0].ToByteArray();
            }
            else
            {
#pragma warning disable CS0618 // Signatures deprecated; still the only path < API 28
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
