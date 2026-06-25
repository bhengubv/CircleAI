// Contracts.cs
//
// (Phase B) Shared abstractions for the external-integration layer.
// Calendar, email, news, weather and home-automation providers all
// implement these so the Companion's ProactiveBriefingService can stitch
// a coherent "what's happening" picture without coupling to specific
// providers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Integration;

// ── Calendar ─────────────────────────────────────────────────────────────

public sealed record CalendarEvent(
    string         EventId,
    string         CalendarId,
    string         Title,
    string?        Description,
    string?        Location,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool           IsAllDay,
    IReadOnlyList<string> Attendees);

public interface ICalendarConnector
{
    string ProviderId { get; }
    bool   IsConfigured { get; }
    ValueTask<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
    ValueTask<CalendarEvent> CreateEventAsync(CalendarEvent ev, CancellationToken ct = default);
    ValueTask DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default);
}

// ── Email ────────────────────────────────────────────────────────────────

public sealed record EmailMessage(
    string         MessageId,
    string         From,
    IReadOnlyList<string> To,
    string         Subject,
    string         BodyText,
    DateTimeOffset ReceivedUtc,
    bool           Unread,
    IReadOnlyList<string> Labels);

public interface IEmailConnector
{
    string ProviderId { get; }
    bool   IsConfigured { get; }
    ValueTask<IReadOnlyList<EmailMessage>> ListUnreadAsync(
        int max, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EmailMessage>> SearchAsync(
        string query, int max, CancellationToken ct = default);
    ValueTask MarkReadAsync(string messageId, CancellationToken ct = default);
}

// ── News + social feeds ──────────────────────────────────────────────────

public sealed record NewsItem(
    string         ItemId,
    string         SourceId,
    string         Title,
    string         Summary,
    Uri            Url,
    DateTimeOffset PublishedUtc,
    IReadOnlyList<string> Tags);

public interface INewsSource
{
    string SourceId { get; }
    bool   IsConfigured { get; }
    ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default);
}

// ── Weather ──────────────────────────────────────────────────────────────

public sealed record WeatherSample(
    DateTimeOffset AtUtc,
    double         TempC,
    double         FeelsLikeC,
    double         PrecipMm,
    double         WindKph,
    int            CloudPct,
    string         Condition);

public interface IWeatherProvider
{
    string ProviderId { get; }
    ValueTask<WeatherSample> CurrentAsync(double lat, double lon, CancellationToken ct = default);
    ValueTask<IReadOnlyList<WeatherSample>> HourlyAsync(
        double lat, double lon, int hours, CancellationToken ct = default);
}

// ── Routing / traffic ────────────────────────────────────────────────────

public sealed record RouteEstimate(
    double         DistanceKm,
    TimeSpan       Duration,
    IReadOnlyList<(double Lat, double Lon)> Polyline);

public interface IRoutingProvider
{
    string ProviderId { get; }
    ValueTask<RouteEstimate> RouteAsync(
        double fromLat, double fromLon, double toLat, double toLon,
        string mode = "car", CancellationToken ct = default);
}

// ── Home automation ──────────────────────────────────────────────────────

public sealed record HaEntity(string EntityId, string FriendlyName, string Domain, string State, IReadOnlyDictionary<string, string> Attributes);

public interface IHomeAutomationConnector
{
    string ProviderId { get; }
    bool   IsConfigured { get; }
    ValueTask<IReadOnlyList<HaEntity>> ListEntitiesAsync(CancellationToken ct = default);
    ValueTask CallServiceAsync(
        string domain, string service,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct = default);
}
