// WolverineOutbox.cs
//
// THE LONG TERM: WHAT THIS DEVICE LEARNED, SENT WHERE A FIX CAN REACH EVERY DEVICE (S6).
//
// Wolverine on the phone fixes what it can by itself. WolverineAPI is where a failure seen on
// many phones becomes a fix in the code, and it can only do that for failures it hears about.
// So every HealthEvent - and what the phone did about it - goes into this outbox, one entry per
// fingerprint with a count, and is posted in batches when the phone is online.
//
// ON BY DEFAULT, TOLD, AND SWITCHABLE OFF - the owner's decision. The first start says so once,
// in a sentence the service speaks through its apps; switching it off empties what is waiting
// and stops anything new being kept. Nothing in it is personal: see WolverineReports.cs.
//
// NO INTAKE YET, AND THAT IS NOT A REASON TO DROP ANYTHING. The device-facing intake on
// WolverineAPI does not exist; it is its own task in another repository, because a phone must
// not hold a service key. Until an endpoint is configured the reports stay queued here - capped,
// so a phone with nowhere to send them cannot fill up - and the first batch after one is set
// carries them all.
//
// ITS OWN HTTP CLIENT, its own files, no reference to anything outside this repository.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircleAI.Core.Logging;

namespace CircleAI.Hosting.SelfHealing.Upstream;

/// <summary>What a flush did.</summary>
public enum OutboxFlush
{
    /// <summary>Nothing was waiting.</summary>
    Empty,

    /// <summary>Reports are switched off; nothing is kept or sent.</summary>
    Off,

    /// <summary>No endpoint is configured; the reports stay queued.</summary>
    NoEndpoint,

    /// <summary>The device is offline; the reports stay queued.</summary>
    Offline,

    /// <summary>The batch was accepted and removed from the queue.</summary>
    Sent,

    /// <summary>The send failed; the reports stay queued for the next try.</summary>
    Failed,
}

/// <summary>Fingerprinted failure reports, kept on the device and posted to WolverineAPI when it can.</summary>
public sealed class WolverineOutbox : IDisposable
{
    /// <summary>The most fingerprints kept waiting; the oldest go first past it.</summary>
    public const int MaxQueued = 200;

    /// <summary>The most reports one post carries.</summary>
    public const int BatchSize = 50;

    private const string QueueFile = "outbox-queue.json";
    private const string SettingsFile = "outbox-settings.json";

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<bool> _online;
    private readonly TimeProvider _clock;
    private readonly Func<string, bool>? _isKnownSubject;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private Dictionary<string, WolverineReport> _queue;
    private OutboxSettings _settings;

    /// <param name="directory">Where the queue and its settings live (the service's own state folder).</param>
    /// <param name="http">The client to post with; null makes this outbox's own.</param>
    /// <param name="online">Whether the device can reach the network now; null asks the device probe.</param>
    /// <param name="clock">Injected for testable time.</param>
    /// <param name="isKnownSubject">Which subjects may travel - catalogued model ids; null lets none.</param>
    public WolverineOutbox(string directory, HttpClient? http = null, Func<bool>? online = null, TimeProvider? clock = null,
        Func<string, bool>? isKnownSubject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _online = online ?? (() => CircleAI.Core.DeviceProbe.Snapshot().Connectivity == CircleAI.Core.Connectivity.Online);
        _clock = clock ?? TimeProvider.System;
        _isKnownSubject = isKnownSubject;
        _settings = Load<OutboxSettings>(SettingsFile) ?? new OutboxSettings();
        _queue = (Load<List<WolverineReport>>(QueueFile) ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Fingerprint))
            .GroupBy(r => r.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    /// <summary>Whether reports are kept and sent. On by default; the person can switch it off.</summary>
    public bool Enabled { get { lock (_gate) return _settings.Enabled; } }

    /// <summary>Where batches are posted; null keeps them queued on the device.</summary>
    public Uri? Endpoint
    {
        get
        {
            lock (_gate)
                return Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    /// <summary>How many fingerprints are waiting.</summary>
    public int Queued { get { lock (_gate) return _queue.Count; } }

    /// <summary>When a batch was last accepted, or null.</summary>
    public DateTimeOffset? LastSentUtc { get { lock (_gate) return _settings.LastSentUtc; } }

    /// <summary>Whether the person has been told that reports are on.</summary>
    public bool Told { get { lock (_gate) return _settings.Told; } }

    /// <summary>What is waiting, oldest first - for the healing verb and for tests.</summary>
    public IReadOnlyList<WolverineReport> Pending()
    {
        lock (_gate) return _queue.Values.OrderBy(r => r.FirstSeenUtc).ToList();
    }

    /// <summary>Keeps <paramref name="health"/> for the next batch: a new fingerprint, or one more of one already waiting.</summary>
    /// <returns>False when reports are switched off and nothing was kept.</returns>
    public bool Enqueue(HealthEvent health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var report = WolverineReport.From(health, _isKnownSubject);
        lock (_gate)
        {
            if (!_settings.Enabled) return false;
            if (_queue.TryGetValue(report.Fingerprint, out var was))
            {
                was.Occurrences++;
                if (report.LastSeenUtc > was.LastSeenUtc) was.LastSeenUtc = report.LastSeenUtc;
                if (report.FirstSeenUtc < was.FirstSeenUtc) was.FirstSeenUtc = report.FirstSeenUtc;
                if (report.Remedy is not null)
                {
                    was.Remedy = report.Remedy;
                    was.RemedyWorked = report.RemedyWorked;
                }
                if (report.Subject is not null) was.Subject = report.Subject;
                if (report.MessageHash is not null) was.MessageHash = report.MessageHash;
                if (string.IsNullOrEmpty(was.Area)) was.Area = report.Area;
            }
            else
            {
                _queue[report.Fingerprint] = report;
                while (_queue.Count > MaxQueued)
                    _queue.Remove(_queue.Values.OrderBy(r => r.LastSeenUtc).First().Fingerprint);
            }
            SaveQueue();
        }
        return true;
    }

    /// <summary>Records what the device did about a fingerprint, and whether it worked once that is known.</summary>
    /// <remarks>
    /// THE ANSWER OFTEN COMES AFTER THE REPORT LEFT. A remedy is checked on the next answer or
    /// the next download, by which time the failure may already have been sent; the outcome then
    /// travels as a report of its own with no new occurrences, keyed on the same fingerprint.
    /// </remarks>
    public void NoteRemedy(string fingerprint, string remedy, bool? worked)
    {
        if (WolverineReport.Identifier(fingerprint) is not { } key || WolverineReport.Identifier(remedy) is not { } name) return;
        lock (_gate)
        {
            if (!_settings.Enabled) return;
            if (!_queue.TryGetValue(key, out var report))
            {
                var parts = key.Split(':', 3);
                if (parts.Length != 3 || parts[0] != WolverineReport.Api) return;
                var now = _clock.GetUtcNow();
                report = new WolverineReport
                {
                    Fingerprint = key, ErrorType = parts[1], Endpoint = parts[2], Occurrences = 0, FirstSeenUtc = now, LastSeenUtc = now,
                };
                _queue[key] = report;
            }
            report.Remedy = name;
            if (worked is not null) report.RemedyWorked = worked;
            SaveQueue();
        }
    }

    /// <summary>
    /// Switches reports on or off. Off empties the queue - the person said no, and what was
    /// waiting goes with it - and nothing new is kept until it is on again.
    /// </summary>
    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            _settings.Enabled = on;
            if (!on) _queue.Clear();
            SaveSettings();
            SaveQueue();
        }
        CircleLog.Info("Heal", on ? "Wolverine reports switched on" : "Wolverine reports switched off; what was waiting is deleted");
    }

    /// <summary>Sets where batches go; null or blank clears it and the reports stay queued. Only https is accepted.</summary>
    /// <returns>False when the address is not an absolute https address.</returns>
    public bool SetEndpoint(string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)
            && !(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            return false;
        lock (_gate)
        {
            _settings.Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
            SaveSettings();
        }
        return true;
    }

    /// <summary>Marks the person told that reports are on; true the first time only.</summary>
    public bool MarkTold()
    {
        lock (_gate)
        {
            if (_settings.Told) return false;
            _settings.Told = true;
            SaveSettings();
            return true;
        }
    }

    /// <summary>
    /// Posts what is waiting, a batch at a time, when reports are on, an endpoint is set and the
    /// device is online. A report leaves the queue only once the server has accepted it.
    /// </summary>
    public async Task<OutboxFlush> FlushAsync(WolverineDeviceClass? device = null, string? appVersion = null, CancellationToken ct = default)
    {
        await _flushing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<WolverineReport> batch;
            Uri? endpoint;
            lock (_gate)
            {
                if (!_settings.Enabled) return OutboxFlush.Off;
                if (_queue.Count == 0) return OutboxFlush.Empty;
                endpoint = Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out var u) ? u : null;
                batch = _queue.Values.OrderBy(r => r.FirstSeenUtc).Take(BatchSize).Select(Copy).ToList();
            }

            if (endpoint is null) return OutboxFlush.NoEndpoint;
            bool online;
            try { online = _online(); }
            catch { online = false; }
            if (!online) return OutboxFlush.Offline;

            var body = new WolverineReportBatch
            {
                SentAtUtc = _clock.GetUtcNow(),
                AppVersion = appVersion is { } v && WolverineReport.Identifier(v.Replace(' ', '_')) is { } version ? version : null,
                Device = device,
                Reports = batch,
            };

            try
            {
                using var response = await _http.PostAsJsonAsync(endpoint, body, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    CircleLog.Info("Heal", $"Wolverine reports not accepted: HTTP {(int)response.StatusCode}; {batch.Count} kept for the next try");
                    return OutboxFlush.Failed;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CircleLog.Info("Heal", $"Wolverine reports could not be sent ({ex.GetType().Name}); {batch.Count} kept for the next try");
                return OutboxFlush.Failed;
            }

            lock (_gate)
            {
                // ONLY WHAT WAS SENT, AND ONLY AS IT WAS SENT: a fingerprint that happened again
                // while the post was in flight keeps the occurrences the batch did not carry.
                foreach (var sent in batch)
                {
                    if (!_queue.TryGetValue(sent.Fingerprint, out var now)) continue;
                    var more = now.Occurrences - sent.Occurrences;
                    if (more > 0 || now.RemedyWorked != sent.RemedyWorked)
                    {
                        now.Occurrences = Math.Max(0, more);
                        now.FirstSeenUtc = sent.LastSeenUtc;
                    }
                    else _queue.Remove(sent.Fingerprint);
                }
                _settings.LastSentUtc = _clock.GetUtcNow();
                SaveQueue();
                SaveSettings();
            }
            CircleLog.Info("Heal", $"Wolverine reports sent: {batch.Count} fingerprint(s)");
            return OutboxFlush.Sent;
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _flushing.Dispose();
    }

    // ── on disk ────────────────────────────────────────────────────────────────

    private static WolverineReport Copy(WolverineReport r) => new()
    {
        Fingerprint = r.Fingerprint, ApiName = r.ApiName, ErrorType = r.ErrorType, Endpoint = r.Endpoint, Area = r.Area,
        Subject = r.Subject, MessageHash = r.MessageHash, Occurrences = r.Occurrences, FirstSeenUtc = r.FirstSeenUtc,
        LastSeenUtc = r.LastSeenUtc, Remedy = r.Remedy, RemedyWorked = r.RemedyWorked,
    };

    private T? Load<T>(string file) where T : class
    {
        try
        {
            var path = Path.Combine(_directory, file);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;   // a damaged queue is an empty one; the next failure starts it again
        }
    }

    private void SaveQueue() => Save(QueueFile, _queue.Values.ToList());

    private void SaveSettings() => Save(SettingsFile, _settings);

    private void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, file);
            var writing = path + ".writing";
            File.WriteAllText(writing, JsonSerializer.Serialize(value));
            File.Move(writing, path, overwrite: true);
        }
        catch (Exception ex)
        {
            CircleLog.Warn("Heal", "the Wolverine outbox could not be written: " + ex.GetType().Name);
        }
    }

    private sealed class OutboxSettings
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

        [JsonPropertyName("told")] public bool Told { get; set; }

        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }

        [JsonPropertyName("lastSentUtc")] public DateTimeOffset? LastSentUtc { get; set; }
    }
}
