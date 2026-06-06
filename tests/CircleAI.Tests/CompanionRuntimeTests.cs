// CompanionRuntimeTests.cs
//
// Item 6 audit follow-up — verifies the CompanionRuntime host:
//   • StartAsync calls the catch-up OnDemand tick when configured
//   • Tick loops fire on their configured cadence (using a small interval
//     + a short wait — we only check that AT LEAST one tick happened, not
//     exact counts, to keep the test stable on slow CI)
//   • StopAsync cancels the loops and disposes the sync engine
//   • IngestMediaAsync throws when no ingester was wired
//   • ConsolidateNowAsync forwards to the consolidator
//   • SyncNowAsync forwards when sync wired; no-op otherwise

using CircleAI.Memory.Consolidation;
using CircleAI.Memory.Multimodal;
using CircleAI.Memory.Runtime;
using CircleAI.Memory.Sync;
using Xunit;

namespace CircleAI.Tests;

public sealed class CompanionRuntimeTests
{
    private sealed class CountingConsolidator : IMemoryConsolidator
    {
        public int TickCount { get; private set; }
        public List<SleepKind> Kinds { get; } = new();

        public Task<ConsolidationOutcome> TickAsync(SleepKind kind, CancellationToken ct = default)
        {
            TickCount++;
            Kinds.Add(kind);
            return Task.FromResult(new ConsolidationOutcome(
                kind, DailySummariesProduced: 0, SemanticClustersProduced: 0,
                PersonaDeltasProduced: 0, CorePromotions: 0,
                EpisodesPruned: 0, DailiesPruned: 0, SemanticsPruned: 0,
                RanAtUtc: DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubSyncEngine : ICompanionStateSyncEngine
    {
        public int StartCount { get; private set; }
        public int SyncCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken ct = default) { StartCount++; return Task.CompletedTask; }
        public Task SyncNowAsync(CancellationToken ct = default) { SyncCount++; return Task.CompletedTask; }
        public Task<SyncableEntry> WriteLocalAsync(string entityType, string entityId, string payload,
            bool isTombstone = false, CancellationToken ct = default)
            => throw new NotImplementedException();
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    // ── Catch-up + start ──────────────────────────────────────────────────

    [Fact]
    public async Task Start_RunsCatchUp_WhenEnabled()
    {
        var c = new CountingConsolidator();
        var rt = new CompanionRuntime(
            c,
            options: new CompanionRuntimeOptions
            {
                CatchUpOnStart = true,
                InitialDelay = TimeSpan.FromHours(1), // make sure periodic ticks don't fire
                DailyTickInterval = TimeSpan.FromHours(1),
                WeeklyTickInterval = TimeSpan.FromHours(1),
                MonthlyTickInterval = TimeSpan.FromHours(1),
            });

        await rt.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, c.TickCount);
            Assert.Equal(SleepKind.OnDemand, c.Kinds[0]);
        }
        finally { await rt.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Start_NoCatchUp_WhenDisabled()
    {
        var c = new CountingConsolidator();
        var rt = new CompanionRuntime(c, options: new CompanionRuntimeOptions
        {
            CatchUpOnStart = false,
            InitialDelay = TimeSpan.FromHours(1),
            DailyTickInterval = TimeSpan.FromHours(1),
            WeeklyTickInterval = TimeSpan.FromHours(1),
            MonthlyTickInterval = TimeSpan.FromHours(1),
        });
        await rt.StartAsync(CancellationToken.None);
        try { Assert.Equal(0, c.TickCount); }
        finally { await rt.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Start_WithSyncEngine_CallsStart()
    {
        var c = new CountingConsolidator();
        var sync = new StubSyncEngine();
        var rt = new CompanionRuntime(c,
            options: new CompanionRuntimeOptions { CatchUpOnStart = false,
                InitialDelay = TimeSpan.FromHours(1),
                DailyTickInterval = TimeSpan.FromHours(1),
                WeeklyTickInterval = TimeSpan.FromHours(1),
                MonthlyTickInterval = TimeSpan.FromHours(1),
                SyncBroadcastInterval = TimeSpan.FromHours(1) },
            syncEngine: sync);

        await rt.StartAsync(CancellationToken.None);
        try { Assert.Equal(1, sync.StartCount); }
        finally { await rt.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Stop_DisposesSyncEngine()
    {
        var c = new CountingConsolidator();
        var sync = new StubSyncEngine();
        var rt = new CompanionRuntime(c,
            options: new CompanionRuntimeOptions { CatchUpOnStart = false,
                InitialDelay = TimeSpan.FromHours(1),
                DailyTickInterval = TimeSpan.FromHours(1),
                WeeklyTickInterval = TimeSpan.FromHours(1),
                MonthlyTickInterval = TimeSpan.FromHours(1) },
            syncEngine: sync);

        await rt.StartAsync(CancellationToken.None);
        await rt.StopAsync(CancellationToken.None);
        Assert.Equal(1, sync.DisposeCount);
    }

    // ── Tick loops actually tick ──────────────────────────────────────────

    [Fact]
    public async Task DailyTickLoop_FiresWithinShortInterval()
    {
        var c = new CountingConsolidator();
        var rt = new CompanionRuntime(c, options: new CompanionRuntimeOptions
        {
            CatchUpOnStart = false,
            InitialDelay = TimeSpan.FromMilliseconds(20),
            DailyTickInterval = TimeSpan.FromMilliseconds(40),
            WeeklyTickInterval = TimeSpan.FromHours(1),
            MonthlyTickInterval = TimeSpan.FromHours(1),
        });
        await rt.StartAsync(CancellationToken.None);
        try
        {
            // Wait long enough for several ticks.
            await Task.Delay(300);
            Assert.Contains(SleepKind.Daily, c.Kinds);
        }
        finally { await rt.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SyncLoop_BroadcastsOnSchedule()
    {
        var c = new CountingConsolidator();
        var sync = new StubSyncEngine();
        var rt = new CompanionRuntime(c,
            options: new CompanionRuntimeOptions
            {
                CatchUpOnStart = false,
                InitialDelay = TimeSpan.FromMilliseconds(20),
                DailyTickInterval = TimeSpan.FromHours(1),
                WeeklyTickInterval = TimeSpan.FromHours(1),
                MonthlyTickInterval = TimeSpan.FromHours(1),
                SyncBroadcastInterval = TimeSpan.FromMilliseconds(40),
            },
            syncEngine: sync);
        await rt.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.True(sync.SyncCount >= 1, $"Expected at least 1 sync broadcast; got {sync.SyncCount}");
        }
        finally { await rt.StopAsync(CancellationToken.None); }
    }

    // ── Public helpers ────────────────────────────────────────────────────

    [Fact]
    public async Task ConsolidateNowAsync_ForwardsToConsolidator()
    {
        var c = new CountingConsolidator();
        var rt = new CompanionRuntime(c);
        var r = await rt.ConsolidateNowAsync();
        Assert.Equal(SleepKind.OnDemand, r.Kind);
        Assert.Equal(1, c.TickCount);
    }

    [Fact]
    public async Task IngestMediaAsync_NoIngester_Throws()
    {
        var rt = new CompanionRuntime(new CountingConsolidator());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rt.IngestMediaAsync(MediaModality.Image, new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public async Task SyncNowAsync_NoEngine_IsNoOp()
    {
        var rt = new CompanionRuntime(new CountingConsolidator());
        await rt.SyncNowAsync(); // must not throw
    }
}
