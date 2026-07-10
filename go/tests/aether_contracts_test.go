// aether_contracts_test.go
//
// Verifies the four CircleAI.Aether contract surfaces + their in-memory
// implementations (aether_contracts.go):
//   - enum ordinals (AetherInstallLevel, AuthChallengeReason, AuthMethod,
//     SecurityDirectiveKind)
//   - AetherVersion.AtLeast comparison
//   - InMemoryAetherContext derived properties
//   - AuthChallengeResult helpers + ScriptedAuthChallenge floor enforcement
//   - SecurityDirective HasTarget / IsPermanent
//   - intelligence record helpers (IsValid, HasChanged, IsDegraded)

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestAetherContractEnum_Ordinals(t *testing.T) {
	il := []struct {
		got  circleai.AetherInstallLevel
		want int
	}{
		{circleai.AetherInstallLevelNone, 0},
		{circleai.AetherInstallLevelApp, 1},
		{circleai.AetherInstallLevelOS, 2},
	}
	for _, c := range il {
		if int(c.got) != c.want {
			t.Errorf("AetherInstallLevel got %d want %d", int(c.got), c.want)
		}
	}

	ar := []struct {
		got  circleai.AuthChallengeReason
		want int
	}{
		{circleai.AuthChallengeReasonOsLevelToggle, 0},
		{circleai.AuthChallengeReasonThreatThresholdReached, 1},
		{circleai.AuthChallengeReasonPrivilegedOperation, 2},
		{circleai.AuthChallengeReasonPeriodicRevalidation, 3},
		{circleai.AuthChallengeReasonManualRequest, 4},
	}
	for _, c := range ar {
		if int(c.got) != c.want {
			t.Errorf("AuthChallengeReason got %d want %d", int(c.got), c.want)
		}
	}

	// AuthMethod is explicitly Biometric=1..Custom=4 in C#.
	am := []struct {
		got  circleai.AuthMethod
		want int
	}{
		{circleai.AuthMethodBiometric, 1},
		{circleai.AuthMethodDeviceAdmin, 2},
		{circleai.AuthMethodBiometricAndDeviceAdmin, 3},
		{circleai.AuthMethodCustom, 4},
	}
	for _, c := range am {
		if int(c.got) != c.want {
			t.Errorf("AuthMethod got %d want %d", int(c.got), c.want)
		}
	}

	sd := []struct {
		got  circleai.SecurityDirectiveKind
		want int
	}{
		{circleai.SecurityDirectiveKindUpdateNodeTrust, 0},
		{circleai.SecurityDirectiveKindAvoidNode, 1},
		{circleai.SecurityDirectiveKindQuarantineNode, 2},
		{circleai.SecurityDirectiveKindReleaseNode, 3},
		{circleai.SecurityDirectiveKindRequestReauth, 4},
		{circleai.SecurityDirectiveKindElevateMonitoring, 5},
	}
	for _, c := range sd {
		if int(c.got) != c.want {
			t.Errorf("SecurityDirectiveKind got %d want %d", int(c.got), c.want)
		}
	}
}

func TestAetherVersion_AtLeast(t *testing.T) {
	v := circleai.NewAetherVersion(2, 7, 0)
	cases := []struct {
		other circleai.AetherVersion
		want  bool
	}{
		{circleai.NewAetherVersion(2, 7, 0), true},  // equal
		{circleai.NewAetherVersion(2, 6, 9), true},  // greater minor/build
		{circleai.NewAetherVersion(1, 9, 9), true},  // greater major
		{circleai.NewAetherVersion(2, 7, 1), false}, // build higher
		{circleai.NewAetherVersion(3, 0, 0), false}, // major higher
		{circleai.NewAetherVersion(2, 8, 0), false}, // minor higher
	}
	for _, c := range cases {
		if got := v.AtLeast(c.other); got != c.want {
			t.Errorf("2.7.0.AtLeast(%+v) got %v want %v", c.other, got, c.want)
		}
	}
	// Trailing-component defaulting: 2.7 == 2.7.0.0.
	if !circleai.NewAetherVersion(2, 7).AtLeast(circleai.NewAetherVersion(2, 7, 0, 0)) {
		t.Error("2.7 should equal 2.7.0.0")
	}
}

func TestInMemoryAetherContext_DerivedProperties(t *testing.T) {
	rt := circleai.NewAetherVersion(2, 7, 0)
	min := circleai.NewAetherVersion(2, 5, 0)

	// OS-managed, enabled, sufficient version.
	ctx := circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel:    circleai.AetherInstallLevelOS,
		RuntimeVersion:  &rt,
		MinimumRequired: &min,
		Enabled:         true,
	})
	if ctx.InstallLevel() != circleai.AetherInstallLevelOS {
		t.Error("install level")
	}
	if !ctx.IsAvailable() || !ctx.IsEnabled() {
		t.Error("OS+enabled should be available and enabled")
	}
	if !ctx.IsSufficient() {
		t.Error("2.7.0 should satisfy min 2.5.0")
	}
	if !ctx.RequiresAuth() {
		t.Error("OS install should require auth")
	}

	// Toggle off → not available/enabled, but still OS-managed and sufficient.
	ctx.SetEnabled(false)
	if ctx.IsAvailable() || ctx.IsEnabled() {
		t.Error("toggled-off OS instance should be unavailable")
	}
	if !ctx.RequiresAuth() {
		t.Error("still OS-managed after toggle-off")
	}

	// None install: never available, never requires auth, IsSufficient true when
	// no minimum is set.
	none := circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel: circleai.AetherInstallLevelNone,
		Enabled:      true, // forced false by constructor
	})
	if none.IsAvailable() || none.IsEnabled() || none.RequiresAuth() {
		t.Error("None install should be unavailable/disabled/no-auth")
	}
	if !none.IsSufficient() {
		t.Error("nil minimum → IsSufficient true")
	}
	if none.SetEnabled(true) {
		t.Error("None install cannot be enabled")
	}

	// App install with runtime below minimum → insufficient.
	old := circleai.NewAetherVersion(2, 0, 0)
	app := circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel:    circleai.AetherInstallLevelApp,
		RuntimeVersion:  &old,
		MinimumRequired: &min,
		Enabled:         true,
	})
	if app.IsSufficient() {
		t.Error("2.0.0 should NOT satisfy min 2.5.0")
	}
	if app.RequiresAuth() {
		t.Error("App install should not require auth")
	}

	// Minimum set but runtime absent → insufficient.
	noRuntime := circleai.NewInMemoryAetherContext(circleai.InMemoryAetherContextOptions{
		InstallLevel:    circleai.AetherInstallLevelApp,
		MinimumRequired: &min,
		Enabled:         true,
	})
	if noRuntime.IsSufficient() {
		t.Error("absent runtime with a minimum → insufficient")
	}
}

func TestAuthChallengeResult_Helpers(t *testing.T) {
	ok := circleai.NewAuthChallengeSuccess(circleai.AuthMethodBiometricAndDeviceAdmin)
	if !ok.Succeeded || ok.FailureReason != nil {
		t.Error("success result should have no failure reason")
	}
	fail := circleai.NewAuthChallengeFailure(circleai.AuthMethodBiometric, "too weak")
	if fail.Succeeded || fail.FailureReason == nil || *fail.FailureReason != "too weak" {
		t.Error("failure result malformed")
	}
}

func TestScriptedAuthChallenge_FloorEnforcement(t *testing.T) {
	ctx := context.Background()

	// Device satisfies biometric+admin → OS toggle succeeds.
	full := circleai.NewScriptedAuthChallenge(circleai.AuthMethodBiometricAndDeviceAdmin)
	r, err := full.RequestOsToggle(ctx, true)
	if err != nil {
		t.Fatalf("toggle: %v", err)
	}
	if !r.Succeeded || r.MethodUsed != circleai.AuthMethodBiometricAndDeviceAdmin {
		t.Errorf("full device should satisfy OS toggle: %+v", r)
	}

	// Device only has biometric → OS toggle FAILS (floor is biometric+admin).
	weak := circleai.NewScriptedAuthChallenge(circleai.AuthMethodBiometric)
	r, _ = weak.RequestOsToggle(ctx, false)
	if r.Succeeded {
		t.Error("biometric-only device must not satisfy the OS-toggle floor")
	}

	// A caller cannot lower the floor below biometric+admin: requesting Biometric
	// on a biometric-only device still fails because the effective min is raised.
	min := circleai.AuthMethodBiometric
	r, _ = weak.Challenge(ctx, circleai.AuthChallengeReasonManualRequest, &min, "p")
	if r.Succeeded {
		t.Error("floor must not drop below biometric+admin even when caller asks for less")
	}

	// Caller RAISES the bar to Custom; a biometric+admin device fails it.
	custom := circleai.AuthMethodCustom
	r, _ = full.Challenge(ctx, circleai.AuthChallengeReasonPrivilegedOperation, &custom, "p")
	if r.Succeeded {
		t.Error("Custom requirement should fail a biometric+admin-only device")
	}
	// A device with Custom satisfies it.
	fuller := circleai.NewScriptedAuthChallenge(circleai.AuthMethodCustom)
	r, _ = fuller.Challenge(ctx, circleai.AuthChallengeReasonPrivilegedOperation, &custom, "p")
	if !r.Succeeded || r.MethodUsed != circleai.AuthMethodCustom {
		t.Errorf("Custom device should satisfy Custom requirement: %+v", r)
	}

	// nil minimum defaults to biometric+admin.
	r, _ = full.Challenge(ctx, circleai.AuthChallengeReasonPeriodicRevalidation, nil, "p")
	if !r.Succeeded {
		t.Error("nil min should default to biometric+admin and succeed on a full device")
	}
}

func TestSecurityDirective_Helpers(t *testing.T) {
	node := "n1"
	perm := circleai.SecurityDirective{
		Kind:         circleai.SecurityDirectiveKindQuarantineNode,
		TargetNodeID: &node,
	}
	if !perm.HasTarget() {
		t.Error("directive with target should HasTarget")
	}
	if !perm.IsPermanent() {
		t.Error("nil duration → permanent")
	}

	// No target (nil / blank).
	if (circleai.SecurityDirective{}).HasTarget() {
		t.Error("nil target should not HasTarget")
	}
	blank := "   "
	if (circleai.SecurityDirective{TargetNodeID: &blank}).HasTarget() {
		t.Error("blank target should not HasTarget")
	}
}

func TestIntelligenceRecord_Helpers(t *testing.T) {
	if !(circleai.NetworkHealthReport{OverallScore: 0.5}).IsValid() {
		t.Error("0.5 health should be valid")
	}
	if (circleai.NetworkHealthReport{OverallScore: 1.1}).IsValid() {
		t.Error("1.1 health should be invalid")
	}
	if !(circleai.ThreatAssessment{ThreatConfidence: 0.9}).IsValid() {
		t.Error("0.9 confidence should be valid")
	}
	if (circleai.ThreatAssessment{ThreatConfidence: -0.1}).IsValid() {
		t.Error("-0.1 confidence should be invalid")
	}

	up := circleai.TrustScoreUpdate{PreviousScore: 0.8, CurrentScore: 0.5}
	if !up.HasChanged() {
		t.Error("0.8→0.5 should have changed")
	}
	if !up.IsDegraded() {
		t.Error("0.8→0.5 should be degraded")
	}
	tiny := circleai.TrustScoreUpdate{PreviousScore: 0.5000, CurrentScore: 0.5005}
	if tiny.HasChanged() {
		t.Error("sub-0.001 delta should not count as changed")
	}
	improved := circleai.TrustScoreUpdate{PreviousScore: 0.5, CurrentScore: 0.9}
	if improved.IsDegraded() {
		t.Error("0.5→0.9 should not be degraded")
	}
}
