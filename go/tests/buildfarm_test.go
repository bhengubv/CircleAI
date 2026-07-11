// buildfarm_test.go
//
// Verifies the CircleAI.BuildFarm port (buildfarm.go): agent pool
// acquire/release/list (busy-tracking), job runner state machine (Running ->
// Succeeded/Failed via Complete), artifact save/get, and null impls.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBuildFarm_AgentPoolAcquireRelease(t *testing.T) {
	p := circleai.NewInMemoryBuildAgentPool()
	p.Register(circleai.BuildAgent{AgentID: "l1", Kind: circleai.BuildAgentLinux})
	p.Register(circleai.BuildAgent{AgentID: "l2", Kind: circleai.BuildAgentLinux})
	p.Register(circleai.BuildAgent{AgentID: "m1", Kind: circleai.BuildAgentMac})

	a, ok := p.Acquire(context.Background(), circleai.BuildAgentLinux)
	if !ok || a.AgentID != "l1" {
		t.Fatalf("first acquire = %+v ok=%v (want l1)", a, ok)
	}
	b, ok := p.Acquire(context.Background(), circleai.BuildAgentLinux)
	if !ok || b.AgentID != "l2" {
		t.Fatalf("second acquire = %+v ok=%v (want l2)", b, ok)
	}
	if _, ok := p.Acquire(context.Background(), circleai.BuildAgentLinux); ok {
		t.Fatalf("no free Linux agents should remain")
	}
	if err := p.Release(context.Background(), "l1"); err != nil {
		t.Fatalf("release: %v", err)
	}
	if a2, ok := p.Acquire(context.Background(), circleai.BuildAgentLinux); !ok || a2.AgentID != "l1" {
		t.Fatalf("released agent should re-acquire: %+v ok=%v", a2, ok)
	}
	if lst, _ := p.List(context.Background()); len(lst) != 3 {
		t.Fatalf("list = %d, want 3", len(lst))
	}
}

func TestBuildFarm_JobRunnerStateMachine(t *testing.T) {
	r := circleai.NewInMemoryBuildJobRunner()
	job, err := r.Start(context.Background(), "l1", "repo", "main")
	if err != nil || job.Phase != circleai.BuildJobRunning {
		t.Fatalf("start = %+v err=%v", job, err)
	}
	if err := r.Complete(job.JobID, true); err != nil {
		t.Fatalf("complete: %v", err)
	}
	got, ok := r.Get(context.Background(), job.JobID)
	if !ok || got.Phase != circleai.BuildJobSucceeded {
		t.Fatalf("job after complete = %+v", got)
	}
	if err := r.Complete("ghost", true); err == nil {
		t.Fatalf("complete unknown job must error")
	}
	if _, err := r.Start(context.Background(), "", "r", "b"); err == nil {
		t.Fatalf("blank agent must error")
	}
}

func TestBuildFarm_ArtifactStore(t *testing.T) {
	s := circleai.NewInMemoryBuildArtifactStore()
	if err := s.Save(context.Background(), circleai.BuildArtifact{ArtifactID: "a1", JobID: "j", Name: "out.zip", Payload: []byte{1, 2, 3}}); err != nil {
		t.Fatalf("save: %v", err)
	}
	got, ok := s.Get(context.Background(), "a1")
	if !ok || len(got.Payload) != 3 {
		t.Fatalf("get artifact = %+v ok=%v", got, ok)
	}
	if err := s.Save(context.Background(), circleai.BuildArtifact{ArtifactID: ""}); err == nil {
		t.Fatalf("blank artifact id must error")
	}
}

func TestBuildFarm_NullImpls(t *testing.T) {
	if _, ok := circleai.NullBuildAgentPoolInstance.Acquire(context.Background(), circleai.BuildAgentLinux); ok {
		t.Fatalf("null pool must not acquire")
	}
	job, _ := circleai.NullBuildJobRunnerInstance.Start(context.Background(), "a", "r", "b")
	if job.Phase != circleai.BuildJobFailed {
		t.Fatalf("null runner job = %+v", job)
	}
}
