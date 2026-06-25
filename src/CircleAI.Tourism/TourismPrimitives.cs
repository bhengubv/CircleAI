// TourismPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Tourism
// vertical: attractions, itineraries, bookings.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Tourism;

public sealed record Attraction(string AttractionId, string Name, string City, string Country, double Lat, double Lon, IReadOnlyList<string> Tags);
public sealed record ItineraryItem(int DayIndex, TimeSpan StartLocal, TimeSpan EndLocal, string AttractionId, string? Note);
public sealed record Itinerary(string ItineraryId, string Title, IReadOnlyList<ItineraryItem> Items);
public sealed record TourismBooking(string BookingId, string ItineraryId, DateTime StartDate, int Travelers, decimal TotalPrice, string Currency);

public interface ITourismBoard
{
    void Add(Attraction a);
    IReadOnlyList<Attraction> AttractionsInCity(string city);
    IReadOnlyList<Attraction> ByTag(string tag);
    void Plan(Itinerary i);
    Itinerary? GetItinerary(string id);
    void Book(TourismBooking b);
    IReadOnlyList<TourismBooking> Bookings { get; }
}

public sealed class InMemoryTourismBoard : ITourismBoard
{
    private readonly ConcurrentDictionary<string, Attraction> _attractions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Itinerary> _itineraries = new(StringComparer.Ordinal);
    private readonly List<TourismBooking> _bookings = new();
    private readonly object _lock = new();

    public void Add(Attraction a) { ArgumentNullException.ThrowIfNull(a); _attractions[a.AttractionId] = a; }

    public IReadOnlyList<Attraction> AttractionsInCity(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) throw new ArgumentException("city required", nameof(city));
        return _attractions.Values.Where(a => string.Equals(a.City, city, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(a => a.Name).ToArray();
    }

    public IReadOnlyList<Attraction> ByTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("tag required", nameof(tag));
        return _attractions.Values.Where(a => a.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                                  .OrderBy(a => a.Name).ToArray();
    }

    public void Plan(Itinerary i) { ArgumentNullException.ThrowIfNull(i); _itineraries[i.ItineraryId] = i; }
    public Itinerary? GetItinerary(string id) => _itineraries.GetValueOrDefault(id);

    public void Book(TourismBooking b) { ArgumentNullException.ThrowIfNull(b); lock (_lock) _bookings.Add(b); }
    public IReadOnlyList<TourismBooking> Bookings { get { lock (_lock) return _bookings.ToArray(); } }
}
