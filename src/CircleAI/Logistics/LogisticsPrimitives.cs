// LogisticsPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Logistics
// vertical. Shipments, routes, vehicles, and a simple route-cost
// estimator that's good enough for tests and adapter wiring.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Logistics;

public sealed record Shipment(string ShipmentId, string Origin, string Destination, double WeightKg, double VolumeM3, string Incoterm, DateTimeOffset PickupAtUtc);
public sealed record Vehicle(string VehicleId, double CapacityKg, double CapacityM3, double CostPerKm);
public sealed record RouteLeg(string FromCode, string ToCode, double DistanceKm);
public sealed record RoutePlan(string PlanId, string VehicleId, IReadOnlyList<RouteLeg> Legs, double TotalDistanceKm, decimal EstimatedCost);

public interface ILogisticsBoard
{
    void RegisterShipment(Shipment s);
    void RegisterVehicle(Vehicle v);
    Shipment? GetShipment(string id);
    IReadOnlyList<Vehicle> Vehicles { get; }
    RoutePlan PlanRoute(string vehicleId, IReadOnlyList<RouteLeg> legs);
}

public sealed class InMemoryLogisticsBoard : ILogisticsBoard
{
    private readonly ConcurrentDictionary<string, Shipment> _shipments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Vehicle>  _vehicles  = new(StringComparer.Ordinal);
    private long _seq;

    public void RegisterShipment(Shipment s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(s.ShipmentId)) throw new ArgumentException("ShipmentId required");
        _shipments[s.ShipmentId] = s;
    }

    public void RegisterVehicle(Vehicle v)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (string.IsNullOrWhiteSpace(v.VehicleId)) throw new ArgumentException("VehicleId required");
        _vehicles[v.VehicleId] = v;
    }

    public Shipment? GetShipment(string id) => _shipments.GetValueOrDefault(id);
    public IReadOnlyList<Vehicle> Vehicles => _vehicles.Values.OrderBy(v => v.VehicleId).ToArray();

    public RoutePlan PlanRoute(string vehicleId, IReadOnlyList<RouteLeg> legs)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) throw new ArgumentException("vehicleId required");
        ArgumentNullException.ThrowIfNull(legs);
        if (!_vehicles.TryGetValue(vehicleId, out var vehicle)) throw new InvalidOperationException($"Unknown vehicle '{vehicleId}'.");
        var totalKm = legs.Sum(l => l.DistanceKm);
        var cost    = (decimal)(totalKm * vehicle.CostPerKm);
        return new RoutePlan($"plan-{System.Threading.Interlocked.Increment(ref _seq)}", vehicleId, legs.ToArray(), totalKm, cost);
    }
}
