// RealEstatePrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the RealEstate
// vertical: listings, valuations, viewings, simple suburb-average
// comparable.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.RealEstate;

public enum PropertyKind { Apartment, House, Townhouse, Commercial, Land }

public sealed record Property(string PropertyId, string Suburb, PropertyKind Kind, int Beds, int Baths, double FloorAreaM2);
public sealed record Listing(string ListingId, string PropertyId, decimal AskingPrice, string Currency, DateTimeOffset ListedUtc, bool IsActive);
public sealed record Valuation(string PropertyId, decimal EstimatedValue, string Source, DateTimeOffset AtUtc);
public sealed record Viewing(string ViewingId, string ListingId, string AttendeeName, DateTimeOffset AtUtc);

public interface IRealEstateBoard
{
    void RegisterProperty(Property p);
    void List(Listing l);
    void Close(string listingId);
    void Value(Valuation v);
    void ScheduleViewing(Viewing v);
    IReadOnlyList<Listing> ActiveInSuburb(string suburb);
    decimal? SuburbAverage(string suburb);
}

public sealed class InMemoryRealEstateBoard : IRealEstateBoard
{
    private readonly ConcurrentDictionary<string, Property> _props = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.Ordinal);
    private readonly List<Valuation> _vals = new();
    private readonly List<Viewing> _viewings = new();
    private readonly object _lock = new();

    public void RegisterProperty(Property p) { ArgumentNullException.ThrowIfNull(p); _props[p.PropertyId] = p; }

    public void List(Listing l) { ArgumentNullException.ThrowIfNull(l); _listings[l.ListingId] = l; }

    public void Close(string listingId)
    {
        if (!_listings.TryGetValue(listingId, out var l)) throw new InvalidOperationException($"Unknown listing {listingId}");
        _listings[listingId] = l with { IsActive = false };
    }

    public void Value(Valuation v) { ArgumentNullException.ThrowIfNull(v); lock (_lock) _vals.Add(v); }
    public void ScheduleViewing(Viewing v) { ArgumentNullException.ThrowIfNull(v); lock (_lock) _viewings.Add(v); }

    public IReadOnlyList<Listing> ActiveInSuburb(string suburb)
    {
        if (string.IsNullOrWhiteSpace(suburb)) throw new ArgumentException("suburb required", nameof(suburb));
        return _listings.Values.Where(l => l.IsActive && _props.TryGetValue(l.PropertyId, out var p)
            && string.Equals(p.Suburb, suburb, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.ListedUtc).ToArray();
    }

    public decimal? SuburbAverage(string suburb)
    {
        var rows = ActiveInSuburb(suburb);
        if (rows.Count == 0) return null;
        return rows.Average(l => l.AskingPrice);
    }
}
