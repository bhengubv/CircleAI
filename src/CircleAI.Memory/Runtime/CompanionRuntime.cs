// CompanionRuntime.cs
//
// Item 6 of the audit follow-up — the host orchestrator that ticks the
// consolidator on a schedule, keeps the sync engine running, and exposes
// a single ingestion entry point for multimodal artefacts. Implements
// IHostedService so the whole pipeline plugs into Generic Host / ASP.NET
// Core with no extra boilerplate.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory.Consolidation;
using CircleAI.Memory.Multimodal;
using CircleAI.Memory.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Memory.Runtime;

/// <summary>
/// Owns the lifecycle of the memory pipeline (consolidator, sync engine,
/// multimodal ingester) and ticks the consolidation passes on a configurable
/// schedule.
/// </summary>
public sealed class CompanionRuntime : IHostedService, IAsyncDisposable
{
    private readonly IMemoryConsolidator _consolidator;
    private readonly ICompanionStateSyncEngine? _syncEngine;
    private readonly MultimodalMemoryIngester? _ingester;
    private readonly CompanionRuntimeOptions _options;
    private readonly ILogger _logger;

    private CancellationTokenSource? _stopCts;
    private Task? _dailyLoop;
    private Task? _weeklyLoop;
    private Task? _monthlyLoop;
    private Task? _syncLoop;

    public CompanionRuntime(
        IMemoryConsolidator consolidator,
        CompanionRuntimeOptions? options = null,
        ICompanionStateSyncEngine? syncEngine = null,
        MultimodalMemoryIngester? ingester = null,
        ILogger<CompanionRuntime>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(consolidator);
        _consolidator = consolidator;
        _syncEngine = syncEngine;
        _ingester = ingester;
        _options = options ?? new CompanionRuntimeOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // ── IHostedService ────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CompanionRuntime starting.");
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_syncEngine is not null)
        {
            await _syncEngine.StartAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Sync engine started.");
        }

        if (_options.CatchUpOnStart)
        {
            try
            {
                var outcome = await _consolidator.TickAsync(SleepKind.OnDemand, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Catch-up consolidation: daily={Daily} weekly={Weekly} monthly={Monthly} core={Core}.",
                    outcome.DailySummariesProduced, outcome.SemanticClustersProduced,
                    outcome.PersonaDeltasProduced, outcome.CorePromotions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catch-up consolidation failed (non-fatal).");
            }
        }

        if (_options.DailyTickInterval > TimeSpan.Zero)
            _dailyLoop = RunPeriodic(SleepKind.Daily, _options.DailyTickInterval, _stopCts.Token);
        if (_options.WeeklyTickInterval > TimeSpan.Zero)
            _weeklyLoop = RunPeriodic(SleepKind.Weekly, _options.WeeklyTickInterval, _stopCts.Token);
        if (_options.MonthlyTickInterval > TimeSpan.Zero)
            _monthlyLoop = RunPeriodic(SleepKind.Monthly, _options.MonthlyTickInterval, _stopCts.Token);
        if (_syncEngine is not null && _options.SyncBroadcastInterval > TimeSpan.Zero)
            _syncLoop = RunSyncBroadcasts(_options.SyncBroadcastInterval, _stopCts.Token);

        _logger.LogInformation("CompanionRuntime started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CompanionRuntime stopping.");
        if (_stopCts is not null)
        {
            try { _stopCts.Cancel(); } catch { }
        }

        await SafeAwait(_dailyLoop);
        await SafeAwait(_weeklyLoop);
        await SafeAwait(_monthlyLoop);
        await SafeAwait(_syncLoop);

        if (_syncEngine is not null)
        {
            await _syncEngine.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("CompanionRuntime stopped.");
    }

    // ── Public helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Triggers an OnDemand consolidation pass. Hosts call this after large
    /// chunks of new activity (e.g. end of a long conversation) when they
    /// don't want to wait for the timer.
    /// </summary>
    public Task<ConsolidationOutcome> ConsolidateNowAsync(CancellationToken ct = default)
        => _consolidator.TickAsync(SleepKind.OnDemand, ct);

    /// <summary>
    /// Forwards multimodal ingestion to the registered ingester. Throws
    /// <see cref="InvalidOperationException"/> when no ingester was wired
    /// (the runtime can be wired without one for text-only hosts).
    /// </summary>
    public Task<IngestionResult> IngestMediaAsync(
        MediaModality modality,
        ReadOnlyMemory<byte> sourceBytes,
        string? mimeType = null,
        string? sourceUri = null,
        System.Collections.Generic.Dictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        if (_ingester is null)
            throw new InvalidOperationException(
                "CompanionRuntime was constructed without a MultimodalMemoryIngester.");
        return _ingester.IngestAsync(modality, sourceBytes, mimeType, sourceUri, tags, ct);
    }

    /// <summary>Forces an immediate sync broadcast. No-op when sync isn't wired.</summary>
    public Task SyncNowAsync(CancellationToken ct = default) =>
        _syncEngine?.SyncNowAsync(ct) ?? Task.CompletedTask;

    // ── IAsyncDisposable ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopCts?.Dispose();
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private async Task RunPeriodic(SleepKind kind, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.InitialDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var outcome = await _consolidator.TickAsync(kind, ct).ConfigureAwait(false);
                    if (outcome.DailySummariesProduced + outcome.SemanticClustersProduced
                      + outcome.PersonaDeltasProduced + outcome.CorePromotions > 0)
                    {
                        _logger.LogInformation(
                            "Consolidation tick {Kind}: daily={Daily} weekly={Weekly} monthly={Monthly} core={Core}.",
                            kind, outcome.DailySummariesProduced, outcome.SemanticClustersProduced,
                            outcome.PersonaDeltasProduced, outcome.CorePromotions);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Consolidation tick {Kind} failed.", kind);
                }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
    }

    private async Task RunSyncBroadcasts(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.InitialDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try { await _syncEngine!.SyncNowAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "Sync broadcast failed."); }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
    }

    private static async Task SafeAwait(Task? t)
    {
        if (t is null) return;
        try { await t.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* logged earlier */ }
    }
}
