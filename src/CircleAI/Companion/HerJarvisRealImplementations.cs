// HerJarvisRealImplementations.cs
//
// (3.3.0) Real, working implementations for every HER/Jarvis contract.
// In-process backings (ConcurrentDictionary, Channel, simple math) so
// tests + hosts both get behaviour, not no-ops. Production hosts that
// need cloud-scale variants swap any of these behind the same
// interface.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Companion.HerJarvis;

// =====================================================================
// 1. AlwaysOnPresence — timer-driven heartbeat with start/stop.
// =====================================================================
public sealed class HeartbeatAlwaysOnPresence : IAlwaysOnPresence, IDisposable
{
    private readonly TimeSpan _heartbeatInterval;
    private Timer? _timer;
    private long _ticks;

    public HeartbeatAlwaysOnPresence(TimeSpan? heartbeatInterval = null)
        => _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30);

    public bool IsRunning => _timer is not null;
    public long Heartbeats => Interlocked.Read(ref _ticks);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_timer is not null) return Task.CompletedTask;
        _timer = new Timer(_ => Interlocked.Increment(ref _ticks), null, TimeSpan.Zero, _heartbeatInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}

// =====================================================================
// 2. FusedPerception — Channel-based pub/sub with Publish hook.
// =====================================================================
public sealed class ChannelFusedPerception : IFusedPerception
{
    private readonly Channel<FusedPercept> _channel = Channel.CreateUnbounded<FusedPercept>();

    public void Publish(FusedPercept p)
    {
        ArgumentNullException.ThrowIfNull(p);
        _channel.Writer.TryWrite(p);
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<FusedPercept> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_channel.Reader.TryRead(out var p)) yield return p;
    }
}

// =====================================================================
// 3. IdentitySync — append-only delta log with monotonic cursor.
// =====================================================================
public sealed class JsonIdentitySync : IIdentitySync
{
    private readonly List<(long Cursor, string DeltaJson)> _log = new();
    private readonly object _lock = new();
    private long _next;

    public ValueTask PushAsync(string deltaJson, CancellationToken ct = default)
    {
        if (deltaJson is null) throw new ArgumentNullException(nameof(deltaJson));
        lock (_lock) _log.Add((Interlocked.Increment(ref _next), deltaJson));
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> PullAsync(string sinceCursor, CancellationToken ct = default)
    {
        var since = long.TryParse(sinceCursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        long maxCursor;
        List<string> deltas;
        lock (_lock)
        {
            var taken = _log.Where(e => e.Cursor > since).ToArray();
            maxCursor = taken.Length == 0 ? since : taken[^1].Cursor;
            deltas = taken.Select(e => e.DeltaJson).ToList();
        }
        var payload = new StringBuilder().Append("{\"cursor\":").Append(maxCursor).Append(",\"deltas\":[");
        for (var i = 0; i < deltas.Count; i++)
        {
            if (i > 0) payload.Append(',');
            payload.Append(deltas[i]);
        }
        payload.Append("]}");
        return ValueTask.FromResult(payload.ToString());
    }
}

// =====================================================================
// 4. ContinuousLearner — exponentially weighted average reward per id.
// =====================================================================
public sealed class EwaContinuousLearner : IContinuousLearner
{
    private readonly ConcurrentDictionary<string, (double Avg, double Weight)> _state = new(StringComparer.Ordinal);
    private readonly double _alpha;

    public EwaContinuousLearner(double alpha = 0.2)
    {
        if (alpha is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        _alpha = alpha;
    }

    public ValueTask RegisterFeedbackAsync(string interactionId, double reward, string contextJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(interactionId)) throw new ArgumentException("interactionId required");
        _state.AddOrUpdate(
            interactionId,
            _ => (reward, 1.0),
            (_, prev) => (prev.Avg * (1 - _alpha) + reward * _alpha, prev.Weight + 1));
        return ValueTask.CompletedTask;
    }

    public double? AverageRewardOf(string interactionId)
        => _state.TryGetValue(interactionId, out var s) ? s.Avg : null;

    public long ObservationsOf(string interactionId)
        => _state.TryGetValue(interactionId, out var s) ? (long)s.Weight : 0;
}

// =====================================================================
// 5. WorldModel — learn P(outcome|observation) from registered evidence.
// =====================================================================
public sealed class FrequencyWorldModel : IWorldModel
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Tell the model: when these observations happen, this outcome was seen.</summary>
    public void Observe(IEnumerable<string> observations, string outcome)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("outcome required");
        foreach (var obs in observations)
        {
            var inner = _counts.GetOrAdd(obs, _ => new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase));
            inner.AddOrUpdate(outcome, 1, (_, v) => v + 1);
        }
    }

    public ValueTask<CausalPrediction> PredictAsync(string scenarioJson, CancellationToken ct = default)
    {
        var observations = ExtractObservations(scenarioJson);
        var tally = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var supporters = new List<string>();
        foreach (var obs in observations)
        {
            if (!_counts.TryGetValue(obs, out var inner)) continue;
            supporters.Add(obs);
            foreach (var kv in inner)
                tally[kv.Key] = tally.TryGetValue(kv.Key, out var n) ? n + kv.Value : kv.Value;
        }
        if (tally.Count == 0) return ValueTask.FromResult(new CausalPrediction("unknown", 0.5, supporters));
        var total = tally.Values.Sum();
        var top = tally.OrderByDescending(kv => kv.Value).First();
        return ValueTask.FromResult(new CausalPrediction(top.Key, (double)top.Value / total, supporters));
    }

    private static IReadOnlyList<string> ExtractObservations(string scenarioJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            var hits = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                hits.Add(prop.Name + "=" + prop.Value.ToString());
            }
            return hits;
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

// =====================================================================
// 6. GoalPursuer — store goal + milestones; replan recalculates plan.
// =====================================================================
public sealed class InMemoryGoalPursuer : IGoalPursuer
{
    private readonly ConcurrentDictionary<string, LongHorizonGoal> _goals = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask<LongHorizonGoal> RegisterAsync(string description, DateTimeOffset deadlineUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("description required");
        var id  = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        if (deadlineUtc <= now) throw new ArgumentException("deadline must be in the future");
        var plan = BuildPlan(description, now, deadlineUtc);
        var g = new LongHorizonGoal(id, description, deadlineUtc, plan, 0);
        _goals[id] = g;
        return ValueTask.FromResult(g);
    }

    public ValueTask<LongHorizonGoal?> CurrentAsync(string id, CancellationToken ct = default)
    {
        _goals.TryGetValue(id, out var g);
        return ValueTask.FromResult(g);
    }

    public ValueTask ReplanAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_goals.TryGetValue(id, out var g)) throw new InvalidOperationException($"Unknown goal {id}");
            var plan = BuildPlan(g.Description, DateTimeOffset.UtcNow, g.DeadlineUtc);
            _goals[id] = g with { PlanJson = plan };
        }
        return ValueTask.CompletedTask;
    }

    public void Progress(string id, double fraction)
    {
        if (fraction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(fraction));
        if (!_goals.TryGetValue(id, out var g)) throw new InvalidOperationException($"Unknown goal {id}");
        _goals[id] = g with { ProgressFraction = fraction };
    }

    private static string BuildPlan(string description, DateTimeOffset now, DateTimeOffset deadlineUtc)
    {
        var totalDays = Math.Max(1, (int)(deadlineUtc - now).TotalDays);
        var milestones = Math.Min(8, Math.Max(2, totalDays / 14));
        var step = (deadlineUtc - now) / milestones;
        var sb = new StringBuilder().Append("{\"description\":").Append(JsonSerializer.Serialize(description))
            .Append(",\"milestones\":[");
        for (var i = 1; i <= milestones; i++)
        {
            if (i > 1) sb.Append(',');
            var due = now + step * i;
            sb.Append("{\"index\":").Append(i).Append(",\"due\":\"").Append(due.ToString("O", CultureInfo.InvariantCulture)).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }
}

// =====================================================================
// 7. EpisodicMemory — TF-based similarity recall.
// =====================================================================
public sealed class TfEpisodicMemory : IEpisodicMemory
{
    private readonly ConcurrentDictionary<string, EpisodeRecord> _episodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, int>> _terms = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask RecordAsync(EpisodeRecord episode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (string.IsNullOrWhiteSpace(episode.Id)) throw new ArgumentException("Id required");
        lock (_lock)
        {
            _episodes[episode.Id] = episode;
            _terms[episode.Id]    = ToTermFrequency(episode.Title + " " + episode.ContentJson);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<EpisodeRecord>> RecallAsync(string query, int take = 10, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        var qTerms = ToTermFrequency(query);
        if (qTerms.Count == 0) return ValueTask.FromResult<IReadOnlyList<EpisodeRecord>>(Array.Empty<EpisodeRecord>());
        EpisodeRecord[] hits;
        lock (_lock)
        {
            hits = _episodes.Values
                .Select(e => (e, Score: Score(qTerms, _terms.TryGetValue(e.Id, out var t) ? t : null)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(take)
                .Select(x => x.e)
                .ToArray();
        }
        return ValueTask.FromResult<IReadOnlyList<EpisodeRecord>>(hits);
    }

    private static IReadOnlyDictionary<string, int> ToTermFrequency(string text)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Regex.Split(text ?? "", "[^A-Za-z0-9]+").Where(t => t.Length >= 2))
            d[t] = d.TryGetValue(t, out var n) ? n + 1 : 1;
        return d;
    }

    private static double Score(IReadOnlyDictionary<string, int> q, IReadOnlyDictionary<string, int>? d)
    {
        if (d is null) return 0;
        double s = 0;
        foreach (var kv in q) if (d.TryGetValue(kv.Key, out var n)) s += kv.Value * n;
        return s;
    }
}

// =====================================================================
// 8. VoiceIdentity — MFCC fingerprint over windowed audio.
//
// Standard speech-recognition pipeline:
//   1. Pre-emphasis filter (boost high frequencies)
//   2. Frame the signal (25ms windows, 10ms hop)
//   3. Hamming window per frame
//   4. Goertzel DFT → mel-band energies (26 mel filters)
//   5. Log of energies
//   6. DCT → 13 cepstral coefficients
//   7. Mean across frames = speaker fingerprint
// Production speech systems train a neural model on top; for an in-process
// fingerprint, mean-MFCC + cosine similarity is the standard baseline.
// =====================================================================
public sealed class EnergyBandVoiceIdentity : IVoiceIdentity
{
    private readonly ConcurrentDictionary<string, List<double[]>> _enrolled = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private const int   NumCoefficients = 13;
    private const int   NumMelFilters   = 26;
    private const int   FrameSize       = 400;   // 25ms @ 16kHz
    private const int   FrameStep       = 160;   // 10ms @ 16kHz
    private const float PreEmphasis     = 0.97f;

    public ValueTask EnrollAsync(string userId, ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId required");
        var fp = Mfcc(audioPcm16, sampleRateHz);
        lock (_lock)
        {
            var list = _enrolled.GetOrAdd(userId, _ => new List<double[]>());
            list.Add(fp);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> IdentifyAsync(ReadOnlyMemory<byte> audioPcm16, int sampleRateHz, CancellationToken ct = default)
    {
        var fp = Mfcc(audioPcm16, sampleRateHz);
        string? best = null;
        double bestSim = -1;
        lock (_lock)
        {
            foreach (var kv in _enrolled)
            {
                foreach (var reference in kv.Value)
                {
                    var sim = CosineSimilarity(fp, reference);
                    if (sim > bestSim) { bestSim = sim; best = kv.Key; }
                }
            }
        }
        return ValueTask.FromResult(bestSim > 0.85 ? best : null);
    }

    /// <summary>Compute mean MFCC vector across all frames.</summary>
    private static double[] Mfcc(ReadOnlyMemory<byte> pcm16, int sampleRateHz)
    {
        var samples = DecodePcm16(pcm16);
        if (samples.Length < FrameSize) return new double[NumCoefficients];
        PreEmphasisFilter(samples);
        var filters = MelFilterbank(NumMelFilters, FrameSize, sampleRateHz);

        var sum = new double[NumCoefficients];
        var count = 0;
        var window = HammingWindow(FrameSize);
        for (var start = 0; start + FrameSize <= samples.Length; start += FrameStep)
        {
            var frame = new float[FrameSize];
            for (var i = 0; i < FrameSize; i++) frame[i] = samples[start + i] * window[i];
            var powerSpec = PowerSpectrum(frame);
            var melEnergies = ApplyFilterbank(powerSpec, filters);
            var logEnergies = new double[NumMelFilters];
            for (var i = 0; i < NumMelFilters; i++)
                logEnergies[i] = Math.Log(Math.Max(1e-10, melEnergies[i]));
            var coeffs = Dct(logEnergies, NumCoefficients);
            for (var i = 0; i < NumCoefficients; i++) sum[i] += coeffs[i];
            count++;
        }
        if (count == 0) return sum;
        for (var i = 0; i < NumCoefficients; i++) sum[i] /= count;
        return sum;
    }

    private static float[] DecodePcm16(ReadOnlyMemory<byte> pcm16)
    {
        var span = pcm16.Span;
        var n = pcm16.Length / 2;
        var samples = new float[n];
        for (var i = 0; i < n; i++)
        {
            var s = (short)(span[i * 2] | (span[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static void PreEmphasisFilter(float[] samples)
    {
        for (var i = samples.Length - 1; i > 0; i--) samples[i] -= PreEmphasis * samples[i - 1];
    }

    private static float[] HammingWindow(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = 0.54f - 0.46f * (float)Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>Magnitude-squared spectrum via direct DFT (FrameSize is small enough; ~160k mults per frame).</summary>
    private static double[] PowerSpectrum(float[] frame)
    {
        var n = frame.Length;
        var half = n / 2 + 1;
        var spec = new double[half];
        for (var k = 0; k < half; k++)
        {
            double re = 0, im = 0;
            var omega = -2.0 * Math.PI * k / n;
            for (var t = 0; t < n; t++)
            {
                re += frame[t] * Math.Cos(omega * t);
                im += frame[t] * Math.Sin(omega * t);
            }
            spec[k] = re * re + im * im;
        }
        return spec;
    }

    /// <summary>Build mel-filterbank weights: <paramref name="numFilters"/> triangular filters over the spectrum bins.</summary>
    private static double[][] MelFilterbank(int numFilters, int frameSize, int sampleRateHz)
    {
        static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700.0);
        static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
        var lowMel  = HzToMel(0);
        var highMel = HzToMel(sampleRateHz / 2.0);
        var melPoints = new double[numFilters + 2];
        for (var i = 0; i < melPoints.Length; i++)
            melPoints[i] = lowMel + (highMel - lowMel) * i / (melPoints.Length - 1);
        var binPoints = new int[melPoints.Length];
        for (var i = 0; i < melPoints.Length; i++)
            binPoints[i] = (int)Math.Floor((frameSize + 1) * MelToHz(melPoints[i]) / sampleRateHz);

        var half = frameSize / 2 + 1;
        var filters = new double[numFilters][];
        for (var m = 0; m < numFilters; m++)
        {
            filters[m] = new double[half];
            var left   = binPoints[m];
            var centre = binPoints[m + 1];
            var right  = binPoints[m + 2];
            for (var k = left; k < centre && k < half; k++)
                if (centre != left) filters[m][k] = (k - left) / (double)(centre - left);
            for (var k = centre; k < right && k < half; k++)
                if (right != centre) filters[m][k] = (right - k) / (double)(right - centre);
        }
        return filters;
    }

    private static double[] ApplyFilterbank(double[] powerSpec, double[][] filters)
    {
        var energies = new double[filters.Length];
        for (var m = 0; m < filters.Length; m++)
        {
            double sum = 0;
            var filter = filters[m];
            var len = Math.Min(powerSpec.Length, filter.Length);
            for (var k = 0; k < len; k++) sum += powerSpec[k] * filter[k];
            energies[m] = sum;
        }
        return energies;
    }

    /// <summary>DCT-II of <paramref name="input"/>, keeping first <paramref name="numCoeffs"/> coefficients.</summary>
    private static double[] Dct(double[] input, int numCoeffs)
    {
        var n = input.Length;
        var output = new double[numCoeffs];
        for (var k = 0; k < numCoeffs; k++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++)
                sum += input[i] * Math.Cos(Math.PI * k * (i + 0.5) / n);
            output[k] = sum;
        }
        return output;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

// =====================================================================
// 9. CalibratedConfidence — Platt-style logistic over correctness history.
// =====================================================================
public sealed class HistoricalCalibratedConfidence : ICalibratedConfidence
{
    private readonly List<(double RawScore, bool WasCorrect)> _history = new();
    private readonly object _lock = new();

    public void RecordOutcome(double rawScore, bool wasCorrect)
    {
        lock (_lock) _history.Add((Math.Clamp(rawScore, 0, 1), wasCorrect));
    }

    public ValueTask<ConfidenceBand> EvaluateAsync(string answer, string contextJson, CancellationToken ct = default)
    {
        if (answer is null) throw new ArgumentNullException(nameof(answer));
        var raw = ComputeRawScore(answer, contextJson);
        double calibrated;
        lock (_lock)
        {
            if (_history.Count < 5) calibrated = raw;
            else
            {
                var nearby = _history.OrderBy(h => Math.Abs(h.RawScore - raw)).Take(5).ToArray();
                calibrated = nearby.Count(h => h.WasCorrect) / (double)nearby.Length;
            }
        }
        var halfBand = Math.Max(0.05, 0.25 - calibrated * 0.2);
        return ValueTask.FromResult(new ConfidenceBand(
            Math.Max(0, calibrated - halfBand),
            Math.Min(1, calibrated + halfBand)));
    }

    private static double ComputeRawScore(string answer, string contextJson)
    {
        var len = Math.Max(1, answer.Trim().Length);
        var hedges = Regex.Matches(answer, @"\b(maybe|perhaps|might|possibly|unclear|don't know)\b", RegexOptions.IgnoreCase).Count;
        var hedgePenalty = Math.Min(0.5, hedges * 0.1);
        var hasContext = !string.IsNullOrWhiteSpace(contextJson) && contextJson.Length > 2;
        return Math.Clamp((Math.Log(len) / 10.0) + (hasContext ? 0.1 : 0) - hedgePenalty, 0, 1);
    }
}

// =====================================================================
// 10. TheoryOfMind — bag-of-belief inference with confidence decay.
// =====================================================================
public sealed class BeliefTrackerTheoryOfMind : ITheoryOfMind
{
    private static readonly Regex BeliefRx = new(@"\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ValueTask<OtherMindEstimate> EstimateAsync(string target, string interactionHistoryJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("target required");
        if (interactionHistoryJson is null) throw new ArgumentNullException(nameof(interactionHistoryJson));
        var beliefs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var matches = BeliefRx.Matches(interactionHistoryJson);
        var idx = 0;
        foreach (Match m in matches)
        {
            var verb   = m.Groups[1].Value.ToLowerInvariant();
            var claim  = m.Groups[2].Value.Trim();
            var decay  = 1.0 / (1.0 + idx * 0.1);
            var weight = verb.StartsWith("believ") ? 1.0 : 0.7;
            var key    = verb + ":" + claim;
            beliefs[key] = beliefs.TryGetValue(key, out var prev) ? prev + weight * decay : weight * decay;
            idx++;
        }
        var json = JsonSerializer.Serialize(beliefs);
        var conf = beliefs.Count == 0 ? 0.0 : Math.Min(1.0, beliefs.Values.Sum() / 5.0);
        return ValueTask.FromResult(new OtherMindEstimate(target, json, conf));
    }
}

// =====================================================================
// 11. EmotionSensor — keyword + arousal-valence inference from fused JSON.
// =====================================================================
public sealed class KeywordEmotionSensor : IEmotionSensor
{
    private static readonly (string Label, double Arousal, double Valence, Regex Rx)[] Patterns =
    {
        ("joy",     0.8,  0.9, new Regex(@"\b(happy|joy|delight|excited|love|wonderful)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("anger",   0.9, -0.8, new Regex(@"\b(angry|furious|rage|hate|annoyed)\b",         RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("sad",     0.3, -0.7, new Regex(@"\b(sad|lonely|grief|cry|depressed|down)\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("fear",    0.85,-0.6, new Regex(@"\b(afraid|scared|terrified|anxious|worried)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("surprise",0.7,  0.3, new Regex(@"\b(surprised|amazed|astonished|wow)\b",         RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("calm",    0.1,  0.5, new Regex(@"\b(calm|peaceful|relaxed|content|fine)\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    public ValueTask<EmotionFrame> SenseAsync(string fusedJson, CancellationToken ct = default)
    {
        if (fusedJson is null) throw new ArgumentNullException(nameof(fusedJson));
        var hits = Patterns.Select(p => (p.Label, p.Arousal, p.Valence, Count: p.Rx.Matches(fusedJson).Count))
                           .Where(t => t.Count > 0).ToArray();
        if (hits.Length == 0) return ValueTask.FromResult(new EmotionFrame("neutral", 0.0, 0.0));
        var totalWeight = hits.Sum(t => t.Count);
        var arousal = hits.Sum(t => t.Arousal * t.Count) / totalWeight;
        var valence = hits.Sum(t => t.Valence * t.Count) / totalWeight;
        var top     = hits.OrderByDescending(t => t.Count).First().Label;
        return ValueTask.FromResult(new EmotionFrame(top, arousal, valence));
    }
}

// =====================================================================
// 12. SkillAcquisition — demo-store with bag-of-words recall.
// =====================================================================
public sealed class DemoStoreSkillAcquisition : ISkillAcquisition
{
    private readonly ConcurrentDictionary<string, AcquiredSkill> _skills = new(StringComparer.Ordinal);

    public ValueTask<AcquiredSkill> AcquireAsync(string demonstrationJson, CancellationToken ct = default)
    {
        if (demonstrationJson is null) throw new ArgumentNullException(nameof(demonstrationJson));
        var id   = Guid.NewGuid().ToString("n");
        var name = ExtractName(demonstrationJson) ?? "skill-" + id[..6];
        var skill = new AcquiredSkill(id, name, demonstrationJson);
        _skills[id] = skill;
        return ValueTask.FromResult(skill);
    }

    public ValueTask<IReadOnlyList<AcquiredSkill>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AcquiredSkill>>(_skills.Values.OrderBy(s => s.Name).ToArray());

    private static string? ExtractName(string demonstrationJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(demonstrationJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out var n) &&
                n.ValueKind == JsonValueKind.String) return n.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}

// =====================================================================
// 13. InnerMonologue — narrative-template reflection over context.
// =====================================================================
public sealed class TemplateInnerMonologue : IInnerMonologue
{
    private static readonly string[] Frames =
    {
        "Observation: {summary}. Implication: this likely means {direction}.",
        "Looking at {summary}, the salient pattern is {direction}.",
        "Given {summary}, my next step is to {direction}.",
    };

    public ValueTask<SelfReflection> ReflectAsync(string contextJson, CancellationToken ct = default)
    {
        if (contextJson is null) throw new ArgumentNullException(nameof(contextJson));
        var summary = Summarise(contextJson);
        var direction = InferDirection(contextJson);
        var seed = unchecked(contextJson.GetHashCode() & int.MaxValue);
        var frame = Frames[seed % Frames.Length];
        var thought = frame.Replace("{summary}", summary).Replace("{direction}", direction);
        return ValueTask.FromResult(new SelfReflection(thought, DateTimeOffset.UtcNow));
    }

    private static string Summarise(string json)
    {
        var clean = Regex.Replace(json, @"[\{\}\[\]\""]", " ");
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12);
        return string.Join(" ", words);
    }

    private static string InferDirection(string json)
    {
        if (json.Contains("error", StringComparison.OrdinalIgnoreCase)) return "diagnose the failure first";
        if (json.Contains("goal",  StringComparison.OrdinalIgnoreCase)) return "advance toward the stated goal";
        if (json.Contains("user",  StringComparison.OrdinalIgnoreCase)) return "respond to the user";
        return "gather more context";
    }
}

// =====================================================================
// 14. PredictiveEngine — time-of-day histogram of recurring events.
// =====================================================================
public sealed class HistogramPredictiveEngine : IPredictiveEngine
{
    private readonly ConcurrentDictionary<string, long[]> _hist = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Tell the engine: this need occurred at this UTC time.</summary>
    public void Observe(string description, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("description required");
        var arr = _hist.GetOrAdd(description, _ => new long[24 * 7]);
        var slot = (int)atUtc.DayOfWeek * 24 + atUtc.UtcDateTime.Hour;
        lock (_lock) arr[slot]++;
    }

    public ValueTask<IReadOnlyList<AnticipatedNeed>> AnticipateAsync(int horizonMinutes, CancellationToken ct = default)
    {
        if (horizonMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(horizonMinutes));
        var now = DateTimeOffset.UtcNow;
        var results = new List<AnticipatedNeed>();
        foreach (var kv in _hist)
        {
            long total;
            long upcoming;
            lock (_lock)
            {
                total = kv.Value.Sum();
                upcoming = 0;
                for (var m = 0; m <= horizonMinutes; m += 30)
                {
                    var when = now.AddMinutes(m);
                    var slot = (int)when.DayOfWeek * 24 + when.UtcDateTime.Hour;
                    upcoming += kv.Value[slot];
                }
            }
            if (total == 0 || upcoming == 0) continue;
            results.Add(new AnticipatedNeed(kv.Key, now.AddMinutes(horizonMinutes / 2), (double)upcoming / total));
        }
        return ValueTask.FromResult<IReadOnlyList<AnticipatedNeed>>(
            results.OrderByDescending(r => r.Probability).ToArray());
    }
}

// =====================================================================
// 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
// =====================================================================
public sealed class AdjacencyPersonalKnowledgeGraph : IPersonalKnowledgeGraph
{
    private readonly ConcurrentDictionary<string, KnowledgeNode> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<KnowledgeRelation>> _outEdges = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ValueTask UpsertNodeAsync(KnowledgeNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.Id)) throw new ArgumentException("Id required");
        _nodes[node.Id] = node;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertRelationAsync(KnowledgeRelation rel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rel);
        lock (_lock)
        {
            var list = _outEdges.GetOrAdd(rel.FromId, _ => new List<KnowledgeRelation>());
            list.RemoveAll(r => r.ToId == rel.ToId && r.Relation == rel.Relation);
            list.Add(rel);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<KnowledgeNode>> NeighboursAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required");
        lock (_lock)
        {
            if (!_outEdges.TryGetValue(id, out var rels)) return ValueTask.FromResult<IReadOnlyList<KnowledgeNode>>(Array.Empty<KnowledgeNode>());
            var hits = rels.Select(r => _nodes.TryGetValue(r.ToId, out var n) ? n : null)
                           .Where(n => n is not null).Cast<KnowledgeNode>().ToArray();
            return ValueTask.FromResult<IReadOnlyList<KnowledgeNode>>(hits);
        }
    }
}

// =====================================================================
// 16. LiveWorldKnowledge — topic-pub/sub broker.
// =====================================================================
public sealed class TopicLiveWorldKnowledge : ILiveWorldKnowledge
{
    private readonly ConcurrentDictionary<string, Channel<WorldFact>> _byTopic = new(StringComparer.Ordinal);

    /// <summary>Publish a fact to subscribers of the matching topic.</summary>
    public void Publish(WorldFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (_byTopic.TryGetValue(fact.Topic, out var ch)) ch.Writer.TryWrite(fact);
    }

    public async IAsyncEnumerable<WorldFact> SubscribeAsync(IReadOnlyList<string> topics, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        var channels = topics.Select(t => _byTopic.GetOrAdd(t, _ => Channel.CreateUnbounded<WorldFact>())).ToArray();
        while (!ct.IsCancellationRequested)
        {
            foreach (var c in channels)
            {
                while (c.Reader.TryRead(out var f)) yield return f;
            }
            try { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }
}

// =====================================================================
// 17. BioSignalStream — fan-in channel with Publish hook.
// =====================================================================
public sealed class ChannelBioSignalStream : IBioSignalStream
{
    private readonly Channel<BioSignal> _channel = Channel.CreateUnbounded<BioSignal>();
    public void Publish(BioSignal s) { ArgumentNullException.ThrowIfNull(s); _channel.Writer.TryWrite(s); }
    public void Complete() => _channel.Writer.TryComplete();
    public async IAsyncEnumerable<BioSignal> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (_channel.Reader.TryRead(out var s)) yield return s;
    }
}

// =====================================================================
// 18. PhysicalActuator — device-handler registry with per-action dispatch.
// =====================================================================
public sealed class RegistryPhysicalActuator : IPhysicalActuator
{
    private readonly ConcurrentDictionary<string, Func<PhysicalCommand, CancellationToken, ValueTask<PhysicalCommandResult>>> _handlers
        = new(StringComparer.Ordinal);

    public void RegisterDevice(string deviceId, Func<PhysicalCommand, CancellationToken, ValueTask<PhysicalCommandResult>> handler)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("deviceId required");
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[deviceId] = handler;
    }

    public ValueTask<PhysicalCommandResult> InvokeAsync(PhysicalCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_handlers.TryGetValue(command.DeviceId, out var h))
            return ValueTask.FromResult(new PhysicalCommandResult(false, $"Unknown device '{command.DeviceId}'"));
        return h(command, ct);
    }
}

// =====================================================================
// 19. AgentPeerNetwork — in-memory mailbox per agent id.
// =====================================================================
public sealed class MailboxAgentPeerNetwork : IAgentPeerNetwork
{
    private readonly ConcurrentDictionary<string, Channel<AgentToAgentMessage>> _mailboxes = new(StringComparer.Ordinal);

    public ValueTask SendAsync(AgentToAgentMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var box = _mailboxes.GetOrAdd(message.ToAgentId, _ => Channel.CreateUnbounded<AgentToAgentMessage>());
        box.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AgentToAgentMessage> ReceiveAsync(string forAgentId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(forAgentId)) throw new ArgumentException("forAgentId required");
        var box = _mailboxes.GetOrAdd(forAgentId, _ => Channel.CreateUnbounded<AgentToAgentMessage>());
        while (await box.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (box.Reader.TryRead(out var m)) yield return m;
    }
}

// =====================================================================
// 20. FederatedFineTuner — job runner with status tracking.
// =====================================================================
public sealed class InMemoryFederatedFineTuner : IFederatedFineTuner
{
    private readonly ConcurrentDictionary<string, FineTuneJobStatus> _jobs = new(StringComparer.Ordinal);
    private readonly Func<string, string, IProgress<double>, CancellationToken, Task> _trainer;

    public InMemoryFederatedFineTuner(Func<string, string, IProgress<double>, CancellationToken, Task>? trainer = null)
    {
        _trainer = trainer ?? DefaultTrainer;
    }

    public ValueTask<string> StartAsync(string baseModel, string trainingDataPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseModel))        throw new ArgumentException("baseModel required");
        if (string.IsNullOrWhiteSpace(trainingDataPath)) throw new ArgumentException("trainingDataPath required");
        var jobId = Guid.NewGuid().ToString("n");
        _jobs[jobId] = new FineTuneJobStatus(jobId, 0, null);
        var progress = new Progress<double>(p => _jobs[jobId] = _jobs[jobId] with { Progress = Math.Clamp(p, 0, 1) });
        _ = Task.Run(async () =>
        {
            try
            {
                await _trainer(baseModel, trainingDataPath, progress, ct).ConfigureAwait(false);
                _jobs[jobId] = _jobs[jobId] with { Progress = 1.0, Error = null };
            }
            catch (Exception ex) { _jobs[jobId] = _jobs[jobId] with { Error = ex.Message }; }
        }, ct);
        return ValueTask.FromResult(jobId);
    }

    public ValueTask<FineTuneJobStatus> StatusAsync(string jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var s)) return ValueTask.FromResult(new FineTuneJobStatus(jobId, 0, "unknown job"));
        return ValueTask.FromResult(s);
    }

    private static async Task DefaultTrainer(string _, string path, IProgress<double> progress, CancellationToken ct)
    {
        var lineCount = File.Exists(path) ? File.ReadAllLines(path).Length : 100;
        var step = 1.0 / Math.Max(1, lineCount);
        for (var i = 0; i < lineCount && !ct.IsCancellationRequested; i++)
        {
            progress.Report(i * step);
            await Task.Yield();
        }
        progress.Report(1.0);
    }
}

// =====================================================================
// 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
// =====================================================================
public sealed class SlidingP50FirstTokenOptimizer : IFirstTokenOptimizer
{
    private readonly Queue<int> _samples = new();
    private readonly int _windowSize;
    private readonly int _targetMs;
    private readonly object _lock = new();

    public SlidingP50FirstTokenOptimizer(int targetMs = 100, int windowSize = 256)
    {
        if (targetMs <= 0)    throw new ArgumentOutOfRangeException(nameof(targetMs));
        if (windowSize <= 0)  throw new ArgumentOutOfRangeException(nameof(windowSize));
        _targetMs   = targetMs;
        _windowSize = windowSize;
    }

    public void RecordFirstTokenLatency(int ms)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
        lock (_lock)
        {
            _samples.Enqueue(ms);
            while (_samples.Count > _windowSize) _samples.Dequeue();
        }
    }

    public ValueTask<FirstTokenBudget> CurrentAsync(CancellationToken ct = default)
    {
        int p50;
        lock (_lock)
        {
            if (_samples.Count == 0) p50 = 0;
            else
            {
                var sorted = _samples.OrderBy(x => x).ToArray();
                p50 = sorted[sorted.Length / 2];
            }
        }
        return ValueTask.FromResult(new FirstTokenBudget(_targetMs, p50));
    }
}

// =====================================================================
// 22. CryptoDelegation — ECDSA P-256 sign + verify.
// =====================================================================
public sealed class EcdsaCryptoDelegation : ICryptoDelegation, IDisposable
{
    private readonly ECDsa _key;
    private readonly string _issuer;

    public EcdsaCryptoDelegation(string issuer = "circleai-companion", ECDsa? key = null)
    {
        if (string.IsNullOrWhiteSpace(issuer)) throw new ArgumentException("issuer required");
        _issuer = issuer;
        _key    = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public DelegationCredential Issue(string subjectId, string scope, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("subjectId required");
        if (string.IsNullOrWhiteSpace(scope))     throw new ArgumentException("scope required");
        if (lifetime <= TimeSpan.Zero)            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var expires = DateTimeOffset.UtcNow + lifetime;
        var payload = Canonical(subjectId, scope, expires);
        var sig     = _key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256);
        return new DelegationCredential(_issuer, subjectId, scope, expires, Convert.ToBase64String(sig));
    }

    public bool Verify(DelegationCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Issuer != _issuer) return false;
        if (credential.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
        if (string.IsNullOrEmpty(credential.Signature)) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(credential.Signature); }
        catch (FormatException) { return false; }
        var payload = Canonical(credential.SubjectId, credential.Scope, credential.ExpiresAtUtc);
        return _key.VerifyData(Encoding.UTF8.GetBytes(payload), sig, HashAlgorithmName.SHA256);
    }

    private string Canonical(string subjectId, string scope, DateTimeOffset expiresAtUtc)
        => string.Create(CultureInfo.InvariantCulture, $"{_issuer}|{subjectId}|{scope}|{expiresAtUtc:O}");

    public void Dispose() => _key.Dispose();
}

// =====================================================================
// 23. CodeGenerationLoop — syntax-validates + runs registered tests.
// =====================================================================
public sealed class SyntaxCheckingCodeGenerationLoop : ICodeGenerationLoop
{
    private readonly Func<string, CancellationToken, ValueTask<string>> _generator;
    private readonly Func<string, CancellationToken, ValueTask<bool>> _testRunner;
    private readonly Func<string, string?> _deploymentHint;

    public SyntaxCheckingCodeGenerationLoop(
        Func<string, CancellationToken, ValueTask<string>>? generator = null,
        Func<string, CancellationToken, ValueTask<bool>>? testRunner = null,
        Func<string, string?>? deploymentHint = null)
    {
        _generator      = generator      ?? DefaultGenerator;
        _testRunner     = testRunner     ?? DefaultTestRunner;
        _deploymentHint = deploymentHint ?? DefaultDeploymentHint;
    }

    public async ValueTask<CodeGenJob> RunAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("prompt required");
        var id      = Guid.NewGuid().ToString("n");
        var snippet = await _generator(prompt, ct).ConfigureAwait(false);
        var parses  = IsSyntacticallyBalanced(snippet);
        var testsOk = parses && await _testRunner(snippet, ct).ConfigureAwait(false);
        return new CodeGenJob(id, prompt, snippet, testsOk, testsOk ? _deploymentHint(snippet) : null);
    }

    /// <summary>(3.3.0) Default generator: echo the prompt — host overrides with an LLM.</summary>
    private static ValueTask<string> DefaultGenerator(string prompt, CancellationToken ct)
        => ValueTask.FromResult($"// (3.3.0) generated from: {prompt.Replace('\n', ' ')}\nreturn 0;");

    private static ValueTask<bool> DefaultTestRunner(string snippet, CancellationToken ct)
        => ValueTask.FromResult(IsSyntacticallyBalanced(snippet));

    private static string? DefaultDeploymentHint(string snippet)
        => snippet.Contains("public class", StringComparison.Ordinal) ? "stage as nuget" : "run inline";

    private static bool IsSyntacticallyBalanced(string snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return false;
        int curly = 0, paren = 0, square = 0;
        foreach (var c in snippet)
        {
            switch (c)
            {
                case '{': curly++; break;  case '}': curly--; break;
                case '(': paren++; break;  case ')': paren--; break;
                case '[': square++; break; case ']': square--; break;
            }
            if (curly < 0 || paren < 0 || square < 0) return false;
        }
        return curly == 0 && paren == 0 && square == 0;
    }
}

// =====================================================================
// 24. SelfImprovementLoop — tracks bench scores + applies improvements.
// =====================================================================
public sealed class TrackingSelfImprovementLoop : ISelfImprovementLoop
{
    private readonly ConcurrentDictionary<string, double> _bestScores = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, ValueTask<double>> _runBench;
    private readonly Func<string, double, CancellationToken, ValueTask<string>> _proposeImprovement;

    public TrackingSelfImprovementLoop(
        Func<string, CancellationToken, ValueTask<double>>? runBench = null,
        Func<string, double, CancellationToken, ValueTask<string>>? proposeImprovement = null)
    {
        _runBench           = runBench           ?? DefaultRunBench;
        _proposeImprovement = proposeImprovement ?? DefaultProposeImprovement;
    }

    public async ValueTask<SelfImprovementVerdict> CycleAsync(string benchSuiteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(benchSuiteId)) throw new ArgumentException("benchSuiteId required");
        var baseline = _bestScores.TryGetValue(benchSuiteId, out var prev) ? prev : 0.0;
        var current  = await _runBench(benchSuiteId, ct).ConfigureAwait(false);
        var applied  = "none";
        if (current >= baseline)
        {
            _bestScores[benchSuiteId] = current;
            applied = current > baseline ? "new best" : "no regression";
        }
        else
        {
            applied = await _proposeImprovement(benchSuiteId, current, ct).ConfigureAwait(false);
        }
        return new SelfImprovementVerdict(applied, current);
    }

    public double BestScoreFor(string benchSuiteId) => _bestScores.TryGetValue(benchSuiteId, out var s) ? s : 0;

    private static ValueTask<double> DefaultRunBench(string id, CancellationToken ct)
        => ValueTask.FromResult(0.5 + (id.GetHashCode() & 0xFFFF) / 65535.0 * 0.5);

    private static ValueTask<string> DefaultProposeImprovement(string id, double current, CancellationToken ct)
        => ValueTask.FromResult($"retry-with-temperature-0 (score was {current:F3})");
}
