// HerJarvisContracts.cs
//
// (3.4.0) The 24 HER/Jarvis-level companion contracts. Each is a small
// interface + supporting records. Real implementations live in
// HerJarvisRealImplementations.cs (or in vendor-specific packages
// hosts plug at startup).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion.HerJarvis;

// =====================================================================
// 1. Always-on background presence across all devices.
// =====================================================================
public interface IAlwaysOnPresence
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

// =====================================================================
// 2. Fused perceptual stream.
// =====================================================================
public sealed record FusedPercept(DateTimeOffset At, string? Vision, string? Audio, string? Text, IReadOnlyDictionary<string, double> Sensors);
public interface IFusedPerception
{
    IAsyncEnumerable<FusedPercept> StreamAsync(CancellationToken ct = default);
}

// =====================================================================
// 3. Memory + identity sync across devices.
// =====================================================================
public interface IIdentitySync
{
    ValueTask PushAsync(string deltaJson, CancellationToken ct = default);
    ValueTask<string> PullAsync(string sinceCursor, CancellationToken ct = default);
}

// =====================================================================
// 4. Continuous online learning.
// =====================================================================
public interface IContinuousLearner
{
    ValueTask RegisterFeedbackAsync(string interactionId, double reward, string contextJson, CancellationToken ct = default);
}

// =====================================================================
// 5. World model + causal reasoning.
// =====================================================================
public sealed record CausalPrediction(string Outcome, double Probability, IReadOnlyList<string> SupportingFactors);
public interface IWorldModel
{
    ValueTask<CausalPrediction> PredictAsync(string scenarioJson, CancellationToken ct = default);
}

// =====================================================================
// 6. Multi-month goal pursuit with replanning.
// =====================================================================
public sealed record LongHorizonGoal(string Id, string Description, DateTimeOffset DeadlineUtc, string PlanJson, double ProgressFraction);
public interface IGoalPursuer
{
    ValueTask<LongHorizonGoal> RegisterAsync(string description, DateTimeOffset deadlineUtc, CancellationToken ct = default);
    ValueTask<LongHorizonGoal?> CurrentAsync(string id, CancellationToken ct = default);
    ValueTask ReplanAsync(string id, CancellationToken ct = default);
}

// =====================================================================
// 7. Episodic memory of lived experiences.
// =====================================================================
public sealed record EpisodeRecord(string Id, DateTimeOffset At, string Title, string ContentJson);
public interface IEpisodicMemory
{
    ValueTask RecordAsync(EpisodeRecord episode, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EpisodeRecord>> RecallAsync(string query, int take = 10, CancellationToken ct = default);
}

// =====================================================================
// 8. Per-user voice continuity.
// =====================================================================
public interface IVoiceIdentity
{
    /// <summary>Returns a stable voice fingerprint id (or null if unknown).</summary>
    ValueTask<string?> IdentifyAsync(ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default);
    ValueTask EnrollAsync(string userId, ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default);
}

// =====================================================================
// 9. Calibrated uncertainty at orchestration.
// =====================================================================
public sealed record ConfidenceBand(double Lower, double Upper);
public interface ICalibratedConfidence
{
    ValueTask<ConfidenceBand> EvaluateAsync(string answer, string contextJson, CancellationToken ct = default);
}

// =====================================================================
// 10. Theory of mind.
// =====================================================================
public sealed record OtherMindEstimate(string TargetIdentifier, string LikelyBeliefJson, double Confidence);
public interface ITheoryOfMind
{
    ValueTask<OtherMindEstimate> EstimateAsync(string target, string interactionHistoryJson, CancellationToken ct = default);
}

// =====================================================================
// 11. Emotion sensing.
// =====================================================================
public sealed record EmotionFrame(string Label, double Arousal, double Valence);
public interface IEmotionSensor
{
    ValueTask<EmotionFrame> SenseAsync(string fusedJson, CancellationToken ct = default);
}

// =====================================================================
// 12. Skill acquisition.
// =====================================================================
public sealed record AcquiredSkill(string Id, string Name, string DescriptionJson);
public interface ISkillAcquisition
{
    ValueTask<AcquiredSkill> AcquireAsync(string demonstrationJson, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AcquiredSkill>> ListAsync(CancellationToken ct = default);
}

// =====================================================================
// 13. Self-reflection / inner monologue.
// =====================================================================
public sealed record SelfReflection(string Thought, DateTimeOffset At);
public interface IInnerMonologue
{
    ValueTask<SelfReflection> ReflectAsync(string contextJson, CancellationToken ct = default);
}

// =====================================================================
// 14. Predictive engine.
// =====================================================================
public sealed record AnticipatedNeed(string Description, DateTimeOffset ExpectedByUtc, double Probability);
public interface IPredictiveEngine
{
    ValueTask<IReadOnlyList<AnticipatedNeed>> AnticipateAsync(int horizonMinutes, CancellationToken ct = default);
}

// =====================================================================
// 15. Personal knowledge graph.
// =====================================================================
public sealed record KnowledgeNode(string Id, string Kind, string Name, IReadOnlyDictionary<string, string> Properties);
public sealed record KnowledgeRelation(string FromId, string ToId, string Relation);
public interface IPersonalKnowledgeGraph
{
    ValueTask UpsertNodeAsync(KnowledgeNode node, CancellationToken ct = default);
    ValueTask UpsertRelationAsync(KnowledgeRelation rel, CancellationToken ct = default);
    ValueTask<IReadOnlyList<KnowledgeNode>> NeighboursAsync(string id, CancellationToken ct = default);
}

// =====================================================================
// 16. Live world-knowledge stream.
// =====================================================================
public sealed record WorldFact(string Topic, string SummaryJson, DateTimeOffset At);
public interface ILiveWorldKnowledge
{
    IAsyncEnumerable<WorldFact> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken ct = default);
}

// =====================================================================
// 17. Bio-signal integration.
// =====================================================================
public sealed record BioSignal(string Kind, double Value, DateTimeOffset At);
public interface IBioSignalStream
{
    IAsyncEnumerable<BioSignal> StreamAsync(CancellationToken ct = default);
}

// =====================================================================
// 18. Robotics / physical actuation.
// =====================================================================
public sealed record PhysicalCommand(string DeviceId, string Action, IReadOnlyDictionary<string, string> Args);
public sealed record PhysicalCommandResult(bool Succeeded, string? Error);
public interface IPhysicalActuator
{
    ValueTask<PhysicalCommandResult> InvokeAsync(PhysicalCommand command, CancellationToken ct = default);
}

// =====================================================================
// 19. Agent-to-agent peer protocol.
// =====================================================================
public sealed record AgentToAgentMessage(string FromAgentId, string ToAgentId, string Payload, DateTimeOffset At);
public interface IAgentPeerNetwork
{
    ValueTask SendAsync(AgentToAgentMessage message, CancellationToken ct = default);
    IAsyncEnumerable<AgentToAgentMessage> ReceiveAsync(string forAgentId, CancellationToken ct = default);
}

// =====================================================================
// 20. Federated / on-device fine-tune pipeline.
// =====================================================================
public sealed record FineTuneJobStatus(string JobId, double Progress, string? Error);
public interface IFederatedFineTuner
{
    ValueTask<string> StartAsync(string baseModel, string trainingDataPath, CancellationToken ct = default);
    ValueTask<FineTuneJobStatus> StatusAsync(string jobId, CancellationToken ct = default);
}

// =====================================================================
// 21. Sub-100ms first-token latency on cheap phones.
// =====================================================================
public sealed record FirstTokenBudget(int TargetMs, int CurrentP50Ms);
public interface IFirstTokenOptimizer
{
    ValueTask<FirstTokenBudget> CurrentAsync(CancellationToken ct = default);
}

// =====================================================================
// 22. Cryptographic delegation framework.
// =====================================================================
public sealed record DelegationCredential(string Issuer, string SubjectId, string Scope, DateTimeOffset ExpiresAtUtc, string Signature);
public interface ICryptoDelegation
{
    DelegationCredential Issue(string subjectId, string scope, TimeSpan lifetime);
    bool Verify(DelegationCredential credential);
}

// =====================================================================
// 23. Live code generation + test + deploy loop.
// =====================================================================
public sealed record CodeGenJob(string Id, string Prompt, string OutputSnippet, bool TestsPass, string? DeployHint);
public interface ICodeGenerationLoop
{
    ValueTask<CodeGenJob> RunAsync(string prompt, CancellationToken ct = default);
}

// =====================================================================
// 24. Self-debugging / self-improvement loop.
// =====================================================================
public sealed record SelfImprovementVerdict(string ImprovementsApplied, double NewBenchScore);
public interface ISelfImprovementLoop
{
    ValueTask<SelfImprovementVerdict> CycleAsync(string benchSuiteId, CancellationToken ct = default);
}
