// TheIndexOnTheEnginesIdleClockTests.cs
//
// THE ENGINE'S OWN WIRING OF THE INDEXED MIND, RUN HERE. The owner, 2026-11-13, of what had run on the P30 only -
// "Engine wiring in ManagedChatGenerator", "Flash-read and low-memory hooks: proven by the phone's log, no unit test",
// "Takeover end to end: no index has passed" - "Create and add tests - They should be green".
//
// IndexingQueueTests runs the queue at the engine's classes. These run the generator itself: its idle clock asking the
// phone whether it is idle, its units standing aside, its check judging the index, a passed index serving the next turn
// and the next load - and what the service's rules then make of the record it left.
//
// HOW IT RUNS ON A PC. The generator's index needs three things a desktop does not give it:
//   - the vector kernel's layout, which the runtime has only on ARM: LanguageModel.PairedForTest takes that path here;
//   - a model it can open - a tokenizer and a chat template: SyntheticQwen35 with speaks;
//   - a phone's readings - memory, the low-memory line, power, the screen: given here in the phone's place.
// All three are read by the whole process, so these tests run alone (TheEngineAloneCollection), never beside another.
//
// THE OWNER'S WORDS EACH TEST CHECKS: "We queue the indexing for whenever the phone is idle - best at bedtime but runs
// whenever it can" (2026-11-12); "no p30 is idle right? so it should be running" (2026-11-13); "REPORT ONLY PASS OR FAIL
// AGAINST A MEASUREMENT"; "without harming the phone".

using System.Runtime.CompilerServices;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Core.Logging;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Store;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

/// <summary>Tests that set what the whole process reads - the engine's layout switch, the phone's readings - and so run alone.</summary>
[CollectionDefinition(nameof(TheEngineAloneCollection), DisableParallelization = true)]
public sealed class TheEngineAloneCollection;

[Collection(nameof(TheEngineAloneCollection))]
public sealed class TheIndexOnTheEnginesIdleClockTests : IDisposable
{
    private const int Layers = 4;
    private const long Mb = 1L << 20;

    private readonly ITestOutputHelper _output;
    private readonly List<string> _said = [];
    private readonly IDisposable _listening;
    private readonly string _root = Directory.CreateTempSubdirectory("engine-idle-").FullName;
    private readonly string _store;
    private readonly string _model;

    // WHAT THE PHONE SAYS OF ITSELF, as each test sets it. To begin with: too little memory for every layer to stay warm,
    // so they are read from flash (the P30 with the 27B); on charge and cool; its screen dark; and its system beginning
    // to end other apps' processes under 64 MB available.
    private static readonly PhoneMemory Enough = new(100 * Mb, null, null);
    private static readonly PhoneMemory Short = new(70 * Mb, null, null);    // within a quarter of the 64 MB line
    private static readonly PowerState OnCharge = new(80, true, false);
    private static readonly PowerState OffCharge = new(80, false, false);
    private PhoneMemory _memory = Enough;
    private PowerState _power = OnCharge;
    private bool? _inUse = false;
    private long? _line = 64 * Mb;

    // What the process read before, put back when the test is done.
    private readonly bool _paired = LanguageModel.PairedForTest;
    private readonly Func<PhoneMemory>? _reading = MemoryNow.ReadingForTest;
    private readonly Func<PowerState>? _powerProbe = DeviceContext.PowerProbe;
    private readonly Func<bool?>? _inUseProbe = DeviceContext.InUseProbe;
    private readonly Func<long?>? _lineProbe = DeviceContext.LowMemoryLineProbe;
    private readonly Func<bool>? _flashProbe = DeviceProbe.PlatformFlashReadProbe;

    public TheIndexOnTheEnginesIdleClockTests(ITestOutputHelper output)
    {
        _output = output;
        _store = Directory.CreateDirectory(Path.Combine(_root, "store")).FullName;
        _model = Path.Combine(_root, "The-Model-PTQ1_0.gguf");
        File.WriteAllBytes(_model, ModelOf(Agrees));

        LanguageModel.PairedForTest = true;
        MemoryNow.ReadingForTest = () => _memory;
        DeviceContext.PowerProbe = () => _power;
        DeviceContext.InUseProbe = () => _inUse;
        DeviceContext.LowMemoryLineProbe = () => _line;
        _listening = CircleLog.AddSink(new Listening(_said));
    }

    public void Dispose()
    {
        _listening.Dispose();
        LanguageModel.PairedForTest = _paired;
        MemoryNow.ReadingForTest = _reading;
        DeviceContext.PowerProbe = _powerProbe;
        DeviceContext.InUseProbe = _inUseProbe;
        DeviceContext.LowMemoryLineProbe = _lineProbe;
        DeviceProbe.PlatformFlashReadProbe = _flashProbe;
        lock (_said)
        {
            foreach (var line in _said.Where(l => l.StartsWith("index:", StringComparison.Ordinal) || l.StartsWith("layers", StringComparison.Ordinal)))
                _output.WriteLine(line);
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Listening(List<string> said) : ICircleLogSink
    {
        public void Write(in CircleLogLine line)
        {
            lock (said) said.Add(line.Message);
        }
    }

    // ---- what the tests are made of ----

    // TWO MODELS, BY THEIR SEEDS, MEASURED WHEN THESE TESTS WERE WRITTEN (2026-11-13, the engine's own check over its 32
    // held-out words). Their weights are random, so their words mean nothing; what differs is how much an index costs
    // them. One gives its own top word every time from an index of every neuron (1,000 per thousand) and 969 from lists
    // of 15%: its index passes with room to spare. The other gives 656 from lists of half its neurons: its index fails.
    private const int Agrees = 502;
    private const int Disagrees = 501;

    // A model that speaks, its ffn_down folded as Bonsai 2's files fold it.
    private static byte[] ModelOf(int seed) =>
        SyntheticQwen35.Build(GgmlType.PTQ1_0, seed: seed, layers: Layers, folded: ["ffn_down"], speaks: true);

    private string ModelFile => Path.GetFileName(_model);

    private ManagedChatGenerator Load(string? model = null, string? store = null) =>
        ManagedChatGenerator.Load(model ?? _model,
            new ManagedGeneratorOptions { ContextLength = 512, Threads = 2, StoreDirectory = store ?? _store, PrimedStartDirectory = "" });

    // What the engine said that has all of these words in it.
    private string[] Said(params string[] parts)
    {
        lock (_said) return [.. _said.Where(l => parts.All(p => l.Contains(p, StringComparison.Ordinal)))];
    }

    // How many lines the engine has said so far; and what it said after that many that has all of these words in it.
    private int SaidSoFar()
    {
        lock (_said) return _said.Count;
    }

    private string[] SaidSince(int from, params string[] parts)
    {
        lock (_said) return [.. _said.Skip(from).Where(l => parts.All(p => l.Contains(p, StringComparison.Ordinal)))];
    }

    // The stores' start-up work is done, and nobody has asked the engine anything for longer than the indexing waits.
    private static void LeaveAlone(ManagedChatGenerator engine)
    {
        Assert.True(engine.StoreWork is null || engine.StoreWork.Wait(TimeSpan.FromSeconds(60)), "the stores' start-up work did not finish in a minute");
        engine.LastAskedForTest(IndexingWhen.Quiet + TimeSpan.FromSeconds(5));
    }

    // The idle clock's ticks, until what is waited for or two minutes have gone.
    private static void TickUntil(ManagedChatGenerator engine, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (!done() && DateTime.UtcNow < until)
        {
            engine.IdleNow();
            if (!done()) Thread.Sleep(10);
        }
    }

    // An index asked for by name, as the phone's developer hook asks: the groups a layer and the share of a layer's
    // neurons in each list, in the control file the idle clock reads.
    private void AskForAnIndex(int groups, int percent) =>
        File.WriteAllText(Path.Combine(_store, "index.txt"), FormattableString.Invariant($"{groups},{percent}"));

    private IndexingStatus? Recorded() => IndexingQueue.ReadStatus(_store, ModelFile);

    // What is on flash of a model's index, beside its record: Watch's records, the core index, step 2's pieces and tiled
    // layers, a cached portion an older engine left, the count of its use - and any write of one that was stopped.
    private static string[] IndexLeft(string store, string modelFile) =>
        [.. new[] { "watch", "index", "core", "mix", "down", "cached", "use" }
            .SelectMany(kind => new[] { $"{modelFile}.{kind}", $"{modelFile}.{kind}.partial" })
            .Where(name => File.Exists(Path.Combine(store, name)))];

    private static int Done(ManagedChatGenerator engine) => engine.Indexing?.Done ?? -1;

    private static GenerationOptions AFewWords => new() { Temperature = 0, MaxTokens = 4, Budget = PowerBudget.None };

    private static GenerationOptions SomeWords => new() { Temperature = 0, MaxTokens = 12, Budget = PowerBudget.None };

    private static readonly ChatMessage[] Hello = [new("user", "Hello")];

    // The turns the index served so far: the engine says, after a turn, where its pieces came from.
    private int ServedFromTheIndex() => Said("index: pieces", "from step 4 in RAM", "read by step 2 when a layer asked").Length;

    // The engine has an index of every neuron, asked for by name, built and judged; what its check measured.
    private IndexingStatus BuildAnIndexOfEveryNeuron(ManagedChatGenerator engine) => BuildAnIndex(engine, groups: 2, percent: 100);

    // The engine has the index asked for by name, built and judged; what its check measured.
    private IndexingStatus BuildAnIndex(ManagedChatGenerator engine, int groups, int percent)
    {
        LeaveAlone(engine);
        AskForAnIndex(groups, percent);
        engine.IdleNow();
        var built = engine.Indexing;
        Assert.True(built is { Complete: true }, $"asked for by name, the indexing says {built}; the engine said: {string.Join(" | ", Said("index:").TakeLast(3))}");
        return built!.Value;
    }

    // ---- "We queue the indexing for whenever the phone is idle - best at bedtime" ----

    // A model whose layers are read from flash has its index begun at load and built by the engine's own idle clock: at
    // bedtime - idle and on charge - unit after unit to the check, each recorded on flash where any process reads it.
    [Fact]
    public void With_its_layers_read_from_flash_the_engine_builds_its_index_while_the_phone_is_idle_and_judges_it()
    {
        using var engine = Load();

        var begun = engine.Indexing;
        Assert.True(begun is { Done: 0, Total: > Layers + 3 }, $"at load the engine's indexing says {begun}");
        Assert.Equal(begun, Recorded());
        Assert.False(engine.IndexAdopted);

        LeaveAlone(engine);
        engine.IdleNow();

        var done = engine.Indexing!.Value;
        Assert.True(done.Complete, $"after a bedtime tick the indexing says {done}; the engine said: {string.Join(" | ", Said("index:").TakeLast(3))}");
        Assert.True(done.Agreement is >= 0 and <= 1000, "the check ran and kept no measure");
        Assert.Equal(done, Recorded());

        // Every unit was said as it was done, in the queue's order, and the layers were read from flash for them.
        Assert.Equal(done.Total - Layers - 3, Said("index: Watch reads passage").Length);
        Assert.Single(Said("index: the core index is built from what Watch saw"));
        Assert.Equal(Layers, Said("index: layer", "pieces go to flash").Length);
        Assert.Single(Said("index: step 2's layers go to flash, tiled"));
        Assert.NotEmpty(Said("layers:", "read from flash"));

        // PASS OR FAIL AGAINST THE MEASUREMENT, said once, and the engine serves the index only if it passed.
        var verdict = Assert.Single(Said("index: the check over", "an index must reach 90% to serve"));
        Assert.Equal(done.Serves, verdict.Contains(": PASS;", StringComparison.Ordinal));
        Assert.Equal(!done.Serves, verdict.Contains(": FAIL;", StringComparison.Ordinal));
        Assert.Equal(done.Serves, engine.IndexAdopted);

        // "Without harming the phone": while a unit read the model its memory was watched and said - the heap, the
        // process, and what the phone has available.
        Assert.NotEmpty(Said("index: memory at layer 0: the managed heap", "the phone has 100 MB available"));

        // The engine's own share lists too few neurons for the layers' own ffn_down to be the fewer bytes: its pieces
        // serve with their down, and the stream brings the mixing alone.
        Assert.Empty(Said("each piece's gate and up alone"));
        Assert.NotEmpty(Said("layers:", "layers warm", "the mixing alone"));
    }

    // "RAM is the turbo": a model whose layers all stay warm needs no index, and none is built - until one is asked for
    // by name, which is how a phone's developer hook asks.
    [Fact]
    public void Where_every_layer_stays_warm_no_index_is_built_until_one_is_asked_for_by_name()
    {
        _memory = new(4096 * Mb, null, null);
        using var engine = Load();

        Assert.Null(engine.Indexing);
        Assert.Null(Recorded());
        Assert.Single(Said("index: every layer fits in RAM here, so none is built"));
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Null(engine.Indexing);   // an idle phone changes nothing: there is no queue

        var built = BuildAnIndexOfEveryNeuron(engine);

        Assert.False(File.Exists(Path.Combine(_store, "index.txt")), "the request was not taken");
        Assert.Equal(built, Recorded());
        Assert.Equal((2, SyntheticQwen35.FeedForward), AskedFor());

        // "RAM is the turbo": with memory to spare, step 4 is read into RAM when the index serves.
        Assert.True(built.Serves, $"an index of every neuron measured {built.Agreement} per thousand");
        Assert.Contains(Said("index: 2 thought groups a layer, lists of 384 of 384 neurons; step 4 holds", "in RAM"),
            line => !line.Contains("step 4 holds 0 pieces", StringComparison.Ordinal));
    }

    // OFF ITS CHARGER THE PHONE ANSWERS AND BUILDS NOTHING (the owner, 2026-11-15: "if we only perform while tethered then
    // we are not performing"; "we should be able to tap in and out without anything crashing out"): idle off its charger
    // no unit begins, however long it is left, and a turn is answered; back on its charger the work goes on.
    [Fact]
    public async Task Idle_off_its_charger_the_engine_answers_and_builds_nothing()
    {
        _power = OffCharge;
        using var engine = Load();
        LeaveAlone(engine);

        for (var tick = 0; tick < 3; tick++) engine.IdleNow();
        Assert.Equal(0, Done(engine));
        Assert.Empty(Said("index: Watch reads passage"));
        Assert.NotNull(await engine.GenerateAsync(Hello, AFewWords));

        _power = OnCharge;
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.True(Done(engine) >= 1, $"back on its charger the indexing says {engine.Indexing}");
    }

    // A UNIT RUNNING WHEN THE PHONE COMES OFF ITS CHARGER STANDS ASIDE, records nothing and says why - asked for by name or
    // not - and the queue goes on from its start when the phone is back on it. (The engine asks the phone at most once a
    // second while a unit runs.)
    [Fact]
    public void A_unit_stands_aside_the_moment_the_phone_comes_off_its_charger_even_one_asked_for_by_name()
    {
        using var engine = Load();
        LeaveAlone(engine);
        var asked = 0;
        DeviceContext.PowerProbe = () => ++asked > 1 ? OffCharge : OnCharge;   // on its cable when the clock looks; off it when the unit asks

        engine.IdleNow();
        Assert.Equal(0, Done(engine));
        Assert.Equal("the phone is off its charger", engine.StoodAsideWhy);
        Assert.Single(Said("index: stood aside after", "Watch reads passage 1 of", "(the phone is off its charger)"));

        // Asked for by name - built with the screen on or off - it stands aside off its charger all the same.
        DeviceContext.PowerProbe = () => OffCharge;
        Thread.Sleep(1100);
        AskForAnIndex(groups: 2, percent: 100);
        engine.IdleNow();
        Assert.Equal(0, Done(engine));
        Assert.Equal(2, Said("index: stood aside after", "(the phone is off its charger)").Length);

        // Back on its charger, the queue in hand goes on.
        DeviceContext.PowerProbe = () => OnCharge;
        Thread.Sleep(1100);
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.True(Done(engine) >= 1, $"back on its charger the indexing says {engine.Indexing}");
        Assert.Equal((2, SyntheticQwen35.FeedForward), AskedFor());
    }

    // ---- "whenever the phone is idle" - and "without harming the phone" ----

    // No unit begins while somebody is using the phone, while it is hot, low or off its charger, while it is short of
    // memory by its own line, or while somebody was asking the engine a moment ago. A screen that does not say holds
    // nothing back.
    [Fact]
    public void No_unit_begins_until_the_phone_is_idle_cool_charged_has_memory_to_spare_and_nobody_is_asking()
    {
        _power = OnCharge;
        using var engine = Load();
        LeaveAlone(engine);
        void Waits(string why)
        {
            engine.IdleNow();
            Assert.True(Done(engine) == 0, $"a unit of indexing began while {why}");
        }

        _inUse = true;
        Waits("somebody is using the phone");
        _inUse = false;

        _power = new PowerState(80, true, true);
        Waits("the phone is hot");
        _power = new PowerState(15, false, false);
        Waits("its battery is low");
        _power = OffCharge;
        Waits("the phone is off its charger");
        _power = OnCharge;

        _memory = Short;
        Waits("it has 70 MB available and ends other apps' processes under 64 MB");
        _memory = Enough;

        // The same 100 MB on a phone that gives no line of its own is under the least there must be.
        _line = null;
        Waits("it has 100 MB available and gives no low-memory line");
        DeviceContext.LowMemoryLineProbe = () => throw new InvalidOperationException("the phone will not say");
        Waits("it has 100 MB available and will not say its low-memory line");
        DeviceContext.LowMemoryLineProbe = () => _line;
        _line = 64 * Mb;

        engine.LastAskedForTest(TimeSpan.FromSeconds(5));
        Waits("somebody asked the engine something five seconds ago");
        Assert.Empty(Said("index: Watch reads passage"));

        // "no p30 is idle right? so it should be running": lit by Stay awake on its cable, the screen does not say, and
        // with nobody asking the phone is idle.
        _inUse = null;
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.True(Done(engine) >= 1, $"idle on its cable the indexing says {engine.Indexing}");
    }

    // THE PHONE IS PICKED UP AS A UNIT BEGINS: the unit stands aside, records nothing, and says why. Put down again, it
    // runs from its start. (The engine asks the phone at most once a second while a unit runs.)
    [Fact]
    public void A_unit_stands_aside_the_moment_the_phone_is_picked_up_and_runs_again_when_it_is_put_down()
    {
        _power = OnCharge;
        using var engine = Load();
        LeaveAlone(engine);
        var asked = 0;
        DeviceContext.InUseProbe = () => ++asked > 1;   // idle when the clock looks; in a hand when the unit asks

        engine.IdleNow();

        Assert.Equal(0, Done(engine));
        Assert.Equal("the phone is in use", engine.StoodAsideWhy);
        Assert.Single(Said("index: stood aside after", "Watch reads passage 1 of", "(the phone is in use)"));
        Assert.Equal(engine.Indexing, Recorded());

        DeviceContext.InUseProbe = () => false;
        Thread.Sleep(1100);
        engine.IdleNow();
        Assert.True(Done(engine) >= 1, $"put down again the indexing says {engine.Indexing}");
    }

    // "WITHOUT HARMING THE PHONE": a unit that finds the phone short of memory stands aside, gives its buffers back, and
    // is not tried again for ten minutes - memory back or not.
    [Fact]
    public void A_unit_that_finds_the_phone_short_of_memory_gives_way_and_is_left_alone_for_a_while()
    {
        _power = OnCharge;
        using var engine = Load();
        LeaveAlone(engine);
        var looked = 0;
        MemoryNow.ReadingForTest = () => ++looked > 1 ? Short : Enough;   // room when the clock looks; none when the unit asks

        engine.IdleNow();

        Assert.Equal(0, Done(engine));
        Assert.Equal("the phone is short of memory: 70 MB available, and it ends other apps' processes under 64 MB", engine.StoodAsideWhy);
        Assert.Single(Said("index: stood aside after", "(the phone is short of memory: 70 MB available"));

        MemoryNow.ReadingForTest = () => Enough;
        Thread.Sleep(1100);
        engine.IdleNow();
        Assert.Equal(0, Done(engine));
        Assert.Empty(Said("index: Watch reads passage"));
    }

    // Asked for by name, the index is built with the phone in somebody's hand - that is what asking by name is for - and
    // heat still stops it.
    [Fact]
    public void An_index_asked_for_by_name_is_built_with_the_phone_in_use_and_still_stands_aside_for_heat()
    {
        using var engine = Load();
        _inUse = true;

        var built = BuildAnIndexOfEveryNeuron(engine);
        Assert.Equal((2, SyntheticQwen35.FeedForward), AskedFor());
        Assert.Empty(Said("index: stood aside"));

        // Another one is asked for, and the phone is hot: nothing of it is done.
        Thread.Sleep(1100);
        _power = new PowerState(80, true, true);
        AskForAnIndex(groups: 4, percent: 50);
        engine.IdleNow();

        Assert.Equal(0, Done(engine));
        Assert.Equal("the phone is hot", engine.StoodAsideWhy);
        Assert.Single(Said("index: stood aside after", "(the phone is hot)"));
        Assert.True(built.Complete);
    }

    // COHERENT THOUGHTS, ASKED FOR BY NAME (the owner, 2026-11-15: "We index thoughts so what we keep warm have to be
    // coherent"): the hook's third field asks for one thought a word, found at the first layer and kept through every
    // layer - and that is the index built, recorded and checked, and what serves says so.
    [Fact]
    public void An_index_of_coherent_thoughts_asked_for_by_name_is_built_coherent_and_checked()
    {
        using var engine = Load();
        LeaveAlone(engine);
        File.WriteAllText(Path.Combine(_store, "index.txt"), "2,50,coherent");
        engine.IdleNow();

        var built = engine.Indexing;
        Assert.True(built is { Complete: true, Agreement: not null }, $"asked for by name, the indexing says {built}; the engine said: {string.Join(" | ", Said("index:").TakeLast(3))}");
        Assert.NotEmpty(Said("index:", "2 coherent thoughts, a word's one thought found at the first layer and kept through every layer"));
        using (var model = GgufFile.Open(_model))
            Assert.True(IndexingQueue.Attempt(model, _store) is { Coherent: true, Groups: 2 }, "the record on flash is not of coherent thoughts");
        Assert.Single(Said("index: the check over", "an index must reach 90% to serve"));
        if (built!.Value.Serves)
            Assert.NotEmpty(Said("index: 2 coherent thoughts, a word's one thought found at the first layer and kept through every layer, lists of", "; step 4 holds "));
    }

    // ---- "REPORT ONLY PASS OR FAIL AGAINST A MEASUREMENT" - and the takeover ----

    // AN INDEX THAT PASSES ITS CHECK SERVES: the turn after it is answered from its pieces, and the next load finds it on
    // flash and serves from its first turn. The takeover, on the engine's side, that had never run anywhere. Lists of
    // half a layer's neurons: an index of every neuron gives way to the model's own file at the next load.
    [Fact]
    public async Task An_index_that_passed_its_check_serves_the_next_turn_and_the_next_load()
    {
        using (var engine = Load())
        {
            var built = BuildAnIndex(engine, groups: 2, percent: 50);
            Assert.True(built.Serves, $"an index of half the neurons measured {built.Agreement} per thousand, under the {IndexingStatus.LeastAgreement} it must reach");
            Assert.Single(Said("index: the check over", ": PASS;"));
            Assert.True(engine.IndexAdopted);
            Assert.Equal(ReadinessSentences.Learning, IndexReadiness.Read(_store, DateTimeOffset.UtcNow, ModelFile)?.Detail);

            // "we need to clean up after ourselves": Watch's records went once the core index was built from them, and
            // it is said. What serves is all still there.
            Assert.Single(Said("index: removed Watch's records, which the core index is built from", "given back to the flash"));
            var left = IndexLeft(_store, ModelFile);
            Assert.DoesNotContain(ModelFile + ".watch", left);
            Assert.Contains(ModelFile + ".index", left);
            Assert.Contains(ModelFile + ".core", left);

            var served = ServedFromTheIndex();
            var reply = await engine.GenerateAsync(Hello, AFewWords);
            Assert.NotNull(reply);
            Assert.True(ServedFromTheIndex() > served, "the turn after a passed check was not answered from the index's pieces");

            // "RAM is the turbo, not the engine": this phone has no memory to spare for a warm index, and the turn was
            // answered from the pieces on flash all the same - their gate and up alone, half a layer's neurons listed
            // being past the 42 in a hundred where the layers' own ffn_down is the fewer bytes (TakesItsOwnDown).
            Assert.NotEmpty(Said("index: 2 thought groups a layer, lists of", "of 384 neurons; step 4 holds 0 pieces", "each piece's gate and up alone"));
        }

        using var again = Load();
        Assert.True(again.Indexing is { Complete: true, Serves: true, FromItsOwnFile: false }, $"the next load says {again.Indexing}");
        Assert.True(again.IndexAdopted);
        Assert.Single(Said("index: complete -", "thought groups a layer"));
        var before = ServedFromTheIndex();
        await again.GenerateAsync(Hello, AFewWords);
        Assert.True(ServedFromTheIndex() > before, "the next load's first turn was not answered from the index's pieces");
        Assert.Single(Said("index: the check over"));   // judged once: the next load does not run the check again
    }

    // AND AN INDEX OF EVERY NEURON GIVES WAY AT THE NEXT LOAD, PASSED OR NOT - as the P30's 9B's did on 2026-11-15, built by
    // the engine before this rule, passed, and serving every word through its copies. The model's own file is every
    // neuron, exact, with fewer bytes a word and no flash of its own. Asked for by name, one serves that sitting; the next
    // load removes what it left and says so, builds nothing, judges nothing, and every turn computes every neuron from the
    // model's own file - and the record says that serves.
    [Fact]
    public async Task An_index_of_every_neuron_that_passed_gives_way_to_the_models_own_file_at_the_next_load()
    {
        using (var engine = Load())
        {
            var built = BuildAnIndexOfEveryNeuron(engine);
            Assert.True(built.Serves, $"an index of every neuron measured {built.Agreement} per thousand, under the {IndexingStatus.LeastAgreement} it must reach");
            Assert.True(engine.IndexAdopted);
        }
        Assert.Contains(ModelFile + ".core", IndexLeft(_store, ModelFile));
        var before = SaidSoFar();

        using var next = Load();

        Assert.True(next.Indexing is { Complete: true, Serves: true, FromItsOwnFile: true, EveryNeuron: true, Agreement: null }, $"the next load says {next.Indexing}");
        Assert.Equal(next.Indexing, Recorded());
        Assert.False(next.IndexAdopted);
        Assert.Single(SaidSince(before, "index: removed what an index of this model left", "given back to the flash"));
        Assert.Single(SaidSince(before, "index: none is built - an index of this model would list all 384 neurons of each layer"));
        Assert.Empty(SaidSince(before, "index: complete -"));
        Assert.Empty(IndexLeft(_store, ModelFile));

        var served = ServedFromTheIndex();
        await next.GenerateAsync(Hello, AFewWords);
        Assert.Equal(served, ServedFromTheIndex());
        var streamed = SaidSince(before, "layers:", "layers warm", "read from flash");   // how the layers' stream opened
        Assert.NotEmpty(streamed);
        Assert.All(streamed, line =>
        {
            Assert.Contains("from the model's own file", line, StringComparison.Ordinal);
            Assert.DoesNotContain("the mixing alone", line, StringComparison.Ordinal);
        });

        LeaveAlone(next);
        next.IdleNow();
        Assert.Single(Said("index: the check over"));   // the one that judged it, on the first load
        Assert.Empty(IndexLeft(_store, ModelFile));
    }

    // AN INDEX OF EVERY NEURON THAT FAILED ITS CHECK - with what the P30 measured, one word in four - IS NOT THE END OF ITS
    // BRAIN (the owner, 2026-11-15: "no models should be blocked", and of a brain that needs every neuron: "Yes, take
    // over"). It differs from every neuron only in its rounded copy of each neuron's output weights, so the next load
    // builds no index at all: what the failed one left goes, every neuron is computed from the model's own file, the
    // record says that serves, nothing is said to the person of an index, and the idle phone builds and checks nothing.
    [Fact]
    public async Task An_index_of_every_neuron_that_failed_its_check_is_followed_by_every_neuron_from_the_models_own_file()
    {
        using (var engine = Load())
            BuildAnIndexOfEveryNeuron(engine);
        var record = Path.Combine(_store, ModelFile + ".indexing");
        File.WriteAllLines(record, File.ReadAllLines(record).Select(line => line.StartsWith("agrees=", StringComparison.Ordinal) ? "agrees=250" : line));
        Assert.True(Recorded() is { Failed: true, NothingLeftToTry: true }, $"the record says {Recorded()}");
        Assert.NotEmpty(IndexLeft(_store, ModelFile));
        var before = SaidSoFar();

        using var next = Load();

        Assert.True(next.Indexing is { Complete: true, Serves: true, Failed: false, NothingLeftToTry: false, FromItsOwnFile: true, EveryNeuron: true, Agreement: null },
            $"the next load says {next.Indexing}");
        Assert.Equal(next.Indexing, Recorded());
        Assert.False(next.IndexAdopted);
        Assert.Single(Said("index: the last index listed every neuron and its check measured 25.0%: an index of every neuron differs from every neuron only in its copy of each neuron's output weights"));
        Assert.Single(Said("index: none is built - an index of this model would list all 384 neurons of each layer", "every neuron is computed from it, exact"));
        Assert.Empty(Said("the fault is this engine's"));
        Assert.Null(IndexReadiness.Read(_store, DateTimeOffset.UtcNow, ModelFile));

        // "they can go we need to clean up after ourselves or we will kill users phones": every file the failed index left
        // is gone, and it is said. The record stays: it is what says this brain computes every neuron from its own file.
        Assert.Single(Said("index: removed what an index of this model left", "given back to the flash"));
        Assert.Empty(IndexLeft(_store, ModelFile));
        Assert.True(File.Exists(record));

        // Its turns compute every neuron: no piece of an index serves them, and the layers' stream brings the whole
        // feed-forward from the model's own file - not the mixing alone.
        var served = ServedFromTheIndex();
        await next.GenerateAsync(Hello, AFewWords);
        Assert.Equal(served, ServedFromTheIndex());
        var streamed = SaidSince(before, "layers:", "layers warm", "read from flash");   // how the layers' stream opened
        Assert.NotEmpty(streamed);
        Assert.All(streamed, line =>
        {
            Assert.Contains("from the model's own file", line, StringComparison.Ordinal);
            Assert.DoesNotContain("the mixing alone", line, StringComparison.Ordinal);
        });

        // The idle phone has nothing to build and nothing to judge.
        LeaveAlone(next);
        next.IdleNow();
        Assert.Single(Said("index: the check over"));           // the one that judged the index of every neuron, on the first load
        Assert.Empty(IndexLeft(_store, ModelFile));
        Assert.Equal(next.Indexing, Recorded());
    }

    // THE SAME INDEX OF EVERY NEURON, FAILED, BUT MADE BY AN EARLIER ENGINE - as the P30's 4B on 2026-11-15, whose index of
    // every neuron measured 87.5% before the engine kept its stamp. What failed it was its copy of the output weights, and
    // this engine builds no index of every neuron: not the first share again - hours of the phone's idle time into the
    // failures it had before - but every neuron from the model's own file.
    [Fact]
    public void An_index_of_every_neuron_an_earlier_engine_failed_is_followed_by_every_neuron_from_the_models_own_file()
    {
        using (var engine = Load())
            BuildAnIndexOfEveryNeuron(engine);
        var record = Path.Combine(_store, ModelFile + ".indexing");
        File.WriteAllLines(record, File.ReadAllLines(record)
            .Where(line => !line.StartsWith("engine=", StringComparison.Ordinal) && !line.StartsWith("share=", StringComparison.Ordinal))
            .Select(line => line.StartsWith("agrees=", StringComparison.Ordinal) ? "agrees=875" : line));
        Assert.True(Recorded() is { Failed: true, NothingLeftToTry: false }, $"the record says {Recorded()}");

        using var next = Load();

        Assert.Single(Said("index: the last index listed every neuron and its check measured 87.5%, made by an earlier engine: an index of every neuron differs from every neuron only in its copy"));
        Assert.Empty(Said("this one builds it again from"));
        Assert.True(next.Indexing is { Complete: true, Serves: true, FromItsOwnFile: true, EveryNeuron: true }, $"the next load says {next.Indexing}");
        Assert.Equal(next.Indexing, Recorded());
        Assert.Contains($"\nengine={IndexingQueue.EngineVersion}\n", File.ReadAllText(record), StringComparison.Ordinal);
        Assert.Empty(IndexLeft(_store, ModelFile));
    }

    // AND WHEN THE ENGINE'S OWN CHECK SAYS FAIL - on this model, lists of half of every layer's neurons give its own top
    // word two times in three - the index it has just built is put down the moment it is measured: the next turn
    // computes every neuron, the person is told nothing of it, and every file of it goes. AND THEN THE REMEDY (the owner,
    // 2026-11-15: "if it fails then check why it failed. clean up and try again, everything depends on the reason why"):
    // too few neurons is the reason, so the engine goes to the next share at once and says why - and after half, that is
    // every neuron, for which no index is built (the owner, 2026-11-15: "Yes, take over"): every neuron is computed from
    // the model's own file, nothing goes to flash, and the brain is ready with nothing left for the idle phone to do.
    [Fact]
    public async Task An_index_the_engines_own_check_fails_is_put_down_and_the_next_share_follows_it()
    {
        var model = Path.Combine(_root, "Another-Model-PTQ1_0.gguf");
        File.WriteAllBytes(model, ModelOf(Disagrees));
        var store = Directory.CreateDirectory(Path.Combine(_root, "another-store")).FullName;
        var file = Path.GetFileName(model);
        using var engine = Load(model, store);
        LeaveAlone(engine);
        File.WriteAllText(Path.Combine(store, "index.txt"), "2,50");

        engine.IdleNow();

        Assert.Single(Said("index: the check over", ": FAIL;"));
        Assert.Single(Said("index: complete, and its check measured", "it does not serve, and every neuron is computed"));
        Assert.False(engine.IndexAdopted);

        // It leaves nothing behind: every file of it went the moment its check was on record, and the engine said how
        // much that gave back.
        Assert.Single(Said("index: removed every file of the index, which failed its check", "given back to the flash"));

        // The reason and the remedy, said: the next share is every neuron, and no index of it is built - its record, in
        // the failed one's place, says every neuron is computed from the model's own file, and that serves.
        Assert.Single(Said("index: the last index listed 50% of each layer's neurons and its check measured", "under the 90% it must reach: too few neurons for this model, so the next lists 100%"));
        Assert.Single(Said("index: none is built - an index of this model would list all 384 neurons of each layer"));
        Assert.Empty(Said("index: 0 of", "1 thought groups a layer"));
        var next = engine.Indexing;
        Assert.True(next is { Complete: true, Serves: true, FromItsOwnFile: true, EveryNeuron: true }, $"after the failed check the engine's indexing says {next}");
        Assert.Equal(next, IndexingQueue.ReadStatus(store, file));
        Assert.Empty(IndexLeft(store, file));
        Assert.False(engine.IndexAdopted);

        var served = ServedFromTheIndex();
        await engine.GenerateAsync(Hello, AFewWords);
        Assert.Equal(served, ServedFromTheIndex());   // that turn computed every neuron

        // The phone's idle time has nothing to build and nothing to judge.
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Single(Said("index: the check over"));
        Assert.Equal(next, engine.Indexing);
        Assert.Empty(IndexLeft(store, file));
    }

    // AN INDEX OF EVERY NEURON WAS NOT EVERY NEURON - ITS ROUNDED COPY OF THE OUTPUT WEIGHTS WAS THE ONE DIFFERENCE - AND
    // THE CLIMB NO LONGER BUILDS ONE. On these models an index of every neuron read from flash with the mixing alone gave
    // another top word than every neuron gives: 906, 938 and 906 per thousand on 2026-11-15. Served with the layers' own
    // ffn_down (TakesItsOwnDown: from 42 in a hundred listed, the model's own is the fewer bytes), the same index asked for
    // by name gives every neuron's top word every time. And where the climb comes to every neuron, a turn of the same model
    // reads every layer from the model's own file with its whole feed-forward - the layers' stream that computes what
    // layers in RAM compute, to the bit (LayerStreamTests, FfnStoreTests) - and no piece of an index serves it.
    [Theory]
    [InlineData(501)]
    [InlineData(504)]
    [InlineData(506)]
    public async Task Where_an_index_of_every_neuron_is_not_every_neuron_the_climb_reads_the_models_own_file(int seed)
    {
        var model = Path.Combine(_root, FormattableString.Invariant($"Seed-{seed}-PTQ1_0.gguf"));
        File.WriteAllBytes(model, ModelOf(seed));
        var asked = Directory.CreateDirectory(Path.Combine(_root, "asked")).FullName;
        var climbed = Directory.CreateDirectory(Path.Combine(_root, "climbed")).FullName;
        var file = Path.GetFileName(model);

        // Asked for by name, an index of every neuron is built as asked - and, its layers taking their own ffn_down, its
        // check measures every neuron's top word every time.
        int measured;
        var beforeAsked = SaidSoFar();
        using (var engine = Load(model, asked))
        {
            LeaveAlone(engine);
            File.WriteAllText(Path.Combine(asked, "index.txt"), "1,100");
            engine.IdleNow();
            Assert.True(engine.Indexing is { Complete: true, FromItsOwnFile: false, Agreement: not null }, $"asked for by name, the indexing says {engine.Indexing}");
            measured = engine.Indexing!.Value.Agreement!.Value;
        }
        Assert.Equal(1000, measured);
        Assert.NotEmpty(SaidSince(beforeAsked, "index: 1 thought groups a layer", "each piece's gate and up alone, the layers bringing their own ffn_down"));
        Assert.All(SaidSince(beforeAsked, "layers:", "layers warm"), line => Assert.DoesNotContain("the mixing alone", line, StringComparison.Ordinal));

        // The climb at every neuron, after an index of half its neurons failed: none is built, and a turn reads the
        // whole feed-forward from the model's own file.
        FailedAtHalf(climbed, model);
        var before = SaidSoFar();
        using var reads = Load(model, climbed);
        Assert.True(reads.Indexing is { Serves: true, FromItsOwnFile: true }, $"the engine's indexing says {reads.Indexing}");
        Assert.False(reads.IndexAdopted);
        var served = ServedFromTheIndex();
        var reply = await reads.GenerateResponseAsync(Hello, SomeWords);

        Assert.True(reply.TokensOut > 0, "the turn computed nothing");
        Assert.Equal(served, ServedFromTheIndex());
        var streamed = SaidSince(before, "layers:", "layers warm", "read from flash");   // how the layers' stream opened
        Assert.NotEmpty(streamed);
        Assert.All(streamed, line =>
        {
            Assert.Contains("from the model's own file", line, StringComparison.Ordinal);
            Assert.DoesNotContain("the mixing alone", line, StringComparison.Ordinal);
        });
        Assert.Empty(IndexLeft(climbed, file));
        _output.WriteLine(FormattableString.Invariant($"seed {seed}: an index of every neuron measured {measured} per thousand; the climb's turn read the whole feed-forward"));
    }

    // THE TAKEOVER WITH NO INDEX AT ALL, from the engine's own record to the service's rules: once the climb comes to every
    // neuron the best brain is ready with nothing built, and once it has answered here it has taken over - and only then
    // is the other the old LLM, and goes.
    [Fact]
    public void A_brain_that_needs_every_neuron_takes_over_from_its_own_file_and_only_then_does_the_other_go()
    {
        var phone = new DeviceProbe(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_152_384, StorageTotalBytes = 113_170_432_000, BrainsReadFromFlash = true };
        using var registry = new ModelRegistryService();
        var small = registry.GetLatestModel("Ternary-Bonsai-2-2B")!;
        var best = registry.GetLatestModel("Ternary-Bonsai-2-9B")! with
        {
            BundleFiles = [new BundleFile(ModelFile, "00", new FileInfo(_model).Length)],
        };
        Assert.True(ModelFit.AnswersThroughItsIndex(best, phone), "the 9B is not a brain this phone reads from flash");
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        foreach (var row in new[] { best, small })
        {
            cat.Upsert(row);
            cat.SetInstalled(row.Name, true);
            cat.SetAssessment(row.Name, compatible: true, rank: 1);
            cat.RecordLoad(row.Name);
        }
        double? answeredAt = null;
        IndexingStatus? Index(ModelEntry row) => BrainInWaiting.IndexOf(row, _store);
        IReadOnlyList<HousekeepingReclaim> Housekeeping() => CatalogueHousekeeping.Plan(cat,
            readFromFlash: row => ModelFit.ReadFromFlash(row, phone),
            ready: row => !ModelFit.ReadFromFlash(row, phone)
                          || BrainInWaiting.HasTakenOver(row, phone, Index(row), row.Name == best.Name ? answeredAt : null));

        // Its index of half its neurons failed its check: the 2B answers, the 9B waits, and nothing goes.
        FailedAtHalf(_store, _model);
        var waiting = BrainInWaiting.Plan(best, [small], phone, Index);
        Assert.Equal(small.Name, waiting.Answers.Name);
        Assert.Equal(best.Name, waiting.Waiting?.Name);
        Assert.Empty(Housekeeping());

        // Loaded, the engine goes to every neuron and builds nothing: the 9B is ready, and nothing waits.
        using (var engine = Load())
            Assert.True(engine.Indexing is { Serves: true, FromItsOwnFile: true }, $"the engine's indexing says {engine.Indexing}");
        Assert.True(Index(best) is { Serves: true, FromItsOwnFile: true }, $"the service reads {Index(best)}");
        Assert.True(BrainInWaiting.Ready(best, phone, Index(best)));
        Assert.Equal(StepState.Underway, BrainInWaiting.StepOf(best, phone, present: true, proven: true, Index(best), measuredSpeed: null));
        var ready = BrainInWaiting.Plan(best, [small], phone, Index);
        Assert.Equal(best.Name, ready.Answers.Name);
        Assert.True(ready.Waiting is null && ready.Said is null);
        Assert.Empty(Housekeeping());   // it has not answered here yet, so the 2B stays

        // It has answered here: it has taken over, the climb has passed its step, and the 2B is the old LLM.
        answeredAt = 0.4;
        Assert.Equal(StepState.Passed, BrainInWaiting.StepOf(best, phone, present: true, proven: true, Index(best), measuredSpeed: answeredAt));
        var gone = Assert.Single(Housekeeping());
        Assert.Equal(small.Name, gone.Id);
        Assert.Contains(best.Name, gone.Reason, StringComparison.Ordinal);
    }

    // The record an index of half a model's neurons leaves once its check has failed, as this engine writes it.
    private static void FailedAtHalf(string store, string model)
    {
        var name = Path.GetFileName(model);
        var length = new FileInfo(model).Length;
        File.WriteAllText(Path.Combine(store, IndexingQueue.RecordName(name)), FormattableString.Invariant(
            $"CAIINDEXING 1\nmodel={name}\nlength={length}\nrequest=0\ngroups={ManagedChatGenerator.IndexGroups}\nlist={SyntheticQwen35.FeedForward / 2}\nshare=50\nengine={IndexingQueue.EngineVersion}\nread=1048576\ncore=0\nshared=0\ndone=12\ntotal=12\nwatch=0\nagrees=656\n"));
    }

    // "REVISE (NIGHTLY)" - "The more you use it the more it learns what the user needs": what the day's turns used of the
    // index is revised into step 2's heat at bedtime - idle and on charge - by the engine's own clock, once a night.
    [Fact]
    public async Task What_the_days_turns_used_of_the_index_is_revised_at_bedtime_once_a_night()
    {
        using var engine = Load();
        Assert.True(BuildAnIndexOfEveryNeuron(engine).Serves);

        // Nothing has been used yet: bedtime has nothing to revise from. (The check's words are not the person's.)
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Empty(Said("index: revised from"));

        await engine.GenerateAsync(Hello, AFewWords);

        // Idle off its charger is not bedtime.
        _power = OffCharge;
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Empty(Said("index: revised from"));

        _power = OnCharge;
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Single(Said("index: revised from", "words of use", "pieces carry the day's heat"));

        // Once a night: more use and another idle hour the same night revise nothing more.
        await engine.GenerateAsync(Hello, AFewWords);
        LeaveAlone(engine);
        engine.IdleNow();
        Assert.Single(Said("index: revised from"));
    }

    // THE TAKEOVER END TO END, from the engine's own record to the service's rules: while the index is built the brain
    // the phone keeps in RAM answers and stays; once the engine's check has passed the best brain is ready; once it has
    // answered here it has taken over - and only then is the other the old LLM, and goes.
    [Fact]
    public void From_the_engines_own_record_the_best_brain_takes_over_and_only_then_does_the_other_go()
    {
        var phone = new DeviceProbe(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_152_384, StorageTotalBytes = 113_170_432_000, BrainsReadFromFlash = true };
        using var registry = new ModelRegistryService();
        var small = registry.GetLatestModel("Ternary-Bonsai-2-2B")!;
        // The brain this engine holds, as its catalogue row would say it: the 27B's size, and this file.
        var best = registry.GetLatestModel("Ternary-Bonsai-2-27B")! with
        {
            BundleFiles = [new BundleFile(ModelFile, "00", new FileInfo(_model).Length)],
        };
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        foreach (var row in new[] { best, small })
        {
            cat.Upsert(row);
            cat.SetInstalled(row.Name, true);
            cat.SetAssessment(row.Name, compatible: true, rank: 1);
            cat.RecordLoad(row.Name);
        }
        double? answeredAt = null;   // the best brain's speed measured on this phone, once it has answered here
        IndexingStatus? Index(ModelEntry row) => BrainInWaiting.IndexOf(row, _store);
        IReadOnlyList<HousekeepingReclaim> Housekeeping() => CatalogueHousekeeping.Plan(cat,
            readFromFlash: row => ModelFit.ReadFromFlash(row, phone),
            ready: row => !ModelFit.ReadFromFlash(row, phone)
                          || BrainInWaiting.HasTakenOver(row, phone, Index(row), row.Name == best.Name ? answeredAt : null));

        using var engine = Load();

        // Loaded, its index begun and nothing built: the 2B answers, the best one waits, and nothing goes.
        var waiting = BrainInWaiting.Plan(best, [small], phone, Index);
        Assert.Equal(small.Name, waiting.Answers.Name);
        Assert.Equal(best.Name, waiting.Waiting?.Name);
        Assert.Equal(ModelFile, waiting.WaitingFile);
        Assert.Contains("its index is 0% built", waiting.Said, StringComparison.Ordinal);
        Assert.Empty(Housekeeping());

        // The engine builds it and its check passes.
        var built = BuildAnIndexOfEveryNeuron(engine);
        Assert.True(built.Serves, $"an index of every neuron measured {built.Agreement} per thousand");
        Assert.Equal(built, Index(best));

        // Ready: the best brain answers, and nothing waits. It has not answered here yet, so the 2B still stays.
        var ready = BrainInWaiting.Plan(best, [small], phone, Index);
        Assert.Equal(best.Name, ready.Answers.Name);
        Assert.True(ready.Waiting is null && ready.Said is null);
        Assert.Empty(Housekeeping());

        // It has answered here: it has taken over, and the 2B is the old LLM.
        answeredAt = 0.4;
        var gone = Assert.Single(Housekeeping());
        Assert.Equal(small.Name, gone.Id);
        Assert.Contains(best.Name, gone.Reason, StringComparison.Ordinal);
    }

    // ---- the hooks a head installs ----

    // "RAM IS THE TURBO NOT THE ENGINE" begins with a head telling the device probe whether its engine reads brains from
    // flash. The Android head installs the engine's own word for it.
    [Fact]
    public void The_device_probe_says_what_the_head_says_of_reading_brains_from_flash()
    {
        using var registry = new ModelRegistryService();
        var big = registry.GetLatestModel("Ternary-Bonsai-2-27B")!;

        DeviceProbe.PlatformFlashReadProbe = () => true;
        var reads = DeviceProbe.Snapshot();
        Assert.True(reads.BrainsReadFromFlash);
        Assert.True(ModelFit.ReadFromFlash(big, reads));

        DeviceProbe.PlatformFlashReadProbe = () => false;
        Assert.False(DeviceProbe.Snapshot().BrainsReadFromFlash);
        Assert.False(ModelFit.ReadFromFlash(big, DeviceProbe.Snapshot()));

        DeviceProbe.PlatformFlashReadProbe = () => throw new InvalidOperationException("no engine here");
        Assert.False(DeviceProbe.Snapshot().BrainsReadFromFlash);   // a head that cannot say reads as one that keeps brains in RAM

        DeviceProbe.PlatformFlashReadProbe = null;
        Assert.False(DeviceProbe.Snapshot().BrainsReadFromFlash);

        // What the Android head installs. It is the processor's truth, not this test's switch: taking the vector kernel's
        // layout for a test does not make a PC a phone that reads its brain from flash.
        DeviceProbe.PlatformFlashReadProbe = () => ManagedChatGenerator.ComputesFromFlashHere;
        Assert.True(LanguageModel.PairedForTest);
        Assert.Equal(CircleAI.Inference.Managed.Kernels.Ptq1PairMatrix.Usable, ManagedChatGenerator.ComputesFromFlashHere);
        Assert.Equal(ManagedChatGenerator.ComputesFromFlashHere, DeviceProbe.Snapshot().BrainsReadFromFlash);
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported) Assert.False(DeviceProbe.Snapshot().BrainsReadFromFlash);
    }

    // The phone's memory reaches the engine through one reading, and a brain let go is given back to the phone.
    [Fact]
    public void The_engine_reads_the_phones_memory_in_one_look_and_gives_back_what_it_let_go()
    {
        _memory = new PhoneMemory(321 * Mb, 55 * Mb, 2048 * Mb);
        Assert.Equal(_memory, MemoryNow.Phone());
        Assert.Equal(321 * Mb, MemoryNow.AvailableBytes());

        // With no reading given, the machine's own: none of it on a machine with no /proc, and never a throw.
        MemoryNow.ReadingForTest = null;
        var own = MemoryNow.Phone();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) Assert.Equal(default(PhoneMemory), own);
        Assert.True(MemoryNow.AvailableBytes() is null or > 0);

        // What the worker does when it lets a brain go, before it builds the next: 64 MB of it, collected.
        var brain = LetGo();
        MemoryNow.GiveBack();
        Assert.False(brain.IsAlive, "a brain let go was still in the heap after it was given back");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LetGo() => new(new byte[64 << 20]);

    // The groups a layer and the neurons a list that the index in the store was begun with, from its record.
    private (int Groups, int ListNeurons)? AskedFor()
    {
        using var file = GgufFile.Open(_model);
        return IndexingQueue.Asked(file, _store);
    }

}
