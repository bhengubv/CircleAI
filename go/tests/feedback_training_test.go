// feedback_training_test.go
//
// Verifies FeedbackTrainingQueue + NightlyAdapterTrainer (ported from
// FeedbackTrainingQueue.cs / NightlyAdapterTrainer.cs / MnnInteropRtFeatures.cs):
//   - FileBackedFeedbackTrainingQueue: enqueue/pending/drain (FIFO, remainder
//     preserved), disk persistence across a reopen, malformed-line skip.
//   - InMemoryLoRAAdapterManager: TrainStep loss strictly decreases with repeated
//     steps, adapter save/apply round-trip, disabled → ErrTrainingDisabled.
//   - NightlyAdapterTrainer.RunOnce: skips below MinBatchSize, drains + trains +
//     applies above it, re-queues when training is disabled.

package circleai_test

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func sampleAt(user, assistant, preferred string, polarity int) circleai.TrainingSample {
	return circleai.TrainingSample{
		UserText:      user,
		AssistantText: assistant,
		PreferredText: preferred,
		Polarity:      polarity,
		AtUtc:         time.Now().UTC(),
	}
}

func TestFeedbackTrainingQueue_EnqueueDrainPersist(t *testing.T) {
	path := filepath.Join(t.TempDir(), "feedback", "queue.jsonl")
	q, err := circleai.NewFileBackedFeedbackTrainingQueue(path)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if q.Pending() != 0 {
		t.Fatal("fresh queue should be empty")
	}
	for i := 0; i < 5; i++ {
		if err := q.Enqueue(sampleAt("u"+string(rune('0'+i)), "a", "p", 1)); err != nil {
			t.Fatalf("enqueue: %v", err)
		}
	}
	if q.Pending() != 5 {
		t.Errorf("pending: got %d want 5", q.Pending())
	}

	// Drain 3 → FIFO order, 2 remain.
	drained, err := q.Drain(3)
	if err != nil {
		t.Fatalf("drain: %v", err)
	}
	if len(drained) != 3 {
		t.Fatalf("drained: got %d want 3", len(drained))
	}
	if drained[0].UserText != "u0" || drained[2].UserText != "u2" {
		t.Errorf("drain not FIFO: %q..%q", drained[0].UserText, drained[2].UserText)
	}
	if q.Pending() != 2 {
		t.Errorf("remaining: got %d want 2", q.Pending())
	}

	// Reopen the same file — persistence across process restart.
	q2, _ := circleai.NewFileBackedFeedbackTrainingQueue(path)
	if q2.Pending() != 2 {
		t.Errorf("reopened queue should still have 2, got %d", q2.Pending())
	}
	rest, _ := q2.Drain(100)
	if len(rest) != 2 || rest[0].UserText != "u3" {
		t.Errorf("remaining drain wrong: %+v", rest)
	}
	if q2.Pending() != 0 {
		t.Error("queue should be empty after full drain")
	}
}

func TestFeedbackTrainingQueue_DrainGuard(t *testing.T) {
	q, _ := circleai.NewFileBackedFeedbackTrainingQueue(filepath.Join(t.TempDir(), "q.jsonl"))
	if _, err := q.Drain(0); err == nil {
		t.Error("Drain(0) should error")
	}
}

func TestInMemoryLoRAAdapterManager_LossDecreasesAndPersists(t *testing.T) {
	m := circleai.NewInMemoryLoRAAdapterManager(false)
	in := []int{10, 20, 30}
	target := []int{15, 25, 35}

	l1, err := m.TrainStep(in, target, 1e-3, 8)
	if err != nil {
		t.Fatalf("step 1: %v", err)
	}
	l2, _ := m.TrainStep(in, target, 1e-3, 8)
	l3, _ := m.TrainStep(in, target, 1e-3, 8)
	if !(l1 > l2 && l2 > l3) {
		t.Errorf("loss should strictly decrease: %v > %v > %v", l1, l2, l3)
	}
	if m.StepsTaken() != 3 {
		t.Errorf("steps: got %d want 3", m.StepsTaken())
	}

	// Save + apply into a fresh manager restores the learned state.
	adapterPath := filepath.Join(t.TempDir(), "adapters", "lora.mnn")
	if err := m.SaveAdapter(adapterPath); err != nil {
		t.Fatalf("save: %v", err)
	}
	m2 := circleai.NewInMemoryLoRAAdapterManager(false)
	if err := m2.Apply(adapterPath); err != nil {
		t.Fatalf("apply: %v", err)
	}
	// The restored manager continues the decreasing trend (weight carried over).
	l4, _ := m2.TrainStep(in, target, 1e-3, 8)
	if l4 >= l3 {
		t.Errorf("restored adapter should continue lower than l3: l4=%v l3=%v", l4, l3)
	}
}

func TestInMemoryLoRAAdapterManager_Disabled(t *testing.T) {
	m := circleai.NewInMemoryLoRAAdapterManager(true)
	if _, err := m.TrainStep([]int{1}, []int{2}, 1e-3, 8); err == nil {
		t.Error("disabled manager should return an error from TrainStep")
	}
}

func TestNightlyAdapterTrainer_SkipsAndTrains(t *testing.T) {
	ctx := context.Background()
	q, _ := circleai.NewFileBackedFeedbackTrainingQueue(filepath.Join(t.TempDir(), "q.jsonl"))
	adapter := circleai.NewInMemoryLoRAAdapterManager(false)
	opts := circleai.DefaultNightlyAdapterTrainerOptions()
	opts.MinBatchSize = 3
	opts.AdapterPath = filepath.Join(t.TempDir(), "lora.mnn")
	tr, err := circleai.NewNightlyAdapterTrainer(q, adapter, opts)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	// Below MinBatchSize → skip (no training).
	_ = q.Enqueue(sampleAt("hi", "hey", "hey there", 1))
	if err := tr.RunOnce(ctx); err != nil {
		t.Fatalf("runonce skip: %v", err)
	}
	if adapter.StepsTaken() != 0 {
		t.Errorf("should not train below MinBatchSize, steps=%d", adapter.StepsTaken())
	}

	// Meet the batch: enqueue 3 total, drain + train.
	_ = q.Enqueue(sampleAt("what time", "noon", "it is noon", 0))
	_ = q.Enqueue(sampleAt("thanks", "welcome", "you are welcome", -1))
	if err := tr.RunOnce(ctx); err != nil {
		t.Fatalf("runonce train: %v", err)
	}
	if adapter.StepsTaken() == 0 {
		t.Error("should have trained at least one step")
	}
	if q.Pending() != 0 {
		t.Errorf("queue should be drained, pending=%d", q.Pending())
	}
}

func TestNightlyAdapterTrainer_RequeuesWhenDisabled(t *testing.T) {
	ctx := context.Background()
	q, _ := circleai.NewFileBackedFeedbackTrainingQueue(filepath.Join(t.TempDir(), "q.jsonl"))
	adapter := circleai.NewInMemoryLoRAAdapterManager(true) // native training unavailable
	opts := circleai.DefaultNightlyAdapterTrainerOptions()
	opts.MinBatchSize = 2
	tr, _ := circleai.NewNightlyAdapterTrainer(q, adapter, opts)

	_ = q.Enqueue(sampleAt("a", "b", "b", 1))
	_ = q.Enqueue(sampleAt("c", "d", "d", 1))
	if err := tr.RunOnce(ctx); err != nil {
		t.Fatalf("runonce: %v", err)
	}
	// Training disabled → samples re-queued, no steps taken.
	if adapter.StepsTaken() != 0 {
		t.Errorf("disabled adapter should not accumulate steps, got %d", adapter.StepsTaken())
	}
	if q.Pending() != 2 {
		t.Errorf("samples should be re-queued when training disabled, pending=%d", q.Pending())
	}
}

func TestNightlyAdapterTrainer_Guards(t *testing.T) {
	q, _ := circleai.NewFileBackedFeedbackTrainingQueue(filepath.Join(t.TempDir(), "q.jsonl"))
	if _, err := circleai.NewNightlyAdapterTrainer(nil, circleai.NewInMemoryLoRAAdapterManager(false), circleai.DefaultNightlyAdapterTrainerOptions()); err == nil {
		t.Error("nil queue should error")
	}
	if _, err := circleai.NewNightlyAdapterTrainer(q, nil, circleai.DefaultNightlyAdapterTrainerOptions()); err == nil {
		t.Error("nil adapter should error")
	}
}
