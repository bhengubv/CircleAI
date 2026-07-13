// InMemoryIntegrationConnectors.cs
//
// Deterministic, dependency-free in-memory reference implementations of the
// integration connector contracts. These are the canonical offline/test
// doubles for ICalendarConnector/IEmailConnector/INewsSource/IWeatherProvider/
// IRoutingProvider/IHomeAutomationConnector — usable without any external
// provider, mirroring the InMemory* pattern every other package ships. The
// real provider bindings live in the CircleAI.Integration.* sub-packages.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Integration;

/// <summary>In-memory <see cref="ICalendarConnector"/>: events are held in a map;
/// listing returns those overlapping the window, ordered by start.</summary>
public sealed class InMemoryCalendarConnector : ICalendarConnector
{
    private readonly ConcurrentDictionary<string, CalendarEvent> _events = new();

    public string ProviderId  => "in-memory";
    public bool   IsConfigured => true;

    public ValueTask<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        => new(_events.Values
            .Where(e => e.StartUtc < toUtc && e.EndUtc > fromUtc)
            .OrderBy(e => e.StartUtc)
            .ToArray());

    public ValueTask<CalendarEvent> CreateEventAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        _events[ev.EventId] = ev;
        return new(ev);
    }

    public ValueTask DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default)
    {
        _events.TryRemove(eventId, out _);
        return default;
    }
}

/// <summary>In-memory <see cref="IEmailConnector"/>: seeded with messages;
/// unread + search read newest-first, MarkRead flips the flag.</summary>
public sealed class InMemoryEmailConnector : IEmailConnector
{
    private readonly ConcurrentDictionary<string, EmailMessage> _messages = new();

    public InMemoryEmailConnector(IEnumerable<EmailMessage>? seed = null)
    {
        if (seed is not null)
            foreach (var m in seed) _messages[m.MessageId] = m;
    }

    public string ProviderId  => "in-memory";
    public bool   IsConfigured => true;

    public ValueTask<IReadOnlyList<EmailMessage>> ListUnreadAsync(int max, CancellationToken ct = default)
        => new(_messages.Values
            .Where(m => m.Unread)
            .OrderByDescending(m => m.ReceivedUtc)
            .Take(Math.Max(0, max))
            .ToArray());

    public ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(string query, int max, CancellationToken ct = default)
    {
        query ??= "";
        return new(_messages.Values
            .Where(m => m.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || m.BodyText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.ReceivedUtc)
            .Take(Math.Max(0, max))
            .ToArray());
    }

    public ValueTask MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (_messages.TryGetValue(messageId, out var m))
            _messages[messageId] = m with { Unread = false };
        return default;
    }
}

/// <summary>In-memory <see cref="INewsSource"/>: seeded items, newest-first.</summary>
public sealed class InMemoryNewsSource : INewsSource
{
    private readonly ConcurrentDictionary<string, NewsItem> _items = new();

    public InMemoryNewsSource(IEnumerable<NewsItem>? seed = null)
    {
        if (seed is not null)
            foreach (var i in seed) _items[i.ItemId] = i;
    }

    public string SourceId    => "in-memory";
    public bool   IsConfigured => true;

    public ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default)
        => new(_items.Values
            .OrderByDescending(i => i.PublishedUtc)
            .Take(Math.Max(0, max))
            .ToArray());
}

/// <summary>In-memory <see cref="IWeatherProvider"/>: deterministic pseudo-weather
/// derived from coordinates + hour (no randomness, reproducible across platforms).</summary>
public sealed class InMemoryWeatherProvider : IWeatherProvider
{
    public string ProviderId => "in-memory";

    public ValueTask<WeatherSample> CurrentAsync(double lat, double lon, CancellationToken ct = default)
        => new(Sample(lat, lon, 0));

    public ValueTask<IReadOnlyList<WeatherSample>> HourlyAsync(
        double lat, double lon, int hours, CancellationToken ct = default)
        => new(Enumerable.Range(0, Math.Max(0, hours)).Select(h => Sample(lat, lon, h)).ToArray());

    private static WeatherSample Sample(double lat, double lon, int hourOffset)
    {
        var tempC = Math.Round(15.0 + 10.0 * Math.Cos((lat + hourOffset) * Math.PI / 12.0), 2);
        return new WeatherSample(
            DateTimeOffset.UnixEpoch.AddHours(hourOffset),
            tempC, Math.Round(tempC - 1.5, 2), 0.0, 12.0, 40, "Clear");
    }
}

/// <summary>In-memory <see cref="IRoutingProvider"/>: great-circle distance and a
/// mode-based speed give a deterministic estimate with a 2-point polyline.</summary>
public sealed class InMemoryRoutingProvider : IRoutingProvider
{
    public string ProviderId => "in-memory";

    public ValueTask<RouteEstimate> RouteAsync(
        double fromLat, double fromLon, double toLat, double toLon,
        string mode = "car", CancellationToken ct = default)
    {
        var km  = Haversine(fromLat, fromLon, toLat, toLon);
        var kph = mode switch { "walk" => 5.0, "bike" => 18.0, "transit" => 30.0, _ => 60.0 };
        var dur = TimeSpan.FromHours(kph <= 0 ? 0 : km / kph);
        return new(new RouteEstimate(Math.Round(km, 3), dur,
            new[] { (fromLat, fromLon), (toLat, toLon) }));
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0, dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

/// <summary>In-memory <see cref="IHomeAutomationConnector"/>: seeded entities;
/// turn_on/turn_off/toggle deterministically mutate matching-domain entity state.</summary>
public sealed class InMemoryHomeAutomationConnector : IHomeAutomationConnector
{
    private readonly ConcurrentDictionary<string, HaEntity> _entities = new();

    public InMemoryHomeAutomationConnector(IEnumerable<HaEntity>? seed = null)
    {
        if (seed is not null)
            foreach (var e in seed) _entities[e.EntityId] = e;
    }

    public string ProviderId  => "in-memory";
    public bool   IsConfigured => true;

    public ValueTask<IReadOnlyList<HaEntity>> ListEntitiesAsync(CancellationToken ct = default)
        => new(_entities.Values.OrderBy(e => e.EntityId).ToArray());

    public ValueTask CallServiceAsync(
        string domain, string service,
        IReadOnlyDictionary<string, object?>? data, CancellationToken ct = default)
    {
        foreach (var e in _entities.Values
                     .Where(e => string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var newState = service switch
            {
                "turn_on"  => "on",
                "turn_off" => "off",
                "toggle"   => e.State == "on" ? "off" : "on",
                _           => e.State,
            };
            _entities[e.EntityId] = e with { State = newState };
        }
        return default;
    }
}
