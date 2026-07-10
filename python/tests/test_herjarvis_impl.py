"""test_herjarvis_impl.py

Verifies the in-process HER/Jarvis implementations ported from
CircleAI.Companion.HerJarvis (HerJarvisRealImplementations.cs). Each impl is
deterministic and exercised against its contract's observable behaviour.
"""
from __future__ import annotations

import asyncio
import struct
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.companion.herjarvis_contracts import (
    AgentToAgentMessage,
    ConfidenceBand,
    EpisodeRecord,
    IAgentPeerNetwork,
    IAlwaysOnPresence,
    IBioSignalStream,
    ICalibratedConfidence,
    ICodeGenerationLoop,
    IContinuousLearner,
    ICryptoDelegation,
    IEmotionSensor,
    IEpisodicMemory,
    IFederatedFineTuner,
    IFirstTokenOptimizer,
    IFusedPerception,
    IGoalPursuer,
    IIdentitySync,
    ILiveWorldKnowledge,
    IPersonalKnowledgeGraph,
    IPhysicalActuator,
    ISelfImprovementLoop,
    ISkillAcquisition,
    IVoiceIdentity,
    KnowledgeNode,
    KnowledgeRelation,
    PhysicalCommand,
    PhysicalCommandResult,
    WorldFact,
    FusedPercept,
    BioSignal,
)
from circle_ai.companion.herjarvis_impl import (
    AdjacencyPersonalKnowledgeGraph,
    ChannelBioSignalStream,
    ChannelFusedPerception,
    DemoStoreSkillAcquisition,
    EnergyBandVoiceIdentity,
    EwaContinuousLearner,
    HeartbeatAlwaysOnPresence,
    HistoricalCalibratedConfidence,
    HmacCryptoDelegation,
    InMemoryFederatedFineTuner,
    InMemoryGoalPursuer,
    JsonIdentitySync,
    KeywordEmotionSensor,
    MailboxAgentPeerNetwork,
    RegistryPhysicalActuator,
    SlidingP50FirstTokenOptimizer,
    SyntaxCheckingCodeGenerationLoop,
    TfEpisodicMemory,
    TopicLiveWorldKnowledge,
    TrackingSelfImprovementLoop,
    _dto_round_trip,
)


def _now() -> datetime:
    return datetime(2026, 7, 8, 12, 0, 0, tzinfo=timezone.utc)


# ── 1. HeartbeatAlwaysOnPresence ───────────────────────────────────────────


async def test_presence_implements_interface_and_toggles() -> None:
    p = HeartbeatAlwaysOnPresence(timedelta(milliseconds=5))
    assert isinstance(p, IAlwaysOnPresence)
    assert p.is_running is False
    await p.start_async()
    assert p.is_running is True
    await asyncio.sleep(0.03)
    assert p.heartbeats >= 1
    await p.stop_async()
    assert p.is_running is False


async def test_presence_start_is_idempotent() -> None:
    p = HeartbeatAlwaysOnPresence(timedelta(seconds=1))
    await p.start_async()
    t = p._task
    await p.start_async()  # no-op
    assert p._task is t
    await p.stop_async()


# ── 2. ChannelFusedPerception ──────────────────────────────────────────────


async def test_fused_perception_publish_stream_complete() -> None:
    fp = ChannelFusedPerception()
    assert isinstance(fp, IFusedPerception)
    a = FusedPercept(_now(), "v", None, "hi", {"lux": 1.0})
    fp.publish(a)
    fp.complete()
    got = [p async for p in fp.stream_async()]
    assert got == [a]


async def test_fused_perception_rejects_none() -> None:
    with pytest.raises(ValueError):
        ChannelFusedPerception().publish(None)  # type: ignore[arg-type]


# ── 3. JsonIdentitySync ────────────────────────────────────────────────────


async def test_identity_sync_cursor_envelope() -> None:
    s = JsonIdentitySync()
    assert isinstance(s, IIdentitySync)
    await s.push_async('{"a":1}')
    await s.push_async('{"b":2}')
    env = await s.pull_async("0")
    assert env == '{"cursor":2,"deltas":[{"a":1},{"b":2}]}'
    # Pulling after the last cursor yields no deltas but preserves the cursor.
    env2 = await s.pull_async("2")
    assert env2 == '{"cursor":2,"deltas":[]}'


async def test_identity_sync_partial_pull() -> None:
    s = JsonIdentitySync()
    await s.push_async('{"a":1}')
    await s.push_async('{"b":2}')
    env = await s.pull_async("1")
    assert env == '{"cursor":2,"deltas":[{"b":2}]}'


async def test_identity_sync_rejects_none() -> None:
    with pytest.raises(ValueError):
        await JsonIdentitySync().push_async(None)  # type: ignore[arg-type]


# ── 4. EwaContinuousLearner ────────────────────────────────────────────────


async def test_ewa_first_observation_is_raw_reward() -> None:
    learner = EwaContinuousLearner(alpha=0.2)
    assert isinstance(learner, IContinuousLearner)
    await learner.register_feedback_async("i1", 1.0, "{}")
    assert learner.average_reward_of("i1") == 1.0
    assert learner.observations_of("i1") == 1


async def test_ewa_blends_subsequent_rewards() -> None:
    learner = EwaContinuousLearner(alpha=0.5)
    await learner.register_feedback_async("i1", 1.0, "{}")
    await learner.register_feedback_async("i1", 0.0, "{}")
    # 1.0*(1-0.5) + 0.0*0.5 = 0.5
    assert learner.average_reward_of("i1") == pytest.approx(0.5)
    assert learner.observations_of("i1") == 2


def test_ewa_alpha_bounds() -> None:
    with pytest.raises(ValueError):
        EwaContinuousLearner(alpha=0.0)
    with pytest.raises(ValueError):
        EwaContinuousLearner(alpha=1.5)


async def test_ewa_rejects_blank_id() -> None:
    with pytest.raises(ValueError):
        await EwaContinuousLearner().register_feedback_async("  ", 1.0, "{}")


# ── 6. InMemoryGoalPursuer ─────────────────────────────────────────────────


async def test_goal_register_and_current() -> None:
    gp = InMemoryGoalPursuer(now_provider=_now)
    assert isinstance(gp, IGoalPursuer)
    deadline = _now() + timedelta(days=60)
    g = await gp.register_async("ship the thing", deadline)
    assert g.description == "ship the thing"
    assert g.progress_fraction == 0.0
    assert '"milestones":[' in g.plan_json
    fetched = await gp.current_async(g.id)
    assert fetched == g


async def test_goal_deadline_must_be_future() -> None:
    gp = InMemoryGoalPursuer(now_provider=_now)
    with pytest.raises(ValueError):
        await gp.register_async("x", _now() - timedelta(days=1))


async def test_goal_progress_and_replan() -> None:
    gp = InMemoryGoalPursuer(now_provider=_now)
    g = await gp.register_async("x", _now() + timedelta(days=30))
    gp.progress(g.id, 0.5)
    assert (await gp.current_async(g.id)).progress_fraction == 0.5
    await gp.replan_async(g.id)  # preserves progress
    assert (await gp.current_async(g.id)).progress_fraction == 0.5


async def test_goal_replan_unknown_raises() -> None:
    gp = InMemoryGoalPursuer(now_provider=_now)
    with pytest.raises(RuntimeError):
        await gp.replan_async("nope")


async def test_goal_plan_uses_dotnet_roundtrip_dates() -> None:
    gp = InMemoryGoalPursuer(now_provider=_now)
    g = await gp.register_async("x", _now() + timedelta(days=28))
    # Round-trip format has a 7-digit fraction and +00:00 offset.
    assert "+00:00" in g.plan_json
    assert ".0000000" in g.plan_json  # zero fractional seconds rendered as 7 zeros


# ── 7. TfEpisodicMemory ────────────────────────────────────────────────────


async def test_episodic_recall_by_term_overlap() -> None:
    mem = TfEpisodicMemory()
    assert isinstance(mem, IEpisodicMemory)
    await mem.record_async(EpisodeRecord("e1", _now(), "Dentist visit", '{"note":"cleaning"}'))
    await mem.record_async(EpisodeRecord("e2", _now(), "Grocery run", '{"note":"milk"}'))
    hits = await mem.recall_async("dentist cleaning", take=5)
    assert [h.id for h in hits] == ["e1"]


async def test_episodic_empty_query_returns_empty() -> None:
    mem = TfEpisodicMemory()
    await mem.record_async(EpisodeRecord("e1", _now(), "t", "c"))
    assert await mem.recall_async("!!!") == []


async def test_episodic_rejects_bad_input() -> None:
    mem = TfEpisodicMemory()
    with pytest.raises(ValueError):
        await mem.record_async(EpisodeRecord("  ", _now(), "t", "c"))
    with pytest.raises(ValueError):
        await mem.recall_async("q", take=0)


# ── 8. EnergyBandVoiceIdentity ─────────────────────────────────────────────


def _tone_pcm16(freq: float, sample_rate: int = 16000, seconds: float = 0.5) -> bytes:
    import math

    n = int(sample_rate * seconds)
    out = bytearray()
    for i in range(n):
        v = int(0.6 * 32767 * math.sin(2 * math.pi * freq * i / sample_rate))
        out += struct.pack("<h", v)
    return bytes(out)


async def test_voice_enroll_then_identify_same_speaker() -> None:
    vi = EnergyBandVoiceIdentity()
    assert isinstance(vi, IVoiceIdentity)
    a = _tone_pcm16(220.0)
    await vi.enroll_async("alice", a, 16000)
    # Identifying the same signal should match (cosine sim = 1.0 > 0.85).
    who = await vi.identify_async(a, 16000)
    assert who == "alice"


async def test_voice_unknown_when_no_enrolment() -> None:
    vi = EnergyBandVoiceIdentity()
    assert await vi.identify_async(_tone_pcm16(300.0), 16000) is None


async def test_voice_rejects_blank_user() -> None:
    with pytest.raises(ValueError):
        await EnergyBandVoiceIdentity().enroll_async("", _tone_pcm16(200.0), 16000)


# ── 9. HistoricalCalibratedConfidence ──────────────────────────────────────


async def test_confidence_passthrough_when_history_small() -> None:
    cc = HistoricalCalibratedConfidence()
    assert isinstance(cc, ICalibratedConfidence)
    band = await cc.evaluate_async("A fairly complete answer of some length.", '{"c":1}')
    assert isinstance(band, ConfidenceBand)
    assert 0.0 <= band.lower <= band.upper <= 1.0


async def test_confidence_hedges_lower_the_score() -> None:
    cc = HistoricalCalibratedConfidence()
    plain = await cc.evaluate_async("The answer is 42 with certainty here.", "{}")
    hedged = await cc.evaluate_async("Maybe perhaps possibly it might be 42.", "{}")
    # Midpoints: hedged should be <= plain.
    assert (hedged.lower + hedged.upper) <= (plain.lower + plain.upper)


async def test_confidence_calibrates_from_history() -> None:
    cc = HistoricalCalibratedConfidence()
    for _ in range(5):
        cc.record_outcome(0.5, True)
    band = await cc.evaluate_async("x", "{}")
    # All 5 nearest were correct -> calibrated ~1.0 -> band near [0.95, 1.0].
    assert band.upper == pytest.approx(1.0)
    assert band.lower >= 0.9


async def test_confidence_rejects_none_answer() -> None:
    with pytest.raises(ValueError):
        await HistoricalCalibratedConfidence().evaluate_async(None, "{}")  # type: ignore[arg-type]


# ── 11. KeywordEmotionSensor ───────────────────────────────────────────────


async def test_emotion_neutral_on_no_hits() -> None:
    es = KeywordEmotionSensor()
    assert isinstance(es, IEmotionSensor)
    f = await es.sense_async('{"text":"the report is due"}')
    assert (f.label, f.arousal, f.valence) == ("neutral", 0.0, 0.0)


async def test_emotion_dominant_label_and_signs() -> None:
    es = KeywordEmotionSensor()
    f = await es.sense_async("I am so happy and excited, I love this wonderful day")
    assert f.label == "joy"
    assert f.valence > 0
    assert f.arousal > 0


async def test_emotion_rejects_none() -> None:
    with pytest.raises(ValueError):
        await KeywordEmotionSensor().sense_async(None)  # type: ignore[arg-type]


# ── 12. DemoStoreSkillAcquisition ──────────────────────────────────────────


async def test_skill_extracts_name_and_lists_sorted() -> None:
    sk = DemoStoreSkillAcquisition()
    assert isinstance(sk, ISkillAcquisition)
    await sk.acquire_async('{"name":"zeta"}')
    await sk.acquire_async('{"name":"alpha"}')
    listed = await sk.list_async()
    assert [s.name for s in listed] == ["alpha", "zeta"]


async def test_skill_default_name_when_absent() -> None:
    sk = DemoStoreSkillAcquisition()
    s = await sk.acquire_async('{"other":1}')
    assert s.name.startswith("skill-")


async def test_skill_rejects_none() -> None:
    with pytest.raises(ValueError):
        await DemoStoreSkillAcquisition().acquire_async(None)  # type: ignore[arg-type]


# ── 15. AdjacencyPersonalKnowledgeGraph ────────────────────────────────────


async def test_kg_neighbours_resolves_relations() -> None:
    kg = AdjacencyPersonalKnowledgeGraph()
    assert isinstance(kg, IPersonalKnowledgeGraph)
    await kg.upsert_node_async(KnowledgeNode("a", "person", "Ann", {}))
    await kg.upsert_node_async(KnowledgeNode("b", "city", "Durban", {}))
    await kg.upsert_relation_async(KnowledgeRelation("a", "b", "lives_in"))
    ns = await kg.neighbours_async("a")
    assert [n.id for n in ns] == ["b"]


async def test_kg_relation_dedup() -> None:
    kg = AdjacencyPersonalKnowledgeGraph()
    await kg.upsert_node_async(KnowledgeNode("a", "k", "A", {}))
    await kg.upsert_node_async(KnowledgeNode("b", "k", "B", {}))
    await kg.upsert_relation_async(KnowledgeRelation("a", "b", "r"))
    await kg.upsert_relation_async(KnowledgeRelation("a", "b", "r"))  # dup collapses
    assert len(await kg.neighbours_async("a")) == 1


async def test_kg_dangling_targets_skipped_and_unknown_empty() -> None:
    kg = AdjacencyPersonalKnowledgeGraph()
    await kg.upsert_node_async(KnowledgeNode("a", "k", "A", {}))
    await kg.upsert_relation_async(KnowledgeRelation("a", "ghost", "r"))
    assert await kg.neighbours_async("a") == []
    assert await kg.neighbours_async("missing") == []


# ── 16. TopicLiveWorldKnowledge ────────────────────────────────────────────


async def test_live_world_knowledge_topic_delivery() -> None:
    lk = TopicLiveWorldKnowledge()
    assert isinstance(lk, ILiveWorldKnowledge)

    async def collect():
        out = []
        async for f in lk.subscribe_async(["markets"]):
            out.append(f)
            if len(out) == 1:
                return out
        return out

    task = asyncio.ensure_future(collect())
    await asyncio.sleep(0.02)
    lk.publish(WorldFact("markets", '{"idx":1}', _now()))
    got = await asyncio.wait_for(task, timeout=2)
    assert got[0].topic == "markets"


# ── 17. ChannelBioSignalStream ─────────────────────────────────────────────


async def test_bio_signal_stream() -> None:
    bs = ChannelBioSignalStream()
    assert isinstance(bs, IBioSignalStream)
    bs.publish(BioSignal("hr", 72.0, _now()))
    bs.complete()
    got = [s async for s in bs.stream_async()]
    assert [s.kind for s in got] == ["hr"]


# ── 18. RegistryPhysicalActuator ───────────────────────────────────────────


async def test_actuator_dispatch_and_unknown_device() -> None:
    act = RegistryPhysicalActuator()
    assert isinstance(act, IPhysicalActuator)

    async def handler(cmd: PhysicalCommand, ct) -> PhysicalCommandResult:
        return PhysicalCommandResult(True, None)

    act.register_device("lamp", handler)
    ok = await act.invoke_async(PhysicalCommand("lamp", "on", {}))
    assert ok.succeeded is True
    miss = await act.invoke_async(PhysicalCommand("door", "open", {}))
    assert miss.succeeded is False
    assert "Unknown device" in miss.error


# ── 19. MailboxAgentPeerNetwork ────────────────────────────────────────────


async def test_agent_peer_mailbox_delivery() -> None:
    net = MailboxAgentPeerNetwork()
    assert isinstance(net, IAgentPeerNetwork)
    msg = AgentToAgentMessage("a1", "a2", "ping", _now())
    await net.send_async(msg)

    async def recv_one():
        async for m in net.receive_async("a2"):
            return m

    got = await asyncio.wait_for(recv_one(), timeout=2)
    assert got.payload == "ping"


# ── 20. InMemoryFederatedFineTuner ─────────────────────────────────────────


async def test_fine_tuner_runs_to_completion() -> None:
    ft = InMemoryFederatedFineTuner()
    assert isinstance(ft, IFederatedFineTuner)
    job = await ft.start_async("base", "no-such-file.jsonl")
    # Await the background training task.
    await asyncio.gather(*ft._tasks)
    status = await ft.status_async(job)
    assert status.progress == pytest.approx(1.0)
    assert status.error is None


async def test_fine_tuner_unknown_job() -> None:
    ft = InMemoryFederatedFineTuner()
    s = await ft.status_async("nope")
    assert s.error == "unknown job"


async def test_fine_tuner_rejects_blanks() -> None:
    ft = InMemoryFederatedFineTuner()
    with pytest.raises(ValueError):
        await ft.start_async("", "path")
    with pytest.raises(ValueError):
        await ft.start_async("m", "  ")


# ── 21. SlidingP50FirstTokenOptimizer ──────────────────────────────────────


async def test_first_token_p50() -> None:
    opt = SlidingP50FirstTokenOptimizer(target_ms=100, window_size=8)
    assert isinstance(opt, IFirstTokenOptimizer)
    for ms in [10, 20, 30, 40, 50]:
        opt.record_first_token_latency(ms)
    budget = await opt.current_async()
    assert budget.target_ms == 100
    # sorted[len//2] = sorted[2] = 30
    assert budget.current_p50_ms == 30


async def test_first_token_window_evicts_oldest() -> None:
    opt = SlidingP50FirstTokenOptimizer(target_ms=50, window_size=2)
    opt.record_first_token_latency(1000)
    opt.record_first_token_latency(10)
    opt.record_first_token_latency(20)  # evicts 1000
    budget = await opt.current_async()
    # window = [10, 20], sorted[1] = 20
    assert budget.current_p50_ms == 20


async def test_first_token_empty_is_zero() -> None:
    opt = SlidingP50FirstTokenOptimizer()
    assert (await opt.current_async()).current_p50_ms == 0


def test_first_token_bounds() -> None:
    with pytest.raises(ValueError):
        SlidingP50FirstTokenOptimizer(target_ms=0)
    with pytest.raises(ValueError):
        SlidingP50FirstTokenOptimizer(window_size=0)


# ── 22. HmacCryptoDelegation ───────────────────────────────────────────────


def test_delegation_issue_verify_roundtrip() -> None:
    cd = HmacCryptoDelegation(now_provider=_now)
    assert isinstance(cd, ICryptoDelegation)
    cred = cd.issue("user-1", "read:memory", timedelta(hours=1))
    assert cred.issuer == "circleai-companion"
    assert cred.subject_id == "user-1"
    assert cd.verify(cred) is True


def test_delegation_rejects_tampered_scope() -> None:
    cd = HmacCryptoDelegation(now_provider=_now)
    cred = cd.issue("user-1", "read", timedelta(hours=1))
    tampered = type(cred)(cred.issuer, cred.subject_id, "admin", cred.expires_at_utc, cred.signature)
    assert cd.verify(tampered) is False


def test_delegation_rejects_expired() -> None:
    cd = HmacCryptoDelegation(now_provider=_now)
    cred = cd.issue("user-1", "read", timedelta(hours=1))
    # A verifier whose clock is past expiry rejects.
    later = HmacCryptoDelegation(signer=cd._signer, now_provider=lambda: _now() + timedelta(hours=2))
    assert later.verify(cred) is False


def test_delegation_rejects_wrong_issuer() -> None:
    cd = HmacCryptoDelegation(issuer="a", now_provider=_now)
    cred = cd.issue("u", "s", timedelta(hours=1))
    other = HmacCryptoDelegation(issuer="b", signer=cd._signer, now_provider=_now)
    assert other.verify(cred) is False


def test_delegation_injected_signer_used() -> None:
    from circle_ai.companion.herjarvis_impl import ISignatureProvider

    class RecordingSigner(ISignatureProvider):
        def __init__(self) -> None:
            self.signed = False

        def sign(self, payload: bytes) -> bytes:
            self.signed = True
            return b"sig"

        def verify(self, payload: bytes, signature: bytes) -> bool:
            return signature == b"sig"

    signer = RecordingSigner()
    cd = HmacCryptoDelegation(signer=signer, now_provider=_now)
    cred = cd.issue("u", "s", timedelta(hours=1))
    assert signer.signed is True
    assert cd.verify(cred) is True


def test_delegation_validation() -> None:
    cd = HmacCryptoDelegation(now_provider=_now)
    with pytest.raises(ValueError):
        cd.issue("", "s", timedelta(hours=1))
    with pytest.raises(ValueError):
        cd.issue("u", "", timedelta(hours=1))
    with pytest.raises(ValueError):
        cd.issue("u", "s", timedelta(0))


# ── 23. SyntaxCheckingCodeGenerationLoop ───────────────────────────────────


async def test_codegen_default_balanced_passes() -> None:
    loop = SyntaxCheckingCodeGenerationLoop()
    assert isinstance(loop, ICodeGenerationLoop)
    job = await loop.run_async("write a function")
    assert job.tests_pass is True
    assert job.deploy_hint == "run inline"


async def test_codegen_unbalanced_fails() -> None:
    loop = SyntaxCheckingCodeGenerationLoop(generator=lambda p, ct: _ret("{ unbalanced"))
    job = await loop.run_async("x")
    assert job.tests_pass is False
    assert job.deploy_hint is None


async def test_codegen_class_snippet_stages_nuget() -> None:
    loop = SyntaxCheckingCodeGenerationLoop(generator=lambda p, ct: _ret("public class C {}"))
    job = await loop.run_async("x")
    assert job.tests_pass is True
    assert job.deploy_hint == "stage as nuget"


async def test_codegen_rejects_blank_prompt() -> None:
    with pytest.raises(ValueError):
        await SyntaxCheckingCodeGenerationLoop().run_async("   ")


async def _ret(s: str) -> str:
    return s


# ── 24. TrackingSelfImprovementLoop ────────────────────────────────────────


async def test_self_improvement_new_best_then_no_regression() -> None:
    scores = iter([0.6, 0.6])

    async def bench(_id, ct):
        return next(scores)

    loop = TrackingSelfImprovementLoop(run_bench=bench)
    assert isinstance(loop, ISelfImprovementLoop)
    v1 = await loop.cycle_async("suite")
    assert v1.improvements_applied == "new best"
    assert loop.best_score_for("suite") == pytest.approx(0.6)
    v2 = await loop.cycle_async("suite")
    assert v2.improvements_applied == "no regression"


async def test_self_improvement_regression_proposes() -> None:
    scores = iter([0.8, 0.3])

    async def bench(_id, ct):
        return next(scores)

    async def propose(_id, cur, ct):
        return f"tuned@{cur}"

    loop = TrackingSelfImprovementLoop(run_bench=bench, propose_improvement=propose)
    await loop.cycle_async("s")  # sets best 0.8
    v = await loop.cycle_async("s")  # 0.3 < 0.8 -> proposes
    assert v.improvements_applied == "tuned@0.3"
    assert v.new_bench_score == pytest.approx(0.3)


async def test_self_improvement_rejects_blank() -> None:
    with pytest.raises(ValueError):
        await TrackingSelfImprovementLoop().cycle_async("  ")


# ── shared helper: .NET round-trip datetime rendering ──────────────────────


def test_dto_round_trip_format() -> None:
    dt = datetime(2026, 7, 8, 6, 30, 0, tzinfo=timezone.utc)
    assert _dto_round_trip(dt) == "2026-07-08T06:30:00.0000000+00:00"
    dt2 = datetime(2026, 1, 2, 3, 4, 5, 123456, tzinfo=timezone.utc)
    assert _dto_round_trip(dt2) == "2026-01-02T03:04:05.1234560+00:00"
