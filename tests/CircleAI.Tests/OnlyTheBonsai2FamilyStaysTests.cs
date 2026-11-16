// OnlyTheBonsai2FamilyStaysTests.cs
//
// THE OWNER, 2026-11-13, of a P30 whose store still held Qwen3.5-2B-MNN beside the fleet's brains:
//
//   "Qwen should be gone as we are currently working only with Bonsai 2 family - 27B 9B 4B and 2B"
//   "All other must go and make space on FLASH"
//
// Written from those words. A brain outside the Bonsai 2 family is the old LLM, whatever its size or its rank, and it
// goes from the phone's flash once a brain of the family has loaded there and answers - not before, so no phone is left
// without a brain. Voices, eyes and ears are not brains: this rule does not judge them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(BrainSpeedCollection))]   // BrainSpeed.Measured is a static the speed tests set
public sealed class OnlyTheBonsai2FamilyStaysTests : IDisposable
{
    private const string TwoB = "Ternary-Bonsai-2-2B";
    private const string NineB = "Ternary-Bonsai-2-9B";
    private const string TwentySevenB = "Ternary-Bonsai-2-27B";

    private const string SmallQwen = "Qwen3.5-0.8B-MNN";      // 548 MB: far smaller than any brain that would replace it
    private const string OldQwen = "Qwen3.5-2B-MNN";          // the one that P30 still held, 1.4 GB
    private const string BigQwen = "Qwen3.6-35B-A3B-MNN";     // rank 16: above every brain of the family
    private const string GgufQwen = "Qwen3.5-4B-GGUF";        // another engine again

    private static readonly ModelEngine[] Shipped = [ModelEngine.Mnn, ModelEngine.Managed];

    private readonly ModelRegistryService _registry = new();

    public OnlyTheBonsai2FamilyStaysTests() => BrainSpeed.Measured = _ => null;

    public void Dispose()
    {
        BrainSpeed.Measured = null;
        _registry.Dispose();
    }

    // A device with room for everything, whose engine keeps a brain in RAM: 16 GB of memory, 12 of it free.
    private static DeviceProbe Roomy() =>
        new(RamAvailableBytes: 12_000_000_000, StorageFreeBytes: 200_000_000_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 16_000_000_000, StorageTotalBytes = 256_000_000_000 };

    // The P30 as it stood on 2026-11-13, its engine reading brains from flash.
    private static DeviceProbe P30() =>
        new(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: 4_830_988_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_152_384, StorageTotalBytes = 113_170_432_000, BrainsReadFromFlash = true };

    // The catalogue's own rows for the models named, every one of them on the device.
    private SqliteModelCatalog Holding(params string[] models)
    {
        var cat = new SqliteModelCatalog("Data Source=:memory:");
        foreach (var name in models)
        {
            var row = _registry.GetLatestModel(name);
            Assert.True(row is not null, $"setup: the registry has no row for {name}");
            cat.Upsert(row!);
            cat.SetInstalled(name, true);
        }
        return cat;
    }

    private static string[] Going(IEnumerable<HousekeepingReclaim> plan) => [.. plan.Select(r => r.Id).Order(StringComparer.Ordinal)];

    // "Bonsai 2 family - 27B 9B 4B and 2B": in the catalogue the family is the brains of the C# engine, every one of
    // them a Ternary-Bonsai-2 row, and every other live brain is a Qwen on another engine.
    [Fact]
    public void The_family_is_the_CSharp_engines_brains_27B_9B_4B_and_2B_and_every_other_brain_is_a_Qwen()
    {
        var brains = _registry.AllModels.Where(m => m.Modality == ModelModality.Chat && !m.Retired).ToList();
        var family = brains.Where(ModelChoice.Fleet).ToList();
        var others = brains.Where(m => !ModelChoice.Fleet(m)).ToList();

        Assert.All(family, m => Assert.StartsWith("Ternary-Bonsai-2-", m.Name, StringComparison.Ordinal));
        Assert.All(brains.Where(m => m.Name.StartsWith("Ternary-Bonsai-2-", StringComparison.Ordinal)), m => Assert.True(ModelChoice.Fleet(m), m.Name));
        foreach (var size in new[] { "27B", "9B", "4B", "2B" })
            Assert.Contains(family, m => m.Name == "Ternary-Bonsai-2-" + size);

        Assert.NotEmpty(others);
        Assert.All(others, m => Assert.StartsWith("Qwen", m.Name, StringComparison.Ordinal));
        Assert.Contains(others, m => m.Name == OldQwen);
    }

    // "All other must go and make space on FLASH": the one far smaller than its replacement, which the size rule kept as
    // a fallback; the one ranked above every brain of the family; the one of yet another engine.
    [Fact]
    public void Every_other_brain_goes_once_a_brain_of_the_family_has_loaded_here_whatever_its_size_or_rank()
    {
        var device = Roomy();
        using var cat = Holding(NineB, SmallQwen, OldQwen, BigQwen, GgufQwen);
        new DeviceModelAssessor(cat, Shipped).Assess(device);
        foreach (var id in new[] { SmallQwen, OldQwen, BigQwen }) cat.RecordLoad(id);   // they answered here before the family did

        // No brain of the family has loaded here yet: the others are this device's only proven brains, and they stay.
        Assert.Empty(CatalogueHousekeeping.Plan(cat));

        cat.RecordLoad(NineB);
        var plan = CatalogueHousekeeping.Plan(cat);

        Assert.Equal(new[] { SmallQwen, OldQwen, GgufQwen, BigQwen }.Order(StringComparer.Ordinal).ToArray(), Going(plan));
        Assert.All(plan, r =>
        {
            Assert.Contains("outside the Bonsai 2 fleet", r.Reason, StringComparison.Ordinal);
            Assert.Contains(NineB, r.Reason, StringComparison.Ordinal);
        });
        var nine = _registry.GetLatestModel(NineB)!;
        Assert.True(_registry.GetLatestModel(BigQwen)!.QualityRank > nine.QualityRank, "setup: the big Qwen is not ranked above the family's brain here");
        Assert.True(nine.TotalBytes > 2 * _registry.GetLatestModel(SmallQwen)!.TotalBytes, "setup: the small Qwen is not far smaller than the family's brain here");
    }

    // The same on that P30, where the family's 2B answers and the 27B waits on its index: the Qwen goes for the 2B.
    [Fact]
    public void On_that_P30_the_Qwen_goes_for_the_familys_2B_while_the_27B_still_waits_on_its_index()
    {
        var phone = P30();
        using var cat = Holding(TwoB, TwentySevenB, OldQwen);
        new DeviceModelAssessor(cat, Shipped).Assess(phone);
        foreach (var id in new[] { TwoB, TwentySevenB, OldQwen }) cat.RecordLoad(id);
        var failed = new IndexingStatus(71, 71, 250);   // what its check measured: one word in four

        var plan = CatalogueHousekeeping.Plan(cat,
            readFromFlash: e => ModelFit.ReadFromFlash(e, phone),
            ready: e => BrainInWaiting.Ready(e, phone, e.Name == TwentySevenB ? failed : null));

        var gone = Assert.Single(plan);
        Assert.Equal(OldQwen, gone.Id);
        Assert.Equal("outside the Bonsai 2 fleet, and Ternary-Bonsai-2-2B has loaded here", gone.Reason);
    }

    // A phone is never left without a brain: nothing goes for a brain of the family that has not loaded here, that this
    // phone has refused, or that cannot answer yet.
    [Fact]
    public void Nothing_goes_for_a_brain_of_the_family_that_has_not_loaded_here_was_refused_or_cannot_answer_yet()
    {
        var device = Roomy();
        using (var cat = Holding(NineB, OldQwen))
        {
            new DeviceModelAssessor(cat, Shipped).Assess(device);
            cat.RecordLoad(OldQwen);
            Assert.Empty(CatalogueHousekeeping.Plan(cat));          // the 9B has landed and never loaded

            cat.RecordLoad(NineB);
            cat.Refuse(NineB, CrashVerdict.Reason);
            Assert.Empty(CatalogueHousekeeping.Plan(cat));          // it loaded, and this phone has refused it since
        }

        // The only brain of the family here waits on its index: the Qwen is what the phone answers with meanwhile.
        var phone = P30();
        using (var cat = Holding(TwentySevenB, OldQwen))
        {
            new DeviceModelAssessor(cat, Shipped).Assess(phone);
            cat.RecordLoad(OldQwen);
            cat.RecordLoad(TwentySevenB);
            IReadOnlyList<HousekeepingReclaim> With(IndexingStatus? index) => CatalogueHousekeeping.Plan(cat,
                readFromFlash: e => ModelFit.ReadFromFlash(e, phone),
                ready: e => BrainInWaiting.Ready(e, phone, e.Name == TwentySevenB ? index : null));

            Assert.Empty(With(null));
            Assert.Empty(With(new IndexingStatus(40, 71)));
            Assert.Empty(With(new IndexingStatus(71, 71, 250)));

            // Its index complete and passed: it answers, and the Qwen goes.
            Assert.Equal(new[] { OldQwen }, Going(With(new IndexingStatus(71, 71, 969))));
        }
    }

    // The brain that is answering this minute is not taken from under itself: it goes at the next start.
    [Fact]
    public void A_brain_outside_the_family_that_is_answering_now_is_left_until_it_is_not()
    {
        var device = Roomy();
        using var cat = Holding(NineB, OldQwen, SmallQwen);
        new DeviceModelAssessor(cat, Shipped).Assess(device);
        foreach (var id in new[] { NineB, OldQwen, SmallQwen }) cat.RecordLoad(id);

        Assert.Equal(new[] { SmallQwen }, Going(CatalogueHousekeeping.Plan(cat, keep: id => id == OldQwen)));
        Assert.Equal(new[] { SmallQwen, OldQwen }.Order(StringComparer.Ordinal).ToArray(), Going(CatalogueHousekeeping.Plan(cat)));
    }

    // "Qwen should be gone": the eyes, a voice, the ears, the wake word and the memory's model are not brains, whatever
    // engine runs them, and the brains of the family are not judged by this rule at all.
    [Fact]
    public void Voices_eyes_ears_and_the_family_itself_are_not_what_goes()
    {
        var device = Roomy();
        var others = new[]
        {
            "SmolVLM-256M-Instruct-MNN", "Piper-en_US-lessac-medium", "Whisper-tiny-ggml", "KWS-Zipformer-HeyB",
            "Granite-Embedding-107M-Multilingual",
        };
        using var cat = Holding([NineB, TwoB, "Ternary-Bonsai-2-2B-Vision", OldQwen, .. others]);
        new DeviceModelAssessor(cat, Shipped).Assess(device);
        foreach (var id in new[] { NineB, TwoB, OldQwen }) cat.RecordLoad(id);

        Assert.Equal(new[] { OldQwen }, Going(CatalogueHousekeeping.Plan(cat)));
    }

    // And the store: the folders go, the flash comes back, and what went and why is said.
    [Fact]
    public void The_other_brains_folders_leave_the_store_and_what_went_is_said()
    {
        var phone = P30();
        using var cat = Holding(TwoB, OldQwen, SmallQwen);
        new DeviceModelAssessor(cat, Shipped).Assess(phone);
        foreach (var id in new[] { TwoB, OldQwen, SmallQwen }) cat.RecordLoad(id);

        var store = Path.Combine(Path.GetTempPath(), "circleai-family-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var (id, file, bytes) in new[] { (TwoB, TwoB + "-PTQ1_0.gguf", 2_000_000), (OldQwen, "llm.mnn.weight", 5_000_000), (SmallQwen, "llm.mnn.weight", 3_000_000) })
            {
                Directory.CreateDirectory(Path.Combine(store, id));
                File.WriteAllText(Path.Combine(store, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(store, id, file), new byte[bytes]);
            }
            var said = new List<string>();

            var gone = CatalogueHousekeeping.Reclaim(cat, store, say: said.Add, readFromFlash: e => ModelFit.ReadFromFlash(e, phone));

            Assert.Equal(2, gone);
            Assert.Equal(new[] { TwoB }, Directory.EnumerateDirectories(store).Select(folder => Path.GetFileName(folder)!).ToArray());
            Assert.True(cat.IsInstalled(TwoB));
            Assert.False(cat.IsInstalled(OldQwen) || cat.IsInstalled(SmallQwen));
            Assert.Contains("reclaimed Qwen3.5-2B-MNN: outside the Bonsai 2 fleet, and Ternary-Bonsai-2-2B has loaded here", said);
            Assert.Contains("reclaimed Qwen3.5-0.8B-MNN: outside the Bonsai 2 fleet, and Ternary-Bonsai-2-2B has loaded here", said);
            Assert.Contains("freed 8 MB", said);
            Assert.Contains("planned=2 reclaimed=2", said);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }
}
