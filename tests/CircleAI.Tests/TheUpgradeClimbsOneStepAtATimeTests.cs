// TheUpgradeClimbsOneStepAtATimeTests.cs
//
// WHICH BRAIN A DEVICE FETCHES NEXT, AS THE INDEXED MIND'S DESIGN HAS IT. The owner, 2026-11-14: "We no longer care too
// much about RAM. We care more about flash. Do you remember why?" - and the design he wrote answers it: "'An LLM has to
// run from RAM' is a habit from data centres, not a law. The real rule is that the bytes needed next must reach the
// processor in time. A phone has hundreds of gigabytes of storage we barely use, and a few gigabytes of RAM that
// everything fights over."
//
// The upgrade asked the flash for room to download and nothing else, and took the best brain that fitted in one jump: a
// phone answering with the 2B fetched the 27B, and found its limit after six gigabytes and a night of indexing. The
// design's own words for what it does instead, each of them a test here:
//
//   "With each success the model is upgraded, until a limit pattern is established."
//   "A device too small for a model takes a smaller one by what its profile says, and reports that."
//   "Core index, on flash: complete and lasting."
//
// So: one step at a time - the 2B, the 4B, the 9B, the 27B; only after a success; with room on the flash for the brain
// and for the index of one that answers through it; a step that failed ends the climb and the last success goes on
// answering; and what was decided is said, with what the flash measured beside it. Phone by phone, those lines are the
// limit pattern.
//
// WHAT THESE DO NOT SETTLE. A step's "success" is still the engine's own check of its index (the same top word as every
// neuron, IndexingStatus.LeastAgreement). The design's test is the score on the exam, and the exam is not in this
// repository; when it is, it replaces that check and nothing here changes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheUpgradeClimbsOneStepAtATimeTests : IDisposable
{
    private const string TwoB = "Ternary-Bonsai-2-2B";
    private const string FourB = "Ternary-Bonsai-2-4B";
    private const string NineB = "Ternary-Bonsai-2-9B";
    private const string TwentySevenB = "Ternary-Bonsai-2-27B";
    private const string OldQwen = "Qwen3.5-2B-MNN";
    private const string FourBQwen = "Qwen3.5-4B-MNN";

    private const long Gb = 1_000_000_000;

    private static readonly ModelEngine[] Shipped = [ModelEngine.Mnn, ModelEngine.Managed];

    private readonly ModelRegistryService _registry = new();

    public void Dispose() => _registry.Dispose();

    // The P30's memory - 3.6 GB, so the 2B and the 4B can be kept in RAM and the 9B and the 27B cannot - with the flash
    // free that a test gives it.
    private static DeviceProbe Phone(bool readsFromFlash, long storageFree) =>
        new(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: storageFree, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 3_867_152_384,
            StorageTotalBytes = 113_170_432_000,
            BrainsReadFromFlash = readsFromFlash,
        };

    // The brains a device may be offered, as the background fetcher lists them: live, and no brain's companion.
    private List<ModelEntry> Offered() =>
        [.. _registry.AllModels.Where(e => e.Modality == ModelModality.Chat && !e.Retired && !BrainCompanions.IsCompanion(e))];

    private static (string Name, StepState State) Passed(string name) => (name, StepState.Passed);

    private static (string Name, StepState State) Underway(string name) => (name, StepState.Underway);

    private static (string Name, StepState State) Failed(string name) => (name, StepState.Failed);

    // The upgrade's next step on a device holding these brains, each with how its step has gone there - and the brains gone
    // from it with nothing left to try of them (their index failed with every neuron listed, by this engine).
    private UpgradeStep Step(DeviceProbe device, (string Name, StepState State)[] here, string[]? spentAndGone = null, string? flash = null)
    {
        var states = here.ToDictionary(b => b.Name, b => b.State, StringComparer.Ordinal);
        var spent = new HashSet<string>(spentAndGone ?? [], StringComparer.Ordinal);
        return ModelChoice.NextStep(Offered(), states.ContainsKey, device,
            e => states.TryGetValue(e.Name, out var went) ? went : spent.Contains(e.Name) ? StepState.Failed : StepState.NotTried,
            Shipped, flash);
    }

    // ---- one step at a time ----

    // That phone answered with the 2B, had room for the 27B, and fetched the 27B. The next step up from the 2B is the
    // 4B, however much flash is free.
    [Fact]
    public void From_the_2B_the_brain_fetched_next_is_the_4B_however_much_flash_is_free()
    {
        foreach (var free in new[] { 1_500_000_000, 4 * Gb, 60 * Gb, 400 * Gb })
        {
            var step = Step(Phone(readsFromFlash: true, storageFree: free), [Passed(TwoB)]);

            Assert.True(step.Fetch?.Name == FourB, $"with {free / 1_000_000} MB of flash free the upgrade from the 2B is {step.Fetch?.Name ?? "nothing"}");
            Assert.Equal(TwoB, step.Answers?.Name);
            Assert.False(step.Limit);
        }

        Assert.Equal("Ternary-Bonsai-2-2B has passed here; Ternary-Bonsai-2-4B is the next step up and is fetched",
            Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(TwoB)]).Said);
    }

    // "With each success the model is upgraded": the 2B, the 4B, the 9B, the 27B - and above the 27B there is nothing.
    [Fact]
    public void Each_success_fetches_the_brain_one_step_above_it_and_above_the_27B_nothing()
    {
        var roomy = Phone(readsFromFlash: true, storageFree: 400 * Gb);

        Assert.Equal(FourB, Step(roomy, [Passed(TwoB)]).Fetch?.Name);
        Assert.Equal(NineB, Step(roomy, [Passed(TwoB), Passed(FourB)]).Fetch?.Name);
        Assert.Equal(TwentySevenB, Step(roomy, [Passed(FourB), Passed(NineB)]).Fetch?.Name);

        var top = Step(roomy, [Passed(NineB), Passed(TwentySevenB)]);
        Assert.Null(top.Fetch);
        Assert.False(top.Limit);
        Assert.Equal(TwentySevenB, top.Answers?.Name);
        Assert.Equal("Ternary-Bonsai-2-27B answers, and no brain this device is offered is above it", top.Said);
    }

    // The same rule through the door the old callers use, where a brain on the device counts as a success.
    [Fact]
    public void The_upgrade_named_for_a_phone_holding_the_2B_is_the_4B_and_never_the_27B()
    {
        var here = new HashSet<string>(StringComparer.Ordinal) { TwoB };

        Assert.Equal(FourB, ModelChoice.Upgrade(Offered(), here.Contains, Phone(readsFromFlash: true, storageFree: 60 * Gb), Shipped)?.Name);
    }

    // ---- only after a success ----

    // A brain that has only just landed has shown nothing yet: nothing is fetched above it until it has loaded and answered.
    [Fact]
    public void Nothing_is_fetched_above_a_brain_that_has_not_yet_loaded_and_answered_here()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Underway(TwoB)]);

        Assert.Null(step.Fetch);
        Assert.Null(step.Answers);
        Assert.False(step.Limit);
        Assert.Equal("Ternary-Bonsai-2-2B is here and has not yet loaded and answered: nothing is fetched above it until it has", step.Said);
    }

    // No brain at all is the first run's to settle, as it was.
    [Fact]
    public void With_no_brain_here_the_first_run_fetches_one_and_the_upgrade_fetches_nothing()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), []);

        Assert.Null(step.Fetch);
        Assert.Equal("no brain is here yet: the first run fetches one", step.Said);
    }

    // The next step is on the phone and its index is being built: it is waited for, and nothing is fetched beside it.
    [Fact]
    public void A_step_that_is_here_and_still_being_made_ready_is_waited_for()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(TwoB), Passed(FourB), Underway(NineB)]);

        Assert.Null(step.Fetch);
        Assert.False(step.Limit);
        Assert.Equal(FourB, step.Answers?.Name);
        Assert.Equal("Ternary-Bonsai-2-9B is here and is being made ready, and Ternary-Bonsai-2-4B answers: nothing is fetched until it has passed or failed", step.Said);
    }

    // One candidate at a time: a phone from before the climb, the 27B on it and its index part built, fetches no 4B
    // beside it. The 27B's result comes first.
    [Fact]
    public void A_better_brain_already_here_and_being_made_ready_is_the_one_candidate()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(TwoB), Underway(TwentySevenB)]);

        Assert.Null(step.Fetch);
        Assert.False(step.Limit);
        Assert.Contains("Ternary-Bonsai-2-27B is here and is being made ready", step.Said, StringComparison.Ordinal);
    }

    // ---- a step with nothing left to try ends the climb, until the engine changes ----

    // "No models should be blocked - if it fails then check why it failed. clean up and try again" (the owner,
    // 2026-11-15): a step fails only when nothing is left to try of it - its index failed with every neuron listed, the
    // engine's fault (BrainInWaiting.StepOf). With room for the 27B three times over, a 9B like that is where this phone's
    // climb ends until the engine changes: the 27B is not fetched over its head, the last success answers, and it is said.
    [Fact]
    public void A_step_with_nothing_left_to_try_ends_the_climb_and_the_last_success_answers_and_it_is_said()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(TwoB), Passed(FourB), Failed(NineB)]);

        Assert.Null(step.Fetch);
        Assert.True(step.Limit);
        Assert.Equal(FourB, step.Answers?.Name);
        Assert.Equal("Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B, and its index failed its check here with every neuron listed - the engine's fault, not this device's: Ternary-Bonsai-2-4B answers, and is this device's limit until the engine changes", step.Said);
    }

    // And once that brain has gone from the flash, its record still says so: the upgrade does not step over it to the brain
    // above, nor fetch it again into the same check.
    [Fact]
    public void A_step_with_nothing_left_to_try_ends_the_climb_though_its_brain_is_no_longer_here()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(TwoB)], spentAndGone: [FourB]);

        Assert.Null(step.Fetch);
        Assert.True(step.Limit);
        Assert.Contains("Ternary-Bonsai-2-4B is the next step up from Ternary-Bonsai-2-2B, and its index failed its check here with every neuron listed", step.Said, StringComparison.Ordinal);
    }

    // THE P30 AS IT STANDS (2026-11-14): the 2B answers, the 27B's index failed its check, 4.3 GB of flash is free. The
    // failed 27B is three steps up and decides nothing yet: the next step from the 2B is the 4B, and it is fetched.
    [Fact]
    public void That_P30_holding_the_2B_and_a_27B_whose_index_failed_fetches_the_4B_next()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 4_300_000_000), [Passed(TwoB), Failed(TwentySevenB)]);

        Assert.Equal(FourB, step.Fetch?.Name);
        Assert.Equal(TwoB, step.Answers?.Name);
        Assert.False(step.Limit);
    }

    // And when the climb reaches the 27B that failed, it ends there.
    [Fact]
    public void When_the_climb_reaches_the_brain_that_failed_it_ends_there()
    {
        var step = Step(Phone(readsFromFlash: true, storageFree: 60 * Gb), [Passed(FourB), Passed(NineB), Failed(TwentySevenB)]);

        Assert.Null(step.Fetch);
        Assert.True(step.Limit);
        Assert.Equal(NineB, step.Answers?.Name);
    }

    // ---- the flash is what is asked ----

    // "Core index, on flash: complete and lasting." A brain that answers through its index is carried by a flash with
    // room for the file and for every piece of that index. That P30 gave the 27B room for 2.7 GB of its 7.2 GB of pieces.
    [Fact]
    public void A_brain_that_answers_through_its_index_is_fetched_only_where_the_flash_has_room_for_both()
    {
        // From the 4B the next step is the 9B, which this phone's RAM cannot keep: 2.0 GB of file, 4.5 GB of index.
        var tight = Step(Phone(readsFromFlash: true, storageFree: 4 * Gb), [Passed(TwoB), Passed(FourB)]);
        Assert.Null(tight.Fetch);
        Assert.True(tight.Limit);
        Assert.Equal(FourB, tight.Answers?.Name);
        Assert.Equal("Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B and needs 6.5 GB of flash for itself and its index; "
                     + "4.0 GB is free: Ternary-Bonsai-2-4B is this device's limit until there is room", tight.Said);

        Assert.Equal(NineB, Step(Phone(readsFromFlash: true, storageFree: 7 * Gb), [Passed(TwoB), Passed(FourB)]).Fetch?.Name);

        // And the 27B from the 9B: 6.0 GB of file, 11.2 GB of index.
        var twelve = Step(Phone(readsFromFlash: true, storageFree: 12 * Gb), [Passed(FourB), Passed(NineB)]);
        Assert.Null(twelve.Fetch);
        Assert.True(twelve.Limit);
        Assert.Contains("needs 17.2 GB of flash for itself and its index; 12.0 GB is free", twelve.Said, StringComparison.Ordinal);
        Assert.Equal(TwentySevenB, Step(Phone(readsFromFlash: true, storageFree: 18 * Gb), [Passed(FourB), Passed(NineB)]).Fetch?.Name);
    }

    // RAM is the turbo: a brain the device keeps warm in RAM answers with no index, so the flash is asked for its file alone.
    [Fact]
    public void A_brain_the_device_keeps_in_RAM_needs_room_for_its_file_alone()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 1_500_000_000);
        var fourB = _registry.GetLatestModel(FourB)!;

        Assert.False(ModelFit.AnswersThroughItsIndex(fourB, phone));
        Assert.Equal(fourB.MinStorageGb, ModelFit.FlashNeededGb(fourB, phone));
        Assert.Equal(FourB, Step(phone, [Passed(TwoB)]).Fetch?.Name);

        // And with no room for the file, the climb waits for room and says how much.
        var none = Step(Phone(readsFromFlash: true, storageFree: 400_000_000), [Passed(TwoB)]);
        Assert.Null(none.Fetch);
        Assert.True(none.Limit);
        Assert.Equal("Ternary-Bonsai-2-4B is the next step up from Ternary-Bonsai-2-2B and needs 1.0 GB of flash for itself; "
                     + "0.4 GB is free: Ternary-Bonsai-2-2B is this device's limit until there is room", none.Said);
    }

    // What a brain needs of the flash where it answers through its index: its file, every piece of step 2, room for step
    // 2's tiled layers and what is kept for the phone - the shares the engine's own budget gives them.
    [Fact]
    public void The_flash_a_brain_needs_is_its_file_and_its_index_complete()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 60 * Gb);
        var big = _registry.GetLatestModel(TwentySevenB)!;

        Assert.True(ModelFit.AnswersThroughItsIndex(big, phone));
        var core = (long)(big.TotalBytes * ModelFit.IndexToFile);
        var index = core + Math.Min(IndexingBudget.MostLayers, core / 4) + IndexingBudget.Reserve;
        Assert.Equal(index, ModelFit.IndexRoomBytes(big));
        Assert.Equal(big.MinStorageGb + index / DeviceProbe.BytesPerGb, ModelFit.FlashNeededGb(big, phone), precision: 9);

        // The budget the engine gives an index with exactly that much free beside the file: every piece of the core.
        var (coreGiven, _) = IndexingBudget.For(index, core);
        Assert.Equal(core, coreGiven);

        // Where the engine keeps a brain in RAM there is no index to make room for.
        Assert.Equal(big.MinStorageGb, ModelFit.FlashNeededGb(big, Phone(readsFromFlash: false, storageFree: 60 * Gb)));
    }

    // The room asked for an index before its file is here is a sum from the file's size (ModelFit.IndexToFile). It is
    // held here against the engine's own count of every piece, for the models whose figures are known - the 2B's and
    // the 4B's read from their files on this PC, the 27B's from what the P30 logged of it - at every size of read the
    // engine stores pieces in: never less than the index takes. And the 27B's count is the phone's own: "the core's
    // pieces may take 2687 MB of the 7168 MB all of them are" (2026-11-13), 5,120 wide. This test said 5,376 until
    // 2026-11-14 and held the constant within one in a hundred of a count the phone never made.
    [Theory]
    [InlineData(TwoB, 24, 6144, 2048)]
    [InlineData(FourB, 32, 9216, 2560)]
    [InlineData(TwentySevenB, 64, 17408, 5120)]
    public void The_room_asked_for_an_index_is_never_less_than_every_piece_of_it_takes(string name, int layers, int feedForward, int embedding)
    {
        var asked = (long)(_registry.GetLatestModel(name)!.TotalBytes * ModelFit.IndexToFile);
        var list = ThoughtIndex.ListOf(Math.Max(Ptq1PairMatrix.TileRows, feedForward * ManagedChatGenerator.IndexPercent / 100), feedForward);

        foreach (var read in FlashProfile.Sizes)
        {
            var every = IndexingBudget.CompleteCore(layers, ManagedChatGenerator.IndexGroups, list, embedding, read);
            Assert.True(asked >= every, $"{name}: {asked:N0} bytes are asked for an index whose pieces take {every:N0} in reads of {read:N0}");
        }

        if (name == TwentySevenB)
        {
            Assert.Equal(2624, list);   // "lists of 2624 of 17408 neurons", as the P30 logged them
            var all = IndexingBudget.CompleteCore(layers, ManagedChatGenerator.IndexGroups, list, embedding, 1 << 20);
            Assert.Equal(7168, (int)Math.Round(all / 1048576.0));
        }
    }

    // ---- where the engine keeps a brain in RAM ----

    // There the climb is the same, and it ends at the brain the device's memory holds - said as what it is.
    [Fact]
    public void Where_the_engine_keeps_a_brain_in_RAM_the_climb_ends_at_what_memory_holds()
    {
        var device = Phone(readsFromFlash: false, storageFree: 60 * Gb);

        Assert.Equal(FourB, Step(device, [Passed(TwoB)]).Fetch?.Name);

        var end = Step(device, [Passed(TwoB), Passed(FourB)]);
        Assert.Null(end.Fetch);
        Assert.True(end.Limit);
        Assert.Equal("Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B, and this device keeps a brain in RAM and could never hold it: "
                     + "Ternary-Bonsai-2-4B is this device's limit", end.Said);
    }

    // ---- only the family ----

    // "we are currently working only with Bonsai 2 family - 27B 9B 4B and 2B": from a brain of another engine the next
    // step is the family's lowest, whatever that brain's rank - never another Qwen, and never the family's best in one jump.
    [Fact]
    public void From_a_brain_of_another_engine_the_next_step_is_the_familys_lowest()
    {
        var roomy = Phone(readsFromFlash: true, storageFree: 60 * Gb);
        Assert.True(_registry.GetLatestModel(FourBQwen)!.QualityRank > _registry.GetLatestModel(TwoB)!.QualityRank, "setup: the Qwen is not ranked above the 2B");

        Assert.Equal(TwoB, Step(roomy, [Passed(OldQwen)]).Fetch?.Name);
        Assert.Equal(TwoB, Step(roomy, [Passed(OldQwen), Passed(FourBQwen)]).Fetch?.Name);
        Assert.Equal(TwoB, Step(Phone(readsFromFlash: false, storageFree: 60 * Gb), [Passed(FourBQwen)]).Fetch?.Name);
    }

    // ---- the flash's speed, said beside the result ----

    // "logs flash speed beside each result. That log is the limit pattern": whatever was decided, what the flash
    // measured is said with it.
    [Fact]
    public void What_the_flash_measured_is_said_beside_each_result()
    {
        const string measured = "87 MB/s straight and 61 MB/s in scattered reads of 256 KB";
        var phone = Phone(readsFromFlash: true, storageFree: 60 * Gb);

        Assert.Equal("Ternary-Bonsai-2-2B has passed here; Ternary-Bonsai-2-4B is the next step up and is fetched (flash: " + measured + ")",
            Step(phone, [Passed(TwoB)], flash: measured).Said);
        Assert.EndsWith("Ternary-Bonsai-2-4B answers, and is this device's limit until the engine changes (flash: " + measured + ")",
            Step(phone, [Passed(FourB), Failed(NineB)], flash: measured).Said, StringComparison.Ordinal);
        Assert.EndsWith("is this device's limit until there is room (flash: " + measured + ")",
            Step(Phone(readsFromFlash: true, storageFree: 4 * Gb), [Passed(FourB)], flash: measured).Said, StringComparison.Ordinal);
    }

    // What the engine measured is kept by the process that measured it, with the cores that process was given. On the
    // P30 Android gave the inference process three and the service eight, so the service is given the speed whatever
    // the cores - and nobody is given another device's.
    [Fact]
    public void What_the_flash_measured_is_read_by_a_process_given_other_cores_than_the_one_that_measured_it()
    {
        var directory = Directory.CreateTempSubdirectory("flash-said-").FullName;
        try
        {
            var path = Path.Combine(directory, "probe.bin");
            var bytes = new byte[5 * 1024 * 1024];
            new Random(7).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            var measured = FlashProfile.Measure(path, directory, TimeSpan.FromMilliseconds(5), cores: 2);

            Assert.Matches(@"^\d+ MB/s straight and \d+ MB/s in scattered reads of (4 KB|64 KB|256 KB|1 MB)$", measured.Speed);
            Assert.Equal(measured.Speed, BrainInWaiting.FlashOf(directory));
            Assert.Equal("; on flash that reads " + measured.Speed, ManagedChatGenerator.CheckedOn(measured));
            Assert.Equal(string.Empty, ManagedChatGenerator.CheckedOn(null));

            // As a process with other cores finds it: the same record, another count at its head.
            var kept = Path.Combine(directory, FlashProfile.FileName);
            var lines = File.ReadAllLines(kept);
            var rest = lines[0][lines[0].IndexOf('|', StringComparison.Ordinal)..];
            lines[0] = "97" + rest;
            File.WriteAllLines(kept, lines);

            Assert.Null(FlashProfile.Remembered(directory));          // its recipe is not this process's
            Assert.Equal(measured.Speed, FlashProfile.KeptBy(directory)?.Speed);
            Assert.Equal(measured.Speed, BrainInWaiting.FlashOf(directory));

            // Another device's - another folder in its record - is nobody's.
            lines[0] = "97" + rest + "-elsewhere";
            File.WriteAllLines(kept, lines);
            Assert.Null(FlashProfile.KeptBy(directory));
            Assert.Null(BrainInWaiting.FlashOf(directory));

            // And where nothing was measured, nothing is said.
            Assert.Null(BrainInWaiting.FlashOf(Path.Combine(directory, "nothing-here")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- how a brain's step has gone, from what the device knows of it ----

    // The 27B on that phone answers through its index, so its step is its index's: underway while it is built, while its
    // check is still to run, or while one listing more of its neurons is built after a check that failed; failed only
    // when nothing is left to try - its index failed with every neuron listed - and a success only once it has also
    // answered there.
    [Fact]
    public void A_brain_that_answers_through_its_index_has_passed_once_its_index_serves_and_it_has_answered()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 4 * Gb);
        var big = _registry.GetLatestModel(TwentySevenB)!;
        StepState Of(bool present, IndexingStatus? index, double? speed = null) =>
            BrainInWaiting.StepOf(big, phone, present, proven: true, index, speed);

        Assert.Equal(StepState.NotTried, Of(present: false, index: null));
        Assert.Equal(StepState.Underway, Of(present: true, index: null));
        Assert.Equal(StepState.Underway, Of(present: true, new IndexingStatus(27, 71)));
        Assert.Equal(StepState.Underway, Of(present: true, new IndexingStatus(71, 71)));            // its check is still to run
        Assert.Equal(StepState.Underway, Of(present: true, new IndexingStatus(71, 71, 950)));       // passed, and has answered nobody yet
        Assert.Equal(StepState.Passed, Of(present: true, new IndexingStatus(71, 71, 950), speed: 0.4));

        // "NO MODELS SHOULD BE BLOCKED - IF IT FAILS THEN CHECK WHY IT FAILED. CLEAN UP AND TRY AGAIN" (the owner,
        // 2026-11-15). That P30's 25.0%, at fifteen in a hundred of its neurons, is a reason with a remedy: the next index
        // lists more, so the brain is underway while it is here - and, once housekeeping has taken it off the flash, a step
        // not tried, fetched again for that index.
        Assert.Equal(StepState.Underway, Of(present: true, new IndexingStatus(71, 71, 250)));
        Assert.Equal(StepState.NotTried, Of(present: false, new IndexingStatus(71, 71, 250)));

        // Only an index of every neuron that still failed, by this engine, leaves nothing to try - here or gone.
        Assert.Equal(StepState.Failed, Of(present: true, new IndexingStatus(71, 71, 250, EveryNeuron: true)));
        Assert.Equal(StepState.Failed, Of(present: false, new IndexingStatus(71, 71, 250, EveryNeuron: true)));
        Assert.Equal(StepState.NotTried, Of(present: false, new IndexingStatus(71, 71)));   // an index begun says nothing yet
    }

    // A brain the device keeps in RAM needs no index: its step is a success once it has loaded there, whatever has
    // become of an index somebody began.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_brain_the_device_keeps_in_RAM_has_passed_once_it_has_loaded_there(bool readsFromFlash)
    {
        var device = Phone(readsFromFlash, storageFree: 4 * Gb);
        var small = _registry.GetLatestModel(TwoB)!;

        Assert.Equal(StepState.NotTried, BrainInWaiting.StepOf(small, device, present: false, proven: false, index: null, measuredSpeed: null));
        Assert.Equal(StepState.Underway, BrainInWaiting.StepOf(small, device, present: true, proven: false, index: null, measuredSpeed: null));
        Assert.Equal(StepState.Passed, BrainInWaiting.StepOf(small, device, present: true, proven: true, index: null, measuredSpeed: null));
        Assert.Equal(StepState.Passed, BrainInWaiting.StepOf(small, device, present: true, proven: true,
            index: new IndexingStatus(71, 71, 250), measuredSpeed: null));
        Assert.Equal(StepState.Passed, BrainInWaiting.StepOf(small, device, present: true, proven: true,
            index: new IndexingStatus(71, 71, 250, EveryNeuron: true), measuredSpeed: 7.5));

        // Nor, once it is gone, does an index of it that failed: it needed none.
        Assert.Equal(StepState.NotTried, BrainInWaiting.StepOf(small, device, present: false, proven: true,
            index: new IndexingStatus(71, 71, 250, EveryNeuron: true), measuredSpeed: null));
    }

    // THE CLIMB ON THAT P30, END TO END, as the fetcher (ModelChoice.NextStep) and the supervisor
    // (BrainInWaiting.PastAFailedIndex, then Plan) take it, had the 27B's index failed with every neuron listed - nothing
    // left to try - and given its 3.6 GB back: the 4B is fetched; once it is here it is the brain the phone takes up - not
    // the 27B, not the 2B; once it has passed and the 2B has gone, the 9B is fetched, the flash having room for it and its
    // index; while that index is built the 4B answers; and if nothing is left to try of it either, the 4B is this phone's
    // limit until the engine changes, and says so.
    [Fact]
    public void On_that_P30_each_step_fetched_is_the_brain_the_phone_takes_up_next()
    {
        var failed = new IndexingStatus(71, 71, 250, EveryNeuron: true);
        ModelEntry Row(string name) => _registry.GetLatestModel(name)!;

        // Each stage: what is on the phone and how each step went, its flash free, and the brains whose index failed.
        (UpgradeStep Step, ModelEntry Brain, WaitingPlan Plan) Stage((string Name, StepState State)[] here, long free, params string[] indexFailed)
        {
            var phone = Phone(readsFromFlash: true, storageFree: free);
            IndexingStatus? Index(ModelEntry e) => indexFailed.Contains(e.Name) ? failed : null;
            var step = Step(phone, here);
            var brain = BrainInWaiting.PastAFailedIndex(Row(TwentySevenB), here.Select(b => Row(b.Name)), phone, Index).Brain;
            var plan = BrainInWaiting.Plan(brain, here.Where(b => b.State == StepState.Passed && b.Name != brain.Name).Select(b => Row(b.Name)), phone, Index);
            return (step, brain, plan);
        }

        // Today, the 3.6 GB given back: the 2B answers and the 4B is fetched.
        var today = Stage([Passed(TwoB), Failed(TwentySevenB)], 7_900_000_000, TwentySevenB);
        Assert.Equal(FourB, today.Step.Fetch?.Name);
        Assert.Equal(TwoB, today.Brain.Name);
        Assert.Equal(TwoB, today.Plan.Answers.Name);

        // The 4B has landed: nothing more is fetched, and it is the phone's brain - validated by the supervisor, as any
        // brain that has not loaded here is, while the 2B goes on answering.
        var landed = Stage([Passed(TwoB), Underway(FourB), Failed(TwentySevenB)], 6_950_000_000, TwentySevenB);
        Assert.Null(landed.Step.Fetch);
        Assert.Equal(FourB, landed.Brain.Name);

        // It has passed, and the 2B has gone: the 4B answers, and the 9B is fetched - 6.5 GB for it and its index.
        var passed = Stage([Passed(FourB), Failed(TwentySevenB)], 7_380_000_000, TwentySevenB);
        Assert.Equal(FourB, passed.Brain.Name);
        Assert.Equal(FourB, passed.Plan.Answers.Name);
        Assert.Equal(NineB, passed.Step.Fetch?.Name);

        // The 9B is here: it is the phone's brain and waits on its index, while the 4B answers.
        var nine = Stage([Passed(FourB), Underway(NineB), Failed(TwentySevenB)], 5_380_000_000, TwentySevenB);
        Assert.Null(nine.Step.Fetch);
        Assert.Equal(NineB, nine.Brain.Name);
        Assert.Equal(FourB, nine.Plan.Answers.Name);
        Assert.Equal(NineB, nine.Plan.Waiting?.Name);

        // Nothing is left to try of it either: the 4B answers, and is this phone's limit until the engine changes.
        var limit = Stage([Passed(FourB), Failed(NineB), Failed(TwentySevenB)], 5_380_000_000, NineB, TwentySevenB);
        Assert.Equal(FourB, limit.Brain.Name);
        Assert.Equal(FourB, limit.Plan.Answers.Name);
        Assert.Null(limit.Plan.Waiting);
        Assert.True(limit.Step.Limit);
        Assert.Equal("Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B, and its index failed its check here with every neuron listed - the engine's fault, not this device's: Ternary-Bonsai-2-4B answers, and is this device's limit until the engine changes",
            limit.Step.Said);
    }

    // "NO MODELS SHOULD BE BLOCKED - IF IT FAILS THEN CHECK WHY IT FAILED. CLEAN UP AND TRY AGAIN" (the owner, 2026-11-15).
    // That P30 as it was: the 27B's index failed its check at fifteen in a hundred of its neurons (25.0%). Too few neurons is
    // a reason with a remedy, so the 27B is still the phone's brain by rank and WAITS on its next index - listing more -
    // loaded while the phone is idle, the 2B answering; and the climb fetches nothing beside it while it is made ready.
    [Fact]
    public void On_that_P30_a_27B_whose_index_failed_at_a_small_share_waits_on_one_listing_more()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 7_900_000_000);
        ModelEntry Row(string name) => _registry.GetLatestModel(name)!;
        var failed = new IndexingStatus(71, 71, 250);
        IndexingStatus? Index(ModelEntry e) => e.Name == TwentySevenB ? failed : null;
        var here = new HashSet<string>(StringComparer.Ordinal) { TwoB, TwentySevenB };

        var brain = BrainInWaiting.PastAFailedIndex(Row(TwentySevenB), here.Select(Row), phone, Index);
        Assert.Equal(TwentySevenB, brain.Brain.Name);
        Assert.Null(brain.Said);

        var plan = BrainInWaiting.Plan(brain.Brain, [Row(TwoB)], phone, Index);
        Assert.Equal(TwoB, plan.Answers.Name);
        Assert.Equal(TwentySevenB, plan.Waiting?.Name);
        Assert.Contains("being built again listing more of its neurons, the last having measured 25.0% of the 90% it must reach", plan.Said, StringComparison.Ordinal);

        var step = ModelChoice.NextStep(Offered(), here.Contains, phone,
            e => BrainInWaiting.StepOf(e, phone, here.Contains(e.Name), proven: here.Contains(e.Name), Index(e), measuredSpeed: e.Name == TwoB ? 7.5 : null),
            Shipped);
        Assert.Null(step.Fetch);
        Assert.False(step.Limit);
        Assert.Equal("Ternary-Bonsai-2-27B is here and is being made ready, and Ternary-Bonsai-2-2B answers: nothing is fetched until it has passed or failed", step.Said);
    }

    // The whole of that P30, from what it knows of its own brains to what it fetches: the 2B has loaded and answers, the
    // 27B's index is complete at 25.0% with nothing left to try of it (every neuron listed, by this engine), and the next
    // brain is the 4B.
    [Fact]
    public void From_what_that_P30_knows_of_its_brains_the_next_one_fetched_is_the_4B()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 4_300_000_000);
        var here = new HashSet<string>(StringComparer.Ordinal) { TwoB, TwentySevenB };
        IndexingStatus? IndexOf(ModelEntry e) => e.Name == TwentySevenB ? new IndexingStatus(71, 71, 250, EveryNeuron: true) : null;

        var step = ModelChoice.NextStep(Offered(), here.Contains, phone,
            e => BrainInWaiting.StepOf(e, phone, here.Contains(e.Name), proven: here.Contains(e.Name), IndexOf(e), measuredSpeed: e.Name == TwoB ? 7.5 : null),
            Shipped, flash: "87 MB/s straight and 61 MB/s in scattered reads of 256 KB");

        Assert.Equal(FourB, step.Fetch?.Name);
        Assert.Equal("Ternary-Bonsai-2-2B has passed here; Ternary-Bonsai-2-4B is the next step up and is fetched "
                     + "(flash: 87 MB/s straight and 61 MB/s in scattered reads of 256 KB)", step.Said);
    }

    // THAT P30 AS IT IS (the owner, 2026-11-14: "Yes, remove them"; 2026-11-15: "no models should be blocked - if it fails
    // then check why it failed. clean up and try again"): only the 4B is on it, with the room the 27B and the 9B gave back,
    // and the records of their checks - at fifteen in a hundred of their neurons - are all that is left of them. Too few
    // neurons is a reason with a remedy, so the 9B is the next step up and is fetched again, for an index listing more.
    // Only records that say nothing is left to try - every neuron listed, by this engine - end the climb at the 4B.
    [Fact]
    public void Once_the_brains_whose_index_failed_have_gone_the_9B_is_fetched_again_unless_nothing_is_left_to_try()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 14_040_000_000);
        var here = new HashSet<string>(StringComparer.Ordinal) { FourB };
        UpgradeStep Climb(Func<ModelEntry, IndexingStatus?> record) => ModelChoice.NextStep(Offered(), here.Contains, phone,
            e => BrainInWaiting.StepOf(e, phone, here.Contains(e.Name), proven: e.Name is FourB or NineB or TwentySevenB,
                record(e), measuredSpeed: null),
            Shipped);

        var again = Climb(e => e.Name == NineB ? new IndexingStatus(39, 39, 125) : e.Name == TwentySevenB ? new IndexingStatus(71, 71, 250) : null);
        Assert.Equal(NineB, again.Fetch?.Name);
        Assert.False(again.Limit);

        var limit = Climb(e => e.Name == NineB ? new IndexingStatus(39, 39, 125, EveryNeuron: true)
                             : e.Name == TwentySevenB ? new IndexingStatus(71, 71, 250, EveryNeuron: true) : null);
        Assert.Null(limit.Fetch);
        Assert.True(limit.Limit);
        Assert.Equal(FourB, limit.Answers?.Name);
        Assert.Equal("Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B, and its index failed its check here with every neuron listed - the engine's fault, not this device's: Ternary-Bonsai-2-4B answers, and is this device's limit until the engine changes",
            limit.Said);

        Assert.Equal(NineB, Climb(_ => null).Fetch?.Name);
    }
}
