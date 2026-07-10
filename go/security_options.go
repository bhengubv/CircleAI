// security_options.go
//
// Ports CircleAI.Security.SecurityOptions (SecurityOptions.cs).
//
// Configuration model for the AI Security Layer. All threshold values are trust
// scores in the [0, 1] range; lower score = more compromised. Thresholds must
// satisfy: QuarantineThreshold < AvoidNodeThreshold < ElevateMonitoringThreshold.

package circleai

import "time"

// SecurityOptions configures thresholds, decay rates, and event retention for
// the AI Security Layer. Pass to NodeTrustRegistry and SecurityLayerService.
// Construct with NewSecurityOptions to obtain the C# defaults.
type SecurityOptions struct {
	// ElevateMonitoringThreshold is the trust score below which monitoring is
	// elevated for the node. Default 0.75 — a 25% trust loss triggers closer
	// observation.
	ElevateMonitoringThreshold float64
	// AvoidNodeThreshold is the trust score below which the node is excluded
	// from routing. Default 0.50 — half trust lost → route around the node.
	AvoidNodeThreshold float64
	// QuarantineThreshold is the trust score at or below which the node is
	// hard-blocked (quarantined). Default 0.25 — severe compromise → no traffic.
	QuarantineThreshold float64
	// RecoveryRatePerSecond is passive trust recovery per second when no adverse
	// events occur. Default 0.001 ≈ full recovery from zero in ~16 minutes of
	// clean behaviour.
	RecoveryRatePerSecond float64
	// EventWindow is the sliding window used for pattern-based indicator
	// detection (e.g. repeated auth attempts). Events outside this window are
	// ignored for pattern analysis. Default 5 minutes.
	EventWindow time.Duration
	// MaxEventsPerNode is the maximum security events retained per node; oldest
	// are dropped first. Default 100.
	MaxEventsPerNode int
	// InitialTrustScore is the trust score assigned to nodes on first
	// observation. Default 1.0 (full trust until evidence says otherwise).
	InitialTrustScore float64
}

// NewSecurityOptions returns SecurityOptions populated with the same defaults
// as the C# object-initializer property defaults.
func NewSecurityOptions() *SecurityOptions {
	return &SecurityOptions{
		ElevateMonitoringThreshold: 0.75,
		AvoidNodeThreshold:         0.50,
		QuarantineThreshold:        0.25,
		RecoveryRatePerSecond:      0.001,
		EventWindow:                5 * time.Minute,
		MaxEventsPerNode:           100,
		InitialTrustScore:          1.0,
	}
}
