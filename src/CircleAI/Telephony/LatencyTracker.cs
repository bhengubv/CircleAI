// LatencyTracker.cs
//
// (3.3.0) Per-stage latency tracking for the voice loop. Records
// observations into a fixed-size sliding window per stage and surfaces
// p50/p95/p99 + max via a snapshot API.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One stage we track latency on.</summary>
public static class LatencyStage
{
    public const string AsrFirstWord     = "asr.first_word";
    public const string AsrFinal         = "asr.final";
    public const string LlmFirstToken    = "llm.first_token";
    public const string LlmFullResponse  = "llm.full_response";
    public const string TtsFirstAudio    = "tts.first_audio";
    public const string TtsFullAudio     = "tts.full_audio";
    public const string EndToEnd         = "voice_loop.end_to_end";
}

/// <summary>(3.3.0) Snapshot of latency for one stage.</summary>
public sealed record LatencySnapshot(
    string   Stage,
    int      Samples,
    TimeSpan Min,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Max);

/// <summary>(3.3.0) Records latency observations and produces percentiles.</summary>
public sealed class LatencyTracker
{
    private readonly int _windowSize;
    private readonly ConcurrentDictionary<string, Queue<long>> _observations = new(StringComparer.Ordinal);

    public LatencyTracker(int windowSize = 256)
    {
        if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize));
        _windowSize = windowSize;
    }

    /// <summary>Record one observation.</summary>
    public void Record(string stage, TimeSpan latency)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("stage required", nameof(stage));
        if (latency < TimeSpan.Zero) return;

        var queue = _observations.GetOrAdd(stage, _ => new Queue<long>());
        lock (queue)
        {
            queue.Enqueue((long)latency.TotalMilliseconds);
            while (queue.Count > _windowSize) queue.Dequeue();
        }
    }

    /// <summary>Snapshot percentiles for one stage.</summary>
    public LatencySnapshot? Snapshot(string stage)
    {
        if (!_observations.TryGetValue(stage, out var queue)) return null;
        long[] sortedArr;
        lock (queue)
        {
            if (queue.Count == 0) return null;
            sortedArr = queue.ToArray();
        }
        Array.Sort(sortedArr);

        TimeSpan Percentile(double p)
        {
            if (sortedArr.Length == 0) return TimeSpan.Zero;
            var idx = (int)Math.Ceiling(p * sortedArr.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sortedArr.Length) idx = sortedArr.Length - 1;
            return TimeSpan.FromMilliseconds(sortedArr[idx]);
        }

        return new LatencySnapshot(
            Stage:   stage,
            Samples: sortedArr.Length,
            Min:     TimeSpan.FromMilliseconds(sortedArr[0]),
            P50:     Percentile(0.50),
            P95:     Percentile(0.95),
            P99:     Percentile(0.99),
            Max:     TimeSpan.FromMilliseconds(sortedArr[^1]));
    }

    /// <summary>Snapshot every tracked stage.</summary>
    public IReadOnlyList<LatencySnapshot> SnapshotAll()
    {
        var list = new List<LatencySnapshot>();
        foreach (var stage in _observations.Keys.ToArray())
        {
            var snap = Snapshot(stage);
            if (snap is not null) list.Add(snap);
        }
        return list;
    }

    public void Reset(string stage)
    {
        if (!_observations.TryGetValue(stage, out var queue)) return;
        lock (queue) queue.Clear();
    }

    public void ResetAll() => _observations.Clear();
}
