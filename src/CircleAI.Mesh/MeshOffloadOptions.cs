// MeshOffloadOptions.cs
//
// Every tunable the router / client / broadcaster reads. Bound through
// IOptions<MeshOffloadOptions>; the AddCircleAiMeshOffload(...) extension lets a
// host configure it. Defaults are chosen so the mesh works out of the box on a
// LAN / hotspot with a 30s freshness window and a 15s advert cadence.

using CircleAI.AetherNet;

namespace CircleAI.Mesh;

/// <summary>
/// Configuration for the CircleAI mesh offload router, client, and advert beacon.
/// </summary>
public sealed class MeshOffloadOptions
{
    /// <summary>
    /// This node's stable identifier on the mesh. Used as the payload source id
    /// and the reply-to address. Defaults to a random id; a host SHOULD set this
    /// to its durable node identity (e.g. its AetherTag) so peers dedupe and
    /// address it consistently across process restarts.
    /// </summary>
    public string LocalNodeId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// How fresh a peer advertisement must be to be considered. Passed straight
    /// to <c>IMeshCapabilityRegistry.Find</c>. Default 30s - twice the default
    /// <see cref="BroadcastInterval"/> so a single dropped beacon does not expire
    /// a peer.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for a peer's completion before treating it as a failure
    /// and moving on. Default 30s - inference on a loaded peer can be slow.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many distinct peers to try before giving up and falling back locally.
    /// Default 2. Clamped to at least 1.
    /// </summary>
    public int MaxPeerAttempts { get; set; } = 2;

    /// <summary>
    /// Multiplier applied to the estimated KV need to compute the minimum spare
    /// budget a peer must advertise. Default 1.0 (no headroom). Raise it to bias
    /// toward peers with slack.
    /// </summary>
    public double KvHeadroomFactor { get; set; } = 1.0;

    /// <summary>
    /// Estimates the KV-cache tokens a turn needs, used as the <c>minFreeKv</c>
    /// filter. The default is a crude char/4 + max-output heuristic; inject a
    /// tokenizer-backed estimate for accuracy.
    /// </summary>
    public Func<OffloadTurn, int> EstimateKvTokens { get; set; }
        = static t => (t.Prompt.Length / 4) + t.MaxOutputTokens;

    /// <summary>
    /// Picks the best peer from the (already model- and budget-filtered)
    /// candidates. Default: strongest brain first (higher <c>DeviceTier</c>),
    /// tie-broken by lowest latency hint, then most spare KV. Returns null only
    /// when the list is empty.
    /// </summary>
    public Func<IReadOnlyList<MeshCapabilityAdvertisement>, MeshCapabilityAdvertisement?> SelectPeer { get; set; }
        = DefaultSelectPeer;

    /// <summary>
    /// Whether this node answers inbound offload requests from peers (i.e. lends
    /// its brain) using <see cref="ILocalInferenceFallback"/>. Default true; set
    /// false for a borrow-only node.
    /// </summary>
    public bool ServeInboundRequests { get; set; } = true;

    /// <summary>
    /// Maximum inbound peer requests served concurrently. Beyond this, the node
    /// replies "at capacity" so the requester falls back fast instead of
    /// stalling. Default 2. Clamped to at least 1.
    /// </summary>
    public int MaxConcurrentServed { get; set; } = 2;

    /// <summary>
    /// Whether the client should call <c>INetworkTransport.StartAsync</c> when it
    /// starts, if the transport is not already available. Default true. Set false
    /// when the host owns the transport lifecycle.
    /// </summary>
    public bool StartTransport { get; set; } = true;

    /// <summary>
    /// How often the advert beacon re-broadcasts our advertisement. Default 15s.
    /// Keep it below <see cref="StaleAfter"/> so peers never expire us.
    /// </summary>
    public TimeSpan BroadcastInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Supplies OUR current advertisement each beacon tick (loaded model, spare
    /// KV, tier, ...), or null when we currently have nothing to offer. When this
    /// itself is null the beacon does nothing - a borrow-only node never
    /// advertises. The beacon re-stamps <c>PeerId</c> to <see cref="LocalNodeId"/>
    /// and the timestamp to now before sending.
    /// </summary>
    public Func<MeshCapabilityAdvertisement?>? OurAdvertisement { get; set; }

    /// <summary>
    /// The default <see cref="SelectPeer"/>: strongest tier, then lowest latency,
    /// then most spare KV.
    /// </summary>
    public static MeshCapabilityAdvertisement? DefaultSelectPeer(
        IReadOnlyList<MeshCapabilityAdvertisement> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        MeshCapabilityAdvertisement? best = null;
        foreach (var c in candidates)
        {
            if (best is null) { best = c; continue; }

            int byTier = c.Tier.CompareTo(best.Tier);
            if (byTier > 0) { best = c; continue; }
            if (byTier < 0) { continue; }

            int cLatency = c.LatencyHintMs ?? int.MaxValue;
            int bLatency = best.LatencyHintMs ?? int.MaxValue;
            if (cLatency < bLatency) { best = c; continue; }
            if (cLatency > bLatency) { continue; }

            if (c.FreeKvTokens > best.FreeKvTokens) { best = c; }
        }
        return best;
    }
}
