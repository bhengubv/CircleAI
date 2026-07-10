// security_aethernet_mapper.go
//
// Ports CircleAI.Security.AetherNet.AetherMapper (AetherMapper.cs) — the static
// translation helpers between the Aether-specific types (aether_events.go,
// aether_contracts.go) and the transport-agnostic Peer types
// (security_peer_types.go).
//
// The C# type is an internal static class of switch expressions. Go has no
// package-private-across-files distinction here (all one package), so these are
// package-level funcs prefixed aetherMap* to keep them out of the exported
// surface while remaining callable from the bridge/adapter files.
//
// Every mapping is an explicit switch with the same default arm the C# uses.

package circleai

// aetherMapToPeerEventKind translates AetherSecurityEventKind →
// PeerSecurityEventKind. Ports AetherMapper.ToPeerEventKind.
func aetherMapToPeerEventKind(kind AetherSecurityEventKind) PeerSecurityEventKind {
	switch kind {
	case AetherSecurityEventKindNodeAuthAttempt:
		return PeerSecurityEventKindAuthAttempt
	case AetherSecurityEventKindRoutingAnomaly:
		return PeerSecurityEventKindRoutingAnomaly
	case AetherSecurityEventKindNodeBehaviourChange:
		return PeerSecurityEventKindBehaviourChange
	case AetherSecurityEventKindEncryptionEvent:
		return PeerSecurityEventKindEncryptionEvent
	case AetherSecurityEventKindIntrusionSignal:
		return PeerSecurityEventKindIntrusionSignal
	case AetherSecurityEventKindPrivilegeAttempt:
		return PeerSecurityEventKindPrivilegeAttempt
	default:
		return PeerSecurityEventKindUnknown
	}
}

// aetherMapToPeerThreatLevel translates AetherThreatLevel → PeerThreatLevel.
// Ports AetherMapper.ToPeerThreatLevel.
func aetherMapToPeerThreatLevel(level AetherThreatLevel) PeerThreatLevel {
	switch level {
	case AetherThreatLevelNone:
		return PeerThreatLevelNone
	case AetherThreatLevelLow:
		return PeerThreatLevelLow
	case AetherThreatLevelMedium:
		return PeerThreatLevelMedium
	case AetherThreatLevelHigh:
		return PeerThreatLevelHigh
	case AetherThreatLevelCritical:
		return PeerThreatLevelCritical
	default:
		return PeerThreatLevelNone
	}
}

// aetherMapToAetherThreatLevel translates PeerThreatLevel → AetherThreatLevel.
// Ports AetherMapper.ToAetherThreatLevel.
func aetherMapToAetherThreatLevel(level PeerThreatLevel) AetherThreatLevel {
	switch level {
	case PeerThreatLevelNone:
		return AetherThreatLevelNone
	case PeerThreatLevelLow:
		return AetherThreatLevelLow
	case PeerThreatLevelMedium:
		return AetherThreatLevelMedium
	case PeerThreatLevelHigh:
		return AetherThreatLevelHigh
	case PeerThreatLevelCritical:
		return AetherThreatLevelCritical
	default:
		return AetherThreatLevelNone
	}
}

// aetherMapToSecurityDirectiveKind translates PeerDirectiveKind →
// SecurityDirectiveKind. Ports AetherMapper.ToSecurityDirectiveKind. The default
// arm maps to ElevateMonitoring, matching the C# reference.
func aetherMapToSecurityDirectiveKind(kind PeerDirectiveKind) SecurityDirectiveKind {
	switch kind {
	case PeerDirectiveKindElevateMonitoring:
		return SecurityDirectiveKindElevateMonitoring
	case PeerDirectiveKindAvoidNode:
		return SecurityDirectiveKindAvoidNode
	case PeerDirectiveKindQuarantineNode:
		return SecurityDirectiveKindQuarantineNode
	case PeerDirectiveKindReleaseNode:
		return SecurityDirectiveKindReleaseNode
	default:
		return SecurityDirectiveKindElevateMonitoring
	}
}
