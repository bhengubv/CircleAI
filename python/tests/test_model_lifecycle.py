"""test_model_lifecycle.py — ModelLifecycleManager admission gate + AdminHandler."""
from __future__ import annotations

import pytest

from circle_ai.inference_server import (
    AdminHandler,
    AdminLoadRequest,
    BackendKind,
    CapabilityTier,
    DeterministicBridgeFactory,
    GpuInfo,
    HostProfile,
    InferenceServerModelRegistry,
    LoadOutcome,
    ModelLifecycleManager,
    ModelLoadDescriptor,
    NativeRuntimeStatus,
    StaticHostProfileProbe,
    UnconfiguredBridgeFactory,
    UnloadOutcome,
)


def _profile(ram_gib=16, vram_gib=0):
    gpu = GpuInfo("test-gpu", vram_gib * 1024**3) if vram_gib else None
    return HostProfile(total_physical_memory_bytes=ram_gib * 1024**3, gpu=gpu)


async def _make_bridge(ct):
    return await DeterministicBridgeFactory().create_async("m", BackendKind.CPU, CapabilityTier.TIER0_TINY, ct)


def _descriptor(model_id="m", backend=BackendKind.CPU, ram=1024, vram=0, factory=None):
    return ModelLoadDescriptor(
        model_id=model_id,
        backend=backend,
        requested_tier=CapabilityTier.TIER1_SMALL,
        vram_required_bytes=vram,
        ram_required_bytes=ram,
        bridge_factory=factory or _make_bridge,
    )


# ── admission gate ───────────────────────────────────────────────────────


async def test_load_success_registers_bridge():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    res = await mgr.load_async(_descriptor("m"))
    assert res.outcome == LoadOutcome.LOADED
    assert reg.resolve("m") is not None
    assert mgr.total_allocated_ram_bytes == 1024


async def test_load_already_loaded_is_noop_success():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    await mgr.load_async(_descriptor("m"))
    res = await mgr.load_async(_descriptor("m"))
    assert res.outcome == LoadOutcome.ALREADY_LOADED


async def test_load_insufficient_ram():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile(ram_gib=1)))
    res = await mgr.load_async(_descriptor("big", ram=4 * 1024**3))
    assert res.outcome == LoadOutcome.INSUFFICIENT_RAM
    assert reg.resolve("big") is None


async def test_load_insufficient_vram_on_gpu_backend():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile(ram_gib=64, vram_gib=2)))
    res = await mgr.load_async(
        _descriptor("gpu", backend=BackendKind.CUDA, ram=1024, vram=8 * 1024**3)
    )
    assert res.outcome == LoadOutcome.INSUFFICIENT_VRAM


async def test_cpu_backend_ignores_vram():
    reg = InferenceServerModelRegistry()
    # No GPU, but CPU backend never checks VRAM even with a vram requirement.
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile(ram_gib=16)))
    res = await mgr.load_async(_descriptor("m", backend=BackendKind.CPU, ram=1024, vram=99 * 1024**3))
    assert res.outcome == LoadOutcome.LOADED


async def test_factory_failure_rolls_back():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))

    async def _boom(ct):
        raise RuntimeError("kaboom")

    res = await mgr.load_async(_descriptor("m", factory=_boom))
    assert res.outcome == LoadOutcome.FACTORY_FAILED
    assert "kaboom" in res.rationale
    # Reservation rolled back — RAM accounting is clean.
    assert mgr.total_allocated_ram_bytes == 0
    assert reg.resolve("m") is None


async def test_unload_disposes_and_deregisters():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    await mgr.load_async(_descriptor("m"))
    outcome = await mgr.unload_async("m")
    assert outcome == UnloadOutcome.UNLOADED
    assert reg.resolve("m") is None
    assert mgr.total_allocated_ram_bytes == 0


async def test_unload_not_loaded():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    assert await mgr.unload_async("ghost") == UnloadOutcome.NOT_LOADED


async def test_list_reflects_loaded():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    await mgr.load_async(_descriptor("a"))
    await mgr.load_async(_descriptor("b"))
    ids = {s.model_id for s in mgr.list()}
    assert ids == {"a", "b"}


# ── AdminHandler (endpoint routing) ──────────────────────────────────────


async def test_admin_load_and_lifecycle_and_unload():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    factory = DeterministicBridgeFactory()
    admin = AdminHandler(mgr, factory)

    res = await admin.load(AdminLoadRequest(model_id="q", backend="Cpu", tier="Tier1_Small", ram_required_bytes=1024))
    assert res.status_code == 200
    assert res.body["outcome"] == "LOADED"
    assert reg.resolve("q") is not None

    life = admin.lifecycle()
    assert life.status_code == 200
    body = life.body_dict
    assert body["total_allocated_ram_bytes"] == 1024
    assert len(body["loaded"]) == 1
    assert body["loaded"][0]["model_id"] == "q"

    un = await admin.unload("q")
    assert un.status_code == 200 and un.body["outcome"] == "Unloaded"


async def test_admin_load_bad_backend_400():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    admin = AdminHandler(mgr, DeterministicBridgeFactory())
    res = await admin.load(AdminLoadRequest(model_id="q", backend="Quantum", tier="Tier1_Small"))
    assert res.status_code == 400
    assert res.body_dict["error"]["code"] == "invalid_backend"


async def test_admin_load_bad_tier_400():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    admin = AdminHandler(mgr, DeterministicBridgeFactory())
    res = await admin.load(AdminLoadRequest(model_id="q", backend="Cpu", tier="Tier9_Ultra"))
    assert res.status_code == 400
    assert res.body_dict["error"]["code"] == "invalid_tier"


async def test_admin_load_missing_model_400():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    admin = AdminHandler(mgr, DeterministicBridgeFactory())
    res = await admin.load(AdminLoadRequest(model_id="", backend="Cpu", tier="Tier1_Small"))
    assert res.status_code == 400


async def test_admin_load_insufficient_ram_507():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile(ram_gib=1)))
    admin = AdminHandler(mgr, DeterministicBridgeFactory())
    res = await admin.load(
        AdminLoadRequest(model_id="big", backend="Cpu", tier="Tier1_Small", ram_required_bytes=4 * 1024**3)
    )
    assert res.status_code == 507
    assert res.body_dict["error"]["code"] == "INSUFFICIENT_RAM"


async def test_admin_unload_not_loaded_404():
    reg = InferenceServerModelRegistry()
    mgr = ModelLifecycleManager(reg, StaticHostProfileProbe(_profile()))
    admin = AdminHandler(mgr, DeterministicBridgeFactory())
    res = await admin.unload("ghost")
    assert res.status_code == 404


async def test_unconfigured_bridge_factory_raises():
    factory = UnconfiguredBridgeFactory()
    with pytest.raises(RuntimeError):
        await factory.create_async("m", BackendKind.CPU, CapabilityTier.TIER0_TINY, None)


async def test_bridge_factory_updates_native_status():
    status = NativeRuntimeStatus()
    factory = DeterministicBridgeFactory(native_status=status)
    assert status.latest is None
    await factory.create_async("m", BackendKind.CPU, CapabilityTier.TIER2_MEDIUM, None)
    assert status.latest is not None
    assert status.latest.self_check_passed is True


def test_backend_and_tier_parse():
    assert BackendKind.parse("cpu") == BackendKind.CPU
    assert BackendKind.parse("CoreML") == BackendKind.CORE_ML
    assert BackendKind.parse("bogus") is None
    assert CapabilityTier.parse("Tier1_Small") == CapabilityTier.TIER1_SMALL
    assert CapabilityTier.parse("TIER4_FRONTIER") == CapabilityTier.TIER4_FRONTIER
    assert CapabilityTier.parse("nope") is None
