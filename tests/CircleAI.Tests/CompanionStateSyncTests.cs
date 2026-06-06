// CompanionStateSyncTests.cs
//
// End-to-end exercise of the sync engine + in-memory store + in-process
// channel. Convergence is the headline test — two engines connected by
// a loopback hub start with different state and end with identical state
// after a SyncNow round-trip.

using CircleAI.Memory;
using CircleAI.Memory.Sync;
using Xunit;

namespace CircleAI.Tests;

public sealed class CompanionStateSyncTests
{
    // ══════════════════════════════════════════════════════════════════════
    // HybridLogicalClock
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hlc_TickIsStrictlyMonotonic()
    {
        var fixedTime = 1_700_000_000_000L;
        long Clock() => fixedTime;
        var hlc = new HybridLogicalClock(nodeShortId: 7, physicalNowMs: Clock);

        var v1 = hlc.Tick();
        var v2 = hlc.Tick();
        var v3 = hlc.Tick();

        Assert.True(v2 > v1);
        Assert.True(v3 > v2);
    }

    [Fact]
    public void Hlc_NodeShortIdEncodedInLowBits()
    {
        var hlc = new HybridLogicalClock(nodeShortId: 42, physicalNowMs: () => 1_700_000_000_000L);
        var v = hlc.Tick();
        Assert.Equal(42L, v & 0x3F);
    }

    [Fact]
    public void Hlc_Observe_AdvancesBeyondPeer()
    {
        var hlc = new HybridLogicalClock(nodeShortId: 1, physicalNowMs: () => 1_700_000_000_000L);
        var ourTick = hlc.Tick();
        var peerVersion = HybridLogicalClock.Compose(physicalMs: 2_000_000_000_000L, logical: 5, nodeShortId: 2);

        hlc.Observe(peerVersion);
        var nextTick = hlc.Tick();

        Assert.True(nextTick > peerVersion);
    }

    [Fact]
    public void Hlc_NodeShortIdOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridLogicalClock(nodeShortId: 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridLogicalClock(nodeShortId: -1));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Store apply rules
    // ══════════════════════════════════════════════════════════════════════

    private static SyncableEntry MakeEntry(
        string type, string id, long version, string payload = "{}", bool tombstone = false,
        string nodeId = "n1", string hash = "00")
    {
        return new SyncableEntry(
            EntityType: type,
            EntityId: id,
            Version: version,
            IsTombstone: tombstone,
            ContentHash: hash,
            Payload: payload,
            SourceNodeId: nodeId,
            AuthoredAt: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Store_Apply_NewEntry_Wins()
    {
        var store = new InMemorySyncableEntryStore();
        var applied = await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100));
        Assert.True(applied);
        var got = await store.GetAsync("PersonaState", "u1");
        Assert.NotNull(got);
    }

    [Fact]
    public async Task Store_Apply_OlderVersion_DoesNotReplace()
    {
        var store = new InMemorySyncableEntryStore();
        await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100));
        var applied = await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 50));
        Assert.False(applied);
    }

    [Fact]
    public async Task Store_Apply_SameVersion_HigherHashWins()
    {
        var store = new InMemorySyncableEntryStore();
        await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100, hash: "11"));
        var applied = await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100, hash: "ff"));
        Assert.True(applied);
        var got = await store.GetAsync("PersonaState", "u1");
        Assert.Equal("ff", got!.ContentHash);
    }

    [Fact]
    public async Task Store_Apply_TombstoneReplacesEqualVersionLive()
    {
        var store = new InMemorySyncableEntryStore();
        await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100, hash: "ff", tombstone: false));
        var applied = await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100, hash: "00", tombstone: true));
        Assert.True(applied);
        var got = await store.GetAsync("PersonaState", "u1");
        Assert.True(got!.IsTombstone);
    }

    [Fact]
    public async Task Store_StateVector_ReturnsMaxPerType()
    {
        var store = new InMemorySyncableEntryStore();
        await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 100));
        await store.ApplyAsync(MakeEntry("PersonaState", "u1", version: 200));
        await store.ApplyAsync(MakeEntry("CoreMemory", "m1", version: 50));

        var vec = await store.GetStateVectorAsync();
        Assert.Equal(2, vec.Count);
        var byType = vec.ToDictionary(v => v.EntityType, v => v.MaxKnownVersion);
        Assert.Equal(200, byType["PersonaState"]);
        Assert.Equal(50, byType["CoreMemory"]);
    }

    [Fact]
    public async Task Store_GetSince_ReturnsOnlyNewer()
    {
        var store = new InMemorySyncableEntryStore();
        await store.ApplyAsync(MakeEntry("CoreMemory", "m1", version: 100));
        await store.ApplyAsync(MakeEntry("CoreMemory", "m2", version: 200));
        await store.ApplyAsync(MakeEntry("CoreMemory", "m3", version: 300));

        var since150 = await store.GetSinceAsync("CoreMemory", sinceVersion: 150);
        Assert.Equal(2, since150.Count);
        Assert.Equal(200, since150[0].Version);
        Assert.Equal(300, since150[1].Version);
    }

    // ══════════════════════════════════════════════════════════════════════
    // In-process channel
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Channel_Broadcast_ReachesEveryOtherChannel_NotSender()
    {
        var hub = new InProcessSyncHub();
        using var a = new InProcessCompanionStateChannel(hub, "A");
        using var b = new InProcessCompanionStateChannel(hub, "B");
        using var c = new InProcessCompanionStateChannel(hub, "C");

        var receivedByA = 0;
        var receivedByB = 0;
        var receivedByC = 0;
        a.Subscribe((_, _) => { Interlocked.Increment(ref receivedByA); return Task.CompletedTask; });
        b.Subscribe((_, _) => { Interlocked.Increment(ref receivedByB); return Task.CompletedTask; });
        c.Subscribe((_, _) => { Interlocked.Increment(ref receivedByC); return Task.CompletedTask; });

        var env = new SyncEnvelope(SyncEnvelopeKind.Announce, "A", null, null, null);
        await a.SendAsync(env);

        Assert.Equal(0, receivedByA);
        Assert.Equal(1, receivedByB);
        Assert.Equal(1, receivedByC);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Engine — convergence (the headline test)
    // ══════════════════════════════════════════════════════════════════════

    private static (CompanionStateSyncEngine engine, InMemorySyncableEntryStore store,
                    InProcessCompanionStateChannel channel, HybridLogicalClock clock)
        WireEngine(InProcessSyncHub hub, string nodeId, long nodeShortId, long fakeTimeMs)
    {
        long t = fakeTimeMs;
        var clock = new HybridLogicalClock(nodeShortId, physicalNowMs: () => Interlocked.Increment(ref t));
        var store = new InMemorySyncableEntryStore();
        var channel = new InProcessCompanionStateChannel(hub, nodeId);
        var engine = new CompanionStateSyncEngine(channel, store, clock);
        return (engine, store, channel, clock);
    }

    [Fact]
    public async Task TwoEngines_StartingFromDifferentState_ConvergeAfterSync()
    {
        var hub = new InProcessSyncHub();
        var (eA, sA, cA, _) = WireEngine(hub, "A", 1, fakeTimeMs: 1_700_000_000_000L);
        var (eB, sB, cB, _) = WireEngine(hub, "B", 2, fakeTimeMs: 1_700_000_000_000L);

        await eA.StartAsync();
        await eB.StartAsync();

        // A writes one CoreMemory locally
        await eA.WriteLocalAsync("CoreMemory", "m1", payload: "{\"text\":\"hello from A\"}");

        // B writes a different one
        await eB.WriteLocalAsync("CoreMemory", "m2", payload: "{\"text\":\"hello from B\"}");

        // Trigger announces from both sides — convergence should happen
        await eA.SyncNowAsync();
        await eB.SyncNowAsync();

        // Both stores should now contain both entries.
        Assert.NotNull(await sA.GetAsync("CoreMemory", "m1"));
        Assert.NotNull(await sA.GetAsync("CoreMemory", "m2"));
        Assert.NotNull(await sB.GetAsync("CoreMemory", "m1"));
        Assert.NotNull(await sB.GetAsync("CoreMemory", "m2"));

        await eA.DisposeAsync();
        await eB.DisposeAsync();
        cA.Dispose();
        cB.Dispose();
    }

    [Fact]
    public async Task TwoEngines_ConflictingWrites_ResolveToHigherVersion()
    {
        var hub = new InProcessSyncHub();
        var (eA, sA, cA, _) = WireEngine(hub, "A", 1, fakeTimeMs: 1_700_000_000_000L);
        // B's clock starts later → B's writes will have higher versions.
        var (eB, sB, cB, _) = WireEngine(hub, "B", 2, fakeTimeMs: 1_800_000_000_000L);

        await eA.StartAsync();
        await eB.StartAsync();

        await eA.WriteLocalAsync("PersonaState", "u1", payload: "{\"verbosity\":\"brief\"}");
        await eB.WriteLocalAsync("PersonaState", "u1", payload: "{\"verbosity\":\"detailed\"}");

        await eA.SyncNowAsync();
        await eB.SyncNowAsync();

        var finalA = await sA.GetAsync("PersonaState", "u1");
        var finalB = await sB.GetAsync("PersonaState", "u1");

        Assert.NotNull(finalA);
        Assert.NotNull(finalB);
        // Both ends must agree on the winner.
        Assert.Equal(finalA!.Version, finalB!.Version);
        Assert.Equal(finalA.ContentHash, finalB.ContentHash);
        // Winner should be B (higher physical clock).
        Assert.Contains("detailed", finalA.Payload);

        await eA.DisposeAsync();
        await eB.DisposeAsync();
        cA.Dispose();
        cB.Dispose();
    }

    [Fact]
    public async Task TombstonePropagates_BetweenEngines()
    {
        var hub = new InProcessSyncHub();
        var (eA, sA, cA, _) = WireEngine(hub, "A", 1, fakeTimeMs: 1_700_000_000_000L);
        var (eB, sB, cB, _) = WireEngine(hub, "B", 2, fakeTimeMs: 1_700_000_000_000L);

        await eA.StartAsync();
        await eB.StartAsync();

        await eA.WriteLocalAsync("CoreMemory", "m1", payload: "{}");
        await eA.SyncNowAsync();
        Assert.NotNull(await sB.GetAsync("CoreMemory", "m1"));

        await eA.WriteLocalAsync("CoreMemory", "m1", payload: "", isTombstone: true);
        await eA.SyncNowAsync();

        var entryOnB = await sB.GetAsync("CoreMemory", "m1");
        Assert.NotNull(entryOnB);
        Assert.True(entryOnB!.IsTombstone);

        await eA.DisposeAsync();
        await eB.DisposeAsync();
        cA.Dispose();
        cB.Dispose();
    }

    [Fact]
    public async Task SyncIsIdempotent_RepeatedSyncProducesNoChange()
    {
        var hub = new InProcessSyncHub();
        var (eA, sA, cA, _) = WireEngine(hub, "A", 1, fakeTimeMs: 1_700_000_000_000L);
        var (eB, sB, cB, _) = WireEngine(hub, "B", 2, fakeTimeMs: 1_700_000_000_000L);

        await eA.StartAsync();
        await eB.StartAsync();

        await eA.WriteLocalAsync("CoreMemory", "m1", payload: "{}");
        await eA.SyncNowAsync();
        var entryFirst = await sB.GetAsync("CoreMemory", "m1");

        // Several more sync passes — content should not change.
        await eA.SyncNowAsync();
        await eB.SyncNowAsync();
        await eA.SyncNowAsync();

        var entrySecond = await sB.GetAsync("CoreMemory", "m1");
        Assert.Equal(entryFirst!.Version, entrySecond!.Version);
        Assert.Equal(entryFirst.ContentHash, entrySecond.ContentHash);

        await eA.DisposeAsync();
        await eB.DisposeAsync();
        cA.Dispose();
        cB.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════════
    // PersonaStateSyncBridge demonstrator
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PersonaBridge_SaveOnA_BroadcastsToB_DecodeRecoversState()
    {
        var hub = new InProcessSyncHub();
        var (eA, _, cA, _) = WireEngine(hub, "A", 1, fakeTimeMs: 1_700_000_000_000L);
        var (eB, sB, cB, _) = WireEngine(hub, "B", 2, fakeTimeMs: 1_700_000_000_000L);
        await eA.StartAsync();
        await eB.StartAsync();

        var storeA = new InMemoryPersonaStore();
        var bridge = new PersonaStateSyncBridge(storeA, eA);

        var persona = new PersonaState
        {
            UserId = "u1",
            Verbosity = "brief",
            Formality = "casual",
        };
        persona.TopicWeights["finance"] = 5.5f;

        await bridge.SaveAsync(persona);
        await eA.SyncNowAsync();

        var entryOnB = await sB.GetAsync(PersonaStateSyncBridge.EntityType, "u1");
        Assert.NotNull(entryOnB);
        var decoded = PersonaStateSyncBridge.TryDecode(entryOnB!);
        Assert.NotNull(decoded);
        Assert.Equal("u1", decoded!.UserId);
        Assert.Equal("brief", decoded.Verbosity);
        Assert.Equal("casual", decoded.Formality);
        Assert.Equal(5.5f, decoded.TopicWeights["finance"]);

        await eA.DisposeAsync();
        await eB.DisposeAsync();
        cA.Dispose();
        cB.Dispose();
    }
}
