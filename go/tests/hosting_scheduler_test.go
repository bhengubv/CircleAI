// hosting_scheduler_test.go
//
// Verifies the CircleAI.Hosting scheduling ports:
//   InMemoryScheduledTaskStore CRUD + due-job filter
//   ScheduledAIService.ExecuteJobNow: state transitions, NextRun recompute,
//     OnJobCompleted callback, failure handling.

package circleai_test

import (
	"context"
	"errors"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// fakeButler is a deterministic IAIService for hosting tests. Ask returns a
// scripted reply (or echoes the question); optionally errors.
type fakeButler struct {
	ready    bool
	askReply string
	askErr   error
	asked    []string
}

func (f *fakeButler) IsReady() bool                 { return f.ready }
func (f *fakeButler) Start(context.Context) error   { f.ready = true; return nil }
func (f *fakeButler) Stop(context.Context) error    { f.ready = false; return nil }
func (f *fakeButler) Prewarm(context.Context) error { f.ready = true; return nil }

func (f *fakeButler) Ask(_ context.Context, question string) (string, error) {
	f.asked = append(f.asked, question)
	if f.askErr != nil {
		return "", f.askErr
	}
	if f.askReply != "" {
		return f.askReply, nil
	}
	return "answer:" + question, nil
}

func (f *fakeButler) Chat(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	return "chat", nil
}

func (f *fakeButler) Stream(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)
	close(out)
	close(errc)
	return out, errc
}

func (f *fakeButler) InvokeTool(context.Context, circleai.ToolInvocation) (circleai.ToolResult, error) {
	return circleai.ToolResult{}, nil
}

func (f *fakeButler) AgenticChat(context.Context, string, *circleai.GenerationOptions) (string, error) {
	return "", nil
}

func (f *fakeButler) SubmitFeedback(context.Context, circleai.FeedbackSignal) error { return nil }
func (f *fakeButler) CheckForUpgrades(context.Context) ([]circleai.UpgradeInfo, error) {
	return nil, nil
}

var _ circleai.IAIService = (*fakeButler)(nil)

func TestInMemoryScheduledTaskStore_CRUD(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryScheduledTaskStore()

	job := circleai.NewCronJob("j1", "n", "p", "0 9 * * *", circleai.DeliveryLocal)
	if _, err := store.Upsert(ctx, job); err != nil {
		t.Fatalf("upsert: %v", err)
	}
	got, err := store.Get(ctx, "j1")
	if err != nil || got == nil || got.ID != "j1" {
		t.Fatalf("get: %v %+v", err, got)
	}
	list, _ := store.List(ctx)
	if len(list) != 1 {
		t.Fatalf("list len = %d, want 1", len(list))
	}
	if err := store.Delete(ctx, "j1"); err != nil {
		t.Fatalf("delete: %v", err)
	}
	got, _ = store.Get(ctx, "j1")
	if got != nil {
		t.Fatal("expected nil after delete")
	}
}

func TestInMemoryScheduledTaskStore_GetDueJobs(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryScheduledTaskStore()

	past := time.Now().UTC().Add(-time.Minute)
	future := time.Now().UTC().Add(time.Hour)

	dueEnabled := circleai.NewCronJob("due", "n", "p", "* * * * *", circleai.DeliveryLocal)
	dueEnabled.NextRunUTC = &past

	dueDisabled := circleai.NewCronJob("disabled", "n", "p", "* * * * *", circleai.DeliveryLocal)
	dueDisabled.NextRunUTC = &past
	dueDisabled.IsEnabled = false

	notDue := circleai.NewCronJob("future", "n", "p", "* * * * *", circleai.DeliveryLocal)
	notDue.NextRunUTC = &future

	neverScheduled := circleai.NewCronJob("never", "n", "p", "* * * * *", circleai.DeliveryLocal)

	for _, j := range []circleai.CronJob{dueEnabled, dueDisabled, notDue, neverScheduled} {
		_, _ = store.Upsert(ctx, j)
	}

	due, err := store.GetDueJobs(ctx)
	if err != nil {
		t.Fatalf("GetDueJobs: %v", err)
	}
	if len(due) != 1 || due[0].ID != "due" {
		t.Fatalf("due jobs = %+v, want just 'due'", due)
	}
}

func TestScheduledAIService_ExecuteJob_Success(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{askReply: "brief ready"}
	store := circleai.NewInMemoryScheduledTaskStore()
	svc := circleai.NewScheduledAIService(butler, store)

	var completed circleai.JobCompletedEventArgs
	fired := false
	svc.OnJobCompleted = func(a circleai.JobCompletedEventArgs) { completed = a; fired = true }

	job := circleai.NewCronJob("j1", "brief", "morning brief", "0 9 * * *", circleai.DeliveryLocal)
	_, _ = store.Upsert(ctx, job)

	args := svc.ExecuteJobNow(ctx, job)

	if !fired {
		t.Fatal("OnJobCompleted did not fire")
	}
	if args.Err != nil {
		t.Fatalf("unexpected error: %v", args.Err)
	}
	if args.Response != "brief ready" {
		t.Errorf("response = %q, want 'brief ready'", args.Response)
	}
	if completed.Job.State != circleai.CronJobSucceeded {
		t.Errorf("state = %d, want Succeeded", completed.Job.State)
	}
	if completed.Job.LastRunUTC == nil {
		t.Error("LastRunUTC should be set")
	}
	if completed.Job.NextRunUTC == nil {
		t.Error("NextRunUTC should be recomputed")
	}

	// Verify the store was updated to Succeeded.
	stored, _ := store.Get(ctx, "j1")
	if stored == nil || stored.State != circleai.CronJobSucceeded {
		t.Errorf("stored state = %+v, want Succeeded", stored)
	}
	if len(butler.asked) != 1 || butler.asked[0] != "morning brief" {
		t.Errorf("butler asked = %v, want ['morning brief']", butler.asked)
	}
}

func TestScheduledAIService_ExecuteJob_Failure(t *testing.T) {
	ctx := context.Background()
	butler := &fakeButler{askErr: errors.New("model down")}
	store := circleai.NewInMemoryScheduledTaskStore()
	svc := circleai.NewScheduledAIService(butler, store)

	job := circleai.NewCronJob("j1", "brief", "p", "0 9 * * *", circleai.DeliveryLocal)
	_, _ = store.Upsert(ctx, job)

	args := svc.ExecuteJobNow(ctx, job)
	if args.Err == nil {
		t.Fatal("expected error propagated")
	}
	if args.Response != "" {
		t.Errorf("response should be empty on failure, got %q", args.Response)
	}
	if args.Job.State != circleai.CronJobFailed {
		t.Errorf("state = %d, want Failed", args.Job.State)
	}
}

func TestScheduledAIService_StartStop(t *testing.T) {
	butler := &fakeButler{askReply: "x"}
	store := circleai.NewInMemoryScheduledTaskStore()
	svc := circleai.NewScheduledAIService(butler, store)
	svc.SetPollInterval(10 * time.Millisecond)

	// Seed a due job.
	past := time.Now().UTC().Add(-time.Minute)
	job := circleai.NewCronJob("j1", "n", "p", "* * * * *", circleai.DeliveryLocal)
	job.NextRunUTC = &past
	_, _ = store.Upsert(context.Background(), job)

	done := make(chan struct{})
	svc.OnJobCompleted = func(circleai.JobCompletedEventArgs) {
		select {
		case <-done:
		default:
			close(done)
		}
	}

	svc.Start()
	select {
	case <-done:
		// fired at least once
	case <-time.After(2 * time.Second):
		t.Fatal("poll loop never fired the due job")
	}
	svc.Stop()
}
