# logistics_primitives.py
#
# Port of CircleAI.Logistics LogisticsPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Logistics vertical:
# shipments, vehicles, route legs/plans + a simple route-cost estimator.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a single lock.
# C# decimal EstimatedCost maps to decimal.Decimal, DateTimeOffset -> datetime.
# PlanRoute sums leg distances, computes cost = totalKm * CostPerKm cast to
# decimal, and mints a monotonically increasing plan id "plan-{n}" (n from a
# thread-safe counter). Blank ids raise ValueError; an unknown vehicle raises
# RuntimeError. `Vehicles` orders by VehicleId (ordinal).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Shipment:
    """Mirrors ``CircleAI.Logistics.Shipment`` — ``record(string ShipmentId,
    string Origin, string Destination, double WeightKg, double VolumeM3,
    string Incoterm, DateTimeOffset PickupAtUtc)``.
    """

    shipment_id: str
    origin: str
    destination: str
    weight_kg: float
    volume_m3: float
    incoterm: str
    pickup_at_utc: datetime


@dataclass(frozen=True, slots=True)
class Vehicle:
    """Mirrors ``CircleAI.Logistics.Vehicle`` — ``record(string VehicleId,
    double CapacityKg, double CapacityM3, double CostPerKm)``.
    """

    vehicle_id: str
    capacity_kg: float
    capacity_m3: float
    cost_per_km: float


@dataclass(frozen=True, slots=True)
class RouteLeg:
    """Mirrors ``CircleAI.Logistics.RouteLeg`` — ``record(string FromCode,
    string ToCode, double DistanceKm)``.
    """

    from_code: str
    to_code: str
    distance_km: float


@dataclass(frozen=True, slots=True)
class RoutePlan:
    """Mirrors ``CircleAI.Logistics.RoutePlan`` — ``record(string PlanId,
    string VehicleId, IReadOnlyList<RouteLeg> Legs, double TotalDistanceKm,
    decimal EstimatedCost)``.
    """

    plan_id: str
    vehicle_id: str
    legs: Sequence[RouteLeg]
    total_distance_km: float
    estimated_cost: Decimal


class ILogisticsBoard(ABC):
    """In-memory board for shipments, vehicles and route planning."""

    @abstractmethod
    def register_shipment(self, s: Shipment) -> None:
        ...

    @abstractmethod
    def register_vehicle(self, v: Vehicle) -> None:
        ...

    @abstractmethod
    def get_shipment(self, id: str) -> Optional[Shipment]:
        ...

    @property
    @abstractmethod
    def vehicles(self) -> List[Vehicle]:
        ...

    @abstractmethod
    def plan_route(self, vehicle_id: str, legs: Sequence[RouteLeg]) -> RoutePlan:
        ...


class InMemoryLogisticsBoard(ILogisticsBoard):
    """Thread-safe in-memory :class:`ILogisticsBoard`."""

    def __init__(self) -> None:
        self._shipments: Dict[str, Shipment] = {}
        self._vehicles: Dict[str, Vehicle] = {}
        self._seq = 0
        self._lock = threading.Lock()

    def register_shipment(self, s: Shipment) -> None:
        if s is None:
            raise ValueError("shipment must not be None")
        if s.shipment_id is None or not s.shipment_id.strip():
            raise ValueError("ShipmentId required")
        with self._lock:
            self._shipments[s.shipment_id] = s

    def register_vehicle(self, v: Vehicle) -> None:
        if v is None:
            raise ValueError("vehicle must not be None")
        if v.vehicle_id is None or not v.vehicle_id.strip():
            raise ValueError("VehicleId required")
        with self._lock:
            self._vehicles[v.vehicle_id] = v

    def get_shipment(self, id: str) -> Optional[Shipment]:
        with self._lock:
            return self._shipments.get(id)

    @property
    def vehicles(self) -> List[Vehicle]:
        with self._lock:
            return sorted(self._vehicles.values(), key=lambda v: v.vehicle_id)

    def plan_route(self, vehicle_id: str, legs: Sequence[RouteLeg]) -> RoutePlan:
        if vehicle_id is None or not vehicle_id.strip():
            raise ValueError("vehicle_id required")
        if legs is None:
            raise ValueError("legs must not be None")
        with self._lock:
            vehicle = self._vehicles.get(vehicle_id)
            if vehicle is None:
                raise RuntimeError(f"Unknown vehicle '{vehicle_id}'.")
            self._seq += 1
            plan_id = f"plan-{self._seq}"
        leg_list = list(legs)
        total_km = sum(l.distance_km for l in leg_list) if leg_list else 0.0
        cost = Decimal(total_km * vehicle.cost_per_km)
        return RoutePlan(plan_id, vehicle_id, leg_list, total_km, cost)
