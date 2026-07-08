// herjarvis_contracts.go
//
// Ported from CircleAI.Companion (HerJarvisContracts.cs) — the C# reference.
// The HER/Jarvis-level companion contracts: small interfaces + supporting
// records. Real implementations live in herjarvis_impls.go.
//
// Contracts already ported elsewhere in this tree are NOT redefined here:
//   - contract 5  IWorldModel / CausalPrediction        -> companion_world_model.go
//   - contract 10 ITheoryOfMind / OtherMindEstimate     -> companion_theory_of_mind.go
//   - contract 13 IInnerMonologue / SelfReflection      -> companion_inner_monologue.go
//   - contract 14 IPredictiveEngine / AnticipatedNeed   -> companion_predictive_engine.go
// KnowledgeNode is also already defined in memory_graph.go and is reused here.
//
// C# async surfaces (Task/ValueTask/IAsyncEnumerable) become idiomatic Go:
//   - Task/ValueTask<T>            -> (T, error) honouring ctx cancellation
//   - IAsyncEnumerable<T>          -> <-chan T (with a paired publish hook)
//   - CancellationToken ct=default -> ctx context.Context (first parameter)

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// 1. Always-on background presence across all devices.
// ---------------------------------------------------------------------------

// IAlwaysOnPresence is an always-on background presence with start/stop.
// Ported from the C# IAlwaysOnPresence.
type IAlwaysOnPresence interface {
	// IsRunning reports whether the presence is currently active.
	IsRunning() bool
	// Start begins the background presence. Idempotent.
	Start(ctx context.Context) error
	// Stop halts the background presence. Idempotent.
	Stop(ctx context.Context) error
}

// ---------------------------------------------------------------------------
// 2. Fused perceptual stream.
// ---------------------------------------------------------------------------

// FusedPercept is a single fused perceptual frame across modalities.
// Ported from the C# record
// FusedPercept(DateTimeOffset At, string? Vision, string? Audio, string? Text,
// IReadOnlyDictionary<string,double> Sensors).
type FusedPercept struct {
	At      time.Time
	Vision  *string
	Audio   *string
	Text    *string
	Sensors map[string]float64
}

// IFusedPerception streams fused percepts. Ported from the C# IFusedPerception.
// Stream returns a channel of percepts that closes when the source completes or
// ctx is cancelled.
type IFusedPerception interface {
	Stream(ctx context.Context) <-chan FusedPercept
}

// ---------------------------------------------------------------------------
// 3. Memory + identity sync across devices.
// ---------------------------------------------------------------------------

// IIdentitySync is an append-only delta log with a monotonic cursor.
// Ported from the C# IIdentitySync.
type IIdentitySync interface {
	// Push appends a delta to the log.
	Push(ctx context.Context, deltaJSON string) error
	// Pull returns a JSON envelope {"cursor":N,"deltas":[...]} of all deltas
	// after sinceCursor.
	Pull(ctx context.Context, sinceCursor string) (string, error)
}

// ---------------------------------------------------------------------------
// 4. Continuous online learning.
// ---------------------------------------------------------------------------

// IContinuousLearner registers reward feedback per interaction for online
// learning. Ported from the C# IContinuousLearner.
type IContinuousLearner interface {
	RegisterFeedback(ctx context.Context, interactionID string, reward float64, contextJSON string) error
}

// ---------------------------------------------------------------------------
// 6. Multi-month goal pursuit with replanning.
// ---------------------------------------------------------------------------

// LongHorizonGoal is a multi-month goal with a plan and progress fraction.
// Ported from the C# record
// LongHorizonGoal(string Id, string Description, DateTimeOffset DeadlineUtc,
// string PlanJson, double ProgressFraction).
type LongHorizonGoal struct {
	ID               string
	Description      string
	DeadlineUTC      time.Time
	PlanJSON         string
	ProgressFraction float64
}

// IGoalPursuer registers, tracks, and replans long-horizon goals.
// Ported from the C# IGoalPursuer. Current returns (nil, nil) for an unknown id.
type IGoalPursuer interface {
	Register(ctx context.Context, description string, deadlineUTC time.Time) (LongHorizonGoal, error)
	Current(ctx context.Context, id string) (*LongHorizonGoal, error)
	Replan(ctx context.Context, id string) error
}

// ---------------------------------------------------------------------------
// 7. Episodic memory of lived experiences.
// ---------------------------------------------------------------------------

// EpisodeRecord is a recorded lived experience. Ported from the C# record
// EpisodeRecord(string Id, DateTimeOffset At, string Title, string ContentJson).
type EpisodeRecord struct {
	ID          string
	At          time.Time
	Title       string
	ContentJSON string
}

// IEpisodicMemory records and recalls episodes by similarity.
// Ported from the C# IEpisodicMemory.
type IEpisodicMemory interface {
	Record(ctx context.Context, episode EpisodeRecord) error
	Recall(ctx context.Context, query string, take int) ([]EpisodeRecord, error)
}

// ---------------------------------------------------------------------------
// 8. Per-user voice continuity.
// ---------------------------------------------------------------------------

// IVoiceIdentity identifies and enrols speakers by voice fingerprint.
// Ported from the C# IVoiceIdentity. Identify returns (nil, nil) when the
// speaker is unknown (no enrolment above the similarity threshold).
type IVoiceIdentity interface {
	// Identify returns a stable voice fingerprint id (or nil if unknown).
	Identify(ctx context.Context, audioPCM16 []byte, sampleRateHz int) (*string, error)
	Enroll(ctx context.Context, userID string, audioPCM16 []byte, sampleRateHz int) error
}

// ---------------------------------------------------------------------------
// 9. Calibrated uncertainty at orchestration.
// ---------------------------------------------------------------------------

// ConfidenceBand is a [Lower, Upper] calibrated confidence interval.
// Ported from the C# record ConfidenceBand(double Lower, double Upper).
type ConfidenceBand struct {
	Lower float64
	Upper float64
}

// ICalibratedConfidence evaluates a calibrated confidence band for an answer.
// Ported from the C# ICalibratedConfidence.
type ICalibratedConfidence interface {
	Evaluate(ctx context.Context, answer, contextJSON string) (ConfidenceBand, error)
}

// ---------------------------------------------------------------------------
// 11. Emotion sensing.
// ---------------------------------------------------------------------------

// EmotionFrame is a sensed emotion label with arousal/valence.
// Ported from the C# record
// EmotionFrame(string Label, double Arousal, double Valence).
type EmotionFrame struct {
	Label   string
	Arousal float64
	Valence float64
}

// IEmotionSensor infers an EmotionFrame from a fused JSON blob.
// Ported from the C# IEmotionSensor.
type IEmotionSensor interface {
	Sense(ctx context.Context, fusedJSON string) (EmotionFrame, error)
}

// ---------------------------------------------------------------------------
// 12. Skill acquisition.
// ---------------------------------------------------------------------------

// AcquiredSkill is a skill learned from a demonstration. Ported from the C#
// record AcquiredSkill(string Id, string Name, string DescriptionJson).
type AcquiredSkill struct {
	ID              string
	Name            string
	DescriptionJSON string
}

// ISkillAcquisition acquires and lists skills from demonstrations.
// Ported from the C# ISkillAcquisition.
type ISkillAcquisition interface {
	Acquire(ctx context.Context, demonstrationJSON string) (AcquiredSkill, error)
	List(ctx context.Context) ([]AcquiredSkill, error)
}

// ---------------------------------------------------------------------------
// 15. Personal knowledge graph.
// ---------------------------------------------------------------------------
//
// KnowledgeNode is already defined in memory_graph.go with the same shape as the
// C# record KnowledgeNode(string Id, string Kind, string Name,
// IReadOnlyDictionary<string,string> Properties) and is reused here.

// KnowledgeRelation is a directed, typed edge between two knowledge nodes.
// Ported from the C# record
// KnowledgeRelation(string FromId, string ToId, string Relation).
type KnowledgeRelation struct {
	FromID   string
	ToID     string
	Relation string
}

// IPersonalKnowledgeGraph is an adjacency-list personal knowledge graph.
// Ported from the C# IPersonalKnowledgeGraph.
type IPersonalKnowledgeGraph interface {
	UpsertNode(ctx context.Context, node KnowledgeNode) error
	UpsertRelation(ctx context.Context, rel KnowledgeRelation) error
	Neighbours(ctx context.Context, id string) ([]KnowledgeNode, error)
}

// ---------------------------------------------------------------------------
// 16. Live world-knowledge stream.
// ---------------------------------------------------------------------------

// WorldFact is a fact published to a topic. Ported from the C# record
// WorldFact(string Topic, string SummaryJson, DateTimeOffset At).
type WorldFact struct {
	Topic       string
	SummaryJSON string
	At          time.Time
}

// ILiveWorldKnowledge is a topic-scoped pub/sub of world facts.
// Ported from the C# ILiveWorldKnowledge. Subscribe returns a channel that
// closes when ctx is cancelled.
type ILiveWorldKnowledge interface {
	Subscribe(ctx context.Context, topics []string) <-chan WorldFact
}

// ---------------------------------------------------------------------------
// 17. Bio-signal integration.
// ---------------------------------------------------------------------------

// BioSignal is a single bio-signal reading. Ported from the C# record
// BioSignal(string Kind, double Value, DateTimeOffset At).
type BioSignal struct {
	Kind  string
	Value float64
	At    time.Time
}

// IBioSignalStream streams bio-signals. Ported from the C# IBioSignalStream.
type IBioSignalStream interface {
	Stream(ctx context.Context) <-chan BioSignal
}

// ---------------------------------------------------------------------------
// 18. Robotics / physical actuation.
// ---------------------------------------------------------------------------

// PhysicalCommand is a command dispatched to a physical device. Ported from the
// C# record PhysicalCommand(string DeviceId, string Action,
// IReadOnlyDictionary<string,string> Args).
type PhysicalCommand struct {
	DeviceID string
	Action   string
	Args     map[string]string
}

// PhysicalCommandResult is the outcome of a physical command. Ported from the C#
// record PhysicalCommandResult(bool Succeeded, string? Error).
type PhysicalCommandResult struct {
	Succeeded bool
	Error     *string
}

// IPhysicalActuator dispatches physical commands to registered devices.
// Ported from the C# IPhysicalActuator.
type IPhysicalActuator interface {
	Invoke(ctx context.Context, command PhysicalCommand) (PhysicalCommandResult, error)
}

// ---------------------------------------------------------------------------
// 19. Agent-to-agent peer protocol.
// ---------------------------------------------------------------------------

// AgentToAgentMessage is a message exchanged between agents. Ported from the C#
// record AgentToAgentMessage(string FromAgentId, string ToAgentId,
// string Payload, DateTimeOffset At).
type AgentToAgentMessage struct {
	FromAgentID string
	ToAgentID   string
	Payload     string
	At          time.Time
}

// IAgentPeerNetwork is an in-memory agent-to-agent mailbox network.
// Ported from the C# IAgentPeerNetwork. Receive returns a channel that closes
// when ctx is cancelled.
type IAgentPeerNetwork interface {
	Send(ctx context.Context, message AgentToAgentMessage) error
	Receive(ctx context.Context, forAgentID string) <-chan AgentToAgentMessage
}

// ---------------------------------------------------------------------------
// 20. Federated / on-device fine-tune pipeline.
// ---------------------------------------------------------------------------

// FineTuneJobStatus is the status of a fine-tune job. Ported from the C# record
// FineTuneJobStatus(string JobId, double Progress, string? Error).
type FineTuneJobStatus struct {
	JobID    string
	Progress float64
	Error    *string
}

// IFederatedFineTuner starts and tracks on-device fine-tune jobs.
// Ported from the C# IFederatedFineTuner.
type IFederatedFineTuner interface {
	Start(ctx context.Context, baseModel, trainingDataPath string) (string, error)
	Status(ctx context.Context, jobID string) (FineTuneJobStatus, error)
}

// ---------------------------------------------------------------------------
// 21. Sub-100ms first-token latency on cheap phones.
// ---------------------------------------------------------------------------

// FirstTokenBudget is the target vs current p50 first-token latency budget.
// Ported from the C# record FirstTokenBudget(int TargetMs, int CurrentP50Ms).
type FirstTokenBudget struct {
	TargetMs     int
	CurrentP50Ms int
}

// IFirstTokenOptimizer reports the current first-token latency budget.
// Ported from the C# IFirstTokenOptimizer.
type IFirstTokenOptimizer interface {
	Current(ctx context.Context) (FirstTokenBudget, error)
}

// ---------------------------------------------------------------------------
// 22. Cryptographic delegation framework.
// ---------------------------------------------------------------------------

// DelegationCredential is a signed delegation of scope to a subject.
// Ported from the C# record DelegationCredential(string Issuer,
// string SubjectId, string Scope, DateTimeOffset ExpiresAtUtc, string Signature).
type DelegationCredential struct {
	Issuer       string
	SubjectID    string
	Scope        string
	ExpiresAtUTC time.Time
	Signature    string
}

// ICryptoDelegation issues and verifies delegation credentials.
// Ported from the C# ICryptoDelegation. Both operations are synchronous in the
// C# reference.
type ICryptoDelegation interface {
	Issue(subjectID, scope string, lifetime time.Duration) (DelegationCredential, error)
	Verify(credential DelegationCredential) bool
}

// ---------------------------------------------------------------------------
// 23. Live code generation + test + deploy loop.
// ---------------------------------------------------------------------------

// CodeGenJob is one code-generation run outcome. Ported from the C# record
// CodeGenJob(string Id, string Prompt, string OutputSnippet, bool TestsPass,
// string? DeployHint).
type CodeGenJob struct {
	ID            string
	Prompt        string
	OutputSnippet string
	TestsPass     bool
	DeployHint    *string
}

// ICodeGenerationLoop generates code, tests it, and hints deployment.
// Ported from the C# ICodeGenerationLoop.
type ICodeGenerationLoop interface {
	Run(ctx context.Context, prompt string) (CodeGenJob, error)
}

// ---------------------------------------------------------------------------
// 24. Self-debugging / self-improvement loop.
// ---------------------------------------------------------------------------

// SelfImprovementVerdict is the outcome of a self-improvement cycle. Ported from
// the C# record
// SelfImprovementVerdict(string ImprovementsApplied, double NewBenchScore).
type SelfImprovementVerdict struct {
	ImprovementsApplied string
	NewBenchScore       float64
}

// ISelfImprovementLoop runs a bench-driven self-improvement cycle.
// Ported from the C# ISelfImprovementLoop.
type ISelfImprovementLoop interface {
	Cycle(ctx context.Context, benchSuiteID string) (SelfImprovementVerdict, error)
}
