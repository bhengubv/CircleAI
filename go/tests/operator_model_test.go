// operator_model_test.go
//
// Verifies the CircleAI.Operator port (operator_model.go): the lifecycle state
// machine (Pending->Downloading->Loading->Ready), observer notifications on every
// transition, delete, get-status, validation, and the null impls.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestOperator_ApplyRunsLifecycleAndNotifies(t *testing.T) {
	op := circleai.NewInMemoryModelOperator()
	var phases []circleai.ModelLifecyclePhase
	unsub := op.Subscribe(func(s circleai.ModelStatus) { phases = append(phases, s.Phase) })
	defer unsub()

	dep := circleai.ModelDeployment{ModelID: "qwen", Namespace: "prod", Replicas: 3, TargetTierLabel: "t2"}
	if err := op.Apply(context.Background(), dep); err != nil {
		t.Fatalf("apply: %v", err)
	}
	want := []circleai.ModelLifecyclePhase{
		circleai.ModelLifecyclePending, circleai.ModelLifecycleDownloading,
		circleai.ModelLifecycleLoading, circleai.ModelLifecycleReady,
	}
	if len(phases) != len(want) {
		t.Fatalf("phases = %v, want %v", phases, want)
	}
	for i := range want {
		if phases[i] != want[i] {
			t.Fatalf("phase[%d] = %d, want %d", i, phases[i], want[i])
		}
	}
	st, ok := op.GetStatus(context.Background(), "qwen", "prod")
	if !ok || st.Phase != circleai.ModelLifecycleReady || st.ReadyReplicas != 3 {
		t.Fatalf("final status = %+v ok=%v", st, ok)
	}
}

func TestOperator_UnsubscribeStopsNotifications(t *testing.T) {
	op := circleai.NewInMemoryModelOperator()
	n := 0
	unsub := op.Subscribe(func(circleai.ModelStatus) { n++ })
	unsub()
	_ = op.Apply(context.Background(), circleai.ModelDeployment{ModelID: "m", Namespace: "n", Replicas: 1})
	if n != 0 {
		t.Fatalf("unsubscribed handler fired %d times", n)
	}
}

func TestOperator_DeleteAndValidation(t *testing.T) {
	op := circleai.NewInMemoryModelOperator()
	_ = op.Apply(context.Background(), circleai.ModelDeployment{ModelID: "m", Namespace: "n", Replicas: 1})
	if err := op.Delete(context.Background(), "m", "n"); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if _, ok := op.GetStatus(context.Background(), "m", "n"); ok {
		t.Fatalf("status should be gone after delete")
	}
	if err := op.Apply(context.Background(), circleai.ModelDeployment{ModelID: "", Namespace: "n"}); err == nil {
		t.Fatalf("blank ModelId must error")
	}
	if err := op.Apply(context.Background(), circleai.ModelDeployment{ModelID: "m", Namespace: "n", Replicas: -1}); err == nil {
		t.Fatalf("negative replicas must error")
	}
}

func TestOperator_NullImpls(t *testing.T) {
	if circleai.NullModelOperatorInstance.BackendID() != "null" {
		t.Fatalf("null backend id")
	}
	if _, ok := circleai.NullModelOperatorInstance.GetStatus(context.Background(), "a", "b"); ok {
		t.Fatalf("null operator should have no status")
	}
	// Null observer subscribe returns a no-op unsubscribe (does not panic).
	circleai.NullDeploymentObserverInstance.Subscribe(func(circleai.ModelStatus) {})()
}
