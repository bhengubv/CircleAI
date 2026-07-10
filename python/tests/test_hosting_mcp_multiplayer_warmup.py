"""test_hosting_mcp_multiplayer_warmup.py

Exercises the remaining hosting sub-hosts + runtime pieces: the MCP JSON-RPC
dispatcher, the multiplayer collaboration hub (LWW-by-rev, presence, colour
hash), the predictive warmup histogram + controller, the thermal throttle
service, and the memory-pressure sources. Ports of CircleAI.Hosting.{Mcp,
Multiplayer, Warmup, ThermalThrottleService, IMemoryPressureSource}.
"""
from __future__ import annotations

import datetime as _dt
import json
from typing import Any, List, Optional

import pytest

from circle_ai.hosting import (
    ArrivalForecast,
    GuestPeerIdentity,
    HistogramRequestPredictor,
    IMcpResourceProvider,
    IMcpTool,
    ManualMemoryPressureSource,
    McpDispatcher,
    McpResource,
    McpResourceContent,
    McpServerInfo,
    McpToolException,
    MemoryPressureLevel,
    MultiplayerHub,
    NullMemoryPressureSource,
    PredictiveWarmupController,
    PredictiveWarmupOptions,
    ThermalState,
    ThermalThrottleService,
    colour_for,
)

_UTC = _dt.timezone.utc


# ── MCP dispatcher ──────────────────────────────────────────────────────────


class _EchoTool(IMcpTool):
    @property
    def name(self) -> str:
        return "echo"

    @property
    def description(self) -> str:
        return "echoes its arguments"

    @property
    def input_schema(self) -> Any:
        return {"type": "object"}

    async def execute_async(self, arguments: dict, ct: object = None) -> Any:
        return {"echoed": arguments}


class _FailingTool(IMcpTool):
    @property
    def name(self) -> str:
        return "boom"

    @property
    def description(self) -> str:
        return "always errors"

    @property
    def input_schema(self) -> Any:
        return {}

    async def execute_async(self, arguments: dict, ct: object = None) -> Any:
        raise McpToolException("tool-level failure")


class _VaultProvider(IMcpResourceProvider):
    @property
    def uri_scheme(self) -> str:
        return "vault://"

    async def list_async(self, ct: object = None) -> List[McpResource]:
        return [McpResource("vault://note/1", "note-1", None, "text/plain")]

    async def read_async(self, uri: str, ct: object = None) -> Optional[McpResourceContent]:
        if uri == "vault://note/1":
            return McpResourceContent(uri, "text/plain", "note body")
        return None


def _rpc(method: str, id_: Any = 1, params: Any = None) -> dict:
    req: dict = {"jsonrpc": "2.0", "method": method, "id": id_}
    if params is not None:
        req["params"] = params
    return req


async def test_mcp_initialize() -> None:
    disp = McpDispatcher(tools=[_EchoTool()])
    resp = await disp.dispatch_async(_rpc("initialize"))
    assert resp["result"]["protocolVersion"] == "2024-11-05"
    assert resp["id"] == "1"  # id echoed as JSON-string form


async def test_mcp_tools_list() -> None:
    disp = McpDispatcher(tools=[_EchoTool()])
    resp = await disp.dispatch_async(_rpc("tools/list"))
    names = [t["name"] for t in resp["result"]["tools"]]
    assert names == ["echo"]


async def test_mcp_tools_call_success() -> None:
    disp = McpDispatcher(tools=[_EchoTool()])
    resp = await disp.dispatch_async(
        _rpc("tools/call", params={"name": "echo", "arguments": {"a": 1}})
    )
    assert resp["result"]["isError"] is False
    inner = json.loads(resp["result"]["content"][0]["text"])
    assert inner == {"echoed": {"a": 1}}


async def test_mcp_tools_call_tool_exception_is_error_envelope() -> None:
    disp = McpDispatcher(tools=[_FailingTool()])
    resp = await disp.dispatch_async(_rpc("tools/call", params={"name": "boom"}))
    assert resp["result"]["isError"] is True
    assert "tool-level failure" in resp["result"]["content"][0]["text"]


async def test_mcp_tools_call_unknown_tool_is_invalid_params() -> None:
    disp = McpDispatcher(tools=[_EchoTool()])
    resp = await disp.dispatch_async(_rpc("tools/call", params={"name": "ghost"}))
    assert resp["error"]["code"] == -32602


async def test_mcp_resources_list_and_read() -> None:
    disp = McpDispatcher(resource_providers=[_VaultProvider()])
    lst = await disp.dispatch_async(_rpc("resources/list"))
    assert lst["result"]["resources"][0]["uri"] == "vault://note/1"
    # description falls back to name when None.
    assert lst["result"]["resources"][0]["description"] == "note-1"

    rd = await disp.dispatch_async(
        _rpc("resources/read", params={"uri": "vault://note/1"})
    )
    assert rd["result"]["contents"][0]["text"] == "note body"


async def test_mcp_resources_read_unknown_scheme() -> None:
    disp = McpDispatcher(resource_providers=[_VaultProvider()])
    resp = await disp.dispatch_async(_rpc("resources/read", params={"uri": "models://x"}))
    assert resp["error"]["code"] == -32602


async def test_mcp_method_not_found() -> None:
    disp = McpDispatcher()
    resp = await disp.dispatch_async(_rpc("does/not/exist"))
    assert resp["error"]["code"] == -32601


async def test_mcp_notification_returns_none() -> None:
    disp = McpDispatcher()
    resp = await disp.dispatch_async({"jsonrpc": "2.0", "method": "notifications/initialized"})
    assert resp is None


async def test_mcp_handle_post_parse_error() -> None:
    disp = McpDispatcher()
    resp = await disp.handle_post_async("{not json")
    assert resp["error"]["code"] == -32700


async def test_mcp_handle_post_batch() -> None:
    disp = McpDispatcher(tools=[_EchoTool()])
    batch = json.dumps([_rpc("tools/list", id_=1), _rpc("initialize", id_=2)])
    resp = await disp.handle_post_async(batch)
    assert isinstance(resp, list) and len(resp) == 2


def test_mcp_manifest_is_deprecated() -> None:
    disp = McpDispatcher(tools=[_EchoTool()], info=McpServerInfo(name="x", version="9.9.9"))
    m = disp.manifest()
    assert m["deprecated"] is True
    assert m["name"] == "x"
    assert m["tools"][0]["name"] == "echo"


# ── MultiplayerHub ──────────────────────────────────────────────────────────


class _CollectingBroadcast:
    def __init__(self) -> None:
        self.events: List[tuple] = []

    def __call__(self, group: str, event: str, args: tuple):
        self.events.append((group, event, args))


@pytest.fixture(autouse=True)
def _reset_hub():
    MultiplayerHub.reset_state_for_testing()
    yield
    MultiplayerHub.reset_state_for_testing()


async def test_hub_join_emits_peer_joined() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1", display_name="Alice"), bc)
    await hub.on_connected_async("conn-1")
    await hub.join_document("conn-1", "doc-A")
    assert ("doc:doc-A", "PeerJoined") == bc.events[0][:2]
    assert MultiplayerHub.peers("doc-A")[0].display_name == "Alice"


async def test_hub_leave_emits_peer_left() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1"), bc)
    await hub.on_connected_async("c1")
    await hub.join_document("c1", "doc")
    await hub.leave_document("c1", "doc")
    assert bc.events[-1][1] == "PeerLeft"


async def test_hub_disconnect_emits_peer_left_when_in_doc() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1"), bc)
    await hub.on_connected_async("c1")
    await hub.join_document("c1", "doc")
    await hub.on_disconnected_async("c1")
    assert bc.events[-1][1] == "PeerLeft"
    assert MultiplayerHub.peers("doc") == []


async def test_hub_edit_lww_accepts_higher_rev() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1"), bc)
    await hub.on_connected_async("c1")
    rev = await hub.send_edit("c1", "doc", "content-v1", 1)
    assert rev == 1
    assert MultiplayerHub.current_rev("doc") == 1
    rev2 = await hub.send_edit("c1", "doc", "content-v2", 2)
    assert rev2 == 2
    assert MultiplayerHub.current_rev("doc") == 2


async def test_hub_edit_lww_rejects_stale_rev() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1"), bc)
    await hub.on_connected_async("c1")
    await hub.send_edit("c1", "doc", "v5", 5)
    # A stale rev (3 <= 5) is rejected; the current server rev is returned.
    returned = await hub.send_edit("c1", "doc", "v3", 3)
    assert returned == 5
    assert MultiplayerHub.current_rev("doc") == 5
    # No EditApplied event emitted for the rejected edit.
    applied = [e for e in bc.events if e[1] == "EditApplied"]
    assert len(applied) == 1  # only the accepted rev-5 edit


async def test_hub_cursor_emits_cursor_changed() -> None:
    bc = _CollectingBroadcast()
    hub = MultiplayerHub(GuestPeerIdentity(peer_id="p1", display_name="Bob"), bc)
    await hub.on_connected_async("c1")
    await hub.send_cursor("c1", "doc", 10, 4)
    ev = [e for e in bc.events if e[1] == "CursorChanged"][0]
    # args: (connection_id, display_name, color, line, ch)
    assert ev[2][3] == 10 and ev[2][4] == 4


def test_colour_for_is_stable_and_hsl() -> None:
    c1 = colour_for("peer-abc")
    c2 = colour_for("peer-abc")
    assert c1 == c2
    assert c1.startswith("hsl(")
    # Distinct ids generally map to distinct hues.
    assert colour_for("peer-abc") != colour_for("totally-different-peer")


def test_guest_peer_identity_defaults() -> None:
    p = GuestPeerIdentity()
    assert p.display_name == "Guest"
    assert len(p.peer_id) == 32  # uuid4().hex


# ── PredictiveWarmup ────────────────────────────────────────────────────────


def test_arrival_forecast_fields() -> None:
    f = ArrivalForecast(0.5, 2.0, 0.9)
    assert f.probability_of_arrival == 0.5
    assert f.expected_count == 2.0
    assert f.confidence == 0.9


def test_histogram_cold_start_is_zero() -> None:
    pred = HistogramRequestPredictor()
    f = pred.predict(_dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC), _dt.timedelta(minutes=1))
    assert f.probability_of_arrival == 0.0
    assert f.confidence == 0.0
    assert pred.observed_arrivals == 0


def test_histogram_learns_and_forecasts() -> None:
    pred = HistogramRequestPredictor(history_days=7)
    slot = _dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC)
    for _ in range(30):
        pred.record_arrival(slot)
    assert pred.observed_arrivals == 30
    f = pred.predict(slot, _dt.timedelta(minutes=1))
    assert f.probability_of_arrival > 0.0
    assert f.expected_count > 0.0
    assert f.confidence == 1.0  # 30 samples >= 25 * 1 minute → full confidence


def test_histogram_zero_window_is_zero() -> None:
    pred = HistogramRequestPredictor()
    pred.record_arrival(_dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC))
    f = pred.predict(_dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC), _dt.timedelta(0))
    assert f.probability_of_arrival == 0.0


def test_histogram_rejects_nonpositive_history() -> None:
    with pytest.raises(ValueError):
        HistogramRequestPredictor(history_days=0)


class _PrewarmSpy:
    def __init__(self) -> None:
        self.prewarms = 0

    async def prewarm_async(self, ct: object = None) -> None:
        self.prewarms += 1


async def test_warmup_controller_ticks_and_fires_above_threshold() -> None:
    svc = _PrewarmSpy()
    pred = HistogramRequestPredictor()
    slot = _dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC)
    for _ in range(60):
        pred.record_arrival(slot)
    opts = PredictiveWarmupOptions(warmup_threshold=0.3, forecast_window=_dt.timedelta(minutes=1))
    ctrl = PredictiveWarmupController(svc, pred, opts, clock=lambda: slot)
    fired = await ctrl.tick_async()
    assert fired is True
    assert svc.prewarms == 1


async def test_warmup_controller_respects_min_time_between() -> None:
    svc = _PrewarmSpy()
    pred = HistogramRequestPredictor()
    slot = _dt.datetime(2026, 7, 8, 9, 0, tzinfo=_UTC)
    for _ in range(60):
        pred.record_arrival(slot)
    opts = PredictiveWarmupOptions(
        warmup_threshold=0.3,
        forecast_window=_dt.timedelta(minutes=1),
        min_time_between_warmups=_dt.timedelta(minutes=5),
    )
    ctrl = PredictiveWarmupController(svc, pred, opts, clock=lambda: slot)
    assert await ctrl.tick_async() is True
    # Same clock time → within the min-gap → no second warmup.
    assert await ctrl.tick_async() is False
    assert svc.prewarms == 1


async def test_warmup_controller_below_threshold_does_not_fire() -> None:
    svc = _PrewarmSpy()
    pred = HistogramRequestPredictor()  # cold → probability 0
    opts = PredictiveWarmupOptions(warmup_threshold=0.5)
    ctrl = PredictiveWarmupController(svc, pred, opts, clock=lambda: _dt.datetime.now(_UTC))
    assert await ctrl.tick_async() is False
    assert svc.prewarms == 0


# ── ThermalThrottleService ──────────────────────────────────────────────────


def test_thermal_default_sampler_is_unknown() -> None:
    svc = ThermalThrottleService()
    assert svc.current_state == ThermalState.UNKNOWN
    assert svc.should_pause_inference is False


def test_thermal_sample_and_should_pause() -> None:
    state = {"v": ThermalState.NORMAL}
    svc = ThermalThrottleService(sampler=lambda: state["v"])
    svc.sample_once()
    assert svc.current_state == ThermalState.NORMAL
    assert svc.should_pause_inference is False
    state["v"] = ThermalState.SERIOUS
    svc.sample_once()
    assert svc.should_pause_inference is True


def test_thermal_fires_change_handler_on_transition_only() -> None:
    state = {"v": ThermalState.NORMAL}
    svc = ThermalThrottleService(sampler=lambda: state["v"])
    seen: List[ThermalState] = []
    svc.add_state_changed_handler(lambda s: seen.append(s))
    svc.sample_once()  # UNKNOWN -> NORMAL (transition)
    svc.sample_once()  # NORMAL -> NORMAL (no transition, no fire)
    state["v"] = ThermalState.CRITICAL
    svc.sample_once()  # NORMAL -> CRITICAL (transition)
    assert seen == [ThermalState.NORMAL, ThermalState.CRITICAL]


def test_thermal_failing_sampler_becomes_unknown() -> None:
    def _boom() -> ThermalState:
        raise RuntimeError("no sensor")

    svc = ThermalThrottleService(sampler=_boom)
    svc.sample_once()
    assert svc.current_state == ThermalState.UNKNOWN


# ── memory pressure ─────────────────────────────────────────────────────────


def test_null_memory_pressure_source_is_normal() -> None:
    src = NullMemoryPressureSource()
    assert src.current == MemoryPressureLevel.NORMAL

    async def _h(prev, nxt):  # pragma: no cover - never invoked
        return None

    # Subscribing is a no-op that returns a disposable-ish token.
    token = src.subscribe(_h)
    assert token is not None
    token.dispose()  # safe no-op


async def test_manual_memory_pressure_notifies_on_change() -> None:
    src = ManualMemoryPressureSource()
    transitions: List[tuple] = []

    async def _h(prev, nxt):
        transitions.append((prev, nxt))

    src.subscribe(_h)
    await src.raise_level(MemoryPressureLevel.CRITICAL)
    assert transitions[-1] == (MemoryPressureLevel.NORMAL, MemoryPressureLevel.CRITICAL)
    assert src.current == MemoryPressureLevel.CRITICAL
    # Idempotent: same level again fires nothing.
    await src.raise_level(MemoryPressureLevel.CRITICAL)
    assert len(transitions) == 1


async def test_manual_memory_pressure_unsubscribe_stops_notifications() -> None:
    src = ManualMemoryPressureSource()
    transitions: List[tuple] = []

    async def _h(prev, nxt):
        transitions.append((prev, nxt))

    token = src.subscribe(_h)
    token.dispose()
    await src.raise_level(MemoryPressureLevel.TRIM)
    assert transitions == []


def test_memory_pressure_level_ordinals() -> None:
    # Ordered coolest->hottest so numeric comparison is meaningful.
    assert int(MemoryPressureLevel.NORMAL) == 0
    assert int(MemoryPressureLevel.TRIM) == 1
    assert int(MemoryPressureLevel.CRITICAL) == 2
    assert MemoryPressureLevel.CRITICAL > MemoryPressureLevel.NORMAL
