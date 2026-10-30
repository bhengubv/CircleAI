// TokenSampler.cs
//
// Picks the next token from one step's logits. In order: the penalties over the last tokens
// this answer has produced, then temperature, top-k, top-p and min-p, then one draw from a
// seeded generator. That is the order Hugging Face and vLLM apply them in, and the Qwen3.5
// card's numbers are tuned for them; the penalties count generated tokens only, as theirs do,
// so a name in the question is not discouraged from the answer. A temperature of zero is
// greedy: the penalties, then the highest logit.

namespace CircleAI.Inference.Managed.Sampling;

/// <summary>How the next token is chosen.</summary>
/// <param name="Temperature">Divides the logits; 0 is greedy.</param>
/// <param name="TopK">Keeps the K most likely tokens; 0 keeps all.</param>
/// <param name="TopP">Keeps the most likely tokens until their probability reaches P.</param>
/// <param name="MinP">Drops tokens less likely than MinP times the likeliest.</param>
/// <param name="PresencePenalty">Subtracted once from every token already in the window.</param>
/// <param name="FrequencyPenalty">Subtracted per occurrence in the window.</param>
/// <param name="RepeatPenalty">Divides a positive logit (multiplies a negative one) of a token in the window; 1 is off.</param>
/// <param name="PenaltyWindow">How many of the latest generated tokens the penalties look at.</param>
public sealed record SamplingSettings(
    float Temperature,
    int TopK,
    float TopP,
    float MinP,
    float PresencePenalty,
    float FrequencyPenalty,
    float RepeatPenalty,
    int PenaltyWindow)
{
    /// <summary>The Qwen3.5 card with thinking on: temperature 1.0, top-p 0.95, top-k 20, min-p 0, presence 1.0, over the last 64.</summary>
    public static SamplingSettings Thinking { get; } = new(1.0f, 20, 0.95f, 0f, 1.0f, 0f, 1.0f, 64);

    /// <summary>The Qwen3.5 card with thinking off: temperature 0.7, top-p 0.8, top-k 20, min-p 0, presence 1.5, over the last 64.</summary>
    public static SamplingSettings Direct { get; } = new(0.7f, 20, 0.8f, 0f, 1.5f, 0f, 1.0f, 64);

    /// <summary>The highest logit, nothing else.</summary>
    public static SamplingSettings Greedy { get; } = new(0f, 0, 1f, 0f, 0f, 0f, 1f, 0);
}

/// <summary>Chooses tokens from logits by <see cref="SamplingSettings"/>, repeatably for a given seed.</summary>
public sealed class TokenSampler
{
    private readonly Random _random;
    private readonly Dictionary<int, int> _counts = [];
    private (int Token, float Logit)[] _candidates = new (int, float)[64];
    private float[] _probabilities = new float[64];

    /// <summary>A sampler; with a <paramref name="seed"/> the same logits give the same tokens every run.</summary>
    public TokenSampler(SamplingSettings settings, int? seed = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _random = seed is { } s ? new Random(s) : new Random();
    }

    /// <summary>The settings this sampler applies.</summary>
    public SamplingSettings Settings { get; }

    /// <summary>
    /// The next token. <paramref name="logits"/> is changed in place by the penalties;
    /// <paramref name="generated"/> holds the tokens this answer has produced so far.
    /// </summary>
    public int Sample(Span<float> logits, IReadOnlyList<int> generated)
    {
        ArgumentNullException.ThrowIfNull(generated);
        if (logits.Length == 0) throw new ArgumentException("There are no logits to choose from.", nameof(logits));
        ApplyPenalties(logits, generated);

        var s = Settings;
        if (s.Temperature <= 0) return ArgMax(logits);

        // Top-k first: the temperature divides every logit alike, so it cannot change which K remain.
        var count = TopK(logits, s.TopK > 0 ? Math.Min(s.TopK, logits.Length) : logits.Length);

        // Temperature, then the probabilities of what is left (candidates are sorted, likeliest first).
        if (_probabilities.Length < count) _probabilities = new float[Math.Max(count, _probabilities.Length * 2)];
        var max = _candidates[0].Logit / s.Temperature;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var p = MathF.Exp(_candidates[i].Logit / s.Temperature - max);
            _probabilities[i] = p;
            sum += p;
        }
        for (var i = 0; i < count; i++) _probabilities[i] = (float)(_probabilities[i] / sum);

        // Top-p: the shortest run of likeliest tokens whose probability reaches P.
        if (s.TopP < 1f)
        {
            double cumulative = 0;
            for (var i = 0; i < count; i++)
            {
                cumulative += _probabilities[i];
                if (cumulative >= s.TopP)
                {
                    count = i + 1;
                    break;
                }
            }
        }

        // Min-p: nothing less likely than MinP times the likeliest.
        if (s.MinP > 0f)
        {
            var floor = s.MinP * _probabilities[0];
            var kept = 1;
            while (kept < count && _probabilities[kept] >= floor) kept++;
            count = kept;
        }

        double total = 0;
        for (var i = 0; i < count; i++) total += _probabilities[i];
        var draw = _random.NextDouble() * total;
        for (var i = 0; i < count; i++)
        {
            draw -= _probabilities[i];
            if (draw < 0) return _candidates[i].Token;
        }
        return _candidates[count - 1].Token;
    }

    // Presence, frequency and repeat penalties over the latest generated tokens.
    private void ApplyPenalties(Span<float> logits, IReadOnlyList<int> generated)
    {
        var s = Settings;
        if (s.PenaltyWindow <= 0 || generated.Count == 0) return;
        if (s.PresencePenalty == 0 && s.FrequencyPenalty == 0 && s.RepeatPenalty == 1) return;

        _counts.Clear();
        for (var i = Math.Max(0, generated.Count - s.PenaltyWindow); i < generated.Count; i++)
        {
            var token = generated[i];
            if ((uint)token >= (uint)logits.Length) continue;
            _counts[token] = _counts.GetValueOrDefault(token) + 1;
        }
        foreach (var (token, occurrences) in _counts)
        {
            ref var logit = ref logits[token];
            if (s.RepeatPenalty != 1) logit = logit <= 0 ? logit * s.RepeatPenalty : logit / s.RepeatPenalty;
            logit -= occurrences * s.FrequencyPenalty + s.PresencePenalty;
        }
    }

    // The k highest logits into _candidates, likeliest first; returns k.
    private int TopK(ReadOnlySpan<float> logits, int k)
    {
        if (_candidates.Length < k) _candidates = new (int, float)[Math.Max(k, _candidates.Length * 2)];
        if (k == logits.Length)
        {
            for (var i = 0; i < k; i++) _candidates[i] = (i, logits[i]);
            Array.Sort(_candidates, 0, k, Comparer<(int Token, float Logit)>.Create((a, b) => b.Logit.CompareTo(a.Logit)));
            return k;
        }

        // A sorted window of the best k seen so far; a logit enters only if it beats the worst.
        var filled = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            var logit = logits[i];
            if (filled == k && logit <= _candidates[k - 1].Logit) continue;
            var at = filled < k ? filled++ : k - 1;
            while (at > 0 && _candidates[at - 1].Logit < logit)
            {
                _candidates[at] = _candidates[at - 1];
                at--;
            }
            _candidates[at] = (i, logit);
        }
        return filled;
    }

    private static int ArgMax(ReadOnlySpan<float> logits)
    {
        var best = 0;
        for (var i = 1; i < logits.Length; i++)
        {
            if (logits[i] > logits[best]) best = i;
        }
        return best;
    }
}
