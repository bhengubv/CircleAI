// ──────────────────────────────────────────────────────────────────────────
// EventTranslator
//
// Internal one-way mapping from AetherNet.Extensibility.Events.* records
// into CircleAI.Aether.* records. Every AetherNet event has a 1:1 CircleAI
// counterpart — they were designed in parallel — so this is pure record
// projection with enum re-mapping where the value sets differ.
//
// The translator NEVER references AetherNet runtime services; it operates
// on the event records only. Keeps the dependency graph thin.
// ──────────────────────────────────────────────────────────────────────────

using AetherNet.Extensibility.Events;
using CircleAI.Aether;

namespace CircleAI.AetherNet;

internal static class EventTranslator
{
    public static AetherNodeEvent Translate(AetherNetNodeEvent e) => new(
        e.NodeId,
        MapNodeKind(e.Kind),
        Translate(e.Health),
        e.OccurredAt);

    public static AetherNodeHealth Translate(AetherNetNodeHealth h) =>
        new(h.TrustScore, h.IsReachable, h.Latency, h.HopCount);

    public static AetherTransportEvent Translate(AetherNetTransportEvent e) => new(
        e.NodeId,
        MapTransportKind(e.Kind),
        MapTransport(e.Transport),
        e.Latency,
        e.PacketLossRate,
        e.OccurredAt);

    public static AetherRouteEvent Translate(AetherNetRouteEvent e) => new(
        e.SourceNodeId,
        e.DestinationNodeId,
        e.Path,
        MapRouteKind(e.Kind),
        e.FailureReason,
        e.OccurredAt);

    public static AetherSecurityEvent Translate(AetherNetSecurityEvent e) => new(
        e.NodeId,
        MapSecurityKind(e.Kind),
        MapThreatLevel(e.ThreatLevel),
        e.Description,
        e.Metadata,
        e.OccurredAt);

    public static AetherNetworkEvent Translate(AetherNetNetworkEvent e) => new(
        MapNetworkKind(e.Kind),
        e.NodeCount,
        e.ActiveRouteCount,
        e.CongestionLevel,
        e.OccurredAt);

    // ── Enum mappings ─────────────────────────────────────────────────────

    private static AetherNodeEventKind MapNodeKind(AetherNetNodeEventKind k) => k switch
    {
        AetherNetNodeEventKind.Joined        => AetherNodeEventKind.Joined,
        AetherNetNodeEventKind.Left          => AetherNodeEventKind.Left,
        AetherNetNodeEventKind.HealthChanged => AetherNodeEventKind.HealthChanged,
        _ => AetherNodeEventKind.HealthChanged,
    };

    private static AetherTransportEventKind MapTransportKind(AetherNetTransportEventKind k) => k switch
    {
        AetherNetTransportEventKind.Selected         => AetherTransportEventKind.Selected,
        AetherNetTransportEventKind.Changed          => AetherTransportEventKind.Changed,
        AetherNetTransportEventKind.LatencyMeasured  => AetherTransportEventKind.LatencyMeasured,
        AetherNetTransportEventKind.PacketLoss       => AetherTransportEventKind.PacketLoss,
        _ => AetherTransportEventKind.Selected,
    };

    // AetherNet has more transports (Wi-Fi Direct, NearLink, HTTP relay);
    // CircleAI's enum is broader OS-classification. Fold related kinds.
    private static AetherTransportKind MapTransport(AetherNetTransportKind k) => k switch
    {
        AetherNetTransportKind.Bluetooth   => AetherTransportKind.Bluetooth,
        AetherNetTransportKind.WiFi        => AetherTransportKind.WiFi,
        AetherNetTransportKind.WiFiDirect  => AetherTransportKind.WiFi,
        AetherNetTransportKind.LoRa        => AetherTransportKind.LoRa,
        AetherNetTransportKind.NFC         => AetherTransportKind.NFC,
        AetherNetTransportKind.NearLink    => AetherTransportKind.Unknown,
        AetherNetTransportKind.HttpRelay   => AetherTransportKind.Cellular,
        _ => AetherTransportKind.Unknown,
    };

    private static AetherRouteEventKind MapRouteKind(AetherNetRouteEventKind k) => k switch
    {
        AetherNetRouteEventKind.Discovered => AetherRouteEventKind.Discovered,
        AetherNetRouteEventKind.Changed    => AetherRouteEventKind.Changed,
        AetherNetRouteEventKind.Failed     => AetherRouteEventKind.Failed,
        _ => AetherRouteEventKind.Changed,
    };

    private static AetherSecurityEventKind MapSecurityKind(AetherNetSecurityEventKind k) => k switch
    {
        AetherNetSecurityEventKind.NodeAuthAttempt      => AetherSecurityEventKind.NodeAuthAttempt,
        AetherNetSecurityEventKind.RoutingAnomaly       => AetherSecurityEventKind.RoutingAnomaly,
        AetherNetSecurityEventKind.NodeBehaviourChange  => AetherSecurityEventKind.NodeBehaviourChange,
        AetherNetSecurityEventKind.EncryptionEvent      => AetherSecurityEventKind.EncryptionEvent,
        AetherNetSecurityEventKind.IntrusionSignal      => AetherSecurityEventKind.IntrusionSignal,
        AetherNetSecurityEventKind.PrivilegeAttempt     => AetherSecurityEventKind.PrivilegeAttempt,
        _ => AetherSecurityEventKind.RoutingAnomaly,
    };

    private static AetherNetworkEventKind MapNetworkKind(AetherNetNetworkEventKind k) => k switch
    {
        AetherNetNetworkEventKind.TopologyChanged     => AetherNetworkEventKind.TopologyChanged,
        AetherNetNetworkEventKind.CongestionDetected  => AetherNetworkEventKind.CongestionDetected,
        AetherNetNetworkEventKind.PartitionDetected   => AetherNetworkEventKind.PartitionDetected,
        _ => AetherNetworkEventKind.TopologyChanged,
    };

    public static AetherThreatLevel MapThreatLevel(AetherNetThreatLevel l) => l switch
    {
        AetherNetThreatLevel.None      => AetherThreatLevel.None,
        AetherNetThreatLevel.Low       => AetherThreatLevel.Low,
        AetherNetThreatLevel.Medium    => AetherThreatLevel.Medium,
        AetherNetThreatLevel.High      => AetherThreatLevel.High,
        AetherNetThreatLevel.Critical  => AetherThreatLevel.Critical,
        _ => AetherThreatLevel.None,
    };

    // Reverse direction — CircleAI directives back to AetherNet.
    public static AetherNetThreatLevel MapThreatLevel(AetherThreatLevel l) => l switch
    {
        AetherThreatLevel.None      => AetherNetThreatLevel.None,
        AetherThreatLevel.Low       => AetherNetThreatLevel.Low,
        AetherThreatLevel.Medium    => AetherNetThreatLevel.Medium,
        AetherThreatLevel.High      => AetherNetThreatLevel.High,
        AetherThreatLevel.Critical  => AetherNetThreatLevel.Critical,
        _ => AetherNetThreatLevel.None,
    };

    public static global::AetherNet.Extensibility.SecurityDirectiveKind MapDirectiveKind(SecurityDirectiveKind k) => k switch
    {
        SecurityDirectiveKind.UpdateNodeTrust      => global::AetherNet.Extensibility.SecurityDirectiveKind.UpdateNodeTrust,
        SecurityDirectiveKind.AvoidNode            => global::AetherNet.Extensibility.SecurityDirectiveKind.AvoidNode,
        SecurityDirectiveKind.QuarantineNode       => global::AetherNet.Extensibility.SecurityDirectiveKind.QuarantineNode,
        SecurityDirectiveKind.ReleaseNode          => global::AetherNet.Extensibility.SecurityDirectiveKind.ReleaseNode,
        SecurityDirectiveKind.RequestReauth        => global::AetherNet.Extensibility.SecurityDirectiveKind.RequestReauth,
        SecurityDirectiveKind.ElevateMonitoring    => global::AetherNet.Extensibility.SecurityDirectiveKind.ElevateMonitoring,
        _ => global::AetherNet.Extensibility.SecurityDirectiveKind.UpdateNodeTrust,
    };

    // Reverse — mesh → CircleAI direction (for AetherNetInboundDirectiveBridge).
    public static SecurityDirectiveKind MapDirectiveKind(global::AetherNet.Extensibility.SecurityDirectiveKind k) => k switch
    {
        global::AetherNet.Extensibility.SecurityDirectiveKind.UpdateNodeTrust      => SecurityDirectiveKind.UpdateNodeTrust,
        global::AetherNet.Extensibility.SecurityDirectiveKind.AvoidNode            => SecurityDirectiveKind.AvoidNode,
        global::AetherNet.Extensibility.SecurityDirectiveKind.QuarantineNode       => SecurityDirectiveKind.QuarantineNode,
        global::AetherNet.Extensibility.SecurityDirectiveKind.ReleaseNode          => SecurityDirectiveKind.ReleaseNode,
        global::AetherNet.Extensibility.SecurityDirectiveKind.RequestReauth        => SecurityDirectiveKind.RequestReauth,
        global::AetherNet.Extensibility.SecurityDirectiveKind.ElevateMonitoring    => SecurityDirectiveKind.ElevateMonitoring,
        _ => SecurityDirectiveKind.UpdateNodeTrust,
    };
}
