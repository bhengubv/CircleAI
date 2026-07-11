// orchestration.go
//
// Ports CircleAI.Orchestration: the agent-swarm dispatch surface used by
// loki-mode.
//
//	AgentRole / AgentPriority / AgentStatus (enums) -> int consts (stable ordinals)
//	AgentTask / SwarmResult / QualityGateResult (records) -> value structs
//	AgentSwarmConfig (record + Default/ForDevice)   -> AgentSwarmConfig
//	IAgentDispatcher                                -> AgentDispatcher interface
//	LocalAgentDispatcher                            -> LocalAgentDispatcher
//	IncidentTrigger (static)                         -> package funcs
//
// LokiOrchestrator and SecurityOrchestrationBridge are the host-side scheduling
// layer built ON this dispatcher; only the dispatch contract + incident mapping
// are in the portable surface named by the work unit.
//
// AgentPriority ordinals are Critical=0, High=1, Normal=2, Low=3 (lower = more
// urgent), so "bump one rank" = decrement toward Critical, matching the C#.

package circleai

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// AgentRole categorises an agent's domain responsibility. Ports AgentRole
// (declaration-order ordinals: Engineering=0, Operations=1, Review=2, Security=3).
type AgentRole int

const (
	// AgentRoleEngineering — writes, reviews, and fixes code.
	AgentRoleEngineering AgentRole = 0
	// AgentRoleOperations — infrastructure, deployments, incident response.
	AgentRoleOperations AgentRole = 1
	// AgentRoleReview — quality review, testing, acceptance criteria.
	AgentRoleReview AgentRole = 2
	// AgentRoleSecurity — security analysis and vulnerability assessment.
	AgentRoleSecurity AgentRole = 3
)

// String returns the C# enum member name (used in dispatch messages).
func (r AgentRole) String() string {
	switch r {
	case AgentRoleEngineering:
		return "Engineering"
	case AgentRoleOperations:
		return "Operations"
	case AgentRoleReview:
		return "Review"
	case AgentRoleSecurity:
		return "Security"
	default:
		return "Engineering"
	}
}

// AgentPriority is a task's execution urgency (lower value = higher urgency).
// Ports AgentPriority (explicit ordinals).
type AgentPriority int

const (
	// AgentPriorityCritical — blocks all other work.
	AgentPriorityCritical AgentPriority = 0
	// AgentPriorityHigh — current session.
	AgentPriorityHigh AgentPriority = 1
	// AgentPriorityNormal — arrival order.
	AgentPriorityNormal AgentPriority = 2
	// AgentPriorityLow — best-effort.
	AgentPriorityLow AgentPriority = 3
)

// AgentStatus is a task/result lifecycle status. Ports AgentStatus
// (declaration-order ordinals: Pending=0, Running=1, Passed=2, Failed=3, Blocked=4).
type AgentStatus int

const (
	// AgentStatusPending — created, not dispatched.
	AgentStatusPending AgentStatus = 0
	// AgentStatusRunning — executing.
	AgentStatusRunning AgentStatus = 1
	// AgentStatusPassed — completed, all gates passed.
	AgentStatusPassed AgentStatus = 2
	// AgentStatusFailed — completed with an error.
	AgentStatusFailed AgentStatus = 3
	// AgentStatusBlocked — halted by a gate or missing handler.
	AgentStatusBlocked AgentStatus = 4
)

// AgentTask is a single unit of work for a swarm. Ports the AgentTask record.
type AgentTask struct {
	ID          uuid.UUID
	Role        AgentRole
	Description string
	Priority    AgentPriority
	Inputs      map[string]string
	CreatedAt   time.Time
}

// NewAgentTask stamps a fresh AgentTask with a new UUID + current UTC time.
// Ports AgentTask.Create (nil inputs -> empty map).
func NewAgentTask(role AgentRole, description string, priority AgentPriority, inputs map[string]string) AgentTask {
	if inputs == nil {
		inputs = map[string]string{}
	}
	return AgentTask{
		ID:          uuid.New(),
		Role:        role,
		Description: description,
		Priority:    priority,
		Inputs:      inputs,
		CreatedAt:   time.Now().UTC(),
	}
}

// SwarmResult is an agent handler's outcome for a task. Ports the SwarmResult
// record.
type SwarmResult struct {
	TaskID      uuid.UUID
	Role        AgentRole
	Status      AgentStatus
	Output      string
	Issues      []string
	CompletedAt time.Time
}

// QualityGateResult is a gate verdict over a SwarmResult. Ports the
// QualityGateResult record.
type QualityGateResult struct {
	Passed   bool
	Blockers []string
	Warnings []string
}

// AgentSwarmConfig tunes swarm scheduling + gates. Ports the AgentSwarmConfig
// record.
type AgentSwarmConfig struct {
	MaxConcurrency                  int
	TaskTimeout                     time.Duration
	RequireReviewPassBeforeDeploy   bool
	RequireSecurityPassBeforeDeploy bool
}

// DefaultAgentSwarmConfig returns the production-safe defaults (4 concurrent,
// 5-minute timeout, both gates enforced). Ports AgentSwarmConfig.Default.
func DefaultAgentSwarmConfig() AgentSwarmConfig {
	return AgentSwarmConfig{
		MaxConcurrency:                  4,
		TaskTimeout:                     5 * time.Minute,
		RequireReviewPassBeforeDeploy:   true,
		RequireSecurityPassBeforeDeploy: true,
	}
}

// AgentSwarmConfigForDevice sizes MaxConcurrency by device tier, keeping the
// other defaults. Ports AgentSwarmConfig.ForDevice.
func AgentSwarmConfigForDevice(probe DeviceProbe) AgentSwarmConfig {
	return AgentSwarmConfig{
		MaxConcurrency:                  DeviceTierDefaults{}.MaxConcurrency(probe.Classify(), probe.CPUCores),
		TaskTimeout:                     5 * time.Minute,
		RequireReviewPassBeforeDeploy:   true,
		RequireSecurityPassBeforeDeploy: true,
	}
}

// AgentDispatcher routes tasks to handlers and runs quality gates. Ports
// IAgentDispatcher.
type AgentDispatcher interface {
	Dispatch(ctx context.Context, task AgentTask) (SwarmResult, error)
	RunQualityGate(ctx context.Context, result SwarmResult) (QualityGateResult, error)
}

// LocalAgentDispatcher is the in-process dispatcher routing tasks to handler
// funcs registered per AgentRole. Ports LocalAgentDispatcher. Construct with
// NewLocalAgentDispatcher; call Dispose to mark it disposed.
type LocalAgentDispatcher struct {
	mu       sync.Mutex
	handlers map[AgentRole]func(ctx context.Context, task AgentTask) (SwarmResult, error)
	disposed bool
}

// NewLocalAgentDispatcher constructs an empty dispatcher.
func NewLocalAgentDispatcher() *LocalAgentDispatcher {
	return &LocalAgentDispatcher{handlers: make(map[AgentRole]func(ctx context.Context, task AgentTask) (SwarmResult, error))}
}

// RegisterHandler registers (replacing) a handler for a role. Ports
// RegisterHandler. Panics if handler is nil.
func (d *LocalAgentDispatcher) RegisterHandler(role AgentRole, handler func(ctx context.Context, task AgentTask) (SwarmResult, error)) {
	if handler == nil {
		panic("handler must not be nil")
	}
	d.mu.Lock()
	d.handlers[role] = handler
	d.mu.Unlock()
}

// Dispatch routes a task to its role handler, or returns a Blocked result when
// no handler is registered. Ports DispatchAsync. Returns an error after Dispose.
func (d *LocalAgentDispatcher) Dispatch(ctx context.Context, task AgentTask) (SwarmResult, error) {
	d.mu.Lock()
	if d.disposed {
		d.mu.Unlock()
		return SwarmResult{}, errDispatcherDisposed
	}
	handler, ok := d.handlers[task.Role]
	d.mu.Unlock()
	if ok {
		return handler(ctx, task)
	}
	return SwarmResult{
		TaskID: task.ID,
		Role:   task.Role,
		Status: AgentStatusBlocked,
		Output: fmt.Sprintf("No handler registered for role %s.", task.Role),
		Issues: []string{fmt.Sprintf("Register a handler for AgentRole.%s before dispatching.", task.Role)},
		CompletedAt: time.Now().UTC(),
	}, nil
}

// RunQualityGate classifies [CRITICAL]/[HIGH]-prefixed issues (case-insensitive)
// as blockers and the rest as warnings. Ports RunQualityGateAsync.
func (d *LocalAgentDispatcher) RunQualityGate(ctx context.Context, result SwarmResult) (QualityGateResult, error) {
	blockers := make([]string, 0)
	warnings := make([]string, 0)
	for _, i := range result.Issues {
		if hasPrefixFold(i, "[CRITICAL]") || hasPrefixFold(i, "[HIGH]") {
			blockers = append(blockers, i)
		} else {
			warnings = append(warnings, i)
		}
	}
	return QualityGateResult{Passed: len(blockers) == 0, Blockers: blockers, Warnings: warnings}, nil
}

// Dispose marks the dispatcher disposed; subsequent Dispatch calls error. Ports
// Dispose (the C# completes an internal channel; the Go port has no queue to
// close, so Dispose only flips the disposed flag).
func (d *LocalAgentDispatcher) Dispose() {
	d.mu.Lock()
	d.disposed = true
	d.mu.Unlock()
}

// errDispatcherDisposed mirrors the C# ObjectDisposedException on a disposed
// LocalAgentDispatcher.
var errDispatcherDisposed = fmt.Errorf("LocalAgentDispatcher has been disposed")

// ── IncidentTrigger ─────────────────────────────────────────────────────────

var incidentCrashTags = toSet("crash", "exception", "unhandled_error", "oom", "null_reference")
var incidentSecurityTags = toSet("auth_failure", "permission_denied", "token_expired", "injection", "overflow")

// IncidentTasksFromMemoryEntry maps an episodic memory entry to the agent tasks
// that should fire for a crash / security incident. Ports
// IncidentTrigger.FromMemoryEntry — always one Operations task on a crash tag,
// plus one Security task when a security tag is also present; empty when not an
// incident.
func IncidentTasksFromMemoryEntry(entry EpisodicMemoryEntry) []AgentTask {
	tags := entry.Tags
	if tags == nil {
		tags = map[string]string{}
	}
	isCrash := false
	for k := range tags {
		if incidentCrashTags[strings.ToLower(k)] {
			isCrash = true
			break
		}
	}
	if !isCrash {
		return []AgentTask{}
	}
	appContext := ""
	if entry.AppContext != nil {
		appContext = *entry.AppContext
	}
	tasks := []AgentTask{
		NewAgentTask(
			AgentRoleOperations,
			"ops-incident: diagnose crash recorded at "+entry.RecordedAtUTC.Format(time.RFC3339Nano),
			AgentPriorityHigh,
			map[string]string{
				"episode_id":     entry.ID.String(),
				"user_text":      entry.UserText,
				"assistant_text": entry.AssistantText,
				"app_context":    appContext,
			}),
	}
	isSecurity := false
	for k := range tags {
		if incidentSecurityTags[strings.ToLower(k)] {
			isSecurity = true
			break
		}
	}
	if isSecurity {
		tasks = append(tasks, NewAgentTask(
			AgentRoleSecurity,
			"ops-security: investigate security incident from episode "+entry.ID.String(),
			AgentPriorityCritical,
			map[string]string{
				"episode_id":  entry.ID.String(),
				"app_context": appContext,
				"tags":        joinKeys(tags),
			}))
	}
	return tasks
}

// IncidentTaskFromAnomalySignal maps a confirmed anomaly signal to a security
// AgentTask, or returns (zero, false) when below dispatchThreshold. Ports
// IncidentTrigger.FromAnomalySignal — confidence drives priority, high-severity
// vectors bump one rank toward Critical.
func IncidentTaskFromAnomalySignal(signal *AnomalySignal, dispatchThreshold float64) (AgentTask, bool) {
	if signal == nil {
		panic("signal must not be nil")
	}
	if float64(signal.Confidence) < dispatchThreshold {
		return AgentTask{}, false
	}
	var priority AgentPriority
	switch {
	case signal.Confidence >= 0.85:
		priority = AgentPriorityCritical
	case signal.Confidence >= 0.60:
		priority = AgentPriorityHigh
	default:
		priority = AgentPriorityNormal
	}
	isHighSeverity := signal.Vector == ThreatVectorControlFlowDrift ||
		signal.Vector == ThreatVectorPrivilegeEscalation ||
		signal.Vector == ThreatVectorNetworkPivot ||
		signal.Vector == ThreatVectorStateCorruption
	if isHighSeverity && priority > AgentPriorityCritical {
		// Lower numeric = higher urgency; bump one rank toward Critical.
		p := int(priority) - 1
		if p < int(AgentPriorityCritical) {
			p = int(AgentPriorityCritical)
		}
		priority = AgentPriority(p)
	}
	inputs := make(map[string]string, len(signal.Evidence)+6)
	for k, v := range signal.Evidence {
		inputs[k] = v
	}
	inputs["signal_id"] = signal.ID
	inputs["vector"] = threatVectorName(signal.Vector)
	inputs["confidence"] = formatFloat3(float64(signal.Confidence))
	inputs["affected_module"] = signal.AffectedModule
	inputs["description"] = signal.Description
	inputs["detected_at"] = signal.DetectedAt.Format(time.RFC3339Nano)
	desc := fmt.Sprintf("ops-security: anomaly %s in %s (confidence %s)",
		threatVectorName(signal.Vector), signal.AffectedModule, formatPercent0(float64(signal.Confidence)))
	return NewAgentTask(AgentRoleSecurity, desc, priority, inputs), true
}

// joinKeys returns the map keys joined by "," (order is unspecified, mirroring
// the C# string.Join over a dictionary's keys).
func joinKeys(m map[string]string) string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	return strings.Join(keys, ",")
}

// formatFloat3 formats f with 3 fractional digits (invariant), matching the C#
// signal.Confidence.ToString("F3", InvariantCulture).
func formatFloat3(f float64) string {
	neg := f < 0
	if neg {
		f = -f
	}
	scaled := int64(f*1000 + 0.5)
	intPart := scaled / 1000
	frac := scaled % 1000
	d0 := byte('0' + (frac/100)%10)
	d1 := byte('0' + (frac/10)%10)
	d2 := byte('0' + frac%10)
	sign := ""
	if neg {
		sign = "-"
	}
	return sign + itoa64(intPart) + "." + string([]byte{d0, d1, d2})
}

// Interface guard.
var _ AgentDispatcher = (*LocalAgentDispatcher)(nil)
