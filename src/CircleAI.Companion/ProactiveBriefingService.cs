// ProactiveBriefingService.cs
//
// (Phase B5) Scheduled hosted service that assembles a "what's happening"
// briefing from registered calendar / email / news / weather connectors,
// runs the result through the LLM for a friendly summary, and pushes the
// outcome through any registered notifier (MauiPushSender, WhatsApp,
// Telegram, etc.).
//
// Schedule is the simplest possible cron — a list of TimeSpans-of-day at
// which the briefing fires. The default is 06:30 and 18:00 UTC; hosts
// override via ProactiveBriefingOptions.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Integration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Companion;

/// <summary>(Phase B5) Configuration knobs for <see cref="ProactiveBriefingService"/>.</summary>
public sealed class ProactiveBriefingOptions
{
    /// <summary>UTC times-of-day at which to fire. Default: 06:30 and 18:00.</summary>
    public IReadOnlyList<TimeSpan> FireTimesUtc { get; init; }
        = new[] { new TimeSpan(6, 30, 0), new TimeSpan(18, 0, 0) };

    /// <summary>Latitude for weather lookup. Null = skip weather.</summary>
    public double? Latitude { get; init; }
    /// <summary>Longitude for weather lookup. Null = skip weather.</summary>
    public double? Longitude { get; init; }

    /// <summary>Headline used by the notifier. Default "Your morning briefing".</summary>
    public string Headline { get; init; } = "Your briefing";

    /// <summary>Where to deliver the briefing. Phone E.164 string for SMS/WhatsApp; channel id for Telegram; etc.</summary>
    public string? DeliveryAddress { get; init; }
}

/// <summary>(Phase B5) Pluggable notifier — hosts wire WhatsApp, Telegram, SMS, push, etc.</summary>
public interface IBriefingNotifier
{
    ValueTask DeliverAsync(string headline, string body, string? address, CancellationToken ct = default);
}

public sealed class ProactiveBriefingService : IHostedService, IAsyncDisposable
{
    private readonly IAIService? _ai;
    private readonly IEnumerable<ICalendarConnector> _calendars;
    private readonly IEnumerable<IEmailConnector>    _emails;
    private readonly IEnumerable<INewsSource>        _news;
    private readonly IWeatherProvider?               _weather;
    private readonly IEnumerable<IBriefingNotifier>  _notifiers;
    private readonly ProactiveBriefingOptions        _opts;
    private readonly ILogger<ProactiveBriefingService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProactiveBriefingService(
        ProactiveBriefingOptions opts,
        IEnumerable<ICalendarConnector>? calendars = null,
        IEnumerable<IEmailConnector>?    emails    = null,
        IEnumerable<INewsSource>?        news      = null,
        IWeatherProvider?                weather   = null,
        IEnumerable<IBriefingNotifier>?  notifiers = null,
        IAIService?                      ai        = null,
        ILogger<ProactiveBriefingService>? logger  = null)
    {
        _opts      = opts      ?? throw new ArgumentNullException(nameof(opts));
        _calendars = calendars ?? Array.Empty<ICalendarConnector>();
        _emails    = emails    ?? Array.Empty<IEmailConnector>();
        _news      = news      ?? Array.Empty<INewsSource>();
        _weather   = weather;
        _notifiers = notifiers ?? Array.Empty<IBriefingNotifier>();
        _ai        = ai;
        _logger    = (ILogger<ProactiveBriefingService>?)logger ?? NullLogger<ProactiveBriefingService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("[ProactiveBriefingService] started with {Count} fire-time(s).", _opts.FireTimesUtc.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        _logger.LogInformation("[ProactiveBriefingService] stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sleep = TimeUntilNextFire(DateTimeOffset.UtcNow);
            try { await Task.Delay(sleep, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try { await FireOnceAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProactiveBriefingService] fire failed");
            }
        }
    }

    /// <summary>Compute time until the next configured fire moment. Always > 30 s to avoid double-fires.</summary>
    internal TimeSpan TimeUntilNextFire(DateTimeOffset now)
    {
        if (_opts.FireTimesUtc.Count == 0) return TimeSpan.FromHours(1);
        var todayBase = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        TimeSpan? best = null;
        foreach (var tod in _opts.FireTimesUtc)
        {
            var candidate = todayBase + tod;
            if (candidate <= now.AddSeconds(30)) candidate = candidate.AddDays(1);
            var gap = candidate - now;
            if (best is null || gap < best) best = gap;
        }
        return best ?? TimeSpan.FromHours(1);
    }

    /// <summary>(Phase B5) Assemble the briefing context, summarise via the LLM, deliver.</summary>
    public async ValueTask FireOnceAsync(CancellationToken ct = default)
    {
        var ctxParts = new List<string>();

        // Calendar — next 24 hours.
        foreach (var cal in _calendars.Where(c => c.IsConfigured))
        {
            try
            {
                var events = await cal.ListEventsAsync(
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), ct).ConfigureAwait(false);
                if (events.Count > 0)
                {
                    ctxParts.Add($"### Calendar ({cal.ProviderId})");
                    foreach (var e in events.OrderBy(e => e.StartUtc).Take(8))
                        ctxParts.Add($"- {e.StartUtc.ToLocalTime():HH:mm} {e.Title}{(string.IsNullOrEmpty(e.Location) ? "" : " @ " + e.Location)}");
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[briefing] calendar {Pid} skipped", cal.ProviderId); }
        }

        // Email — unread.
        foreach (var em in _emails.Where(c => c.IsConfigured))
        {
            try
            {
                var unread = await em.ListUnreadAsync(5, ct).ConfigureAwait(false);
                if (unread.Count > 0)
                {
                    ctxParts.Add($"### Unread email ({em.ProviderId})");
                    foreach (var m in unread)
                        ctxParts.Add($"- {m.From}: {m.Subject}");
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[briefing] email {Pid} skipped", em.ProviderId); }
        }

        // News — latest from each source.
        foreach (var src in _news.Where(s => s.IsConfigured))
        {
            try
            {
                var items = await src.FetchLatestAsync(5, ct).ConfigureAwait(false);
                if (items.Count > 0)
                {
                    ctxParts.Add($"### News ({src.SourceId})");
                    foreach (var i in items) ctxParts.Add($"- {i.Title}");
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[briefing] news {Sid} skipped", src.SourceId); }
        }

        // Weather — if location configured.
        if (_weather is not null && _opts.Latitude is double lat && _opts.Longitude is double lon)
        {
            try
            {
                var now = await _weather.CurrentAsync(lat, lon, ct).ConfigureAwait(false);
                ctxParts.Add($"### Weather ({_weather.ProviderId})");
                ctxParts.Add($"- {now.TempC:F0}°C {now.Condition}, feels {now.FeelsLikeC:F0}°C, wind {now.WindKph:F0} km/h");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[briefing] weather skipped"); }
        }

        if (ctxParts.Count == 0)
        {
            _logger.LogDebug("[ProactiveBriefingService] no signals; skipping fire");
            return;
        }

        var context = string.Join("\n", ctxParts);
        var prompt  = "Summarise the user's morning briefing in 80 words or less. Warm but factual. End with the one thing they should do first today.\n\n" + context;

        string summary;
        if (_ai is not null)
        {
            try { summary = await _ai.ChatAsync(new[] { new ChatMessage("user", prompt) }, ct: ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[briefing] AI summarisation failed; sending raw context");
                summary = context;
            }
        }
        else summary = context;

        foreach (var notifier in _notifiers)
        {
            try { await notifier.DeliverAsync(_opts.Headline, summary, _opts.DeliveryAddress, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[briefing] notifier failed"); }
        }
    }
}
