// orchestration_test.go
//
// Verifies the CircleAI.Orchestration port (orchestration.go): AgentTask factory,
// swarm config defaults + device sizing, LocalAgentDispatcher (handler routing,
// blocked-when-unregistered, quality gate blocker/warning classification, dispose),
// and the IncidentTrigger mappings (memory entry crash/security, anomaly signal).

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestOrchestration_AgentTaskAndConfig(t *testing.T) {
	task := circleai.NewAgentTask(circleai.AgentRoleEngineering, "do", circleai.AgentPriorityHigh, nil)
	if task.Inputs == nil || task.ID.String() == "" || task.Role != circleai.AgentRoleEngineering {
		t.Fatalf("task = %+v", task)
	}
	def := circleai.DefaultAgentSwarmConfig()
	if def.MaxConcurrency != 4 || def.TaskTimeout != 5*time.Minute || !def.RequireReviewPassBeforeDeploy {
		t.Fatalf("default config = %+v", def)
	}
	// Wearable device -> 1 concurrent (ThermalSealed classifies as wearable).
	probe := circleai.DeviceProbe{RAMAvailableBytes: 512 << 20, CPUCores: 8, ThermalClass: circleai.ThermalSealed}
	if cfg := circleai.AgentSwarmConfigForDevice(probe); cfg.MaxConcurrency != 1 {
		t.Fatalf("wearable config concurrency = %d, want 1", cfg.MaxConcurrency)
	}
}

func TestOrchestration_DispatcherRoutingAndGate(t *testing.T) {
	d := circleai.NewLocalAgentDispatcher()
	d.RegisterHandler(circleai.AgentRoleEngineering, func(ctx context.Context, task circleai.AgentTask) (circleai.SwarmResult, error) {
		return circleai.SwarmResult{TaskID: task.ID, Role: task.Role, Status: circleai.AgentStatusPassed, Output: "done"}, nil
	})
	res, err := d.Dispatch(context.Background(), circleai.NewAgentTask(circleai.AgentRoleEngineering, "x", circleai.AgentPriorityNormal, nil))
	if err != nil || res.Status != circleai.AgentStatusPassed {
		t.Fatalf("dispatch = %+v err=%v", res, err)
	}
	// No handler for Security -> Blocked.
	blocked, _ := d.Dispatch(context.Background(), circleai.NewAgentTask(circleai.AgentRoleSecurity, "y", circleai.AgentPriorityNormal, nil))
	if blocked.Status != circleai.AgentStatusBlocked {
		t.Fatalf("unregistered role should block, got %+v", blocked)
	}

	// Quality gate: [CRITICAL]/[HIGH] block, others warn.
	gate, _ := d.RunQualityGate(context.Background(), circleai.SwarmResult{
		Issues: []string{"[CRITICAL] leak", "[high] slow", "style nit"},
	})
	if gate.Passed || len(gate.Blockers) != 2 || len(gate.Warnings) != 1 {
		t.Fatalf("gate = %+v", gate)
	}

	d.Dispose()
	if _, err := d.Dispatch(context.Background(), circleai.NewAgentTask(circleai.AgentRoleEngineering, "z", circleai.AgentPriorityNormal, nil)); err == nil {
		t.Fatalf("dispatch after dispose must error")
	}
}

func TestOrchestration_IncidentFromMemoryEntry(t *testing.T) {
	ctx := "tgn.app"
	entry := circleai.EpisodicMemoryEntry{
		ID:            circleai.NewEpisodicMemoryEntry("u", "a").ID,
		RecordedAtUTC: time.Now().UTC(),
		AppContext:    &ctx,
		Tags:          map[string]string{"crash": "true", "injection": "sqli"},
	}
	tasks := circleai.IncidentTasksFromMemoryEntry(entry)
	if len(tasks) != 2 {
		t.Fatalf("crash+security should yield 2 tasks, got %d", len(tasks))
	}
	if tasks[0].Role != circleai.AgentRoleOperations || tasks[1].Role != circleai.AgentRoleSecurity {
		t.Fatalf("task roles = %v / %v", tasks[0].Role, tasks[1].Role)
	}
	// Non-incident -> empty.
	none := circleai.IncidentTasksFromMemoryEntry(circleai.EpisodicMemoryEntry{Tags: map[string]string{"locale": "en"}})
	if len(none) != 0 {
		t.Fatalf("non-incident should yield no tasks, got %d", len(none))
	}
}

func TestOrchestration_IncidentFromAnomalySignal(t *testing.T) {
	// Below threshold -> no task.
	low := circleai.NewAnomalySignal(circleai.ThreatVectorMemoryAnomaly, 0.10, "mod", "d", nil)
	if _, ok := circleai.IncidentTaskFromAnomalySignal(low, 0.30); ok {
		t.Fatalf("below-threshold signal must not dispatch")
	}
	// High-severity vector at 0.60 -> High, bumped one rank -> Critical.
	sig := circleai.NewAnomalySignal(circleai.ThreatVectorControlFlowDrift, 0.60, "companion", "drift", map[string]string{"k": "v"})
	task, ok := circleai.IncidentTaskFromAnomalySignal(sig, 0.30)
	if !ok || task.Role != circleai.AgentRoleSecurity || task.Priority != circleai.AgentPriorityCritical {
		t.Fatalf("anomaly task = %+v ok=%v (want Security/Critical)", task, ok)
	}
	if task.Inputs["k"] != "v" || task.Inputs["vector"] != "ControlFlowDrift" {
		t.Fatalf("anomaly inputs = %+v", task.Inputs)
	}
}
