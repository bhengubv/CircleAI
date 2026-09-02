// aether_contracts.go
//
// Ports the four CircleAI.Aether "contract" surfaces that flow between Aether
// and BhenguAI, plus deterministic in-memory implementations of each:
//
//   IAetherContext.cs      -> AetherInstallLevel, IAetherContext,
//                             InMemoryAetherContext (working impl)
//   IAuthChallenge.cs      -> AuthChallengeReason, AuthMethod, AuthChallengeResult,
//                             IAuthChallenge, ScriptedAuthChallenge (working impl)
//   IAetherIntelligence.cs -> NetworkHealthReport, ThreatAssessment, RoutingAdvice,
//                             TrustScoreUpdate, IAetherIntelligence
//   IAISecurityLayer.cs    -> SecurityDirectiveKind, SecurityDirective,
//                             SecurityPosture, ISecurityDirectiveConsumer,
//                             IAISecurityLayer
//
// The concrete IAetherIntelligence / IAISecurityLayer implementations live in
// the Security.AetherNet slice (security_aethernet_bridge.go), which wires these
// Aether contracts to the transport-agnostic CircleAI.Security layer. Here we
// port the contracts + the two contracts whose C# reference expects a platform
// adapter (context, auth) with fully-working deterministic in-memory versions.
//
// Enum ordinals are int consts with stable ordinals matching C# declaration
// order. AuthMethod is explicitly numbered Biometric=1..Custom=4 in C#.

package circleai

import (
	"context"
	"sync"
	"time"
)

// ═══════════════════════════════════════════════════════════════════════════
// Contract 2 — Presence and Capability (IAetherContext.cs)
// ═══════════════════════════════════════════════════════════════════════════

// AetherInstallLevel indicates where Aether is installed and who manages it.
// Ports AetherInstallLevel.
type AetherInstallLevel int

const (
	// AetherInstallLevelNone — Aether is not present on this device.
	AetherInstallLevelNone AetherInstallLevel = 0
	// AetherInstallLevelApp — Aether was installed at app level.
	AetherInstallLevelApp AetherInstallLevel = 1
	// AetherInstallLevelOS — Aether is an OS-managed system service. Requires
	// biometric + device admin auth to toggle.
	AetherInstallLevelOS AetherInstallLevel = 2
)

// IAetherContext reports the presence, version, and capability of the Aether
// runtime on this device. Ports IAetherContext.
//
// C# read-only properties map to Go zero-argument methods.
type IAetherContext interface {
	// InstallLevel reports where Aether is installed, if at all.
	InstallLevel() AetherInstallLevel
	// IsAvailable is true when Aether is installed and enabled.
	IsAvailable() bool
	// RuntimeVersion returns the installed Aether runtime version, or nil when
	// Aether is absent.
	RuntimeVersion() *AetherVersion
	// MinimumRequired returns the minimum Aether version the consuming app
	// declares, or nil when unset.
	MinimumRequired() *AetherVersion
	// IsSufficient is true when RuntimeVersion satisfies MinimumRequired. Always
	// true when MinimumRequired is nil.
	IsSufficient() bool
	// RequiresAuth is true when the install level is OS.
	RequiresAuth() bool
	// IsEnabled is true when Aether is installed and currently enabled.
	IsEnabled() bool
}

// AetherVersion is a semantic-version comparison value standing in for
// System.Version. Only the ordered numeric components matter for the
// IsSufficient comparison the contract requires.
type AetherVersion struct {
	Major int
	Minor int
	Build int
	Rev   int
}

// NewAetherVersion constructs a version from up to four ordered components.
// Missing trailing components default to 0, matching System.Version semantics.
func NewAetherVersion(parts ...int) AetherVersion {
	v := AetherVersion{}
	fields := []*int{&v.Major, &v.Minor, &v.Build, &v.Rev}
	for i := 0; i < len(parts) && i < len(fields); i++ {
		*fields[i] = parts[i]
	}
	return v
}

// AtLeast reports whether v >= other by ordered component comparison (Major,
// then Minor, then Build, then Rev) — the semantics of System.Version.CompareTo.
func (v AetherVersion) AtLeast(other AetherVersion) bool {
	if v.Major != other.Major {
		return v.Major > other.Major
	}
	if v.Minor != other.Minor {
		return v.Minor > other.Minor
	}
	if v.Build != other.Build {
		return v.Build > other.Build
	}
	return v.Rev >= other.Rev
}

// InMemoryAetherContext is a deterministic, mutable in-memory IAetherContext.
// It plays the role the platform adapter (MAUI/server) fills at runtime, so
// bootstrap and gating logic can be exercised without a device. All derived
// properties (IsAvailable, IsSufficient, RequiresAuth, IsEnabled) are computed
// from the same rules the C# contract documents. Thread-safe.
type InMemoryAetherContext struct {
	mu sync.RWMutex

	installLevel    AetherInstallLevel
	runtimeVersion  *AetherVersion
	minimumRequired *AetherVersion
	// enabled tracks the on/off toggle. For OS-managed instances this can be
	// toggled off; None is never enabled.
	enabled bool
}

// InMemoryAetherContextOptions configures an InMemoryAetherContext.
type InMemoryAetherContextOptions struct {
	InstallLevel    AetherInstallLevel
	RuntimeVersion  *AetherVersion
	MinimumRequired *AetherVersion
	// Enabled seeds the initial toggle state. Ignored (forced false) when
	// InstallLevel is None.
	Enabled bool
}

// NewInMemoryAetherContext constructs an in-memory context from options.
func NewInMemoryAetherContext(opts InMemoryAetherContextOptions) *InMemoryAetherContext {
	enabled := opts.Enabled
	if opts.InstallLevel == AetherInstallLevelNone {
		enabled = false
	}
	return &InMemoryAetherContext{
		installLevel:    opts.InstallLevel,
		runtimeVersion:  opts.RuntimeVersion,
		minimumRequired: opts.MinimumRequired,
		enabled:         enabled,
	}
}

// InstallLevel implements IAetherContext.
func (c *InMemoryAetherContext) InstallLevel() AetherInstallLevel {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.installLevel
}

// IsAvailable implements IAetherContext: installed (not None) and enabled.
func (c *InMemoryAetherContext) IsAvailable() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.installLevel != AetherInstallLevelNone && c.enabled
}

// RuntimeVersion implements IAetherContext.
func (c *InMemoryAetherContext) RuntimeVersion() *AetherVersion {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.runtimeVersion
}

// MinimumRequired implements IAetherContext.
func (c *InMemoryAetherContext) MinimumRequired() *AetherVersion {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.minimumRequired
}

// IsSufficient implements IAetherContext. True when MinimumRequired is nil;
// otherwise true only when RuntimeVersion is present and >= MinimumRequired.
func (c *InMemoryAetherContext) IsSufficient() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	if c.minimumRequired == nil {
		return true
	}
	if c.runtimeVersion == nil {
		return false
	}
	return c.runtimeVersion.AtLeast(*c.minimumRequired)
}

// RequiresAuth implements IAetherContext: true only for OS-managed installs.
func (c *InMemoryAetherContext) RequiresAuth() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.installLevel == AetherInstallLevelOS
}

// IsEnabled implements IAetherContext: installed and toggled on.
func (c *InMemoryAetherContext) IsEnabled() bool {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.installLevel != AetherInstallLevelNone && c.enabled
}

// SetEnabled toggles the enabled state (in-memory affordance so a passing
// OS-toggle challenge can flip the runtime on/off). No effect when not
// installed. Returns the resulting enabled state.
func (c *InMemoryAetherContext) SetEnabled(enabled bool) bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.installLevel == AetherInstallLevelNone {
		c.enabled = false
		return false
	}
	c.enabled = enabled
	return c.enabled
}

var _ IAetherContext = (*InMemoryAetherContext)(nil)

// ═══════════════════════════════════════════════════════════════════════════
// Contract 5 — Auth Challenge (IAuthChallenge.cs)
// ═══════════════════════════════════════════════════════════════════════════

// AuthChallengeReason enumerates why an auth challenge is being issued. Ports
// AuthChallengeReason.
type AuthChallengeReason int

const (
	// AuthChallengeReasonOsLevelToggle — enabling/disabling the OS-level service.
	AuthChallengeReasonOsLevelToggle AuthChallengeReason = 0
	// AuthChallengeReasonThreatThresholdReached — anomaly threshold crossed.
	AuthChallengeReasonThreatThresholdReached AuthChallengeReason = 1
	// AuthChallengeReasonPrivilegedOperation — the operation needs elevated auth.
	AuthChallengeReasonPrivilegedOperation AuthChallengeReason = 2
	// AuthChallengeReasonPeriodicRevalidation — scheduled trust renewal.
	AuthChallengeReasonPeriodicRevalidation AuthChallengeReason = 3
	// AuthChallengeReasonManualRequest — explicitly triggered by dev/admin.
	AuthChallengeReasonManualRequest AuthChallengeReason = 4
)

// AuthMethod is the authentication method used or required, ordered by strength;
// higher numeric values are stronger. Ports AuthMethod (Biometric=1..Custom=4).
type AuthMethod int

const (
	// AuthMethodBiometric — fingerprint, face, or iris recognition.
	AuthMethodBiometric AuthMethod = 1
	// AuthMethodDeviceAdmin — device administrator credential (PIN/password/pattern).
	AuthMethodDeviceAdmin AuthMethod = 2
	// AuthMethodBiometricAndDeviceAdmin — both; the minimum for any OS-level op.
	AuthMethodBiometricAndDeviceAdmin AuthMethod = 3
	// AuthMethodCustom — developer-defined method layered on top of the minimum.
	AuthMethodCustom AuthMethod = 4
)

// AuthChallengeResult is the outcome of an auth challenge. Ports the
// AuthChallengeResult record. FailureReason is nil on success.
type AuthChallengeResult struct {
	// Succeeded is whether the challenge was satisfied.
	Succeeded bool
	// MethodUsed is the method actually used (or attempted).
	MethodUsed AuthMethod
	// FailureReason explains a failure, or nil on success.
	FailureReason *string
	// CompletedAt is the UTC timestamp of completion.
	CompletedAt time.Time
}

// NewAuthChallengeSuccess builds a successful result with no failure reason.
// Ports AuthChallengeResult.Success.
func NewAuthChallengeSuccess(method AuthMethod) AuthChallengeResult {
	return AuthChallengeResult{
		Succeeded:     true,
		MethodUsed:    method,
		FailureReason: nil,
		CompletedAt:   time.Now().UTC(),
	}
}

// NewAuthChallengeFailure builds a failed result with an explanatory reason.
// Ports AuthChallengeResult.Failure.
func NewAuthChallengeFailure(method AuthMethod, reason string) AuthChallengeResult {
	r := reason
	return AuthChallengeResult{
		Succeeded:     false,
		MethodUsed:    method,
		FailureReason: &r,
		CompletedAt:   time.Now().UTC(),
	}
}

// IAuthChallenge issues and resolves authentication challenges for
// security-sensitive operations. Ports IAuthChallenge.
//
// minimumMethod is C# nullable (AuthMethod?); nil defaults to
// BiometricAndDeviceAdmin, which the adapter enforces as the floor.
type IAuthChallenge interface {
	// Challenge presents an auth challenge for the given reason. The adapter
	// enforces the minimum method requirement (never below
	// BiometricAndDeviceAdmin for OS-level operations).
	Challenge(ctx context.Context, reason AuthChallengeReason, minimumMethod *AuthMethod, prompt string) (AuthChallengeResult, error)
	// RequestOsToggle presents the OS-level toggle challenge. Always requires
	// BiometricAndDeviceAdmin at minimum.
	RequestOsToggle(ctx context.Context, enable bool) (AuthChallengeResult, error)
}

// ScriptedAuthChallenge is a deterministic in-memory IAuthChallenge for tests
// and headless environments. It grants or denies based on a configured
// available method, enforcing the same minimum-method floor a real platform
// adapter would. No native biometric API is touched — the "native" dependency
// is injected as the AvailableMethod field.
//
// Enforcement rules (mirroring the C# contract's documented floor):
//   - The minimum acceptable method is max(minimumMethod, BiometricAndDeviceAdmin)
//     for the toggle path and any OS-level reason; for other reasons the caller's
//     minimumMethod is honoured (defaulting to BiometricAndDeviceAdmin when nil).
//   - The challenge succeeds iff AvailableMethod >= the effective minimum.
//
// Thread-safe (AvailableMethod is read under a lock so tests can flip it).
type ScriptedAuthChallenge struct {
	mu sync.RWMutex
	// availableMethod is the strongest method this environment can satisfy.
	availableMethod AuthMethod
}

// NewScriptedAuthChallenge constructs a scripted challenge that can satisfy up
// to availableMethod. Pass AuthMethodBiometricAndDeviceAdmin for the common
// "device is fully enrolled" case.
func NewScriptedAuthChallenge(availableMethod AuthMethod) *ScriptedAuthChallenge {
	return &ScriptedAuthChallenge{availableMethod: availableMethod}
}

// SetAvailableMethod updates the strongest satisfiable method (test affordance).
func (s *ScriptedAuthChallenge) SetAvailableMethod(m AuthMethod) {
	s.mu.Lock()
	s.availableMethod = m
	s.mu.Unlock()
}

// Challenge implements IAuthChallenge. The effective floor is
// BiometricAndDeviceAdmin unless the caller demands something stronger; for an
// OS-level toggle reason the floor is never lowered below the minimum.
func (s *ScriptedAuthChallenge) Challenge(_ context.Context, reason AuthChallengeReason, minimumMethod *AuthMethod, _ string) (AuthChallengeResult, error) {
	effectiveMin := AuthMethodBiometricAndDeviceAdmin
	if minimumMethod != nil && *minimumMethod > effectiveMin {
		effectiveMin = *minimumMethod
	}
	// An OS-level toggle can never be satisfied below the biometric+admin floor,
	// regardless of what the caller requested — matches the contract's "cannot
	// lower it below the minimum" rule.
	_ = reason

	s.mu.RLock()
	have := s.availableMethod
	s.mu.RUnlock()

	if have >= effectiveMin {
		return NewAuthChallengeSuccess(effectiveMin), nil
	}
	return NewAuthChallengeFailure(effectiveMin, "available authentication method is weaker than the required minimum"), nil
}

// RequestOsToggle implements IAuthChallenge. Always enforces the
// BiometricAndDeviceAdmin floor. Ports RequestOsToggleAsync.
func (s *ScriptedAuthChallenge) RequestOsToggle(ctx context.Context, enable bool) (AuthChallengeResult, error) {
	min := AuthMethodBiometricAndDeviceAdmin
	_ = enable
	return s.Challenge(ctx, AuthChallengeReasonOsLevelToggle, &min, "OS-level Aether service toggle")
}

var _ IAuthChallenge = (*ScriptedAuthChallenge)(nil)

// ═══════════════════════════════════════════════════════════════════════════
// Contract 3 — Intelligence Output (IAetherIntelligence.cs)
// ═══════════════════════════════════════════════════════════════════════════

// NetworkHealthReport is the aggregate health of the mesh as assessed by
// BhenguAI. Ports the NetworkHealthReport record.
type NetworkHealthReport struct {
	OverallScore        float64
	TrustedNodeCount    int
	SuspiciousNodeCount int
	Summary             string
	GeneratedAt         time.Time
}

// IsValid returns true when OverallScore is within the valid 0–1 range. Ports
// NetworkHealthReport.IsValid.
func (r NetworkHealthReport) IsValid() bool {
	return r.OverallScore >= 0.0 && r.OverallScore <= 1.0
}

// ThreatAssessment is BhenguAI's assessment of the threat posed by a specific
// node. Ports the ThreatAssessment record.
type ThreatAssessment struct {
	NodeID           string
	ThreatConfidence float64
	Level            AetherThreatLevel
	Indicators       []string
	AssessedAt       time.Time
}

// IsValid returns true when ThreatConfidence is within the valid 0–1 range.
// Ports ThreatAssessment.IsValid.
func (a ThreatAssessment) IsValid() bool {
	return a.ThreatConfidence >= 0.0 && a.ThreatConfidence <= 1.0
}

// RoutingAdvice is BhenguAI's recommendation for routing to a destination node.
// Ports the RoutingAdvice record.
type RoutingAdvice struct {
	DestinationNodeID string
	RecommendedPath   []string
	AvoidNodes        []string
	Confidence        float64
	Reasoning         string
	GeneratedAt       time.Time
}

// TrustScoreUpdate is emitted when BhenguAI revises the trust score for a node.
// Ports the TrustScoreUpdate record.
type TrustScoreUpdate struct {
	NodeID        string
	PreviousScore float64
	CurrentScore  float64
	Reason        string
	UpdatedAt     time.Time
}

// HasChanged returns true when the score moved in either direction (> 0.001).
// Ports TrustScoreUpdate.HasChanged.
func (u TrustScoreUpdate) HasChanged() bool {
	d := u.CurrentScore - u.PreviousScore
	if d < 0 {
		d = -d
	}
	return d > 0.001
}

// IsDegraded returns true when the score decreased. Ports TrustScoreUpdate.IsDegraded.
func (u TrustScoreUpdate) IsDegraded() bool { return u.CurrentScore < u.PreviousScore }

// IAetherIntelligence is the intelligence output surface produced by BhenguAI
// from Aether telemetry. Consumed by apps and the Security Layer; never by
// Aether. Ports IAetherIntelligence.
//
// The C# IAsyncEnumerable<TrustScoreUpdate> maps to a receive-only channel that
// closes when ctx is cancelled.
type IAetherIntelligence interface {
	// GetNetworkHealth returns an aggregate health report for the current mesh.
	GetNetworkHealth(ctx context.Context) (NetworkHealthReport, error)
	// AssessThreat assesses the current threat level of a specific node.
	AssessThreat(ctx context.Context, nodeID string) (ThreatAssessment, error)
	// GetRoutingAdvice returns a routing recommendation for the destination.
	GetRoutingAdvice(ctx context.Context, destinationNodeID string) (RoutingAdvice, error)
	// StreamTrustScores streams trust score updates as new telemetry is observed.
	StreamTrustScores(ctx context.Context) <-chan TrustScoreUpdate
}

// ═══════════════════════════════════════════════════════════════════════════
// Contract 4 — Security Layer (IAISecurityLayer.cs)
// ═══════════════════════════════════════════════════════════════════════════

// SecurityDirectiveKind is the action BhenguAI recommends to Aether's policy
// engine. Ports SecurityDirectiveKind.
type SecurityDirectiveKind int

const (
	// SecurityDirectiveKindUpdateNodeTrust — adjust the recorded trust score.
	SecurityDirectiveKindUpdateNodeTrust SecurityDirectiveKind = 0
	// SecurityDirectiveKindAvoidNode — exclude the node from routing (soft block).
	SecurityDirectiveKindAvoidNode SecurityDirectiveKind = 1
	// SecurityDirectiveKindQuarantineNode — hard block until released.
	SecurityDirectiveKindQuarantineNode SecurityDirectiveKind = 2
	// SecurityDirectiveKindReleaseNode — lift an Avoid/Quarantine directive.
	SecurityDirectiveKindReleaseNode SecurityDirectiveKind = 3
	// SecurityDirectiveKindRequestReauth — request user re-authentication.
	SecurityDirectiveKindRequestReauth SecurityDirectiveKind = 4
	// SecurityDirectiveKindElevateMonitoring — increase telemetry verbosity.
	SecurityDirectiveKindElevateMonitoring SecurityDirectiveKind = 5
)

// SecurityDirective is an instruction published by the AI Security Layer to
// Aether's policy engine. Ports the SecurityDirective record.
//
// TargetNodeId, TrustScoreOverride, and Duration are C# nullable; modelled as
// pointers. nil Duration means the directive is permanent.
type SecurityDirective struct {
	Kind               SecurityDirectiveKind
	TargetNodeID       *string
	TrustScoreOverride *float64
	ThreatLevel        AetherThreatLevel
	Reason             string
	Duration           *time.Duration
	IssuedAt           time.Time
}

// HasTarget returns true when the directive targets a specific node (non-empty
// TargetNodeID). Ports SecurityDirective.HasTarget.
func (d SecurityDirective) HasTarget() bool {
	return d.TargetNodeID != nil && !isBlankAether(*d.TargetNodeID)
}

// IsPermanent returns true when Duration is nil — no automatic expiry. Ports
// SecurityDirective.IsPermanent.
func (d SecurityDirective) IsPermanent() bool { return d.Duration == nil }

// SecurityPosture is a point-in-time summary of the AI Security Layer's current
// posture. Ports the SecurityPosture record.
type SecurityPosture struct {
	OverallThreatLevel   AetherThreatLevel
	QuarantinedNodeCount int
	MonitoredNodeCount   int
	IsActive             bool
	AssessedAt           time.Time
}

// ISecurityDirectiveConsumer receives security directives from the AI Security
// Layer. Ports ISecurityDirectiveConsumer.
type ISecurityDirectiveConsumer interface {
	// OnDirective is called each time the security layer issues a directive.
	OnDirective(directive SecurityDirective)
}

// IAISecurityLayer is the AI Security Layer contract. BhenguAI implements this by
// subscribing to IAetherTelemetry and producing SecurityDirective outputs
// consumed via ISecurityDirectiveConsumer. Ports IAISecurityLayer.
//
// The C# IDisposable returned by SubscribeToDirectives maps to an unsubscribe
// func.
type IAISecurityLayer interface {
	// Start wires the layer to an Aether telemetry feed and begins processing.
	Start(ctx context.Context, telemetry IAetherTelemetry) error
	// Stop stops processing and releases all telemetry subscriptions.
	Stop(ctx context.Context) error
	// SubscribeToDirectives subscribes a policy engine to receive directives.
	// Call the returned func to unsubscribe.
	SubscribeToDirectives(consumer ISecurityDirectiveConsumer) (unsubscribe func())
	// GetPosture returns the current security posture snapshot.
	GetPosture(ctx context.Context) (SecurityPosture, error)
}

// isBlankAether reports whether s is empty or all whitespace (mirrors
// string.IsNullOrWhiteSpace). Local helper to avoid depending on other files.
func isBlankAether(s string) bool {
	for _, r := range s {
		if r != ' ' && r != '\t' && r != '\n' && r != '\r' && r != '\v' && r != '\f' {
			return false
		}
	}
	return true
}
