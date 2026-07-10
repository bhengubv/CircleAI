"""test_companion_state_sync.py

Verifies the companion-state sync engine end-to-end over the in-process
loopback channel: event-driven Push convergence, the Announce/Request/Push
catch-up protocol, HLC version + content-hash stamping, dispose semantics, and
the three concrete bridges (Persona, LoRA adapter, conversation).

Mirrors CircleAI.Memory.Sync.CompanionStateSyncEngine and bridges (C# — spec).
"""
from __future__ import annotations

import hashlib
import pathlib
from datetime import datetime, timezone

import pytest

from circle_ai.memory.persona_state import PersonaState
from circle_ai.memory.sync import (
    CompanionConversationSyncBridge,
    CompanionStateSyncEngine,
    ConversationStateDelta,
    HybridLogicalClock,
    InMemorySyncableEntryStore,
    InProcessCompanionStateChannel,
    InProcessSyncHub,
    LoraAdapterSyncBridge,
    PersonaStateSyncBridge,
    SyncableEntry,
    SyncEnvelopeKind,
)


def _mono_clock(node: int) -> HybridLogicalClock:
    """A clock whose physical time advances by 1ms per read — keeps versions
    strictly increasing across engines regardless of wall time.
    """
    counter = {"t": 1_000_000}

    def now() -> int:
        counter["t"] += 1
        return counter["t"]

    return HybridLogicalClock(node, physical_now_ms=now)


def _make_engine(hub: InProcessSyncHub, node_id: str, node_short: int):
    channel = InProcessCompanionStateChannel(hub, node_id)
    store = InMemorySyncableEntryStore()
    engine = CompanionStateSyncEngine(channel, store, _mono_clock(node_short))
    return channel, store, engine


# ── write stamping ────────────────────────────────────────────────────────────


async def test_write_local_stamps_version_and_content_hash() -> None:
    hub = InProcessSyncHub()
    _, store, engine = _make_engine(hub, "n1", 1)
    entry = await engine.write_local_async("PersonaState", "u1", "hello")
    assert entry.version > 0
    assert entry.content_hash == hashlib.sha256(b"hello").hexdigest()
    assert entry.source_node_id == "n1"
    assert entry.is_tombstone is False
    # Persisted locally.
    got = await store.get_async("PersonaState", "u1")
    assert got is not None and got.payload == "hello"
    await engine.dispose_async()


async def test_write_local_tombstone_has_empty_payload_hash_of_empty() -> None:
    hub = InProcessSyncHub()
    _, _, engine = _make_engine(hub, "n1", 1)
    entry = await engine.write_local_async(
        "ConversationState", "s1", "", is_tombstone=True
    )
    assert entry.is_tombstone is True
    assert entry.content_hash == hashlib.sha256(b"").hexdigest()
    await engine.dispose_async()


@pytest.mark.parametrize("bad_type", ["", "  ", None])
async def test_write_local_rejects_blank_entity_type(bad_type) -> None:
    hub = InProcessSyncHub()
    _, _, engine = _make_engine(hub, "n1", 1)
    with pytest.raises(ValueError):
        await engine.write_local_async(bad_type, "id", "x")
    await engine.dispose_async()


# ── event-driven convergence (Push) ───────────────────────────────────────────


async def test_started_engines_converge_via_push_on_local_write() -> None:
    hub = InProcessSyncHub()
    _, store_a, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    await engine_a.write_local_async("PersonaState", "u", "from-A")

    # B received the Push and applied it.
    got = await store_b.get_async("PersonaState", "u")
    assert got is not None and got.payload == "from-A"
    await engine_a.dispose_async()
    await engine_b.dispose_async()


async def test_unstarted_engine_does_not_broadcast() -> None:
    hub = InProcessSyncHub()
    _, _, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_b.start_async()  # B listens
    # A never started → no Push emitted.
    await engine_a.write_local_async("PersonaState", "u", "silent")
    assert await store_b.get_async("PersonaState", "u") is None
    await engine_b.dispose_async()


# ── announce / request / push catch-up ────────────────────────────────────────


async def test_announce_pulls_missing_history_into_late_joiner() -> None:
    hub = InProcessSyncHub()
    # A accumulates state BEFORE B is listening (writes before start → no push).
    _, store_a, engine_a = _make_engine(hub, "A", 1)
    e1 = await engine_a.write_local_async("PersonaState", "u1", "v1")
    e2 = await engine_a.write_local_async("CoreMemory", "c1", "m1")

    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    # B announces its (empty) vector; A is ahead and does nothing on B's announce.
    # A announces its vector; B requests; A pushes; B converges.
    await engine_a.sync_now_async()

    got1 = await store_b.get_async("PersonaState", "u1")
    got2 = await store_b.get_async("CoreMemory", "c1")
    assert got1 is not None and got1.version == e1.version
    assert got2 is not None and got2.version == e2.version
    await engine_a.dispose_async()
    await engine_b.dispose_async()


async def test_announce_no_op_when_peer_has_nothing_new() -> None:
    hub = InProcessSyncHub()
    _, store_a, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()
    await engine_a.write_local_async("PersonaState", "u", "x")  # both converge

    # Re-announcing changes nothing and does not error.
    await engine_a.sync_now_async()
    await engine_b.sync_now_async()
    assert (await store_b.get_async("PersonaState", "u")).payload == "x"
    await engine_a.dispose_async()
    await engine_b.dispose_async()


# ── conflict convergence: both write same entity ──────────────────────────────


async def test_concurrent_writes_converge_deterministically() -> None:
    hub = InProcessSyncHub()
    _, store_a, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    await engine_a.write_local_async("PersonaState", "u", "A-writes")
    await engine_b.write_local_async("PersonaState", "u", "B-writes")

    # Both stores must agree on the same winning entry.
    a = await store_a.get_async("PersonaState", "u")
    b = await store_b.get_async("PersonaState", "u")
    assert a is not None and b is not None
    assert (a.version, a.content_hash) == (b.version, b.content_hash)
    assert a.payload == b.payload
    await engine_a.dispose_async()
    await engine_b.dispose_async()


# ── dispose ───────────────────────────────────────────────────────────────────


async def test_operations_after_dispose_raise() -> None:
    hub = InProcessSyncHub()
    _, _, engine = _make_engine(hub, "A", 1)
    await engine.start_async()
    await engine.dispose_async()
    with pytest.raises(RuntimeError):
        await engine.sync_now_async()
    with pytest.raises(RuntimeError):
        await engine.write_local_async("T", "id", "x")


async def test_dispose_is_idempotent_and_async_context_manager_works() -> None:
    hub = InProcessSyncHub()
    channel = InProcessCompanionStateChannel(hub, "A")
    store = InMemorySyncableEntryStore()
    async with CompanionStateSyncEngine(channel, store, _mono_clock(1)) as engine:
        await engine.start_async()
        await engine.write_local_async("T", "id", "x")
    # Exiting the context disposed it; a second dispose is a no-op.
    await engine.dispose_async()


# ── PersonaStateSyncBridge ────────────────────────────────────────────────────


class _MemPersonaStore:
    def __init__(self) -> None:
        self._by_user: dict[str, PersonaState] = {}

    async def load_async(self, user_id, *, ct=None):
        return self._by_user.get(user_id, PersonaState(user_id=user_id))

    async def save_async(self, persona, *, ct=None):
        self._by_user[persona.user_id] = persona


async def test_persona_bridge_saves_locally_and_pushes_decodable_payload() -> None:
    hub = InProcessSyncHub()
    _, store_a, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    persona_store = _MemPersonaStore()
    bridge = PersonaStateSyncBridge(persona_store, engine_a)

    persona = PersonaState(
        user_id="alice",
        verbosity="brief",
        formality="formal",
        preferred_locale="en-ZA",
        topic_weights={"finance": 0.9},
        disfavoured_topics={"sports"},
        total_interactions=12,
        positive_signals=8,
        negative_signals=4,
    )
    await bridge.save_async(persona)

    # Persisted through the store.
    assert (await persona_store.load_async("alice")).verbosity == "brief"
    # Pushed to B and decodes back to an equivalent PersonaState.
    entry = await store_b.get_async(PersonaStateSyncBridge.ENTITY_TYPE, "alice")
    assert entry is not None
    decoded = PersonaStateSyncBridge.try_decode(entry)
    assert decoded is not None
    assert decoded.user_id == "alice"
    assert decoded.verbosity == "brief"
    assert decoded.formality == "formal"
    assert decoded.preferred_locale == "en-ZA"
    assert decoded.topic_weights == {"finance": 0.9}
    assert decoded.disfavoured_topics == {"sports"}
    assert decoded.positive_signals == 8
    assert decoded.negative_signals == 4
    await engine_a.dispose_async()
    await engine_b.dispose_async()


def test_persona_bridge_try_decode_ignores_tombstone_and_wrong_type() -> None:
    tomb = SyncableEntry(
        PersonaStateSyncBridge.ENTITY_TYPE, "u", 1, True, "h", "", "n",
        datetime.now(timezone.utc),
    )
    assert PersonaStateSyncBridge.try_decode(tomb) is None
    wrong = SyncableEntry(
        "SomethingElse", "u", 1, False, "h", "{}", "n", datetime.now(timezone.utc)
    )
    assert PersonaStateSyncBridge.try_decode(wrong) is None


# ── LoraAdapterSyncBridge ─────────────────────────────────────────────────────


async def test_lora_bridge_publishes_file_and_peer_writes_it_back(tmp_path) -> None:
    hub = InProcessSyncHub()
    _, _, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    src = tmp_path / "adapter.bin"
    payload_bytes = bytes(range(256)) * 4
    src.write_bytes(payload_bytes)

    bridge_a = LoraAdapterSyncBridge(engine_a)
    await bridge_a.publish_async("personal-alice", str(src), step_count=1234)

    entry = await store_b.get_async(LoraAdapterSyncBridge.ENTITY_TYPE, "personal-alice")
    assert entry is not None
    dest = tmp_path / "out" / "adapter.bin"
    snapshot = await LoraAdapterSyncBridge.try_write_async(entry, str(dest))
    assert snapshot is not None
    assert snapshot.adapter_id == "personal-alice"
    assert snapshot.step_count == 1234
    assert dest.read_bytes() == payload_bytes  # byte-exact round trip
    await engine_a.dispose_async()
    await engine_b.dispose_async()


async def test_lora_publish_missing_file_raises() -> None:
    hub = InProcessSyncHub()
    _, _, engine = _make_engine(hub, "A", 1)
    bridge = LoraAdapterSyncBridge(engine)
    with pytest.raises(FileNotFoundError):
        await bridge.publish_async("id", "does-not-exist.bin", 1)
    await engine.dispose_async()


async def test_lora_try_write_ignores_tombstone() -> None:
    tomb = SyncableEntry(
        LoraAdapterSyncBridge.ENTITY_TYPE, "id", 1, True, "h", "", "n",
        datetime.now(timezone.utc),
    )
    assert await LoraAdapterSyncBridge.try_write_async(tomb, "x.bin") is None


# ── CompanionConversationSyncBridge ───────────────────────────────────────────


async def test_conversation_bridge_publish_and_decode_round_trip() -> None:
    hub = InProcessSyncHub()
    _, _, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    bridge = CompanionConversationSyncBridge(engine_a)
    delta = ConversationStateDelta(
        session_id="sess-1",
        user_text="hi",
        assistant_text="hel",
        is_turn_complete=False,
        started_at_utc=datetime(2026, 6, 1, 10, tzinfo=timezone.utc),
        updated_at_utc=datetime(2026, 6, 1, 10, 0, 1, tzinfo=timezone.utc),
    )
    await bridge.publish_async(delta)

    entry = await store_b.get_async(
        CompanionConversationSyncBridge.ENTITY_TYPE, "sess-1"
    )
    assert entry is not None
    decoded = CompanionConversationSyncBridge.try_decode(entry)
    assert decoded is not None
    assert decoded.session_id == "sess-1"
    assert decoded.user_text == "hi"
    assert decoded.assistant_text == "hel"
    assert decoded.is_turn_complete is False
    assert decoded.started_at_utc == delta.started_at_utc
    await engine_a.dispose_async()
    await engine_b.dispose_async()


async def test_conversation_bridge_terminate_writes_tombstone() -> None:
    hub = InProcessSyncHub()
    _, _, engine_a = _make_engine(hub, "A", 1)
    _, store_b, engine_b = _make_engine(hub, "B", 2)
    await engine_a.start_async()
    await engine_b.start_async()

    bridge = CompanionConversationSyncBridge(engine_a)
    entry = await bridge.terminate_async("sess-9")
    assert entry.is_tombstone is True

    got = await store_b.get_async(
        CompanionConversationSyncBridge.ENTITY_TYPE, "sess-9"
    )
    assert got is not None and got.is_tombstone is True
    # A tombstone decodes to None.
    assert CompanionConversationSyncBridge.try_decode(got) is None
    await engine_a.dispose_async()
    await engine_b.dispose_async()


async def test_conversation_bridge_rejects_blank_session_id() -> None:
    hub = InProcessSyncHub()
    _, _, engine = _make_engine(hub, "A", 1)
    bridge = CompanionConversationSyncBridge(engine)
    with pytest.raises(ValueError):
        await bridge.terminate_async("   ")
    await engine.dispose_async()
