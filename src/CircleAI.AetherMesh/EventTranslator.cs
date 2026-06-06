// ──────────────────────────────────────────────────────────────────────────
// EventTranslator
//
// Internal one-way mapping from AetherMesh.Extensibility.Events.* records
// into CircleAI.Aether.* records. Every AetherMesh event has a 1:1 CircleAI
// counterpart — they were designed in parallel — so this is pure record
// projection with enum re-mapping where the value sets differ.
//
// The translator NEVER references AetherMesh runtime services; it operates
// on the event records only. Keeps the dependency graph thin.
// ──────────────────────────────────────────────────────────────────────────

using AetherMesh.Extensibility.Events;
using CircleAI.Aether;

namespace CircleAI.AetherMesh;

internal static class EventTranslator
{
    public static AetherNodeEvent Translate(AetherMeshNodeEvent e) => new(
        e.NodeId,
        MapNodeKind(e.Kind),
        Translate(e.Health),
        e.OccurredAt);

    public static AetherNodeHealth Translate(AetherMeshNodeHealth h) =>
        new(h.TrustScore, h.IsReachable, h.Latency, h.HopCount);

    public static AetherTransportEvent Translate(AetherMeshTransportEvent e) => new(
        e.NodeId,
        MapTransportKind(e.Kind),
        MapTransport(e.Transport),
        e.Latency,
        e.PacketLossRate,
        e.OccurredAt);

    public static AetherRouteEvent Translate(AetherMeshRouteEvent e) => new(
        e.SourceNodeId,
        e.DestinationNodeId,
        e.Path,
        MapRouteKind(e.Kind),
        e.FailureReason,
        e.OccurredAt);

    public static AetherSecurityEvent Translate(AetherMeshSecurityEvent e) => new(
        e.NodeId,
        MapSecurityKind(e.Kind),
        MapThreatLevel(e.ThreatLevel),
        e.Description,
        e.Metadata,
        e.OccurredAt);

    public static AetherNetworkEvent Translate(AetherMeshNetworkEvent e) => new(
        MapNetworkKind(e.Kind),
        e.NodeCount,
        e.ActiveRouteCount,
        e.CongestionLevel,
        e.OccurredAt);

    // ── Enum mappings ─────────────────────────────────────────────────────

    private static AetherNodeEventKind MapNodeKind(AetherMeshNodeEventKind k) => k switch
    {
        AetherMeshNodeEventKind.Joined        => AetherNodeEventKind.Joined,
        AetherMeshNodeEventKind.Left          => AetherNodeEventKind.Left,
        AetherMeshNodeEventKind.HealthChanged => AetherNodeEventKind.HealthChanged,
        _ => AetherNodeEventKind.HealthChanged,
    };

    private static AetherTransportEventKind MapTransportKind(AetherMeshTransportEventKind k) => k switch
    {
        AetherMeshTransportEventKind.Selected         => AetherTransportEventKind.Selected,
        AetherMeshTransportEventKind.Changed          => AetherTransportEventKind.Changed,
        AetherMeshTransportEventKind.LatencyMeasured  => AetherTransportEventKind.LatencyMeasured,
        AetherMeshTransportEventKind.PacketLoss       => AetherTransportEventKind.PacketLoss,
        _ => AetherTransportEventKind.Selected,
    };

    // AetherMesh has more transports (Wi-Fi Direct, NearLink, HTTP relay);
    // CircleAI's enum is broader OS-classification. Fold related kinds.
    private static AetherTransportKind MapTransport(AetherMeshTransportKind k) => k switch
    {
        AetherMeshTransportKind.Bluetooth   => AetherTransportKind.Bluetooth,
        AetherMeshTransportKind.WiFi        => AetherTransportKind.WiFi,
        AetherMeshTransportKind.WiFiDirect  => AetherTransportKind.WiFi,
        AetherMeshTransportKind.LoRa        => AetherTransportKind.LoRa,
        AetherMeshTransportKind.NFC         => AetherTransportKind.NFC,
        AetherMeshTransportKind.NearLink    => AetherTransportKind.Unknown,
        AetherMeshTransportKind.HttpRelay   => AetherTransportKind.Cellular,
        _ => AetherTransportKind.Unknown,
    };

    private static AetherRouteEventKind MapRouteKind(AetherMeshRouteEventKind k) => k switch
    {
        AetherMeshRouteEventKind.Discovered => AetherRouteEventKind.Discovered,
        AetherMeshRouteEventKind.Changed    => AetherRouteEventKind.Changed,
        AetherMeshRouteEventKind.Failed     => AetherRouteEventKind.Failed,
        _ => AetherRouteEventKind.Changed,
    };

    private static AetherSecurityEventKind MapSecurityKind(AetherMeshSecurityEventKind k) => k switch
    {
        AetherMeshSecurityEventKind.NodeAuthAttempt      => AetherSecurityEventKind.NodeAuthAttempt,
        AetherMeshSecurityEventKind.RoutingAnomaly       => AetherSecurityEventKind.RoutingAnomaly,
        AetherMeshSecurityEventKind.NodeBehaviourChange  => AetherSecurityEventKind.NodeBehaviourChange,
        AetherMeshSecurityEventKind.EncryptionEvent      => AetherSecurityEventKind.EncryptionEvent,
        AetherMeshSecurityEventKind.IntrusionSignal      => AetherSecurityEventKind.IntrusionSignal,
        AetherMeshSecurityEventKind.PrivilegeAttempt     => AetherSecurityEventKind.PrivilegeAttempt,
        _ => AetherSecurityEventKind.RoutingAnomaly,
    };

    private static AetherNetworkEventKind MapNetworkKind(AetherMeshNetworkEventKind k) => k switch
    {
        AetherMeshNetworkEventKind.TopologyChanged     => AetherNetworkEventKind.TopologyChanged,
        AetherMeshNetworkEventKind.CongestionDetected  => AetherNetworkEventKind.CongestionDetected,
        AetherMeshNetworkEventKind.PartitionDetected   => AetherNetworkEventKind.PartitionDetected,
        _ => AetherNetworkEventKind.TopologyChanged,
    };

    public static AetherThreatLevel MapThreatLevel(AetherMeshThreatLevel l) => l switch
    {
        AetherMeshThreatLevel.None      => AetherThreatLevel.None,
        AetherMeshThreatLevel.Low       => AetherThreatLevel.Low,
        AetherMeshThreatLevel.Medium    => AetherThreatLevel.Medium,
        AetherMeshThreatLevel.High      => AetherThreatLevel.High,
        AetherMeshThreatLevel.Critical  => AetherThreatLevel.Critical,
        _ => AetherThreatLevel.None,
    };

    // Reverse direction — CircleAI directives back to AetherMesh.
    public static AetherMeshThreatLevel MapThreatLevel(AetherThreatLevel l) => l switch
    {
        AetherThreatLevel.None      => AetherMeshThreatLevel.None,
        AetherThreatLevel.Low       => AetherMeshThreatLevel.Low,
        AetherThreatLevel.Medium    => AetherMeshThreatLevel.Medium,
        AetherThreatLevel.High      => AetherMeshThreatLevel.High,
        AetherThreatLevel.Critical  => AetherMeshThreatLevel.Critical,
        _ => AetherMeshThreatLevel.None,
    };

    public static global::AetherMesh.Extensibility.SecurityDirectiveKind MapDirectiveKind(SecurityDirectiveKind k) => k switch
    {
        SecurityDirectiveKind.UpdateNodeTrust      => global::AetherMesh.Extensibility.SecurityDirectiveKind.UpdateNodeTrust,
        SecurityDirectiveKind.AvoidNode            => global::AetherMesh.Extensibility.SecurityDirectiveKind.AvoidNode,
        SecurityDirectiveKind.QuarantineNode       => global::AetherMesh.Extensibility.SecurityDirectiveKind.QuarantineNode,
        SecurityDirectiveKind.ReleaseNode          => global::AetherMesh.Extensibility.SecurityDirectiveKind.ReleaseNode,
        SecurityDirectiveKind.RequestReauth        => global::AetherMesh.Extensibility.SecurityDirectiveKind.RequestReauth,
        SecurityDirectiveKind.ElevateMonitoring    => global::AetherMesh.Extensibility.SecurityDirectiveKind.ElevateMonitoring,
        _ => global::AetherMesh.Extensibility.SecurityDirectiveKind.UpdateNodeTrust,
    };
}
