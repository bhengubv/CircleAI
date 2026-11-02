// DeviceWolverineTests.cs
//
// Wolverine on the device, started and fed (S6): a HealthEvent raised anywhere reaches the loop
// and the outbox; a rule's remedy is applied, checked, and pending until an answer proves it; the
// same failure returning disproves it and the ladder moves on; the device remembers which worked;
// a knob back at full that worked is forgotten; the link's rows carry no message; the dial, the
// reports and the endpoint are switched over the link, the endpoint by our own apps only.

using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Hosting.SelfHealing.Upstream;
using CircleAI.Inference;
using CircleAI.Linking;
using CircleAI.SelfKnowledge;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

[Collection(nameof(WolverineCollection))]
public sealed class DeviceWolverineTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TempDir("wolverine");
    private readonly WolverineClock _clock = new(Noon);
    private readonly WolverineLog _log = new();
    private readonly WolverinePolicy _policy = new();
    private readonly List<string> _said = [];
    private readonly List<DeviceWolverine> _built = [];
    private string? _autonomy;

    public DeviceWolverineTests() => Reset();

    public void Dispose()
    {
        foreach (var w in _built) w.Dispose();
        Reset();
        Delete(_dir);
    }

    private DeviceWolverine Build(WolverineOutbox? outbox = null, Func<string, bool>? redownload = null, bool start = true)
    {
        var w = new DeviceWolverine(new WolverineOptions
        {
            StateDirectory = _dir,
            Policy = _policy,
            Log = _log,
            Announce = _said.Add,
            FullThreads = 3,
            HasAnotherBrain = _ => true,
            Redownload = redownload,
            ReadAutonomy = _ => Task.FromResult(_autonomy),
            WriteAutonomy = (v, _) => { _autonomy = v; return Task.CompletedTask; },
            Outbox = outbox,
            FlushEvery = TimeSpan.Zero,
            TakeEarlierEvents = false,
            Clock = _clock,
        });
        _built.Add(w);
        if (start) w.Start();
        return w;
    }

    private WolverineOutbox Outbox() => new(Path.Combine(_dir, "outbox"), online: () => true, clock: _clock);

    private static HealthEvent OomAnswering() => Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode);

    [Fact]
    public async Task A_brain_short_of_memory_while_answering_gets_fewer_bits_quietly_and_the_remedy_waits_for_its_proof()
    {
        var w = Build();
        var e = OomAnswering();

        var record = await w.HandleAsync(e);

        Assert.Equal(HealingOutcome.Healed, record!.Outcome);
        Assert.Equal(Remedies.FewerMemoryBits, record.Category);
        Assert.Equal(KvCompressionMode.TurboQuant3Bit, BrainTuning.Current(Noon).WorkingMemory);
        Assert.Empty(_said);   // nobody would notice it
        Assert.Equal(Remedies.FewerMemoryBits, w.PendingRemedies[e.Fingerprint]);
    }

    [Fact]
    public async Task A_health_event_raised_anywhere_reaches_the_loop()
    {
        Build();
        var e = Engine("IllegalInstruction", WorkPhase.Decode);

        HealthEvents.Raise(e);

        await Eventually.TrueAsync(() => Task.FromResult(_log.Records.Any(r => r.Signature == e.Fingerprint)), "the raised event to be healed");
        Assert.True(BrainTuning.Current(Noon).ScalarKernels);
        Assert.Contains(WolverineSentences.ScalarKernels, _said);
    }

    [Fact]
    public async Task An_answer_after_the_remedy_proves_it_and_the_device_remembers_what_worked()
    {
        var memory = new CircleAI.Memory.MemoryService(Path.Combine(_dir, "self"));
        var self = SelfModel.Current = new SelfModel(memory);
        var w = Build();
        var e = OomAnswering();

        await w.HandleAsync(e);
        w.BrainAnswered(Model);

        Assert.Empty(w.PendingRemedies);
        Assert.Equal(Remedies.FewerMemoryBits, self.RemedyThatWorked(e.Fingerprint));

        // AND IT IS LONG-TERM: a new run reads it back from the memory.
        await Eventually.TrueAsync(async () => (await memory.AllAsync(500)).Any(a => SelfSubjects.IsAboutRemedy(a.Subject)),
            "the remedy's atom to land");
        var next = new SelfModel(memory);
        await next.LoadAsync();
        Assert.Equal(Remedies.FewerMemoryBits, next.RemedyThatWorked(e.Fingerprint));
        (memory as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task The_same_failure_returning_disproves_the_remedy_and_the_ladder_goes_on()
    {
        var self = SelfModel.Current = new SelfModel(new CircleAI.Memory.MemoryService(Path.Combine(_dir, "self")));
        var w = Build();

        var first = await w.HandleAsync(OomAnswering());    // 3 bits
        var second = await w.HandleAsync(OomAnswering());   // did not work: 2 bits
        var third = await w.HandleAsync(OomAnswering());    // bits at their floor: a shorter context

        Assert.Equal(Remedies.FewerMemoryBits, first!.Category);
        Assert.Equal(Remedies.FewerMemoryBits, second!.Category);
        Assert.Equal(Remedies.ShorterContext, third!.Category);
        var tuning = BrainTuning.Current(Noon);
        Assert.Equal(KvCompressionMode.TurboQuant2Bit, tuning.WorkingMemory);
        Assert.Equal(2048, tuning.MaxContext);
        Assert.Contains(self.RemediesFor(OomAnswering().Fingerprint), r => r.Remedy == Remedies.FewerMemoryBits && r.Failed == 2);
    }

    [Fact]
    public async Task A_knob_back_at_full_that_worked_is_forgotten()
    {
        var w = Build();
        await w.HandleAsync(Engine("IllegalInstruction", WorkPhase.Decode));
        Assert.Equal(1, HealingKnobs.Trips(SelfKnowledge.SelfHeal.Switch.VectorKernels));

        w.BrainAnswered(Model);   // still inside the window: the remedy is proven, the count is kept
        Assert.Equal(1, HealingKnobs.Trips(SelfKnowledge.SelfHeal.Switch.VectorKernels));

        _clock.Now = Noon + SelfKnowledge.SelfHeal.OffFor(1) + TimeSpan.FromMinutes(1);
        w.BrainAnswered(Model);   // back at full by the clock, and it answered
        Assert.Equal(0, HealingKnobs.Trips(SelfKnowledge.SelfHeal.Switch.VectorKernels));
    }

    [Fact]
    public async Task A_failure_no_rule_covers_is_recorded_and_still_goes_upstream()
    {
        using var outbox = Outbox();
        var w = Build(outbox);
        var e = Engine(nameof(FailureKind.Unknown), WorkPhase.Load);

        var record = await w.HandleAsync(e);

        Assert.Equal(HealingOutcome.Recorded, record!.Outcome);
        Assert.Contains(outbox.Pending(), r => r.Fingerprint == e.Fingerprint);
        Assert.True(BrainTuning.Current(Noon).IsNone);
    }

    [Fact]
    public async Task A_landed_download_proves_the_redownload()
    {
        var self = SelfModel.Current = new SelfModel(new CircleAI.Memory.MemoryService(Path.Combine(_dir, "self")));
        var fetched = new List<string>();
        var w = Build(redownload: m => { fetched.Add(m); return true; });
        var e = Download(nameof(ModelIntegrityException));

        var record = await w.HandleAsync(e);
        Assert.Equal(Remedies.Redownload, record!.Category);
        Assert.Equal(new[] { Model }, fetched);

        w.ModelLanded(Model);
        Assert.Equal(Remedies.Redownload, self.RemedyThatWorked(e.Fingerprint));
    }

    [Fact]
    public async Task The_links_rows_say_what_is_down_and_carry_no_message()
    {
        var w = Build();
        var e = HealthEvents.From("Voice", "speak",
            new OutOfMemoryException("could not voice my bank pin is 4321 for Thandiwe"));
        await w.HandleAsync(e);
        await w.HandleAsync(Engine("IllegalInstruction", WorkPhase.Decode));

        var rows = await w.RowsAsync(20);

        Assert.Equal(LinkHealingRows.Summary, LinkHealingRows.KindOf(rows[0]));
        Assert.Contains(rows, r => LinkHealingRows.KindOf(r) == LinkHealingRows.Knob && r[1] == BrainTuning.Knobs.Kernels);
        Assert.Contains(rows, r => LinkHealingRows.KindOf(r) == LinkHealingRows.Switch && r[1] == SelfKnowledge.SelfHeal.Switch.VectorKernels && r[2] == "Red");
        Assert.Equal(2, rows.Count(r => LinkHealingRows.KindOf(r) == LinkHealingRows.Item));
        var all = string.Join("|", rows.SelectMany(r => r));
        Assert.DoesNotContain("bank", all);
        Assert.DoesNotContain("4321", all);
        Assert.DoesNotContain("Thandiwe", all);
    }

    [Fact]
    public async Task The_dial_the_reports_and_the_endpoint_are_switched_over_the_link()
    {
        using var outbox = Outbox();
        var w = Build(outbox);

        Assert.Null(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgAutonomy] = AutonomyLevels.SuggestOnly }, firstParty: true));
        Assert.Equal(AutonomyLevels.SuggestOnly, _autonomy);
        Assert.NotNull(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgAutonomy] = "whatever" }, firstParty: true));

        Assert.NotNull(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgEndpoint] = "https://wolverine.example/api/device-reports" }, firstParty: false));
        Assert.Null(outbox.Endpoint);
        Assert.NotNull(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgEndpoint] = "http://insecure.example/" }, firstParty: true));
        Assert.Null(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgEndpoint] = "https://wolverine.example/api/device-reports" }, firstParty: true));
        Assert.NotNull(outbox.Endpoint);

        Assert.Null(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgReports] = "off" }, firstParty: false));
        Assert.False(outbox.Enabled);
        Assert.Contains(WolverineSentences.ReportsOff, _said);

        var summary = (await w.RowsAsync(0))[0];
        Assert.Equal(AutonomyLevels.SuggestOnly, summary[1]);
        Assert.Equal("0", summary[2]);
        Assert.Equal("1", summary[4]);
    }

    [Fact]
    public void The_person_is_told_once_that_reports_are_on()
    {
        using var outbox = Outbox();
        Build(outbox);
        Assert.Equal(new[] { WolverineSentences.ReportsOn }, _said);

        using var again = new WolverineOutbox(Path.Combine(_dir, "outbox"), online: () => true, clock: _clock);
        Build(again);
        Assert.Single(_said);
    }

    [Fact]
    public async Task A_self_check_runs_the_real_loop()
    {
        var w = Build();

        Assert.Null(await w.SetAsync(new Dictionary<string, string> { [LinkHealingRows.ArgSelfCheck] = "1" }, firstParty: true));

        Assert.Contains(_log.Records, r => r.Source == "self-check");
    }

    [Fact]
    public async Task A_remedy_run_in_place_is_remembered_with_its_outcome()
    {
        var self = SelfModel.Current = new SelfModel(new CircleAI.Memory.MemoryService(Path.Combine(_dir, "self")));
        var w = Build();
        var e = HealthEvents.Of(HealingRules.ToolsArea, "tool call", "MalformedToolCall", "AIService.AgenticChatAsync")
            with { Remedy = Remedies.ToolCallRetry, Worked = true };

        var record = await w.HandleAsync(e);

        Assert.Equal(HealingOutcome.Healed, record!.Outcome);
        Assert.Equal(Remedies.ToolCallRetry, self.RemedyThatWorked(e.Fingerprint));
    }
}
