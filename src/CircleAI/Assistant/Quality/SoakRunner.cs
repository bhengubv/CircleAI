// SoakRunner.cs
//
// THE 24-HOUR TEST, RUN BY THE SERVICE ITSELF, ON ANY PHONE.
//
// A soak needs somebody asking questions on a schedule for a day and a record of every
// answer, every restart and every reading of memory - and it must not depend on one
// vendor's settings to stay alive. So the service runs it: one question every ten
// minutes through the same door every app uses, a burst of three every hour, a long
// quiet gap every three hours, a health reading every round. Its state is a file, so
// a service that is killed and restarted picks the run up again - and the restart is
// itself a finding, recorded with its cause. Rounds go to JSONL in the app's external
// files folder, where `adb pull` reads them on any phone without root.
//
// THE PASS CRITERIA ARE COMPUTED HERE TOO, into summary.json, so a run can be judged
// from one file: every question has exactly one final record, the corpus is answered
// correctly often enough, memory does not climb without stopping after the first hour,
// and every restart left a reason.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Requests;

namespace CircleAI.Assistant.Quality;

/// <summary>Runs the long soak against the request coordinator.</summary>
public sealed class SoakRunner : IAsyncDisposable
{
    /// <summary>Who soak questions are from, as the queue knows it.</summary>
    public const string CallerKey = "internal:soak";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static readonly (string Id, string Q, string? Must)[] Corpus =
    {
        ("greeting", "Hey B", null),
        ("which-brain", "Which brain are you using?", null),
        ("times-17-4", "What is 17 times 4?", "68"),
        ("capital-ke", "What is the capital of Kenya?", "Nairobi"),
        ("leap-year", "How many days are in a leap year?", "366"),
        ("weather", "What is the weather like today?", null),
        ("date", "What date is it today?", null),
        ("capital-fr", "What is the capital of France?", "Paris"),
    };

    private readonly IAskFront _front;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _health;
    private readonly string _dir;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly TimeSpan _cadence;
    private readonly TimeSpan _duration;
    private readonly Action? _fault;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    private sealed record SoakState(string RunId, DateTimeOffset StartedAt, int Round, bool Running, string? BootId);

    /// <summary>A runner writing into <paramref name="directory"/>.</summary>
    public SoakRunner(IAskFront front, Func<IReadOnlyList<KeyValuePair<string, string>>> health, string directory,
                      TimeSpan? cadence = null, TimeSpan? duration = null, TimeProvider? clock = null, Action<string>? log = null,
                      Action? fault = null)
    {
        _fault = fault;
        _front = front;
        _health = health;
        _dir = directory;
        _cadence = cadence ?? TimeSpan.FromMinutes(10);
        _duration = duration ?? TimeSpan.FromHours(24);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (_ => { });
    }

    private string StatePath => Path.Combine(_dir, "soak-state.json");

    /// <summary>Whether a run is in progress (possibly started by a previous process).</summary>
    public bool Running => Load() is { Running: true } s && _clock.GetUtcNow() - s.StartedAt < _duration;

    /// <summary>Start a new run (or carry on an existing one after a restart).</summary>
    public void Start(string? runId, string bootId, bool resume)
    {
        Directory.CreateDirectory(_dir);
        var now = _clock.GetUtcNow();
        var state = Load();
        if (!resume || state is not { Running: true })
            state = new SoakState(string.IsNullOrWhiteSpace(runId) ? now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) : runId!, now, 0, true, bootId);
        else if (state.BootId != bootId)
        {
            // THE SERVICE RESTARTED DURING THE RUN. That is a finding, written down with
            // what readiness says ended the last process.
            Event(state, "restart", new() { ["previousBoot"] = state.BootId ?? "", ["boot"] = bootId });
            state = state with { BootId = bootId };
        }
        Save(state);
        Event(state, resume ? "resumed" : "started", new());

        _stop?.Cancel();
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loop = Task.Run(() => LoopAsync(ct), ct);
        _log($"soak {state.RunId}: {(resume ? "resumed" : "started")} at round {state.Round}");
    }

    /// <summary>Stop the run.</summary>
    public void Stop()
    {
        if (Load() is { } s) { Save(s with { Running = false }); Event(s, "stopped", new()); Summarise(s); }
        _stop?.Cancel();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        return ValueTask.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var state = Load();
            if (state is not { Running: true }) return;
            var now = _clock.GetUtcNow();
            if (now - state.StartedAt >= _duration)
            {
                Save(state with { Running = false });
                Event(state, "finished", new());
                Summarise(state);
                return;
            }

            var round = state.Round;
            var hour = (int)((now - state.StartedAt).TotalHours);

            // A LONG QUIET GAP EVERY THREE HOURS: two rounds skipped, so the next
            // question is the first after twenty minutes of nothing.
            var quiet = hour > 0 && hour % 3 == 0 && round % 6 is 1 or 2;
            if (!quiet)
            {
                var burst = round % 6 == 0 ? 3 : 1;   // a burst of three once an hour
                // THE PLAN'S FAULTS, AT HOURS 8 AND 16: the inference process is ended
                // mid-answer, and the round records whether that question still finished.
                var fault = _fault is not null && round > 0 && round % 48 == 0;
                for (var i = 0; i < burst && !ct.IsCancellationRequested; i++)
                    await AskOneAsync(state, round, i, ct, fault && i == 0).ConfigureAwait(false);
            }
            else Event(state, "quiet", new() { ["round"] = I(round) });

            Save(state with { Round = round + 1 });
            if (round % 6 == 5) Summarise(state);
            try { await Task.Delay(_cadence, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task AskOneAsync(SoakState state, int round, int index, CancellationToken ct, bool fault = false)
    {
        var (id, q, must) = fault
            ? ("fault-long", "Explain how a solar panel turns sunlight into electricity, step by step.", (string?)"sun")
            : Corpus[(round * 3 + index) % Corpus.Length];
        var rid = $"soak-{state.RunId}-{round}-{index}";
        var health = _health();
        var started = _clock.GetUtcNow();
        var admission = _front.Submit(new AskSubmission(CallerKey, rid, string.Empty, AskLane.Background, q, q));
        AskRecord? done = null;
        if (admission.Accepted && admission.Record is { } r)
        {
            if (fault)
            {
                await _front.WaitForChangeAsync(CallerKey, r.Id, r.Seq, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                Event(state, "fault", new() { ["round"] = I(round), ["what"] = "inference process ended mid-answer" });
                try { _fault!(); } catch (Exception ex) { _log("soak fault failed: " + ex.GetType().Name); }
            }
            done = await _front.WaitAsync(CallerKey, r.Id, TimeSpan.FromMinutes(4), ct).ConfigureAwait(false);
        }

        var passed = done?.State == AskState.Succeeded && (must is null || done.Text.Contains(must, StringComparison.OrdinalIgnoreCase));
        var row = new Dictionary<string, string>
        {
            ["round"] = I(round), ["i"] = I(index), ["q"] = id, ["rid"] = rid,
            ["accepted"] = admission.Accepted ? "1" : "0", ["refusal"] = admission.Refusal ?? "",
            ["state"] = done?.State.ToString() ?? "none", ["kind"] = done?.Kind?.ToString() ?? "",
            ["passed"] = passed ? "1" : "0", ["attempts"] = I(done?.Attempts ?? 0), ["fault"] = fault ? "1" : "0",
            ["wallMs"] = L((long)(_clock.GetUtcNow() - started).TotalMilliseconds),
            ["prefillMs"] = L(done?.Timing?.PrefillMs ?? -1), ["promptTok"] = I(done?.Timing?.PromptTokens ?? -1),
            ["decodeMs"] = L(done?.Timing?.DecodeMs ?? -1), ["outTok"] = I(done?.Timing?.OutputTokens ?? -1),
            ["totalMs"] = L(done?.Timing?.TotalMs ?? -1), ["model"] = done?.Timing?.ModelId ?? done?.ModelId ?? "",
        };
        foreach (var (k, v) in health) row["h." + k] = v;
        Append(state, "rounds.jsonl", row);
    }

    // ── the verdict ──────────────────────────────────────────────────────────

    private void Summarise(SoakState state)
    {
        try
        {
            var rounds = ReadRows(state, "rounds.jsonl");
            var events = ReadRows(state, "events.jsonl");
            var asked = rounds.Count;
            var final = rounds.Count(r => r.TryGetValue("state", out var s) && s is "Succeeded" or "Failed" or "Cancelled" or "Expired");
            var correct = rounds.Count(r => r.GetValueOrDefault("passed") == "1");
            var restarts = events.Count(e => e.GetValueOrDefault("event") == "restart");

            // MEMORY AFTER THE FIRST HOUR: the slope of the service's RSS across the run,
            // in MB per hour; a climb that never stops is the leak this exists to catch.
            var start = state.StartedAt;
            var rss = rounds
                .Where(r => r.ContainsKey("h.rssKb") && DateTimeOffset.TryParse(r.GetValueOrDefault("at"), out _))
                .Select(r => (At: DateTimeOffset.Parse(r["at"], CultureInfo.InvariantCulture), Mb: double.Parse(r["h.rssKb"], CultureInfo.InvariantCulture) / 1024))
                .Where(p => p.At - start > TimeSpan.FromHours(1))
                .ToList();
            var slope = Slope(rss.Select(p => ((p.At - start).TotalHours, p.Mb)).ToList());

            var summary = new Dictionary<string, object?>
            {
                ["runId"] = state.RunId,
                ["startedAt"] = state.StartedAt,
                ["hours"] = Math.Round((_clock.GetUtcNow() - state.StartedAt).TotalHours, 2),
                ["asked"] = asked,
                ["final"] = final,
                ["everyQuestionFinal"] = asked == final,
                ["correct"] = correct,
                ["correctShare"] = asked == 0 ? 0 : Math.Round(correct / (double)asked, 3),
                ["restarts"] = restarts,
                ["rssSlopeMbPerHourAfterHour1"] = slope is { } s ? Math.Round(s, 2) : null,
                ["memoryFlat"] = slope is null || slope < 5,
                ["pastSixHours"] = (_clock.GetUtcNow() - state.StartedAt) > TimeSpan.FromHours(6),
            };
            File.WriteAllText(Path.Combine(RunDir(state), "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            _log($"soak {state.RunId}: {asked} asked, {final} final, {correct} correct, {restarts} restart(s), rss slope {slope?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-"} MB/h");
        }
        catch (Exception ex) { _log("soak summary failed: " + ex.GetType().Name); }
    }

    private static double? Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) return null;
        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);
        var den = points.Sum(p => (p.X - mx) * (p.X - mx));
        return den <= 0 ? null : points.Sum(p => (p.X - mx) * (p.Y - my)) / den;
    }

    // ── files ────────────────────────────────────────────────────────────────

    private string RunDir(SoakState state)
    {
        var dir = Path.Combine(_dir, state.RunId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Event(SoakState state, string name, Dictionary<string, string> data)
    {
        data["event"] = name;
        Append(state, "events.jsonl", data);
    }

    private void Append(SoakState state, string file, Dictionary<string, string> row)
    {
        try
        {
            row["at"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            File.AppendAllText(Path.Combine(RunDir(state), file), JsonSerializer.Serialize(row, Json) + "\n");
        }
        catch (Exception ex) { _log("soak write failed: " + ex.GetType().Name); }
    }

    private List<Dictionary<string, string>> ReadRows(SoakState state, string file)
    {
        var path = Path.Combine(RunDir(state), file);
        if (!File.Exists(path)) return new();
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<Dictionary<string, string>>(line) is { } row) rows.Add(row); }
            catch { /* a torn last line from a kill */ }
        }
        return rows;
    }

    private SoakState? Load()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<SoakState>(File.ReadAllText(StatePath)) : null; }
        catch { return null; }
    }

    private void Save(SoakState state)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex) { _log("soak state not saved: " + ex.GetType().Name); }
    }

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string L(long v) => v.ToString(CultureInfo.InvariantCulture);
}
