// model_alignment_test.go
//
// Verifies the CircleAI.ModelAlignment port (model_alignment.go):
//   - InMemoryAlignmentToolkit: Apply accepts reversible profiles and refuses
//     non-reversible ones; Revert removes an applied profile and reports the
//     not-applied / unknown-model cases; ListApplied returns a snapshot copy;
//     blank modelId/profileId are argument errors.
//   - RefuseAlignedPublishAuditor: refuses to publish once a profile is applied,
//     allows publish after it is reverted; nil toolkit is a ctor error.
//   - Null* fail-closed defaults refuse to apply/revert and always allow publish.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func reversibleProfile(id string) circleai.AlignmentProfile {
	return circleai.AlignmentProfile{
		ProfileID:                id,
		Description:              "test",
		RefusalCategoriesRemoved: []string{"cat"},
		CreatedAtUTC:             time.Now().UTC(),
		IsReversible:             true,
	}
}

func TestInMemoryAlignmentToolkit_ApplyReversible(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	if tk.BackendID() != "in-memory" {
		t.Errorf("backend id: got %q", tk.BackendID())
	}
	ctx := context.Background()

	res, err := tk.Apply(ctx, "m1", reversibleProfile("p1"))
	if err != nil {
		t.Fatalf("apply err: %v", err)
	}
	if !res.Success || res.ProfileID != "p1" || res.FailureReason != "" {
		t.Errorf("apply result: %+v", res)
	}

	applied, err := tk.ListApplied(ctx, "m1")
	if err != nil {
		t.Fatalf("list err: %v", err)
	}
	if len(applied) != 1 || applied[0].ProfileID != "p1" {
		t.Errorf("list applied: %+v", applied)
	}
}

func TestInMemoryAlignmentToolkit_RefusesNonReversible(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	p := reversibleProfile("p1")
	p.IsReversible = false
	res, err := tk.Apply(context.Background(), "m1", p)
	if err != nil {
		t.Fatalf("apply err: %v", err)
	}
	if res.Success {
		t.Error("non-reversible must be refused")
	}
	if !strings.Contains(res.FailureReason, "Non-reversible") {
		t.Errorf("reason: %q", res.FailureReason)
	}
	// Nothing should have been recorded.
	applied, _ := tk.ListApplied(context.Background(), "m1")
	if len(applied) != 0 {
		t.Errorf("refused profile must not be stored, got %d", len(applied))
	}
}

func TestInMemoryAlignmentToolkit_Revert(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	ctx := context.Background()
	_, _ = tk.Apply(ctx, "m1", reversibleProfile("p1"))

	// Revert an applied profile.
	res, err := tk.Revert(ctx, "m1", "p1")
	if err != nil {
		t.Fatalf("revert err: %v", err)
	}
	if !res.Success {
		t.Errorf("revert should succeed: %+v", res)
	}
	if applied, _ := tk.ListApplied(ctx, "m1"); len(applied) != 0 {
		t.Errorf("profile should be gone, got %d", len(applied))
	}

	// Revert a profile not applied to a known model.
	_, _ = tk.Apply(ctx, "m1", reversibleProfile("p2"))
	res, _ = tk.Revert(ctx, "m1", "does-not-exist")
	if res.Success || !strings.Contains(res.FailureReason, "not applied") {
		t.Errorf("revert not-applied: %+v", res)
	}

	// Revert against an unknown model.
	res, _ = tk.Revert(ctx, "ghost", "p1")
	if res.Success || res.FailureReason != "Unknown model" {
		t.Errorf("revert unknown model: %+v", res)
	}
}

func TestInMemoryAlignmentToolkit_ArgErrors(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	ctx := context.Background()
	if _, err := tk.Apply(ctx, "   ", reversibleProfile("p1")); err == nil {
		t.Error("blank modelId should error on Apply")
	}
	if _, err := tk.Revert(ctx, "", "p1"); err == nil {
		t.Error("blank modelId should error on Revert")
	}
	if _, err := tk.Revert(ctx, "m1", ""); err == nil {
		t.Error("blank profileId should error on Revert")
	}
	if _, err := tk.ListApplied(ctx, ""); err == nil {
		t.Error("blank modelId should error on ListApplied")
	}
}

func TestInMemoryAlignmentToolkit_ListReturnsCopy(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	ctx := context.Background()
	_, _ = tk.Apply(ctx, "m1", reversibleProfile("p1"))
	got, _ := tk.ListApplied(ctx, "m1")
	got[0].ProfileID = "MUTATED"
	again, _ := tk.ListApplied(ctx, "m1")
	if again[0].ProfileID != "p1" {
		t.Error("ListApplied must return a snapshot copy, internal state was mutated")
	}
}

func TestRefuseAlignedPublishAuditor(t *testing.T) {
	tk := circleai.NewInMemoryAlignmentToolkit()
	aud, err := circleai.NewRefuseAlignedPublishAuditor(tk)
	if err != nil {
		t.Fatalf("ctor err: %v", err)
	}
	if aud.BackendID() != "refuse-aligned" {
		t.Errorf("backend id: got %q", aud.BackendID())
	}
	ctx := context.Background()

	// Clean model → publish ok.
	if err := aud.AssertOkToPublish(ctx, "m1"); err != nil {
		t.Errorf("clean model should publish: %v", err)
	}

	// After applying a profile → publish refused.
	_, _ = tk.Apply(ctx, "m1", reversibleProfile("p1"))
	err = aud.AssertOkToPublish(ctx, "m1")
	if err == nil {
		t.Fatal("aligned model must refuse publish")
	}
	if !strings.Contains(err.Error(), "alignment profile") || !strings.Contains(err.Error(), "m1") {
		t.Errorf("error should explain the refusal: %v", err)
	}

	// After reverting → publish ok again.
	_, _ = tk.Revert(ctx, "m1", "p1")
	if err := aud.AssertOkToPublish(ctx, "m1"); err != nil {
		t.Errorf("reverted model should publish: %v", err)
	}
}

func TestRefuseAlignedPublishAuditor_NilToolkit(t *testing.T) {
	if _, err := circleai.NewRefuseAlignedPublishAuditor(nil); err == nil {
		t.Error("nil toolkit should be a ctor error")
	}
}

func TestRefuseAlignedPublishAuditor_BlankModel(t *testing.T) {
	aud, _ := circleai.NewRefuseAlignedPublishAuditor(circleai.NewInMemoryAlignmentToolkit())
	if err := aud.AssertOkToPublish(context.Background(), ""); err == nil {
		t.Error("blank modelId should error")
	}
}

func TestModelAlignment_NullFailClosed(t *testing.T) {
	ctx := context.Background()
	tk := circleai.NullAlignmentToolkitInstance
	if tk.BackendID() != "null" {
		t.Errorf("null toolkit backend id: got %q", tk.BackendID())
	}

	res, _ := tk.Apply(ctx, "m1", reversibleProfile("p1"))
	if res.Success || res.ProfileID != "p1" || !strings.Contains(res.FailureReason, "no real backend") {
		t.Errorf("null apply: %+v", res)
	}
	res, _ = tk.Revert(ctx, "m1", "p9")
	if res.Success || res.ProfileID != "p9" || !strings.Contains(res.FailureReason, "nothing to revert") {
		t.Errorf("null revert: %+v", res)
	}
	if applied, _ := tk.ListApplied(ctx, "m1"); len(applied) != 0 {
		t.Errorf("null list should be empty, got %d", len(applied))
	}

	aud := circleai.NullAlignmentAuditorInstance
	if aud.BackendID() != "null" {
		t.Errorf("null auditor backend id: got %q", aud.BackendID())
	}
	if err := aud.AssertOkToPublish(ctx, "m1"); err != nil {
		t.Errorf("null auditor must always allow publish: %v", err)
	}
}
