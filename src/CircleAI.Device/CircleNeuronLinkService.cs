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
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using CircleAI.Hosting.Chat;
using CircleAI.Linking;
using CircleAI.Memory;
using CircleAI.Skills;
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

    /// <summary>
    /// The person's long-term memory, for the recall / remember verbs. The host wires
    /// this to the SAME store the app itself uses, so a linked app recalls and writes
    /// the person's real memory — not a second copy. Null means the memory verbs
    /// answer "memory not available" rather than inventing a store.
    /// </summary>
    public static IMemoryService? Memory { get; set; }

    /// <summary>
    /// The skill library for the skills verb. Defaults to the built-in consumer pack
    /// (a prebuilt database, no model), so the verb works with zero host wiring; a
    /// host may set its own store before first bind.
    /// </summary>
    public static ISkillStore? Skills { get; set; }

    /// <summary>
    /// The capability catalogue for the discovery verb. Defaults to the honest
    /// embedded manifest, so "what can you do" answers from fact with no wiring.
    /// </summary>
    public static ICapabilityCatalog? Catalog { get; set; }

    /// <inheritdoc/>
    public override IBinder OnBind(Intent? intent) => new LinkBinder(this);

    private sealed class LinkBinder : Binder
    {
        private readonly CircleNeuronLinkService _service;
        public LinkBinder(CircleNeuronLinkService service) => _service = service;

        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            if (data is null || (code != LinkIpc.TransactAsk && code != LinkIpc.TransactVerb))
                return base.OnTransact(code, data, reply, flags);

            data.EnforceInterface(LinkIpc.Descriptor);

            var uid = Binder.CallingUid;   // reliable inside the transaction
            var request = ReadMap(data);
            reply?.WriteNoException();

            if (code == LinkIpc.TransactAsk)
            {
                LinkTurnReply result;
                try { result = _service.ServeAskAsync(uid, request).GetAwaiter().GetResult(); }
                catch (Exception ex) { result = LinkTurnReply.Failure(ex.Message); }
                WriteMap(reply, LinkTurnCodec.Encode(result));
            }
            else   // LinkIpc.TransactVerb
            {
                LinkRowsReply result;
                try { result = _service.ServeVerbAsync(uid, request).GetAwaiter().GetResult(); }
                catch (Exception ex) { result = LinkRowsReply.Failure(ex.Message); }
                WriteMap(reply, LinkVerbCodec.Encode(result));
            }
            return true;
        }
    }

    /// <summary>
    /// Judge a caller for a given scope, never prompting. Returns null when the
    /// caller may proceed, or the reason it may not.
    /// </summary>
    /// <remarks>
    /// NEVER PROMPT HERE. First-party callers auto-mint; everyone else must have
    /// approved the link already via LinkConsentActivity, so the auth callback just
    /// says no and an un-approved caller is told to link first — for the exact scope
    /// the verb needs, so a Chat-only grant cannot reach memory or the library.
    /// </remarks>
    private async Task<string?> AuthorizeAsync(int uid, LinkScope required)
    {
        var identity = CallerIdentity(uid);
        if (identity is null) return "unknown caller";

        var grants = Grants;
        if (grants is null) return "linking not available";

        var gate = new LinkGate(grants, FirstPartySignatures);
        var authorizer = new LinkAuthorizer(grants, gate, GrantLifetime);
        var grant = await authorizer.AuthorizeAsync(
            new LinkRequest(identity.Value.Package, identity.Value.Signature, required),
            static _ => Task.FromResult(false),
            DateTimeOffset.UtcNow).ConfigureAwait(false);

        return grant is null ? $"not linked for {required} — approve in Circle AI first" : null;
    }

    private async Task<LinkTurnReply> ServeAskAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var turn = LinkTurnCodec.TryDecodeRequest(requestMap);
        if (turn is null) return LinkTurnReply.Failure("no message");

        var denied = await AuthorizeAsync(uid, LinkScope.Chat).ConfigureAwait(false);
        if (denied is not null) return LinkTurnReply.Failure(denied);

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

    /// <summary>
    /// Serve a structured verb — recall / remember (memory), skills (library),
    /// capabilities (discovery). Each is gated on its own scope; the model-free
    /// verbs answer instantly, so a linked app reaches the same memory, skills, and
    /// honest self-catalogue it would get in-process.
    /// </summary>
    private async Task<LinkRowsReply> ServeVerbAsync(int uid, IReadOnlyDictionary<string, string> requestMap)
    {
        var req = LinkVerbCodec.TryDecodeRequest(requestMap);
        if (req is null) return LinkRowsReply.Failure("unknown verb");

        var denied = await AuthorizeAsync(uid, LinkVerbs.RequiredScope(req.Verb)).ConfigureAwait(false);
        if (denied is not null) return LinkRowsReply.Failure(denied);

        switch (req.Verb)
        {
            case LinkVerb.Capabilities:
            {
                var catalog = Catalog ?? CapabilityCatalog.Default;
                var rows = catalog.All()
                    .Select(c => (IReadOnlyList<string>)new[] { c.Id, c.Status, c.Summary })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Skills:
            {
                var store = Skills ?? ConsumerSkillPack.Shared;
                var hits = string.IsNullOrWhiteSpace(req.Query)
                    ? await store.ListAsync().ConfigureAwait(false)
                    : await store.SearchAsync(req.Query!).ConfigureAwait(false);
                var rows = hits
                    .Select(s => (IReadOnlyList<string>)new[] { s.Id, s.Name })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Recall:
            {
                var memory = Memory;
                if (memory is null) return LinkRowsReply.Failure("memory not available");
                var result = await memory.RecallAsync(
                    new Situation(Text: req.Query ?? string.Empty),
                    new RecallBudget(MaxAtoms: req.Limit)).ConfigureAwait(false);
                var rows = result.Atoms
                    .Select(a => (IReadOnlyList<string>)new[] { a.Text })
                    .ToList();
                return LinkRowsReply.Success(rows);
            }

            case LinkVerb.Remember:
            {
                var memory = Memory;
                if (memory is null) return LinkRowsReply.Failure("memory not available");
                if (string.IsNullOrWhiteSpace(req.Text)) return LinkRowsReply.Failure("nothing to remember");
                await memory.RememberAsync(new MemoryAtom { Text = req.Text!, Subject = req.Subject })
                    .ConfigureAwait(false);
                return LinkRowsReply.Success(Array.Empty<IReadOnlyList<string>>());
            }

            default:
                return LinkRowsReply.Failure("unknown verb");
        }
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
