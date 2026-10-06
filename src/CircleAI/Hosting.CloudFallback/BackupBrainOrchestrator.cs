// BackupBrainOrchestrator.cs
//
// (3.3.0) Runtime LLM failover: track which brain is primary, mark it
// degraded after N consecutive failures, switch to the next backup,
// retry the primary after a cool-down. Different from
// CloudFallbackChain (start-of-call ordering) — this is mid-call
// between-turn failover.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Hosting.CloudFallback;

/// <summary>(3.3.0) Health state of one brain in the chain.</summary>
public enum BrainHealth { Healthy, Degraded, CoolingDown }

/// <summary>(3.3.0) Snapshot of brain health for monitoring.</summary>
public sealed record BrainStatus(string Label, BrainHealth Health, int ConsecutiveFailures);

/// <summary>(3.3.0) Policy knobs.</summary>
/// <param name="DegradedAfterFailures">How many consecutive failures push a brain to degraded.</param>
/// <param name="CoolDownDuration">How long a degraded brain stays out before retry.</param>
/// <param name="MaxRetriesPerTurn">How many brains to try before giving up on one turn.</param>
public sealed record BackupBrainPolicy(
    int       DegradedAfterFailures = 2,
    TimeSpan? CoolDownDuration      = null,
    int       MaxRetriesPerTurn     = 3)
{
    public TimeSpan CoolDownDurationOrDefault => CoolDownDuration ?? TimeSpan.FromSeconds(30);
}

/// <summary>(3.3.0) Wraps an ordered set of brains; switches on failure, retries primary on cool-down.</summary>
public sealed class BackupBrainOrchestrator : IChatGenerator
{
    private readonly List<BrainEntry> _brains;
    private readonly BackupBrainPolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;

    public BackupBrainOrchestrator(
        IEnumerable<IChatGenerator>          brains,
        BackupBrainPolicy?                   policy = null,
        Func<DateTimeOffset>?                clock  = null,
        ILogger<BackupBrainOrchestrator>?    logger = null)
    {
        ArgumentNullException.ThrowIfNull(brains);
        _brains = new List<BrainEntry>();
        foreach (var b in brains)
        {
            _brains.Add(new BrainEntry(b));
        }
        if (_brains.Count == 0) throw new ArgumentException("At least one brain is required.", nameof(brains));
        _policy = policy ?? new BackupBrainPolicy();
        _clock  = clock  ?? (() => DateTimeOffset.UtcNow);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<BrainStatus> Statuses
    {
        get
        {
            var now = _clock();
            var list = new List<BrainStatus>();
            foreach (var e in _brains)
            {
                lock (e.Gate)
                {
                    var h = e.HealthAt(now, _policy.CoolDownDurationOrDefault);
                    var label = (e.Brain as IConfigurableChatGenerator)?.EngineLabel ?? e.Brain.GetType().Name;
                    list.Add(new BrainStatus(label, h, e.Consecutive));
                }
            }
            return list;
        }
    }

    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        CancellationToken          ct      = default)
    {
        var maxRetries = Math.Min(_policy.MaxRetriesPerTurn, _brains.Count);
        var tried = new HashSet<BrainEntry>();
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var pick = PickAvailable(tried);
            if (pick is null) break;
            tried.Add(pick);
            try
            {
                var result = await pick.Brain.GenerateAsync(messages, options, ct).ConfigureAwait(false);
                pick.RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brain {Brain} failed; trying next backup", pick.Brain.GetType().Name);
                pick.RecordFailure(_policy.DegradedAfterFailures, _clock());
            }
        }
        return "[All brains failed.]";
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var maxRetries = Math.Min(_policy.MaxRetriesPerTurn, _brains.Count);
        var tried = new HashSet<BrainEntry>();
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var pick = PickAvailable(tried);
            if (pick is null) break;
            tried.Add(pick);
            bool streamedAny = false;
            bool failed      = false;
            await foreach (var chunk in IterateStreamSafe(pick, messages, options, ct).ConfigureAwait(false))
            {
                if (chunk is null)
                {
                    failed = true;
                    break;
                }
                streamedAny = true;
                yield return chunk;
            }
            if (failed)
            {
                pick.RecordFailure(_policy.DegradedAfterFailures, _clock());
                if (!streamedAny) continue; // try the backup
            }
            if (streamedAny)
            {
                pick.RecordSuccess();
                yield break;
            }
        }
        yield return "[All brains failed.]";
    }

    private static async IAsyncEnumerable<string?> IterateStreamSafe(
        BrainEntry pick,
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<string>? enumerator = null;
        bool initFailed = false;
        try
        {
            enumerator = pick.Brain.StreamAsync(messages, options, ct).GetAsyncEnumerator(ct);
        }
        catch
        {
            initFailed = true;
        }
        if (initFailed)
        {
            yield return null;
            yield break;
        }

        try
        {
            while (true)
            {
                string? current = null;
                bool failed     = false;
                bool ended      = false;
                try
                {
                    if (!await enumerator!.MoveNextAsync().ConfigureAwait(false))
                    {
                        ended = true;
                    }
                    else
                    {
                        current = enumerator.Current;
                    }
                }
                catch
                {
                    failed = true;
                }
                if (ended)  yield break;
                if (failed) { yield return null; yield break; }
                yield return current;
            }
        }
        finally
        {
            if (enumerator is not null) await enumerator.DisposeAsync();
        }
    }

    private BrainEntry? PickAvailable(HashSet<BrainEntry>? skip = null)
    {
        var now = _clock();
        foreach (var e in _brains)
        {
            if (skip is not null && skip.Contains(e)) continue;
            lock (e.Gate)
            {
                var h = e.HealthAt(now, _policy.CoolDownDurationOrDefault);
                if (h is BrainHealth.Healthy or BrainHealth.CoolingDown)
                {
                    return e;
                }
            }
        }
        // None healthy — pick first untried brain anyway (degraded might recover).
        foreach (var e in _brains)
        {
            if (skip is null || !skip.Contains(e)) return e;
        }
        return null;
    }

    public void Dispose() { GC.SuppressFinalize(this); }

    private sealed class BrainEntry
    {
        public readonly IChatGenerator Brain;
        public readonly object Gate = new();
        public int Consecutive;
        public DateTimeOffset DegradedSince;
        public bool IsDegraded;

        public BrainEntry(IChatGenerator brain) { Brain = brain; }

        public BrainHealth HealthAt(DateTimeOffset now, TimeSpan coolDown)
        {
            if (!IsDegraded) return BrainHealth.Healthy;
            if (now - DegradedSince >= coolDown) return BrainHealth.CoolingDown; // half-open: ready for retry
            return BrainHealth.Degraded;
        }

        public void RecordSuccess()
        {
            lock (Gate)
            {
                Consecutive = 0;
                IsDegraded = false;
            }
        }

        public void RecordFailure(int threshold, DateTimeOffset now)
        {
            lock (Gate)
            {
                Consecutive++;
                if (Consecutive >= threshold)
                {
                    IsDegraded   = true;
                    DegradedSince = now;
                }
            }
        }
    }
}
