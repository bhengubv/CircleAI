# companion/herjarvis_contracts.py
#
# The HER/Jarvis-level companion reasoning contracts. Ported from
# CircleAI.Companion.HerJarvis (HerJarvisContracts.cs) — the C# reference.
#
# This module ports every one of the 24 HER/Jarvis contracts and their
# supporting records:
#
#   1.  IAlwaysOnPresence                                — background presence
#   2.  IFusedPerception       (+ FusedPercept)          — fused perceptual stream
#   3.  IIdentitySync                                    — memory/identity sync
#   4.  IContinuousLearner                               — online learning
#   5.  IWorldModel            (+ CausalPrediction)      — world model + causal reasoning
#   6.  IGoalPursuer           (+ LongHorizonGoal)       — multi-month goal pursuit
#   7.  IEpisodicMemory        (+ EpisodeRecord)         — episodic memory
#   8.  IVoiceIdentity                                   — per-user voice continuity
#   9.  ICalibratedConfidence  (+ ConfidenceBand)        — calibrated uncertainty
#   10. ITheoryOfMind          (+ OtherMindEstimate)     — theory of mind
#   11. IEmotionSensor         (+ EmotionFrame)          — emotion sensing
#   12. ISkillAcquisition      (+ AcquiredSkill)         — skill acquisition
#   13. IInnerMonologue        (+ SelfReflection)        — self-reflection
#   14. IPredictiveEngine      (+ AnticipatedNeed)       — anticipatory prediction
#   15. IPersonalKnowledgeGraph(+ KnowledgeNode/Relation)— personal knowledge graph
#   16. ILiveWorldKnowledge    (+ WorldFact)             — live world-knowledge stream
#   17. IBioSignalStream       (+ BioSignal)             — bio-signal integration
#   18. IPhysicalActuator      (+ PhysicalCommand/Result)— robotics / actuation
#   19. IAgentPeerNetwork      (+ AgentToAgentMessage)   — agent-to-agent protocol
#   20. IFederatedFineTuner    (+ FineTuneJobStatus)     — federated fine-tune
#   21. IFirstTokenOptimizer   (+ FirstTokenBudget)      — first-token latency
#   22. ICryptoDelegation      (+ DelegationCredential)  — cryptographic delegation
#   23. ICodeGenerationLoop    (+ CodeGenJob)            — code gen/test/deploy loop
#   24. ISelfImprovementLoop   (+ SelfImprovementVerdict)— self-improvement loop
#
# Interfaces are abc.ABC with @abstractmethod (async where the C# member is
# awaitable; async-iterator where the C# member returns IAsyncEnumerable).
# Records are frozen, slotted dataclasses. C#'s CancellationToken is modelled as
# an optional keyword-only ``ct`` argument (a cooperative-cancellation
# placeholder), matching the rest of the companion Python port.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import AsyncIterator, Mapping, Optional, Sequence


# =====================================================================
# 1. Always-on background presence across all devices.
# =====================================================================
class IAlwaysOnPresence(ABC):
    """Always-on background presence contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IAlwaysOnPresence``.
    """

    @property
    @abstractmethod
    def is_running(self) -> bool:
        """Whether the presence loop is currently running."""
        ...

    @abstractmethod
    async def start_async(self, *, ct: Optional[object] = None) -> None:
        """Start the background presence loop (idempotent)."""
        ...

    @abstractmethod
    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        """Stop the background presence loop (idempotent)."""
        ...


# =====================================================================
# 2. Fused perceptual stream.
# =====================================================================
@dataclass(frozen=True, slots=True)
class FusedPercept:
    """A single fused perceptual frame across modalities.

    Mirrors ``CircleAI.Companion.HerJarvis.FusedPercept``.
    """

    at: datetime
    vision: Optional[str]
    audio: Optional[str]
    text: Optional[str]
    sensors: Mapping[str, float]


class IFusedPerception(ABC):
    """Fused perceptual stream contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IFusedPerception``.
    """

    @abstractmethod
    def stream_async(self, *, ct: Optional[object] = None) -> AsyncIterator[FusedPercept]:
        """Stream fused percepts as they arrive."""
        ...


# =====================================================================
# 3. Memory + identity sync across devices.
# =====================================================================
class IIdentitySync(ABC):
    """Memory + identity sync contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IIdentitySync``.
    """

    @abstractmethod
    async def push_async(self, delta_json: str, *, ct: Optional[object] = None) -> None:
        """Append a delta to the sync log."""
        ...

    @abstractmethod
    async def pull_async(self, since_cursor: str, *, ct: Optional[object] = None) -> str:
        """Pull the accumulated deltas after ``since_cursor`` as a JSON envelope."""
        ...


# =====================================================================
# 4. Continuous online learning.
# =====================================================================
class IContinuousLearner(ABC):
    """Continuous online-learning contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IContinuousLearner``.
    """

    @abstractmethod
    async def register_feedback_async(
        self,
        interaction_id: str,
        reward: float,
        context_json: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Register a reward signal for a past interaction."""
        ...


# =====================================================================
# 5. World model + causal reasoning.
# =====================================================================
@dataclass(frozen=True, slots=True)
class CausalPrediction:
    """A single predicted outcome with probability and supporting factors.

    Mirrors ``CircleAI.Companion.HerJarvis.CausalPrediction``.
    """

    outcome: str
    probability: float
    supporting_factors: Sequence[str] = ()


class IWorldModel(ABC):
    """World model + causal reasoning contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IWorldModel``.
    """

    @abstractmethod
    async def predict_async(
        self, scenario_json: str, *, ct: Optional[object] = None
    ) -> CausalPrediction:
        """Predict the most likely outcome for the given scenario JSON."""
        ...


# =====================================================================
# 6. Multi-month goal pursuit with replanning.
# =====================================================================
@dataclass(frozen=True, slots=True)
class LongHorizonGoal:
    """A long-horizon goal with a plan and progress fraction.

    Mirrors ``CircleAI.Companion.HerJarvis.LongHorizonGoal``.
    """

    id: str
    description: str
    deadline_utc: datetime
    plan_json: str
    progress_fraction: float


class IGoalPursuer(ABC):
    """Multi-month goal-pursuit contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IGoalPursuer``.
    """

    @abstractmethod
    async def register_async(
        self, description: str, deadline_utc: datetime, *, ct: Optional[object] = None
    ) -> LongHorizonGoal:
        """Register a new long-horizon goal and build its initial plan."""
        ...

    @abstractmethod
    async def current_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Optional[LongHorizonGoal]:
        """Fetch the current state of a goal, or ``None`` if unknown."""
        ...

    @abstractmethod
    async def replan_async(self, id: str, *, ct: Optional[object] = None) -> None:
        """Recompute the plan for an existing goal from now to its deadline."""
        ...


# =====================================================================
# 7. Episodic memory of lived experiences.
# =====================================================================
@dataclass(frozen=True, slots=True)
class EpisodeRecord:
    """One recorded lived-experience episode.

    Mirrors ``CircleAI.Companion.HerJarvis.EpisodeRecord``.
    """

    id: str
    at: datetime
    title: str
    content_json: str


class IEpisodicMemory(ABC):
    """Episodic-memory contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IEpisodicMemory``.
    """

    @abstractmethod
    async def record_async(
        self, episode: EpisodeRecord, *, ct: Optional[object] = None
    ) -> None:
        """Record an episode."""
        ...

    @abstractmethod
    async def recall_async(
        self, query: str, take: int = 10, *, ct: Optional[object] = None
    ) -> Sequence[EpisodeRecord]:
        """Recall the ``take`` most relevant episodes for ``query``."""
        ...


# =====================================================================
# 8. Per-user voice continuity.
# =====================================================================
class IVoiceIdentity(ABC):
    """Per-user voice-continuity contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IVoiceIdentity``.
    """

    @abstractmethod
    async def identify_async(
        self, audio_pcm16: bytes, sample_rate_hz: int, *, ct: Optional[object] = None
    ) -> Optional[str]:
        """Return a stable voice fingerprint id (or ``None`` if unknown)."""
        ...

    @abstractmethod
    async def enroll_async(
        self,
        user_id: str,
        audio_pcm16: bytes,
        sample_rate_hz: int,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Enroll a sample of ``user_id``'s voice."""
        ...


# =====================================================================
# 9. Calibrated uncertainty at orchestration.
# =====================================================================
@dataclass(frozen=True, slots=True)
class ConfidenceBand:
    """A calibrated lower/upper confidence band.

    Mirrors ``CircleAI.Companion.HerJarvis.ConfidenceBand``.
    """

    lower: float
    upper: float


class ICalibratedConfidence(ABC):
    """Calibrated-confidence contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ICalibratedConfidence``.
    """

    @abstractmethod
    async def evaluate_async(
        self, answer: str, context_json: str, *, ct: Optional[object] = None
    ) -> ConfidenceBand:
        """Evaluate a calibrated confidence band for an answer."""
        ...


# =====================================================================
# 10. Theory of mind.
# =====================================================================
@dataclass(frozen=True, slots=True)
class OtherMindEstimate:
    """An estimate of another mind's likely belief state.

    Mirrors ``CircleAI.Companion.HerJarvis.OtherMindEstimate``.
    """

    target_identifier: str
    likely_belief_json: str
    confidence: float


class ITheoryOfMind(ABC):
    """Theory-of-mind contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ITheoryOfMind``.
    """

    @abstractmethod
    async def estimate_async(
        self,
        target: str,
        interaction_history_json: str,
        *,
        ct: Optional[object] = None,
    ) -> OtherMindEstimate:
        """Estimate ``target``'s likely beliefs from interaction history."""
        ...


# =====================================================================
# 11. Emotion sensing.
# =====================================================================
@dataclass(frozen=True, slots=True)
class EmotionFrame:
    """A sensed emotion frame with arousal and valence.

    Mirrors ``CircleAI.Companion.HerJarvis.EmotionFrame``.
    """

    label: str
    arousal: float
    valence: float


class IEmotionSensor(ABC):
    """Emotion-sensing contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IEmotionSensor``.
    """

    @abstractmethod
    async def sense_async(
        self, fused_json: str, *, ct: Optional[object] = None
    ) -> EmotionFrame:
        """Sense the dominant emotion frame from fused JSON."""
        ...


# =====================================================================
# 12. Skill acquisition.
# =====================================================================
@dataclass(frozen=True, slots=True)
class AcquiredSkill:
    """A skill acquired from a demonstration.

    Mirrors ``CircleAI.Companion.HerJarvis.AcquiredSkill``.
    """

    id: str
    name: str
    description_json: str


class ISkillAcquisition(ABC):
    """Skill-acquisition contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ISkillAcquisition``.
    """

    @abstractmethod
    async def acquire_async(
        self, demonstration_json: str, *, ct: Optional[object] = None
    ) -> AcquiredSkill:
        """Acquire a skill from a demonstration."""
        ...

    @abstractmethod
    async def list_async(self, *, ct: Optional[object] = None) -> Sequence[AcquiredSkill]:
        """List all acquired skills, ordered by name."""
        ...


# =====================================================================
# 13. Self-reflection / inner monologue.
# =====================================================================
@dataclass(frozen=True, slots=True)
class SelfReflection:
    """A single reflective thought captured at a moment in time.

    Mirrors ``CircleAI.Companion.HerJarvis.SelfReflection``.
    """

    thought: str
    at: datetime


class IInnerMonologue(ABC):
    """Self-reflection / inner-monologue contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IInnerMonologue``.
    """

    @abstractmethod
    async def reflect_async(
        self, context_json: str, *, ct: Optional[object] = None
    ) -> SelfReflection:
        """Reflect on the given context JSON and return a single thought."""
        ...


# =====================================================================
# 14. Predictive engine.
# =====================================================================
@dataclass(frozen=True, slots=True)
class AnticipatedNeed:
    """A predicted upcoming need, with an ETA and probability.

    Mirrors ``CircleAI.Companion.HerJarvis.AnticipatedNeed``.
    """

    description: str
    expected_by_utc: datetime
    probability: float


class IPredictiveEngine(ABC):
    """Predictive-engine contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IPredictiveEngine``.
    """

    @abstractmethod
    async def anticipate_async(
        self, horizon_minutes: int, *, ct: Optional[object] = None
    ) -> Sequence[AnticipatedNeed]:
        """Anticipate upcoming needs within ``horizon_minutes`` minutes."""
        ...


# =====================================================================
# 15. Personal knowledge graph.
# =====================================================================
@dataclass(frozen=True, slots=True)
class KnowledgeNode:
    """A node in the personal knowledge graph.

    Mirrors ``CircleAI.Companion.HerJarvis.KnowledgeNode``.
    """

    id: str
    kind: str
    name: str
    properties: Mapping[str, str]


@dataclass(frozen=True, slots=True)
class KnowledgeRelation:
    """A directed relation between two knowledge nodes.

    Mirrors ``CircleAI.Companion.HerJarvis.KnowledgeRelation``.
    """

    from_id: str
    to_id: str
    relation: str


class IPersonalKnowledgeGraph(ABC):
    """Personal-knowledge-graph contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IPersonalKnowledgeGraph``.
    """

    @abstractmethod
    async def upsert_node_async(
        self, node: KnowledgeNode, *, ct: Optional[object] = None
    ) -> None:
        """Insert or replace a node."""
        ...

    @abstractmethod
    async def upsert_relation_async(
        self, rel: KnowledgeRelation, *, ct: Optional[object] = None
    ) -> None:
        """Insert or replace a relation (deduped on to-id + relation)."""
        ...

    @abstractmethod
    async def neighbours_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Sequence[KnowledgeNode]:
        """Return the out-neighbours of a node."""
        ...


# =====================================================================
# 16. Live world-knowledge stream.
# =====================================================================
@dataclass(frozen=True, slots=True)
class WorldFact:
    """A live world-knowledge fact on a topic.

    Mirrors ``CircleAI.Companion.HerJarvis.WorldFact``.
    """

    topic: str
    summary_json: str
    at: datetime


class ILiveWorldKnowledge(ABC):
    """Live-world-knowledge stream contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ILiveWorldKnowledge``.
    """

    @abstractmethod
    def subscribe_async(
        self, topics: Sequence[str], *, ct: Optional[object] = None
    ) -> AsyncIterator[WorldFact]:
        """Subscribe to facts on the given topics."""
        ...


# =====================================================================
# 17. Bio-signal integration.
# =====================================================================
@dataclass(frozen=True, slots=True)
class BioSignal:
    """A single bio-signal sample.

    Mirrors ``CircleAI.Companion.HerJarvis.BioSignal``.
    """

    kind: str
    value: float
    at: datetime


class IBioSignalStream(ABC):
    """Bio-signal stream contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IBioSignalStream``.
    """

    @abstractmethod
    def stream_async(self, *, ct: Optional[object] = None) -> AsyncIterator[BioSignal]:
        """Stream bio-signals as they arrive."""
        ...


# =====================================================================
# 18. Robotics / physical actuation.
# =====================================================================
@dataclass(frozen=True, slots=True)
class PhysicalCommand:
    """A command dispatched to a physical device.

    Mirrors ``CircleAI.Companion.HerJarvis.PhysicalCommand``.
    """

    device_id: str
    action: str
    args: Mapping[str, str]


@dataclass(frozen=True, slots=True)
class PhysicalCommandResult:
    """The outcome of a physical command.

    Mirrors ``CircleAI.Companion.HerJarvis.PhysicalCommandResult``.
    """

    succeeded: bool
    error: Optional[str]


class IPhysicalActuator(ABC):
    """Physical-actuation contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IPhysicalActuator``.
    """

    @abstractmethod
    async def invoke_async(
        self, command: PhysicalCommand, *, ct: Optional[object] = None
    ) -> PhysicalCommandResult:
        """Invoke a command on a registered device."""
        ...


# =====================================================================
# 19. Agent-to-agent peer protocol.
# =====================================================================
@dataclass(frozen=True, slots=True)
class AgentToAgentMessage:
    """One message between two agents.

    Mirrors ``CircleAI.Companion.HerJarvis.AgentToAgentMessage``.
    """

    from_agent_id: str
    to_agent_id: str
    payload: str
    at: datetime


class IAgentPeerNetwork(ABC):
    """Agent-to-agent peer-network contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IAgentPeerNetwork``.
    """

    @abstractmethod
    async def send_async(
        self, message: AgentToAgentMessage, *, ct: Optional[object] = None
    ) -> None:
        """Deliver a message to the recipient's mailbox."""
        ...

    @abstractmethod
    def receive_async(
        self, for_agent_id: str, *, ct: Optional[object] = None
    ) -> AsyncIterator[AgentToAgentMessage]:
        """Stream messages addressed to ``for_agent_id``."""
        ...


# =====================================================================
# 20. Federated / on-device fine-tune pipeline.
# =====================================================================
@dataclass(frozen=True, slots=True)
class FineTuneJobStatus:
    """Status of a fine-tune job.

    Mirrors ``CircleAI.Companion.HerJarvis.FineTuneJobStatus``.
    """

    job_id: str
    progress: float
    error: Optional[str]


class IFederatedFineTuner(ABC):
    """Federated fine-tune contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IFederatedFineTuner``.
    """

    @abstractmethod
    async def start_async(
        self, base_model: str, training_data_path: str, *, ct: Optional[object] = None
    ) -> str:
        """Start a fine-tune job and return its id."""
        ...

    @abstractmethod
    async def status_async(
        self, job_id: str, *, ct: Optional[object] = None
    ) -> FineTuneJobStatus:
        """Return the current status of a job."""
        ...


# =====================================================================
# 21. Sub-100ms first-token latency on cheap phones.
# =====================================================================
@dataclass(frozen=True, slots=True)
class FirstTokenBudget:
    """First-token latency budget: target vs current p50.

    Mirrors ``CircleAI.Companion.HerJarvis.FirstTokenBudget``.
    """

    target_ms: int
    current_p50_ms: int


class IFirstTokenOptimizer(ABC):
    """First-token-latency optimiser contract.

    Mirrors ``CircleAI.Companion.HerJarvis.IFirstTokenOptimizer``.
    """

    @abstractmethod
    async def current_async(self, *, ct: Optional[object] = None) -> FirstTokenBudget:
        """Return the current first-token latency budget."""
        ...


# =====================================================================
# 22. Cryptographic delegation framework.
# =====================================================================
@dataclass(frozen=True, slots=True)
class DelegationCredential:
    """A signed delegation credential.

    Mirrors ``CircleAI.Companion.HerJarvis.DelegationCredential``.
    """

    issuer: str
    subject_id: str
    scope: str
    expires_at_utc: datetime
    signature: str


class ICryptoDelegation(ABC):
    """Cryptographic-delegation contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ICryptoDelegation``.
    """

    @abstractmethod
    def issue(self, subject_id: str, scope: str, lifetime: timedelta) -> DelegationCredential:
        """Issue a signed delegation credential."""
        ...

    @abstractmethod
    def verify(self, credential: DelegationCredential) -> bool:
        """Verify a delegation credential's signature and validity."""
        ...


# =====================================================================
# 23. Live code generation + test + deploy loop.
# =====================================================================
@dataclass(frozen=True, slots=True)
class CodeGenJob:
    """One code-generation job outcome.

    Mirrors ``CircleAI.Companion.HerJarvis.CodeGenJob``.
    """

    id: str
    prompt: str
    output_snippet: str
    tests_pass: bool
    deploy_hint: Optional[str]


class ICodeGenerationLoop(ABC):
    """Code-generation-loop contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ICodeGenerationLoop``.
    """

    @abstractmethod
    async def run_async(self, prompt: str, *, ct: Optional[object] = None) -> CodeGenJob:
        """Generate, syntax-check, test, and hint deployment for a prompt."""
        ...


# =====================================================================
# 24. Self-debugging / self-improvement loop.
# =====================================================================
@dataclass(frozen=True, slots=True)
class SelfImprovementVerdict:
    """The verdict of one self-improvement cycle.

    Mirrors ``CircleAI.Companion.HerJarvis.SelfImprovementVerdict``.
    """

    improvements_applied: str
    new_bench_score: float


class ISelfImprovementLoop(ABC):
    """Self-improvement-loop contract.

    Mirrors ``CircleAI.Companion.HerJarvis.ISelfImprovementLoop``.
    """

    @abstractmethod
    async def cycle_async(
        self, bench_suite_id: str, *, ct: Optional[object] = None
    ) -> SelfImprovementVerdict:
        """Run one bench-and-improve cycle for a suite."""
        ...


__all__ = [
    # 1
    "IAlwaysOnPresence",
    # 2
    "FusedPercept",
    "IFusedPerception",
    # 3
    "IIdentitySync",
    # 4
    "IContinuousLearner",
    # 5
    "CausalPrediction",
    "IWorldModel",
    # 6
    "LongHorizonGoal",
    "IGoalPursuer",
    # 7
    "EpisodeRecord",
    "IEpisodicMemory",
    # 8
    "IVoiceIdentity",
    # 9
    "ConfidenceBand",
    "ICalibratedConfidence",
    # 10
    "OtherMindEstimate",
    "ITheoryOfMind",
    # 11
    "EmotionFrame",
    "IEmotionSensor",
    # 12
    "AcquiredSkill",
    "ISkillAcquisition",
    # 13
    "SelfReflection",
    "IInnerMonologue",
    # 14
    "AnticipatedNeed",
    "IPredictiveEngine",
    # 15
    "KnowledgeNode",
    "KnowledgeRelation",
    "IPersonalKnowledgeGraph",
    # 16
    "WorldFact",
    "ILiveWorldKnowledge",
    # 17
    "BioSignal",
    "IBioSignalStream",
    # 18
    "PhysicalCommand",
    "PhysicalCommandResult",
    "IPhysicalActuator",
    # 19
    "AgentToAgentMessage",
    "IAgentPeerNetwork",
    # 20
    "FineTuneJobStatus",
    "IFederatedFineTuner",
    # 21
    "FirstTokenBudget",
    "IFirstTokenOptimizer",
    # 22
    "DelegationCredential",
    "ICryptoDelegation",
    # 23
    "CodeGenJob",
    "ICodeGenerationLoop",
    # 24
    "SelfImprovementVerdict",
    "ISelfImprovementLoop",
]
