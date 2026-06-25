// FeedbackTrainingQueue.cs
//
// (Phase D2) Append-only queue of user feedback signals that the
// NightlyAdapterTrainer drains into LoRA training batches. The queue is
// disk-backed so survival across process restarts is preserved without
// needing a database. Each line of the file is one JSON-encoded sample.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>(Phase D2) One feedback-tagged turn that will inform fine-tuning.</summary>
/// <param name="UserText">What the user said.</param>
/// <param name="AssistantText">What we replied (the "current" answer).</param>
/// <param name="PreferredText">User's correction or the accepted form. Falls back to AssistantText for thumbs-up.</param>
/// <param name="Polarity">+1 (positive) / -1 (negative) / 0 (correction).</param>
/// <param name="AtUtc">When the feedback was given.</param>
public sealed record TrainingSample(
    string         UserText,
    string         AssistantText,
    string         PreferredText,
    int            Polarity,
    DateTimeOffset AtUtc);

public interface IFeedbackTrainingQueue
{
    ValueTask EnqueueAsync(TrainingSample sample, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TrainingSample>> DrainAsync(int maxSamples, CancellationToken ct = default);
    int Pending { get; }
}

/// <summary>(Phase D2) Append-only line-delimited JSON file queue.</summary>
public sealed class FileBackedFeedbackTrainingQueue : IFeedbackTrainingQueue
{
    private readonly string _path;
    private readonly object _writeLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public FileBackedFeedbackTrainingQueue(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required");
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(_path)) File.WriteAllText(_path, string.Empty);
    }

    public int Pending
    {
        get
        {
            if (!File.Exists(_path)) return 0;
            var count = 0;
            using var sr = File.OpenText(_path);
            while (sr.ReadLine() is not null) count++;
            return count;
        }
    }

    public ValueTask EnqueueAsync(TrainingSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var line = JsonSerializer.Serialize(sample, JsonOpts);
        lock (_writeLock)
        {
            File.AppendAllText(_path, line + "\n");
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<TrainingSample>> DrainAsync(int maxSamples, CancellationToken ct = default)
    {
        if (maxSamples <= 0) throw new ArgumentOutOfRangeException(nameof(maxSamples));
        if (!File.Exists(_path)) return Array.Empty<TrainingSample>();

        List<string> remaining;
        var taken = new List<TrainingSample>();
        lock (_writeLock)
        {
            var allLines = File.ReadAllLines(_path);
            var takeCount = Math.Min(maxSamples, allLines.Length);
            for (var i = 0; i < takeCount; i++)
            {
                try { taken.Add(JsonSerializer.Deserialize<TrainingSample>(allLines[i], JsonOpts)!); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FeedbackTrainingQueue] malformed line skipped: {ex.Message}");
                }
            }
            remaining = new List<string>(allLines.Length - takeCount);
            for (var i = takeCount; i < allLines.Length; i++) remaining.Add(allLines[i]);
            File.WriteAllLines(_path, remaining);
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return taken;
    }
}
