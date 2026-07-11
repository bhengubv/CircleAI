"""test_logistics_board.py — CircleAI.Logistics port.

Covers the domain records, InMemoryLogisticsBoard (shipment/vehicle registration
with blank-id guards, id-ordered Vehicles, route planning with distance sum +
cost = totalKm*CostPerKm, monotonic plan ids, unknown-vehicle rejection) and the
static LogisticsDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    ILogisticsBoard,
    InMemoryLogisticsBoard,
    LogisticsDomainContext,
    RouteLeg,
    RoutePlan,
    Shipment,
    Vehicle,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def test_board_is_ilogisticsboard():
    assert isinstance(InMemoryLogisticsBoard(), ILogisticsBoard)


def test_register_and_get_shipment():
    board = InMemoryLogisticsBoard()
    board.register_shipment(Shipment("s1", "JNB", "CPT", 100.0, 1.5, "EXW", _T0))
    assert board.get_shipment("s1").destination == "CPT"
    assert board.get_shipment("nope") is None


def test_register_shipment_blank_id_raises():
    board = InMemoryLogisticsBoard()
    with pytest.raises(ValueError):
        board.register_shipment(Shipment("  ", "JNB", "CPT", 1.0, 1.0, "EXW", _T0))


def test_register_shipment_none_raises():
    with pytest.raises(ValueError):
        InMemoryLogisticsBoard().register_shipment(None)  # type: ignore[arg-type]


def test_vehicles_ordered_by_id():
    board = InMemoryLogisticsBoard()
    board.register_vehicle(Vehicle("v2", 1000.0, 10.0, 5.0))
    board.register_vehicle(Vehicle("v1", 2000.0, 20.0, 4.0))
    assert [v.vehicle_id for v in board.vehicles] == ["v1", "v2"]


def test_register_vehicle_blank_id_raises():
    with pytest.raises(ValueError):
        InMemoryLogisticsBoard().register_vehicle(Vehicle(" ", 1.0, 1.0, 1.0))


def test_plan_route_sums_distance_and_cost():
    board = InMemoryLogisticsBoard()
    board.register_vehicle(Vehicle("v1", 1000.0, 10.0, 2.0))
    legs = [RouteLeg("JNB", "BFN", 400.0), RouteLeg("BFN", "CPT", 600.0)]
    plan = board.plan_route("v1", legs)
    assert isinstance(plan, RoutePlan)
    assert plan.vehicle_id == "v1"
    assert plan.total_distance_km == 1000.0
    assert plan.estimated_cost == Decimal("2000")
    assert list(plan.legs) == legs


def test_plan_route_ids_are_monotonic():
    board = InMemoryLogisticsBoard()
    board.register_vehicle(Vehicle("v1", 1000.0, 10.0, 1.0))
    p1 = board.plan_route("v1", [RouteLeg("a", "b", 1.0)])
    p2 = board.plan_route("v1", [RouteLeg("a", "b", 1.0)])
    assert p1.plan_id == "plan-1" and p2.plan_id == "plan-2"


def test_plan_route_unknown_vehicle_raises():
    with pytest.raises(RuntimeError):
        InMemoryLogisticsBoard().plan_route("nope", [RouteLeg("a", "b", 1.0)])


def test_plan_route_blank_vehicle_raises():
    with pytest.raises(ValueError):
        InMemoryLogisticsBoard().plan_route(" ", [RouteLeg("a", "b", 1.0)])


def test_logistics_domain_context():
    assert LogisticsDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Logistics]")
    assert list(LogisticsDomainContext.ComplianceFlags) == [
        "RTMS",
        "SARS_Customs",
        "AARTO",
        "POPIA",
        "Incoterms_2020",
    ]
    assert list(LogisticsDomainContext.SuggestedTools) == [
        "route_planner",
        "fleet_tracker",
        "customs_portal",
        "analytics",
    ]
