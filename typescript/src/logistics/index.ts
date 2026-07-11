// logistics/index.ts
// Full-parity port of CircleAI.Logistics (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Logistics vertical: shipments,
// vehicles, route legs, and a route planner that sums leg distances and
// estimates cost as distance × the vehicle's cost-per-km. Plus the static
// LogisticsDomainContext.
//
// NOTE: The C# LogisticsCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   double WeightKg / DistanceKm ... → number
//   decimal EstimatedCost            → number
//   DateTimeOffset PickupAtUtc      → Date
//   IReadOnlyList<RouteLeg> Legs     → readonly RouteLeg[]
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Vehicles    — ordered by VehicleId ascending (default comparer / ordinal).
//   PlanRoute   — throws on unknown vehicle; total distance = Σ leg.DistanceKm;
//                 cost = totalKm × vehicle.CostPerKm; plan id "plan-{n}".

/** A freight shipment. Mirrors C# `Shipment` record. */
export interface Shipment {
  readonly shipmentId: string;
  readonly origin: string;
  readonly destination: string;
  readonly weightKg: number;
  readonly volumeM3: number;
  readonly incoterm: string;
  /** UTC pickup instant (C# `DateTimeOffset PickupAtUtc`). */
  readonly pickupAtUtc: Date;
}

/** Constructs a {@link Shipment}. */
export function shipment(
  shipmentId: string,
  origin: string,
  destination: string,
  weightKg: number,
  volumeM3: number,
  incoterm: string,
  pickupAtUtc: Date,
): Shipment {
  return { shipmentId, origin, destination, weightKg, volumeM3, incoterm, pickupAtUtc };
}

/** A vehicle in the fleet. Mirrors C# `Vehicle` record. */
export interface Vehicle {
  readonly vehicleId: string;
  readonly capacityKg: number;
  readonly capacityM3: number;
  readonly costPerKm: number;
}

/** Constructs a {@link Vehicle}. */
export function vehicle(vehicleId: string, capacityKg: number, capacityM3: number, costPerKm: number): Vehicle {
  return { vehicleId, capacityKg, capacityM3, costPerKm };
}

/** A single leg of a route. Mirrors C# `RouteLeg` record. */
export interface RouteLeg {
  readonly fromCode: string;
  readonly toCode: string;
  readonly distanceKm: number;
}

/** Constructs a {@link RouteLeg}. */
export function routeLeg(fromCode: string, toCode: string, distanceKm: number): RouteLeg {
  return { fromCode, toCode, distanceKm };
}

/** A planned, costed route. Mirrors C# `RoutePlan` record. */
export interface RoutePlan {
  readonly planId: string;
  readonly vehicleId: string;
  readonly legs: readonly RouteLeg[];
  readonly totalDistanceKm: number;
  readonly estimatedCost: number;
}

/** Constructs a {@link RoutePlan}. */
export function routePlan(
  planId: string,
  vehicleId: string,
  legs: readonly RouteLeg[],
  totalDistanceKm: number,
  estimatedCost: number,
): RoutePlan {
  return { planId, vehicleId, legs, totalDistanceKm, estimatedCost };
}

/** The logistics board contract. Mirrors C# `ILogisticsBoard`. */
export interface ILogisticsBoard {
  registerShipment(s: Shipment): void;
  registerVehicle(v: Vehicle): void;
  getShipment(id: string): Shipment | undefined;
  readonly vehicles: readonly Vehicle[];
  planRoute(vehicleId: string, legs: readonly RouteLeg[]): RoutePlan;
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link ILogisticsBoard}. */
export class InMemoryLogisticsBoard implements ILogisticsBoard {
  private readonly shipments = new Map<string, Shipment>();
  private readonly vehiclesById = new Map<string, Vehicle>();
  private seq = 0;

  registerShipment(s: Shipment): void {
    if (s == null) throw new Error("s required");
    if (s.shipmentId == null || s.shipmentId.trim() === "") throw new Error("ShipmentId required");
    this.shipments.set(s.shipmentId, s);
  }

  registerVehicle(v: Vehicle): void {
    if (v == null) throw new Error("v required");
    if (v.vehicleId == null || v.vehicleId.trim() === "") throw new Error("VehicleId required");
    this.vehiclesById.set(v.vehicleId, v);
  }

  getShipment(id: string): Shipment | undefined {
    return this.shipments.get(id);
  }

  get vehicles(): readonly Vehicle[] {
    return [...this.vehiclesById.values()].sort((a, b) => ordinalCompare(a.vehicleId, b.vehicleId));
  }

  planRoute(vehicleId: string, legs: readonly RouteLeg[]): RoutePlan {
    if (vehicleId == null || vehicleId.trim() === "") throw new Error("vehicleId required");
    if (legs == null) throw new Error("legs required");
    const vehicle = this.vehiclesById.get(vehicleId);
    if (vehicle === undefined) throw new Error(`Unknown vehicle '${vehicleId}'.`);
    const totalKm = legs.reduce((sum, l) => sum + l.distanceKm, 0);
    const cost = totalKm * vehicle.costPerKm;
    return {
      planId: `plan-${++this.seq}`,
      vehicleId,
      legs: [...legs],
      totalDistanceKm: totalKm,
      estimatedCost: cost,
    };
  }
}

/**
 * Static domain context for the Logistics vertical. Mirrors C#
 * `LogisticsDomainContext`.
 */
export const LogisticsDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Logistics] Expert logistics and supply chain assistant. Help with route optimisation, fleet maintenance scheduling, customs documentation, incoterms, 3PL management, warehouse layout, and last-mile delivery strategy. Apply cost-per-km and load efficiency metrics. Compliance: RTMS, SARS customs regulations, AARTO, POPIA.",
  complianceFlags: ["RTMS", "SARS_Customs", "AARTO", "POPIA", "Incoterms_2020"] as readonly string[],
  suggestedTools: ["route_planner", "fleet_tracker", "customs_portal", "analytics"] as readonly string[],
} as const;
