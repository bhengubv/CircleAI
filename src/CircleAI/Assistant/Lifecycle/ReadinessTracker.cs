// ReadinessTracker.cs
//
// THE ONE PLACE THE SERVICE'S STATE IS WRITTEN DOWN. See IReadinessTracker for why
// there must be exactly one. This implementation keeps the snapshot in memory, bumps
// the sequence only on a real change, and - when given a path - saves it, so the next
// process can say what ended the last one instead of starting from "not started".

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Thread-safe <see cref="IReadinessTracker"/> with optional persistence.</summary>
public sealed class ReadinessTracker : IReadinessTracker
{
    private const int FailuresKept = 5;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly string? _persistPath;
    private readonly TimeProvider _clock;
    private readonly List<string> _order = new();
    private readonly Dictionary<string, ComponentState> _components = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _settings = new();
    private readonly List<Failure> _failures = new();
    private ComponentState _brain;
    private ReadinessSnapshot _current;
    private long _seq;
    private DateTimeOffset _lastSaved = DateTimeOffset.MinValue;

    /// <summary>A tracker, persisted to <paramref name="persistPath"/> when given.</summary>
    public ReadinessTracker(string? persistPath = null, TimeProvider? clock = null, string? bootId = null)
    {
        _persistPath = persistPath;
        _clock = clock ?? TimeProvider.System;
        BootId = string.IsNullOrWhiteSpace(bootId) ? Guid.NewGuid().ToString("N") : bootId!;

        Previous = Load(persistPath);
        if (Previous is not null)
        {
            _seq = Previous.Seq;   // a client that saw the last process's seq still sees news
            _failures.AddRange(Previous.RecentFailures.Take(FailuresKept));
        }

        _brain = ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Unknown, "starting", _clock.GetUtcNow());
        _current = Build(++_seq);
    }

    /// <summary>What the previous process persisted, if anything.</summary>
    public ReadinessSnapshot? Previous { get; }

    /// <inheritdoc />
    public string BootId { get; }

    /// <inheritdoc />
    public ReadinessSnapshot Current { get { lock (_gate) return _current; } }

    /// <inheritdoc />
    public long Seq { get { lock (_gate) return _seq; } }

    /// <inheritdoc />
    public event EventHandler<ReadinessSnapshot>? Changed;

    /// <inheritdoc />
    public void Report(ComponentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ReadinessSnapshot snapshot;
        bool phaseChanged;
        lock (_gate)
        {
            var isBrain = string.Equals(state.Component, ReadinessComponents.Brain, StringComparison.Ordinal);
            var before = isBrain ? _brain : _components.GetValueOrDefault(state.Component);
            if (before is not null && Same(before, state)) return;

            phaseChanged = before is null || before.Phase != state.Phase || before.Kind != state.Kind;
            if (isBrain) _brain = state;
            else
            {
                if (!_components.ContainsKey(state.Component)) _order.Add(state.Component);
                _components[state.Component] = state;
            }
            snapshot = _current = Build(++_seq);
        }
        Publish(snapshot, saveNow: phaseChanged);
    }

    /// <inheritdoc />
    public void Failed(Failure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ReadinessSnapshot snapshot;
        lock (_gate)
        {
            _failures.Insert(0, failure);
            if (_failures.Count > FailuresKept) _failures.RemoveRange(FailuresKept, _failures.Count - FailuresKept);
            snapshot = _current = Build(++_seq);
        }
        Publish(snapshot, saveNow: true);
    }

    /// <inheritdoc />
    public void Setting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ReadinessSnapshot snapshot;
        lock (_gate)
        {
            var at = _settings.FindIndex(p => p.Key == key);
            if (at >= 0 && _settings[at].Value == value) return;
            if (at >= 0) _settings[at] = new(key, value ?? string.Empty);
            else _settings.Add(new(key, value ?? string.Empty));
            snapshot = _current = Build(++_seq);
        }
        Publish(snapshot, saveNow: false);
    }

    // A REPORT IS THE SAME WHEN ONLY ITS TIMESTAMP DIFFERS. A component that says
    // "Ready" every ten seconds must not wake every poller every ten seconds.
    private static bool Same(ComponentState a, ComponentState b) =>
        a.Phase == b.Phase && a.Step == b.Step && a.Kind == b.Kind && a.Pause == b.Pause
        && a.Detail == b.Detail && a.ModelId == b.ModelId && Nullable.Equals(a.Progress, b.Progress)
        && Nullable.Equals(a.RetryAfter, b.RetryAfter);

    private ReadinessSnapshot Build(long seq) => new(
        ReadinessSnapshot.CurrentSchema, seq, BootId, _brain,
        _order.Select(n => _components[n]).ToArray(),
        _failures.ToArray(),
        _settings.ToArray());

    private void Publish(ReadinessSnapshot snapshot, bool saveNow)
    {
        Save(snapshot, saveNow);
        try { Changed?.Invoke(this, snapshot); }
        catch { /* a reader that throws must not break the writer */ }
    }

    // PROGRESS IS SAVED AT MOST EVERY TWO SECONDS, phases and failures at once. A
    // download reports progress many times a second; the disk does not need each one.
    private void Save(ReadinessSnapshot snapshot, bool now)
    {
        if (_persistPath is null) return;
        var at = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!now && at - _lastSaved < TimeSpan.FromSeconds(2)) return;
            _lastSaved = at;
        }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_persistPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Persisted.From(snapshot), Json));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch { /* readiness on disk is a courtesy to the next process, never worth a crash */ }
    }

    private static ReadinessSnapshot? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Persisted>(File.ReadAllText(path), Json)?.To(); }
        catch { return null; }   // a torn or old file says nothing; the new run starts clean
    }

    // A flat shape for the file, so a renamed record member does not lose the history.
    private sealed record Persisted(
        long Seq, string BootId, PersistedState Brain, PersistedState[] Components, PersistedFailure[] Failures)
    {
        public static Persisted From(ReadinessSnapshot s) => new(
            s.Seq, s.BootId, PersistedState.From(s.Brain),
            s.Components.Select(PersistedState.From).ToArray(),
            s.RecentFailures.Select(PersistedFailure.From).ToArray());

        public ReadinessSnapshot To() => new(
            ReadinessSnapshot.CurrentSchema, Seq, BootId ?? string.Empty,
            (Brain ?? new PersistedState(ReadinessComponents.Brain, 0, 0, null, 0, "", null, 0)).To(),
            (Components ?? Array.Empty<PersistedState>()).Select(c => c.To()).ToArray(),
            (Failures ?? Array.Empty<PersistedFailure>()).Select(f => f.To()).ToArray(),
            Array.Empty<KeyValuePair<string, string>>());
    }

    private sealed record PersistedState(
        string Component, int Phase, int Step, int? Kind, int Pause, string Detail, string? ModelId, long SinceMs)
    {
        public static PersistedState From(ComponentState c) => new(
            c.Component, (int)c.Phase, (int)c.Step, c.Kind is { } k ? (int)k : null, (int)c.Pause, c.Detail,
            c.ModelId, c.Since.ToUnixTimeMilliseconds());

        public ComponentState To() => new(
            Component ?? ReadinessComponents.Brain, (BrainPhase)Phase, (LoadStep)Step,
            Kind is { } k ? (FailureKind)k : null, (PauseReason)Pause, Detail ?? string.Empty, ModelId,
            DateTimeOffset.FromUnixTimeMilliseconds(SinceMs));
    }

    private sealed record PersistedFailure(
        int Kind, int Certainty, int Phase, string? ModelId, string Evidence, string Detail, long AtMs)
    {
        public static PersistedFailure From(Failure f) => new(
            (int)f.Kind, (int)f.Certainty, (int)f.Phase, f.ModelId, f.Evidence, f.Detail, f.At.ToUnixTimeMilliseconds());

        public Failure To() => new(
            (FailureKind)Kind, (Certainty)Certainty, (WorkPhase)Phase, ModelId, Evidence ?? string.Empty,
            Detail ?? string.Empty, DateTimeOffset.FromUnixTimeMilliseconds(AtMs));
    }
}
