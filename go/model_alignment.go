// model_alignment.go
//
// Ports the CircleAI.ModelAlignment module:
//   Records:    AlignmentProfile, AlignmentResult (Contracts.cs)
//   Interfaces: IAlignmentToolkit, IAlignmentAuditor (Contracts.cs)
//   Impls:      InMemoryAlignmentToolkit, RefuseAlignedPublishAuditor
//               (InMemoryModelAlignment.cs); NullAlignmentToolkit,
//               NullAlignmentAuditor (NullImplementations.cs).
//
// Targeted abliteration lives behind contracts so a host can apply / revert it
// deliberately — and so we can refuse to publish abliterated weights.
// InMemoryAlignmentToolkit.Apply only allows reversible profiles (matches the
// "no permanent abliteration" licence stance); RefuseAlignedPublishAuditor
// REFUSES to publish any model that has applied alignment profiles. Null*
// implementations are fail-closed defaults.
//
// Async note: the C# surface is ValueTask-based with a CancellationToken. The Go
// port takes ctx context.Context and returns (result, error). AssertOkToPublish
// throws InvalidOperationException in C#; the Go port returns that condition as a
// non-nil error.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"
)

// AlignmentProfile describes an applied alignment (abliteration) delta. Ports
// AlignmentProfile.
type AlignmentProfile struct {
	// ProfileID is the stable identifier for this alignment profile.
	ProfileID string
	// Description is a human-readable summary.
	Description string
	// RefusalCategoriesRemoved lists the refusal categories this profile strips.
	RefusalCategoriesRemoved []string
	// CreatedAtUTC is when the profile was created.
	CreatedAtUTC time.Time
	// IsReversible reports whether the delta can be reverted.
	IsReversible bool
}

// AlignmentResult is the outcome of an apply / revert operation. Ports
// AlignmentResult. FailureReason is empty on success (maps the C# nullable
// string; empty == null).
type AlignmentResult struct {
	// ProfileID is the profile the operation concerned.
	ProfileID string
	// Success reports whether the operation succeeded.
	Success bool
	// FailureReason explains a failure; empty when Success is true.
	FailureReason string
}

// IAlignmentToolkit is a targeted-abliteration toolkit: apply / revert / list
// alignment profiles. Ports IAlignmentToolkit.
type IAlignmentToolkit interface {
	// BackendID identifies the implementation.
	BackendID() string
	// Apply applies profile to modelId. Ports ApplyAsync.
	Apply(ctx context.Context, modelID string, profile AlignmentProfile) (AlignmentResult, error)
	// Revert removes the named profile from modelId. Ports RevertAsync.
	Revert(ctx context.Context, modelID, profileID string) (AlignmentResult, error)
	// ListApplied returns the profiles currently applied to modelId. Ports
	// ListAppliedAsync.
	ListApplied(ctx context.Context, modelID string) ([]AlignmentProfile, error)
}

// IAlignmentAuditor refuses to upload / publish weights that carry alignment
// deltas. Ports IAlignmentAuditor.
type IAlignmentAuditor interface {
	// BackendID identifies the implementation.
	BackendID() string
	// AssertOkToPublish returns a non-nil error if the model has applied
	// alignment profiles (i.e. publishing would distribute modified weights).
	// Ports AssertOkToPublishAsync (which throws in C#).
	AssertOkToPublish(ctx context.Context, modelID string) error
}

// ─── InMemoryAlignmentToolkit ──────────────────────────────────────────────

// InMemoryAlignmentToolkit is a thread-safe in-memory alignment toolkit. Apply
// only allows reversible profiles. Ports InMemoryAlignmentToolkit.
type InMemoryAlignmentToolkit struct {
	mu      sync.Mutex
	byModel map[string][]AlignmentProfile
}

// NewInMemoryAlignmentToolkit constructs an empty toolkit.
func NewInMemoryAlignmentToolkit() *InMemoryAlignmentToolkit {
	return &InMemoryAlignmentToolkit{byModel: make(map[string][]AlignmentProfile)}
}

// BackendID returns "in-memory". Ports InMemoryAlignmentToolkit.BackendId.
func (t *InMemoryAlignmentToolkit) BackendID() string { return "in-memory" }

// Apply records a reversible profile against modelID; non-reversible profiles
// are refused with a failed result (not an error). Ports
// InMemoryAlignmentToolkit.ApplyAsync.
func (t *InMemoryAlignmentToolkit) Apply(_ context.Context, modelID string, profile AlignmentProfile) (AlignmentResult, error) {
	if isBlank(modelID) {
		return AlignmentResult{}, errors.New("modelId required")
	}
	if !profile.IsReversible {
		return AlignmentResult{
			ProfileID:     profile.ProfileID,
			Success:       false,
			FailureReason: "Non-reversible alignment refused by InMemoryAlignmentToolkit",
		}, nil
	}
	t.mu.Lock()
	t.byModel[modelID] = append(t.byModel[modelID], profile)
	t.mu.Unlock()
	return AlignmentResult{ProfileID: profile.ProfileID, Success: true}, nil
}

// Revert removes the named profile from modelID. Ports
// InMemoryAlignmentToolkit.RevertAsync.
func (t *InMemoryAlignmentToolkit) Revert(_ context.Context, modelID, profileID string) (AlignmentResult, error) {
	if isBlank(modelID) {
		return AlignmentResult{}, errors.New("modelId required")
	}
	if isBlank(profileID) {
		return AlignmentResult{}, errors.New("profileId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	list, ok := t.byModel[modelID]
	if !ok {
		return AlignmentResult{ProfileID: profileID, Success: false, FailureReason: "Unknown model"}, nil
	}
	kept := list[:0:0]
	removed := 0
	for _, p := range list {
		if p.ProfileID == profileID {
			removed++
			continue
		}
		kept = append(kept, p)
	}
	t.byModel[modelID] = kept
	if removed > 0 {
		return AlignmentResult{ProfileID: profileID, Success: true}, nil
	}
	return AlignmentResult{ProfileID: profileID, Success: false, FailureReason: "Profile not applied to this model"}, nil
}

// ListApplied returns a copy of the profiles applied to modelID. Ports
// InMemoryAlignmentToolkit.ListAppliedAsync.
func (t *InMemoryAlignmentToolkit) ListApplied(_ context.Context, modelID string) ([]AlignmentProfile, error) {
	if isBlank(modelID) {
		return nil, errors.New("modelId required")
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	list, ok := t.byModel[modelID]
	if !ok {
		return []AlignmentProfile{}, nil
	}
	out := make([]AlignmentProfile, len(list))
	copy(out, list)
	return out, nil
}

// ─── RefuseAlignedPublishAuditor ───────────────────────────────────────────

// RefuseAlignedPublishAuditor refuses to publish weights that carry alignment
// deltas. Wired by default. Ports RefuseAlignedPublishAuditor.
type RefuseAlignedPublishAuditor struct {
	toolkit IAlignmentToolkit
}

// NewRefuseAlignedPublishAuditor builds the auditor over toolkit (required).
// Mirrors the C# ctor's null guard.
func NewRefuseAlignedPublishAuditor(toolkit IAlignmentToolkit) (*RefuseAlignedPublishAuditor, error) {
	if toolkit == nil {
		return nil, errors.New("toolkit is required")
	}
	return &RefuseAlignedPublishAuditor{toolkit: toolkit}, nil
}

// BackendID returns "refuse-aligned". Ports RefuseAlignedPublishAuditor.BackendId.
func (a *RefuseAlignedPublishAuditor) BackendID() string { return "refuse-aligned" }

// AssertOkToPublish returns a non-nil error when modelID has any applied
// alignment profiles. Ports RefuseAlignedPublishAuditor.AssertOkToPublishAsync
// (which throws InvalidOperationException).
func (a *RefuseAlignedPublishAuditor) AssertOkToPublish(ctx context.Context, modelID string) error {
	if isBlank(modelID) {
		return errors.New("modelId required")
	}
	applied, err := a.toolkit.ListApplied(ctx, modelID)
	if err != nil {
		return err
	}
	if len(applied) > 0 {
		return fmt.Errorf(
			"cannot publish '%s': %d alignment profile(s) applied — this would distribute weights with safety modifications",
			modelID, len(applied))
	}
	return nil
}

// ─── Null (fail-closed) implementations ────────────────────────────────────

// NullAlignmentToolkit refuses to apply anything and has nothing to revert or
// list. Ports NullAlignmentToolkit.
type NullAlignmentToolkit struct{}

// NullAlignmentToolkitInstance is the shared singleton. Mirrors
// NullAlignmentToolkit.Instance.
var NullAlignmentToolkitInstance = &NullAlignmentToolkit{}

// BackendID returns "null".
func (*NullAlignmentToolkit) BackendID() string { return "null" }

// Apply always fails. Ports NullAlignmentToolkit.ApplyAsync.
func (*NullAlignmentToolkit) Apply(_ context.Context, _ string, profile AlignmentProfile) (AlignmentResult, error) {
	return AlignmentResult{
		ProfileID:     profile.ProfileID,
		Success:       false,
		FailureReason: "NullAlignmentToolkit: no real backend wired.",
	}, nil
}

// Revert always fails. Ports NullAlignmentToolkit.RevertAsync.
func (*NullAlignmentToolkit) Revert(_ context.Context, _ string, profileID string) (AlignmentResult, error) {
	return AlignmentResult{
		ProfileID:     profileID,
		Success:       false,
		FailureReason: "NullAlignmentToolkit: nothing to revert.",
	}, nil
}

// ListApplied always returns empty. Ports NullAlignmentToolkit.ListAppliedAsync.
func (*NullAlignmentToolkit) ListApplied(context.Context, string) ([]AlignmentProfile, error) {
	return []AlignmentProfile{}, nil
}

// NullAlignmentAuditor always allows publishing (nothing was applied). Ports
// NullAlignmentAuditor.
type NullAlignmentAuditor struct{}

// NullAlignmentAuditorInstance is the shared singleton. Mirrors
// NullAlignmentAuditor.Instance.
var NullAlignmentAuditorInstance = &NullAlignmentAuditor{}

// BackendID returns "null".
func (*NullAlignmentAuditor) BackendID() string { return "null" }

// AssertOkToPublish always succeeds. Ports
// NullAlignmentAuditor.AssertOkToPublishAsync.
func (*NullAlignmentAuditor) AssertOkToPublish(context.Context, string) error { return nil }

// (isBlank — which mirrors string.IsNullOrWhiteSpace — is the package-shared
// helper defined in sync_channel.go.)

// Compile-time assertions that the implementations satisfy the contracts.
var (
	_ IAlignmentToolkit = (*InMemoryAlignmentToolkit)(nil)
	_ IAlignmentToolkit = (*NullAlignmentToolkit)(nil)
	_ IAlignmentAuditor = (*RefuseAlignedPublishAuditor)(nil)
	_ IAlignmentAuditor = (*NullAlignmentAuditor)(nil)
)
