// TravelPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Travel
// vertical: flights, hotel-stays, trips, expense totals.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Travel;

public sealed record Flight(string FlightId, string From, string To, DateTimeOffset DepartUtc, DateTimeOffset ArriveUtc, string Carrier, string Cabin, decimal Price, string Currency);
public sealed record HotelStay(string StayId, string Hotel, string City, DateTime CheckIn, DateTime CheckOut, decimal NightlyRate, string Currency);
public sealed record TravelTrip(string TripId, string Name, DateTime StartDate, DateTime EndDate, IReadOnlyList<string> FlightIds, IReadOnlyList<string> StayIds);

public interface ITravelBoard
{
    void Add(Flight f);
    void Add(HotelStay s);
    void Plan(TravelTrip t);
    TravelTrip? GetTrip(string id);
    Flight? GetFlight(string id);
    HotelStay? GetStay(string id);
    decimal TripCost(string tripId);
    IReadOnlyList<TravelTrip> UpcomingTrips(DateTime now);
}

public sealed class InMemoryTravelBoard : ITravelBoard
{
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HotelStay> _stays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TravelTrip> _trips = new(StringComparer.Ordinal);

    public void Add(Flight f) { ArgumentNullException.ThrowIfNull(f); _flights[f.FlightId] = f; }
    public void Add(HotelStay s) { ArgumentNullException.ThrowIfNull(s); _stays[s.StayId] = s; }
    public void Plan(TravelTrip t) { ArgumentNullException.ThrowIfNull(t); _trips[t.TripId] = t; }

    public TravelTrip? GetTrip(string id) => _trips.GetValueOrDefault(id);
    public Flight? GetFlight(string id) => _flights.GetValueOrDefault(id);
    public HotelStay? GetStay(string id) => _stays.GetValueOrDefault(id);

    public decimal TripCost(string tripId)
    {
        if (!_trips.TryGetValue(tripId, out var t)) throw new InvalidOperationException($"Unknown trip {tripId}");
        decimal total = 0m;
        foreach (var fid in t.FlightIds) if (_flights.TryGetValue(fid, out var f)) total += f.Price;
        foreach (var sid in t.StayIds)
        {
            if (_stays.TryGetValue(sid, out var s))
            {
                var nights = Math.Max(1, (s.CheckOut - s.CheckIn).Days);
                total += s.NightlyRate * nights;
            }
        }
        return total;
    }

    public IReadOnlyList<TravelTrip> UpcomingTrips(DateTime now)
        => _trips.Values.Where(t => t.StartDate >= now).OrderBy(t => t.StartDate).ToArray();
}
