// TheServiceAndTheBrainInWaitingTests.cs
//
// WHAT THE SERVICE DOES ABOUT A BRAIN THAT WAITS ON ITS INDEX, AS TESTS. The owner, 2026-11-13, of the list of what had
// run on the P30 only or nowhere at all - "Android service code: compiled and run on the P30 only", "Three supervisor
// branches never ran anywhere: back-off after a failed idle load; phone picked up mid-index; check fails while the brain
// is loaded" - "Create and add tests - They should be green".
//
// Those decisions sat in the service's Android head, which no test project can reference. They are in the library now
// (BrainInWaiting, WorkerWire, IndexingWhen), and the supervisor, the worker and the application carry them out. Each
// test here states the rule in the words it was given in:
//
//   "This is a background process for hours of course just like updating an OS is hours of time" (2026-11-13)
//   "no p30 is idle right? so it should be running" (2026-11-13)
//   "REPORT ONLY PASS OR FAIL AGAINST A MEASUREMENT" (the indexed mind's rule)

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheServiceAndTheBrainInWaitingTests : IDisposable
{
    private const string TwoB = "Ternary-Bonsai-2-2B";
    private const string FourB = "Ternary-Bonsai-2-4B";
    private const string NineB = "Ternary-Bonsai-2-9B";
    private const string TwentySevenB = "Ternary-Bonsai-2-27B";

    private readonly ModelRegistryService _registry = new();

    public void Dispose() => _registry.Dispose();

    private ModelEntry Big => _registry.GetLatestModel(TwentySevenB)!;

    private ModelEntry Nine => _registry.GetLatestModel(NineB)!;

    private ModelEntry Four => _registry.GetLatestModel(FourB)!;

    private ModelEntry Small => _registry.GetLatestModel(TwoB)!;

    // The P30 as it stood on 2026-11-13, its engine reading brains from flash.
    private static DeviceProbe P30() =>
        new(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 3_867_152_384,
            StorageTotalBytes = 113_170_432_000,
            BrainsReadFromFlash = true,
        };

    private WaitingPlan PlanWith(IndexingStatus? index, params ModelEntry[] others) =>
        BrainInWaiting.Plan(Big, others, P30(), e => e.Name == TwentySevenB ? index : null);

    // ---- who answers and who waits (the end of the supervisor's choice) ----

    // "just like updating an OS": the phone goes on answering with the brain it keeps in RAM, and the best one waits.
    [Fact]
    public void While_the_best_brains_index_is_built_the_other_answers_and_the_best_one_waits_and_it_is_said()
    {
        var notBegun = PlanWith(null, Small);
        Assert.Equal(TwoB, notBegun.Answers.Name);
        Assert.Equal(TwentySevenB, notBegun.Waiting?.Name);
        Assert.Equal("Ternary-Bonsai-2-27B-PTQ1_0.gguf", notBegun.WaitingFile);
        Assert.Equal("Ternary-Bonsai-2-27B is this phone's brain and cannot answer yet: its index is not begun. "
                     + "Ternary-Bonsai-2-2B answers until it is complete, and Ternary-Bonsai-2-27B is loaded while the phone is idle so that it gets built",
            notBegun.Said);

        var part = PlanWith(new IndexingStatus(27, 71), Small);
        Assert.Equal(TwentySevenB, part.Waiting?.Name);
        Assert.Contains("its index is 38% built.", part.Said, StringComparison.Ordinal);

        // Every unit done and no measure kept - a record from before the check's measure was kept: its check is still
        // to run, so the brain is still loaded for it.
        var unmeasured = PlanWith(new IndexingStatus(71, 71), Small);
        Assert.Equal(TwentySevenB, unmeasured.Waiting?.Name);
        Assert.Contains("its index is built and its check is still to run.", unmeasured.Said, StringComparison.Ordinal);
    }

    // "REPORT ONLY PASS OR FAIL AGAINST A MEASUREMENT": what the P30 measured on 2026-11-13, said as what it is. And "if it
    // fails then check why it failed. clean up and try again" (the owner, 2026-11-15): at fifteen in a hundred of its
    // neurons, too few is the reason and more is the remedy, so the brain still waits - loaded while the phone is idle, for
    // an index listing more - while the other answers. Only an index of every neuron that failed, by this engine, leaves
    // nothing to build: then it is not loaded for it again until the engine changes.
    [Fact]
    public void A_best_brain_whose_index_failed_waits_on_one_listing_more_and_is_let_be_only_when_nothing_is_left()
    {
        var again = PlanWith(new IndexingStatus(71, 71, 250), Small);
        Assert.Equal(TwoB, again.Answers.Name);
        Assert.Equal(TwentySevenB, again.Waiting?.Name);
        Assert.Equal("Ternary-Bonsai-2-27B-PTQ1_0.gguf", again.WaitingFile);
        Assert.Equal("Ternary-Bonsai-2-27B is this phone's brain and cannot answer yet: its index is being built again listing more of its "
                     + "neurons, the last having measured 25.0% of the 90% it must reach. Ternary-Bonsai-2-2B answers until it is complete, "
                     + "and Ternary-Bonsai-2-27B is loaded while the phone is idle so that it gets built",
            again.Said);

        var failed = PlanWith(new IndexingStatus(71, 71, 250, EveryNeuron: true), Small);

        Assert.Equal(TwoB, failed.Answers.Name);
        Assert.Null(failed.Waiting);
        Assert.Null(failed.WaitingFile);
        Assert.Equal("Ternary-Bonsai-2-27B is this phone's brain by rank, and its index failed its check with every neuron listed: the same top word as every neuron "
                     + "25.0% of the time, of the 90% it must reach - the engine's fault, not this phone's. It does not answer here until the engine changes; Ternary-Bonsai-2-2B answers",
            failed.Said);

        Assert.True(BrainInWaiting.Failed(new IndexingStatus(71, 71, 899)));
        Assert.False(BrainInWaiting.Failed(new IndexingStatus(71, 71, 900)));   // it passed
        Assert.False(BrainInWaiting.Failed(new IndexingStatus(71, 71)));        // never measured: the check is still to run
        Assert.False(BrainInWaiting.Failed(new IndexingStatus(70, 71, 250)));   // not complete: its pieces are being written again
        Assert.False(BrainInWaiting.Failed(null));
    }

    // Once its index has passed, the best brain is the brain: it answers, nothing waits and nothing is said.
    [Fact]
    public void A_best_brain_whose_index_passed_answers_and_so_does_one_with_nothing_here_to_stand_in_for_it()
    {
        var passed = PlanWith(new IndexingStatus(71, 71, 969), Small);
        Assert.Equal(TwentySevenB, passed.Answers.Name);
        Assert.True(passed.Waiting is null && passed.WaitingFile is null && passed.Said is null);

        // "a mini cooper must do the best it can do with what it has": nothing else here, so it answers as well as it can.
        var alone = PlanWith(null);
        Assert.Equal(TwentySevenB, alone.Answers.Name);
        Assert.True(alone.Waiting is null && alone.Said is null);

        // And a brain the phone keeps in RAM never waits.
        var kept = BrainInWaiting.Plan(Small, [Big], P30(), _ => null);
        Assert.Equal(TwoB, kept.Answers.Name);
        Assert.Null(kept.Waiting);
    }

    // ---- the clock (the supervisor's sitting) ----

    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // The phone idle, nothing asked for five minutes, no load in flight, the 2B loaded and the 27B waiting.
    private static SittingNow Quiet(string? running = TwoB) =>
        new(Waiting: TwentySevenB, Running: running, Loading: false, NobodyAsking: true, PhoneIdle: true,
            SinceLastTurn: TimeSpan.FromMinutes(5), Now: Noon, NotBefore: DateTimeOffset.MinValue);

    private static SittingMove Move(SittingNow now, bool failed = false, bool ready = false, bool heldOff = false) =>
        BrainInWaiting.Sitting(now, () => failed, () => ready, () => heldOff);

    // "no p30 is idle right? so it should be running": idle and quiet, the brain in waiting is loaded for its index.
    [Fact]
    public void An_idle_phone_that_nobody_has_asked_for_three_minutes_loads_the_brain_in_waiting_for_its_index()
    {
        Assert.Equal(SittingMove.LoadForItsIndex, Move(Quiet()));
        Assert.Equal(SittingMove.LoadForItsIndex, Move(Quiet(running: null)));   // nothing loaded at all: still its time

        Assert.Equal(TimeSpan.FromMinutes(3), BrainInWaiting.SitAfter);
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { SinceLastTurn = TimeSpan.FromSeconds(179) }));
        Assert.Equal(SittingMove.LoadForItsIndex, Move(Quiet() with { SinceLastTurn = TimeSpan.FromSeconds(180) }));
    }

    // Nothing is done with no brain in waiting, while a load is in flight, while somebody is asking, while the phone is
    // in a hand or unwell, or while the brain is held off after setbacks.
    [Fact]
    public void The_clock_leaves_everything_alone_until_it_is_the_brain_in_waitings_time()
    {
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { Waiting = null }));
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { Loading = true }));
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { NobodyAsking = false }));
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { PhoneIdle = false }));
        Assert.Equal(SittingMove.Nothing, Move(Quiet(), heldOff: true));
    }

    // THE FIRST OF THE THREE THAT NEVER RAN: back-off after a failed idle load. On the P30 the clock loaded the 27B again
    // fifty-five seconds after its load was ended, into the same end. Ten minutes after the first failure, twice as long
    // after each that follows, two hours at most.
    [Fact]
    public void After_a_load_for_the_index_that_failed_the_next_waits_ten_minutes_then_twice_as_long_each_time()
    {
        Assert.Equal(new[] { 10, 20, 40, 80, 120, 120, 120 },
            new[] { 1, 2, 3, 4, 5, 6, 40 }.Select(failures => (int)BrainInWaiting.SitAgainAfter(failures).TotalMinutes).ToArray());
        Assert.Equal(TimeSpan.FromMinutes(10), BrainInWaiting.SitAgainAfter(0));   // asked with none counted: the first wait

        // What the supervisor does with it: no load before the time it set, and one at it.
        var failedAt = Noon;
        var notBefore = failedAt + BrainInWaiting.SitAgainAfter(1);
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { Now = failedAt.AddSeconds(55), NotBefore = notBefore }));
        Assert.Equal(SittingMove.Nothing, Move(Quiet() with { Now = failedAt.AddMinutes(9), NotBefore = notBefore }));
        Assert.Equal(SittingMove.LoadForItsIndex, Move(Quiet() with { Now = failedAt.AddMinutes(10), NotBefore = notBefore }));
    }

    // THE SECOND: the phone picked up mid-index. The brain loaded for its index answers nobody, so the brain that answers
    // is brought back before anything is asked - and a brain whose index has passed is the brain, and stays.
    [Fact]
    public void Picked_up_while_the_brain_in_waiting_is_loaded_the_phone_gets_its_answering_brain_back()
    {
        var sitting = Quiet(running: TwentySevenB);

        Assert.Equal(SittingMove.Nothing, Move(sitting));                                              // idle still: it goes on indexing
        Assert.Equal(SittingMove.LetGoInUse, Move(sitting with { PhoneIdle = false }));                // picked up
        Assert.Equal(SittingMove.Nothing, Move(sitting with { PhoneIdle = false }, ready: true));      // its index passed: it stays
        Assert.Equal(SittingMove.Nothing, Move(sitting with { PhoneIdle = false, NobodyAsking = false }));   // a question is being answered: that first
    }

    // THE THIRD: the check fails while the brain is loaded. Nothing is left to build and it answers nobody, so the brain
    // that answers is brought back now - with the phone still idle - not at the next question's cost.
    [Fact]
    public void When_its_check_fails_while_it_is_loaded_the_brain_in_waiting_is_let_go_at_once()
    {
        var sitting = Quiet(running: TwentySevenB);

        Assert.Equal(SittingMove.LetGoFailed, Move(sitting, failed: true));
        Assert.Equal(SittingMove.LetGoFailed, Move(sitting with { PhoneIdle = false }, failed: true));
    }

    // Each question is asked only when its answer decides something: a record is read from flash to answer two of them.
    [Fact]
    public void The_clock_asks_the_index_and_the_setbacks_only_when_the_answer_decides_something()
    {
        var asked = new List<string>();
        SittingMove Ask(SittingNow now) => BrainInWaiting.Sitting(now,
            () => { asked.Add("failed"); return false; }, () => { asked.Add("ready"); return false; }, () => { asked.Add("held off"); return false; });

        Ask(Quiet() with { Waiting = null });
        Ask(Quiet() with { Loading = true });
        Ask(Quiet() with { PhoneIdle = false });
        Assert.Empty(asked);

        Ask(Quiet());                                   // not loaded, idle: only whether it is held off
        Assert.Equal(new[] { "held off" }, asked.ToArray());
        asked.Clear();

        Ask(Quiet(running: TwentySevenB));              // loaded and idle: whether its check failed, and no more
        Assert.Equal(new[] { "failed" }, asked.ToArray());
        asked.Clear();

        Ask(Quiet(running: TwentySevenB) with { PhoneIdle = false });   // loaded and picked up: then whether it can answer
        Assert.Equal(new[] { "failed", "ready" }, asked.ToArray());
    }

    // The whole of it in order, as one phone lives it: download, index in idle time, a question in the middle, the check.
    [Fact]
    public void From_arrival_to_takeover_each_move_comes_when_its_time_has_come()
    {
        var phone = P30();
        IndexingStatus? index = null;
        WaitingPlan Plan() => BrainInWaiting.Plan(Big, [Small], phone, e => e.Name == TwentySevenB ? index : null);
        SittingMove Clock(string? running, bool idle, TimeSpan quiet) => BrainInWaiting.Sitting(
            new SittingNow(Plan().Waiting?.Name, running, Loading: false, NobodyAsking: true, PhoneIdle: idle, quiet, Noon, DateTimeOffset.MinValue),
            failedItsCheck: () => index is { NothingLeftToTry: true },   // as the supervisor asks it (FailedItsCheck)
            readyToAnswer: () => BrainInWaiting.Ready(Big, phone, index),
            heldOff: () => false);

        // The 27B has landed; nothing is indexed. The 2B answers, and in use nothing moves.
        Assert.Equal(TwoB, Plan().Answers.Name);
        Assert.Equal(SittingMove.Nothing, Clock(TwoB, idle: false, TimeSpan.FromMinutes(30)));

        // Left alone: the 27B is loaded for its index.
        Assert.Equal(SittingMove.LoadForItsIndex, Clock(TwoB, idle: true, TimeSpan.FromMinutes(4)));
        index = new IndexingStatus(12, 71);

        // Picked up part way: the 2B comes back, and the index stays as far as it got.
        Assert.Equal(SittingMove.LetGoInUse, Clock(TwentySevenB, idle: false, TimeSpan.FromMinutes(20)));
        Assert.Equal(TwoB, Plan().Answers.Name);
        Assert.Contains("16% built", Plan().Said, StringComparison.Ordinal);

        // Idle again: loaded again, and it carries on to its check.
        Assert.Equal(SittingMove.LoadForItsIndex, Clock(TwoB, idle: true, TimeSpan.FromMinutes(3)));
        index = new IndexingStatus(71, 71, 969);

        // Passed: the 27B is the brain. It stays loaded when the phone is picked up, and nothing waits any more.
        Assert.Equal(SittingMove.Nothing, Clock(TwentySevenB, idle: false, TimeSpan.Zero));
        Assert.Equal(TwentySevenB, Plan().Answers.Name);
        Assert.Null(Plan().Waiting);
        Assert.Equal(SittingMove.Nothing, Clock(TwentySevenB, idle: true, TimeSpan.FromHours(1)));

        // Had it failed at fifteen in a hundred of its neurons instead: it stays loaded while the phone is idle - its engine
        // goes straight on to an index listing more - and when it has been let go it is loaded again for that one; the 2B
        // answers meanwhile (the owner, 2026-11-15: "clean up and try again").
        index = new IndexingStatus(71, 71, 250);
        Assert.Equal(SittingMove.Nothing, Clock(TwentySevenB, idle: true, TimeSpan.FromHours(1)));
        Assert.Equal(SittingMove.LoadForItsIndex, Clock(TwoB, idle: true, TimeSpan.FromHours(1)));
        Assert.Equal(TwoB, Plan().Answers.Name);

        // Had it failed with every neuron listed, by this engine: let go at once, never loaded for it again until the
        // engine changes, and the 2B answers.
        index = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        Assert.Equal(SittingMove.Nothing, Clock(TwoB, idle: true, TimeSpan.FromHours(1)));   // nothing waits: Plan says so
        Assert.Equal(TwoB, Plan().Answers.Name);
    }

    // ---- what "idle" is to the service ----

    // "no p30 is idle right? so it should be running": a screen that does not say is no sign of use; heat and a low
    // battery are reasons to wait.
    [Fact]
    public void The_phone_is_idle_when_nothing_says_it_is_in_use_and_it_is_neither_hot_nor_low()
    {
        var well = new PowerState(82, true, false);
        Assert.True(BrainInWaiting.PhoneIdle(false, well));
        Assert.True(BrainInWaiting.PhoneIdle(null, well));                          // lit by Stay awake: the screen does not say
        Assert.False(BrainInWaiting.PhoneIdle(true, well));

        Assert.False(BrainInWaiting.PhoneIdle(false, new PowerState(82, true, true)));     // hot
        Assert.False(BrainInWaiting.PhoneIdle(false, new PowerState(15, false, false)));   // low, off its charger
        Assert.True(BrainInWaiting.PhoneIdle(false, new PowerState(15, true, false)));     // low but charging
        Assert.True(BrainInWaiting.PhoneIdle(null, PowerState.Unknown));                   // a device that says nothing is not held back
    }

    // The application's reading of the screen: Android's "Stay awake" is a bit a kind of charger, the same bits the
    // battery says it is plugged into. That P30's setting was 7, and it was on its USB cable.
    [Fact]
    public void A_screen_kept_lit_by_stay_awake_on_the_charger_it_is_plugged_into_does_not_say_the_phone_is_in_use()
    {
        const int mains = 1, usb = 2, wireless = 4, unplugged = 0;

        Assert.True(IndexingWhen.KeptLit(stayAwakeOn: 7, pluggedInto: usb));
        Assert.True(IndexingWhen.KeptLit(mains | usb, mains));
        Assert.False(IndexingWhen.KeptLit(mains | usb, wireless));   // awake on a cable, and it is on a pad
        Assert.False(IndexingWhen.KeptLit(7, unplugged));            // off its charger the screen times out by itself
        Assert.False(IndexingWhen.KeptLit(0, usb));                  // Stay awake is off

        // And what the indexing is told, as the application composes the two.
        bool? InUse(bool lit, int stayAwakeOn, int pluggedInto) => IndexingWhen.InUse(lit, lit && IndexingWhen.KeptLit(stayAwakeOn, pluggedInto));
        Assert.Null(InUse(lit: true, 7, usb));
        Assert.True(InUse(lit: true, 0, usb));
        Assert.True(InUse(lit: true, 7, unplugged));
        Assert.False(InUse(lit: false, 7, usb));
    }

    // ---- how the brain in waiting is built, and what crosses to the worker ----

    // Built small and not warmed: the 27B's working memory for its whole context was 750 MB of live heap on the P30, and
    // its first prefill was minutes of reading that ended two loads at the load's own limit.
    [Fact]
    public void A_brain_loaded_for_its_index_is_built_small_with_the_engines_own_memory_and_is_not_warmed()
    {
        Assert.Equal(1.0 / 16, BrainInWaiting.ForItsIndexContextShare);
        Assert.Equal(2048, (int)(32_768 * BrainInWaiting.ForItsIndexContextShare));   // the 27B's context, a sixteenth of it

        // Whatever the balance asks of a brain that answers, the one loaded for its index is not reshaped by it.
        KvCompressionMode? none = null, threeBits = KvCompressionMode.TurboQuant3Bit;
        Assert.Equal((none, 1.0 / 16), BrainInWaiting.LoadShape(forItsIndex: true, threeBits, 0.5));
        Assert.Equal((threeBits, 0.5), BrainInWaiting.LoadShape(forItsIndex: false, threeBits, 0.5));
        Assert.Equal((none, 1.0), BrainInWaiting.LoadShape(forItsIndex: false, none, 1.0));

        Assert.False(BrainInWaiting.WarmsOnStart(forItsIndex: true, asConfigured: true));
        Assert.True(BrainInWaiting.WarmsOnStart(forItsIndex: false, asConfigured: true));
        Assert.False(BrainInWaiting.WarmsOnStart(forItsIndex: false, asConfigured: false));
    }

    // The load crosses to the inference process as a flat map, and both ends read what the other wrote.
    [Fact]
    public void A_load_for_the_index_crosses_to_the_worker_and_is_read_back_as_it_was_asked()
    {
        var (kv, share) = BrainInWaiting.LoadShape(forItsIndex: true, KvCompressionMode.TurboQuant3Bit, 0.5);
        var asked = new WorkerLoadAsk(TwentySevenB, "/data/models", SkipHash: true, Engine: null, kv, share, ForItsIndex: true);
        var map = new Dictionary<string, string>();
        WorkerWire.PutLoad(map, asked);

        Assert.Equal(WorkerWire.OpLoad, map[WorkerWire.Op]);
        Assert.Equal("1", map[WorkerWire.ForIndex]);
        Assert.Equal("0.0625", map[WorkerWire.ContextShare]);
        Assert.False(map.ContainsKey(WorkerWire.KvMode));   // the engine's own: nothing is sent
        Assert.Equal(asked, WorkerWire.GetLoad(map));

        // A load that answers, shaped by the balance: its bits and its share cross, and no word of an index.
        var answering = new WorkerLoadAsk(TwoB, "/data/models", SkipHash: false, ModelEngine.Managed, KvCompressionMode.TurboQuant3Bit, 0.5, ForItsIndex: false);
        map.Clear();
        WorkerWire.PutLoad(map, answering);
        Assert.False(map.ContainsKey(WorkerWire.ForIndex));
        Assert.Equal(answering, WorkerWire.GetLoad(map));

        // A plain load is the four keys it always was, so a worker of the build before reads it as it always did - and a
        // map from the build before, with none of the new keys, reads as a brain as built that answers.
        var plain = new WorkerLoadAsk(TwoB, "/data/models", SkipHash: false, null, null, 1.0, ForItsIndex: false);
        map.Clear();
        WorkerWire.PutLoad(map, plain);
        Assert.Equal(new[] { WorkerWire.Directory, WorkerWire.Model, WorkerWire.Op, WorkerWire.SkipHash }.Order(StringComparer.Ordinal),
            map.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(plain, WorkerWire.GetLoad(map));
        Assert.Equal(new WorkerLoadAsk(null, null, false, null, null, 1.0, false), WorkerWire.GetLoad(new Dictionary<string, string>()));

        // A share that is not a share reads as all of the context.
        foreach (var bad in new[] { "0", "1", "1.5", "-0.25", "half" })
        {
            map[WorkerWire.ContextShare] = bad;
            Assert.Equal(1.0, WorkerWire.GetLoad(map).ContextShare);
        }
    }

    // A developer's check of drafting (SoakReceiver "engine" --ei drafts --ei seed) crosses too, and only a check carries
    // it: a plain load says nothing of drafting, and a brain built to draft is not the brain that decodes plainly.
    [Fact]
    public void A_check_of_drafting_crosses_to_the_worker_and_only_a_check_carries_it()
    {
        var check = new WorkerLoadAsk(TwoB, "/data/models", SkipHash: true, null, null, 1.0, ForItsIndex: false, Drafts: 2, Seed: 7);
        var map = new Dictionary<string, string>();
        WorkerWire.PutLoad(map, check);
        Assert.Equal("2", map[WorkerWire.Drafts]);
        Assert.Equal("7", map[WorkerWire.Seed]);
        Assert.Equal(check, WorkerWire.GetLoad(map));

        var plain = check with { Drafts = 0, Seed = null };
        Assert.False(WorkerWire.SameBrain(plain, check));
        Assert.True(WorkerWire.SameBrain(check, check));

        // A number that is no count of drafts reads as plain decoding, and a seed that is no number as none.
        foreach (var bad in new[] { "0", "-1", "9", "two" })
        {
            map[WorkerWire.Drafts] = bad;
            Assert.Equal(0, WorkerWire.GetLoad(map).Drafts);
        }
        map[WorkerWire.Seed] = "seven";
        Assert.Null(WorkerWire.GetLoad(map).Seed);

        // And nothing the product builds drafts or fixes a seed unless a check asked (OPEN-GAPS C7).
        Assert.Equal(0, CircleAI.Inference.Managed.ManagedChatGenerator.DraftsForTest);
        Assert.Null(CircleAI.Inference.Managed.ManagedChatGenerator.SeedForTest);
    }

    // ---- a brain with nothing left to try is not the phone's brain ----

    // "With each success the model is upgraded, until a limit pattern is established" - and the step below a brain with
    // nothing left to try is the phone's brain. The selector went on naming the 27B on that P30, whose index failed at
    // 25.0%. The 4B the climb fetches next has not loaded there yet, so it was nobody's stand-in, and nothing would ever
    // have loaded it. Had that index listed every neuron, by this engine - the engine's fault - that is where it stands.
    [Fact]
    public void The_best_brain_here_whose_index_has_not_failed_takes_the_place_of_one_that_has()
    {
        var phone = P30();
        var failed = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        IndexingStatus? Index(ModelEntry e) => e.Name == TwentySevenB ? failed : null;

        // That P30, had it failed like that: the 2B, and it is said.
        var today = BrainInWaiting.PastAFailedIndex(Big, [Small, Big], phone, Index);
        Assert.Equal(TwoB, today.Brain.Name);
        Assert.Equal("Ternary-Bonsai-2-27B's index failed its check here with every neuron listed (25.0%): Ternary-Bonsai-2-2B is this phone's brain until the engine changes", today.Said);

        // The 4B has landed and not loaded yet: it is the phone's brain, and the supervisor validates it before it answers.
        Assert.Equal(FourB, BrainInWaiting.PastAFailedIndex(Big, [Small, Four, Big], phone, Index).Brain.Name);

        // The 9B has landed above it: it is the phone's brain, and it waits on its index while the 4B answers.
        var nine = BrainInWaiting.PastAFailedIndex(Big, [Four, Nine, Big], phone, Index);
        Assert.Equal(NineB, nine.Brain.Name);
        var plan = BrainInWaiting.Plan(nine.Brain, [Four], phone, Index);
        Assert.Equal(FourB, plan.Answers.Name);
        Assert.Equal(NineB, plan.Waiting?.Name);

        // And when nothing is left to try of it either: the 4B.
        IndexingStatus? Both(ModelEntry e) => e.Name is TwentySevenB or NineB ? failed : null;
        var back = BrainInWaiting.PastAFailedIndex(Big, [Four, Nine, Big], phone, Both);
        Assert.Equal(FourB, back.Brain.Name);
        Assert.Equal("Ternary-Bonsai-2-27B's index failed its check here with every neuron listed (25.0%): Ternary-Bonsai-2-4B is this phone's brain until the engine changes", back.Said);
    }

    // What has something left to try stands: an index passed, not begun, part built, built with its check still to run, or
    // failed at a share below every neuron (the next lists more, IndexingRungs) - the chosen brain goes on, to answer or to
    // wait on its index. A brain the device keeps in RAM never failed here, whatever became of an index somebody began; and
    // where the engine keeps every brain in RAM, none did.
    [Fact]
    public void A_brain_whose_index_has_not_failed_here_stands()
    {
        var phone = P30();
        foreach (var index in new IndexingStatus?[] { null, new IndexingStatus(27, 71), new IndexingStatus(71, 71), new IndexingStatus(71, 71, 950), new IndexingStatus(71, 71, 250) })
        {
            var past = BrainInWaiting.PastAFailedIndex(Big, [Small, Big], phone, e => e.Name == TwentySevenB ? index : null);
            Assert.True(past.Brain.Name == TwentySevenB && past.Said is null, $"with its index at {index}, the 27B gave way to {past.Brain.Name}");
        }

        Assert.False(BrainInWaiting.FailedHere(Big, phone, new IndexingStatus(71, 71, 250)));   // failed at a smaller share: tried again
        var failed = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        Assert.True(BrainInWaiting.FailedHere(Big, phone, failed));
        Assert.False(BrainInWaiting.FailedHere(Small, phone, failed));
        var small = BrainInWaiting.PastAFailedIndex(Small, [Small, Big], phone, _ => failed);
        Assert.Equal(TwoB, small.Brain.Name);
        Assert.Null(small.Said);

        var inRam = new DeviceProbe(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_152_384, StorageTotalBytes = 113_170_432_000, BrainsReadFromFlash = false };
        Assert.False(BrainInWaiting.FailedHere(Big, inRam, failed));
    }

    // Only the brain with nothing left to try is here: it is what the phone has, it answers as well as it can, and that is
    // said.
    [Fact]
    public void With_no_other_brain_here_the_one_whose_index_failed_answers_as_well_as_it_can()
    {
        var failed = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        var past = BrainInWaiting.PastAFailedIndex(Big, [Big], P30(), _ => failed);

        Assert.Equal(TwentySevenB, past.Brain.Name);
        Assert.Equal("Ternary-Bonsai-2-27B's index failed its check here with every neuron listed (25.0%), and no other brain is here: it answers as well as it can", past.Said);

        // Nor is another brain whose index failed here any better a choice.
        Assert.Equal(TwentySevenB, BrainInWaiting.PastAFailedIndex(Big, [Nine, Big], P30(), _ => failed).Brain.Name);
    }

    // ---- a brain that lands is taken up now ----

    // The owner, 2026-11-14: "Yes, do it" - a brain the climb fetches is taken up as soon as it lands, not at the next load.
    // On the P30 the 4B landed at 15:42:52 behind the 2B, the landing asked the brain to be ready, the 2B was, and an hour
    // and a half later nothing had validated the 4B: the supervisor chose only when it loaded.
    [Fact]
    public void A_brain_that_lands_is_chosen_again_now_and_the_first_load_still_chooses_where_nothing_answers()
    {
        var asked = 0;
        Func<string?> Choosing(string? choice) => () => { asked++; return choice; };

        // Nothing answers yet: the first load chooses, through the catalogue's gate, and nothing is chosen here.
        var first = BrainInWaiting.TakingUp(null, false, Choosing(FourB));
        Assert.Equal(TakeUpMove.FirstLoadChooses, first.Move);
        Assert.Null(first.Choice);
        Assert.Equal(0, asked);

        // That P30: the 2B answering when the 4B landed. Chosen again now, the 2B goes on answering - the choice leaves the
        // 4B to be validated, and the supervisor validates it when its time has come.
        var p30 = BrainInWaiting.TakingUp(TwoB, false, Choosing(TwoB));
        Assert.Equal(TakeUpMove.Stays, p30.Move);
        Assert.Equal(TwoB, p30.Choice);

        // Nothing this phone may use is left to choose from: the brain that answers stays.
        Assert.Equal(TakeUpMove.Stays, BrainInWaiting.TakingUp(TwoB, false, Choosing(null)).Move);

        // A brain needing no validation landed - one that has loaded here before - and it is the phone's brain: it answers.
        var other = BrainInWaiting.TakingUp(TwoB, false, Choosing(FourB));
        Assert.Equal(TakeUpMove.Switches, other.Move);
        Assert.Equal(FourB, other.Choice);

        // The brain in waiting is loaded for its index: chosen again, and the clock that sits with it says when it goes.
        var sitting = BrainInWaiting.TakingUp(NineB, true, Choosing(FourB));
        Assert.Equal(TakeUpMove.SittingDecides, sitting.Move);
        Assert.Equal(FourB, sitting.Choice);
        Assert.Equal(4, asked);
    }

    // A brain new to the phone that answers through its index is not asked three questions first: with no index it reads
    // every layer from flash for every word - 26 to 74 seconds a word for the 27B on the P30. It waits on its index, and
    // the brain beside it answers. One the phone keeps in RAM is validated as before.
    [Fact]
    public void A_new_brain_that_answers_through_its_index_waits_on_it_and_one_kept_in_RAM_is_validated()
    {
        var phone = P30();

        // The 9B, the climb's step after the 4B on that P30: no index, part built, or built with its check still to run.
        foreach (var index in new IndexingStatus?[] { null, new IndexingStatus(27, 71), new IndexingStatus(71, 71) })
            Assert.True(BrainInWaiting.WaitsOnItsIndex(Nine, phone, index), $"with its index at {index} the 9B was sent to be asked questions");

        // Its index serves: it is validated as any other brain new here.
        Assert.False(BrainInWaiting.WaitsOnItsIndex(Nine, phone, new IndexingStatus(71, 71, 950)));

        // The 4B that landed is kept in RAM on that P30: never waits, whatever became of an index somebody began of it.
        Assert.False(BrainInWaiting.WaitsOnItsIndex(Four, phone, null));
        Assert.False(BrainInWaiting.WaitsOnItsIndex(Four, phone, new IndexingStatus(27, 71)));

        // And where the engine keeps every brain in RAM, nothing waits.
        var inRam = new DeviceProbe(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_152_384, StorageTotalBytes = 113_170_432_000, BrainsReadFromFlash = false };
        Assert.False(BrainInWaiting.WaitsOnItsIndex(Nine, inRam, null));

        // So when the 9B lands there, after the 4B has passed: it is the phone's brain past a 27B with nothing left to try,
        // it is not asked questions, and it waits on its index while the 4B answers.
        var failed = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        IndexingStatus? Index(ModelEntry e) => e.Name == TwentySevenB ? failed : null;
        var chosen = BrainInWaiting.PastAFailedIndex(Big, [Small, Four, Nine, Big], phone, Index).Brain;
        Assert.Equal(NineB, chosen.Name);
        Assert.True(BrainInWaiting.WaitsOnItsIndex(chosen, phone, Index(chosen)));
        var plan = BrainInWaiting.Plan(chosen, [Small, Four, Big], phone, Index);
        Assert.Equal(FourB, plan.Answers.Name);
        Assert.Equal(NineB, plan.Waiting?.Name);
    }

    // The head carries the take-up out: the landing hook asks the supervisor before it asks the brain to be ready, the
    // supervisor decides by the library and chooses again, and its choice sends a brain that waits on its index to wait on
    // it - and keeps, while one is validated, a brain that answers here, never one whose index failed.
    [Fact]
    public void The_service_takes_up_a_brain_that_lands_and_validates_only_what_can_answer_now()
    {
        var landing = Code("src", "CircleAIService", "Droid", "StepFour.cs");
        var takeUp = landing.IndexOf("await supervisor.TakeUpAsync(name, CancellationToken.None)", StringComparison.Ordinal);
        var ready = landing.IndexOf("await brain.EnsureReadyAsync(CancellationToken.None)", Math.Max(takeUp, 0), StringComparison.Ordinal);
        Assert.True(takeUp > 0 && ready > takeUp, "a brain that lands is not taken up before the brain is asked to be ready");

        var supervisor = Code("src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs");
        var start = supervisor.IndexOf("public async Task<bool> TakeUpAsync(string landed, CancellationToken ct)", StringComparison.Ordinal);
        var end = supervisor.IndexOf("private string StatePath(", Math.Max(start, 0), StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the supervisor's take-up was not found");
        var take = supervisor[start..end];
        foreach (var step in new[]
                 {
                     "BrainInWaiting.TakingUp(running, forItsIndex, () =>",
                     "return Choose();",
                     "await ValidateNowIfDueAsync(ct)",
                     "if (await ValidateCandidateIfDueAsync(ct).ConfigureAwait(false)) return;",
                     "SchedulerIdle?.Invoke() != true",
                     "_loading = Task.Run(() => LoadAsync(choice!))",
                 })
        {
            Assert.Contains(step, take, StringComparison.Ordinal);
        }

        // In the choice: a brain new here that waits on its index is not sent to be validated, and the brain kept while one
        // is validated is never one whose index failed here.
        Assert.Contains("bool WaitsOnItsIndexHere(string id) => registry.GetLatestModel(id) is { } e && BrainInWaiting.WaitsOnItsIndex(e, probe, BrainInWaiting.IndexOf(e));",
                        supervisor, StringComparison.Ordinal);
        Assert.Contains("bool IndexFailedHere(string id) => registry.GetLatestModel(id) is { } e && BrainInWaiting.FailedHere(e, probe, BrainInWaiting.IndexOf(e));",
                        supervisor, StringComparison.Ordinal);
        Assert.Contains("if (catalogue is not null && catalogue.Assessment(wanted!) is { Proven: false } && !WaitsOnItsIndexHere(wanted!))",
                        supervisor, StringComparison.Ordinal);
        var proven = supervisor.IndexOf("var proven = CircleAI.Inference.ModelChoice.Best(loader.InstalledIds()", StringComparison.Ordinal);
        var kept = supervisor.IndexOf("_pendingCandidate = wanted;", Math.Max(proven, 0), StringComparison.Ordinal);
        Assert.True(proven > 0 && kept > proven, "the brain kept while one is validated was not found");
        Assert.Contains(".Where(id => !IndexFailedHere(id))", supervisor[proven..kept], StringComparison.Ordinal);
    }

    // ---- the Android half, guarded at the source ----

    // The head that carries these decisions out cannot be referenced by a test project, so it is read: each decision is
    // the library's, called where the head used to decide for itself, and no copy of a rule is left behind in the head.
    [Fact]
    public void The_Android_service_carries_out_the_librarys_decisions_and_keeps_no_copy_of_them()
    {
        var supervisor = Code("src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs");
        foreach (var call in new[]
                 {
                     "BrainInWaiting.Plan(best, others, probe",
                     "BrainInWaiting.Sitting(",
                     "BrainInWaiting.SitAgainAfter(++_sitFailures)",
                     "BrainInWaiting.LoadShape(forItsIndex, shape.KvMode, shape.ContextShare)",
                     "BrainInWaiting.PhoneIdle(DeviceContext.InUseProbe?.Invoke()",
                     "BrainInWaiting.IndexOf(entry) is { NothingLeftToTry: true }",
                     "LoadAsync(waiting, forItsIndex: true)",
                 })
        {
            Assert.Contains(call, supervisor, StringComparison.Ordinal);
        }
        foreach (var copy in new[] { "TimeSpan SitAgainAfter(", "ForIndexContextShare", "TimeSpan SitAfter", "BrainInWaiting.StandIn(" })
            Assert.DoesNotContain(copy, supervisor, StringComparison.Ordinal);

        // A brain whose index failed here gives way to the best one here that has not - before the proven brain is kept
        // while a new one is validated, so the brain the climb fetched is the one validated.
        var past = supervisor.IndexOf("BrainInWaiting.PastAFailedIndex(picked,", StringComparison.Ordinal);
        var validated = supervisor.IndexOf("catalogue.Assessment(wanted!) is { Proven: false }", StringComparison.Ordinal);
        Assert.True(past > 0 && validated > past, "the supervisor does not set aside a brain whose index failed before it validates the next");
        Assert.Contains("loader.InstalledIds().Where(UsableNow).Select(id => registry.GetLatestModel(id)!), probe,", supervisor, StringComparison.Ordinal);

        var connection = Code("src", "CircleAIService", "Droid", "Inference", "WorkerConnection.cs");
        Assert.Contains("WorkerWire.PutLoad(map, new WorkerLoadAsk(", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("[WorkerWire.ForIndex]", connection, StringComparison.Ordinal);

        var worker = Code("src", "CircleAIService", "Droid", "Inference", "InferenceWorkerService.cs");
        Assert.Contains("WorkerWire.GetLoad(req)", worker, StringComparison.Ordinal);
        Assert.Contains("WorkerWire.SameBrain(", worker, StringComparison.Ordinal);
        Assert.Contains("BrainInWaiting.WarmsOnStart(request.ForItsIndex, options.WarmOnStart)", worker, StringComparison.Ordinal);
        Assert.Contains("MemoryNow.GiveBack()", worker, StringComparison.Ordinal);   // a brain let go is given back before the next is built

        var application = Code("src", "CircleAIService", "ServiceApplication.cs");
        Assert.Contains("DeviceContext.InUseProbe = InUse;", application, StringComparison.Ordinal);
        Assert.Contains("IndexingWhen.InUse(lit, lit && KeptLitWhileItCharges())", application, StringComparison.Ordinal);
        Assert.Contains("IndexingWhen.KeptLit(stayOn, plugged)", application, StringComparison.Ordinal);
        Assert.Contains("DeviceContext.LowMemoryLineProbe = LowMemoryLine;", application, StringComparison.Ordinal);
        Assert.Contains("supervisor.SitWithTheBrainInWaitingAsync(CancellationToken.None)", application, StringComparison.Ordinal);

        // The background fetcher takes the upgrade's next step from the library, tells it how each brain's step has gone
        // here and what the flash measured, and says what was decided. No brain is refused any more (the owner, 2026-11-15:
        // "no models should be blocked"): the step is told nothing of a refusal, and none leaves a brain out of the list.
        var fetcher = Code("src", "CircleAI", "Device", "ModelFetchService.cs");
        Assert.Contains("ModelChoice.NextStep(chat, name => loader.ModelPresent(name), probe,", fetcher, StringComparison.Ordinal);
        Assert.Contains("BrainInWaiting.StepOf(entry, probe, loader.ModelPresent(entry.Name),", fetcher, StringComparison.Ordinal);
        Assert.DoesNotContain("CrashVerdict.IsRefused", fetcher, StringComparison.Ordinal);
        Assert.Contains("flash: BrainInWaiting.FlashOf()", fetcher, StringComparison.Ordinal);
        Assert.Contains("\"upgrade: \" + step.Said", fetcher, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelChoice.Upgrade(", fetcher, StringComparison.Ordinal);
        Assert.DoesNotContain("!CrashVerdict.IsRefused(catalogue, e.Name)", fetcher, StringComparison.Ordinal);

        // AND IT IS ASKED WHEN NOTHING ELSE IS OWED (2026-11-14). The upgrade was decided only inside a fetch that
        // something owed had started, so the P30 - owing nothing - never asked for the 4B. The step up starts the fetch
        // when it can be fetched now, on Wi-Fi and on charge, and says when it waits.
        var owed = fetcher.IndexOf("public static void StartIfAnythingIsOwed(", StringComparison.Ordinal);
        var asked = fetcher.IndexOf("var step = NextUpgrade(registry, loader);", owed, StringComparison.Ordinal);
        var launch = fetcher.IndexOf("private static void Launch(", StringComparison.Ordinal);
        Assert.True(owed > 0 && asked > owed && launch > asked, "a phone that owes nothing does not ask for the step up before it decides not to fetch");
        // And a wait says what it waits for: on the P30 "waits for Wi-Fi and a charger" was said of a phone on unmetered
        // Wi-Fi and USB power, and could not say which of the two was wrong.
        Assert.Contains("if (WaitsFor(context) is { } wanting)", fetcher, StringComparison.Ordinal);
        Assert.Contains("$\"upgrade: {next.Name} waits for {wanting}\"", fetcher, StringComparison.Ordinal);
        Assert.Contains("if (cm.ActiveNetwork is not { } active) return \"a network this app may use: Android gives it none yet\";", fetcher, StringComparison.Ordinal);

        // A step up that waits is asked again two minutes later, once a process: Android let the P30's service onto its
        // unmetered Wi-Fi a minute after it started, and the hourly review is an hour away.
        Assert.Contains("AskAgainSoon(context);", fetcher, StringComparison.Ordinal);
        Assert.Contains("if (Interlocked.Exchange(ref _askedAgain, 1) != 0) return;", fetcher, StringComparison.Ordinal);

        // And the service may ask Android about its network at all: without ACCESS_NETWORK_STATE every such question
        // threw, and the P30's step up "waits for a network: SecurityException" on unmetered Wi-Fi (2026-11-14).
        var manifest = System.IO.File.ReadAllText(Source("src", "CircleAIService", "Platforms", "Android", "AndroidManifest.xml"));
        Assert.Contains("<uses-permission android:name=\"android.permission.ACCESS_NETWORK_STATE\" />", manifest, StringComparison.Ordinal);
        Assert.Contains("HasCapability(global::Android.Net.NetCapability.NotMetered)", fetcher, StringComparison.Ordinal);
        Assert.Contains("(context.GetSystemService(BatteryService) as BatteryManager)?.IsCharging == true", fetcher, StringComparison.Ordinal);

        // And the hourly review that asks it takes the phone to be idle by the service's own reading, as the index does - a
        // screen that keeps itself lit says nothing of use - not by the screen being off, which a phone kept lit on its
        // cable never is.
        var review = Code("src", "CircleAI", "Device", "IdleModelReview.cs");
        Assert.Contains("var idle = Idle(app);", review, StringComparison.Ordinal);
        Assert.Contains("return BrainInWaiting.PhoneIdle(inUse(), CircleAI.Core.DeviceContext.PowerProbe?.Invoke()", review, StringComparison.Ordinal);
        Assert.DoesNotContain("var idle = ScreenIsOff(app);", review, StringComparison.Ordinal);
        Assert.DoesNotContain("InUseProbe?.Invoke() is { } inUse", review, StringComparison.Ordinal);

        // And both of the head's memory probes tell the device probe, in the engine's own word, whether brains are read
        // from flash here.
        foreach (var probe in new[] { "AndroidDeviceMemory.cs", "DeviceMemoryProbe.cs" })
        {
            Assert.Contains("DeviceProbe.PlatformFlashReadProbe = () => CircleAI.Inference.Managed.ManagedChatGenerator.ComputesFromFlashHere;",
                Code("src", "CircleAI", "Device", probe), StringComparison.Ordinal);
        }
    }

    // A source file of the Android head, its comments taken out: a rule named in a comment is not a rule that runs.
    private static string Code(params string[] parts) =>
        System.Text.RegularExpressions.Regex.Replace(System.IO.File.ReadAllText(Source(parts)), @"^[ \t]*///?.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);

    // Where a file of the repository is; the guard fails when it has moved.
    private static string Source(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "CircleAI.sln")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var path = System.IO.Path.Combine(dir!, System.IO.Path.Combine(parts));
        Assert.True(System.IO.File.Exists(path), path + " has moved - this guard points at nothing");
        return path;
    }

    // The worker keeps the brain it holds when the same one is asked for again - and a brain loaded for its index, built
    // small and not warmed, is not the brain that answers: asked to answer, it is built again.
    [Fact]
    public void The_worker_builds_the_brain_again_when_it_is_asked_for_in_another_shape_or_to_answer()
    {
        var forIndex = new WorkerLoadAsk(TwentySevenB, "/m", true, null, null, BrainInWaiting.ForItsIndexContextShare, ForItsIndex: true);

        Assert.True(WorkerWire.SameBrain(forIndex, forIndex));
        Assert.True(WorkerWire.SameBrain(forIndex, forIndex with { SkipHash = false, Directory = "/elsewhere" }));   // neither is the brain's shape
        Assert.False(WorkerWire.SameBrain(forIndex, forIndex with { ForItsIndex = false }));
        Assert.False(WorkerWire.SameBrain(forIndex, forIndex with { ForItsIndex = false, ContextShare = 1.0 }));
        Assert.False(WorkerWire.SameBrain(forIndex, forIndex with { Model = TwoB }));
        Assert.False(WorkerWire.SameBrain(forIndex, forIndex with { KvMode = KvCompressionMode.TurboQuant3Bit }));
        Assert.False(WorkerWire.SameBrain(forIndex, forIndex with { Engine = ModelEngine.Managed }));
        Assert.True(WorkerWire.SameBrain(forIndex, forIndex with { ContextShare = BrainInWaiting.ForItsIndexContextShare + 0.0001 }));
    }
}
