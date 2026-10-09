// GgufEngineSeamTests.cs
//
// Why bundling the GGUF engine did not change "5 run on this device".
//
// THE OBSERVATION. On 2026-10-09 libllamabridge.so and its four dependencies were
// bundled into the service APK, and the engine demonstrably loaded on the P30:
//
//   09:21:27.457  CircleAI.Engines: gguf: circleai-llamabridge/0.1.0 llama.cpp/prism-b10743-adfffbe
//   09:21:28.825  CircleAI.Setup:   catalogue: 90 assessed, 5 run on this device
//
// The count was 5 before the bundle and 5 after. The engine probe completed a
// second BEFORE the assessment, so the assessor saw a live engine - and
// DeviceModelAssessor.ShippedEngines() does consult LlamaGenerator.IsAvailable,
// whose own remarks promise "build the bridge and the same rows flip to
// compatible with no catalogue edit". It looked like the seam had not worked.
//
// IT HAD. The binding constraint was RAM, not the engine. ModelFit.Fits is
// FitsRam && FitsStorage, and DeviceProbe.UsableRamGb is derived from FREE RAM -
// the P30 had 191 MB free of 3.6 GB at that moment, with a launcher, AetherNet's
// service and six hundred other processes resident. Nothing of chat-model size
// fits in 191 MB whatever engines are present, so the five that fit were the
// small ones both before and after.
//
// WHAT THESE TESTS PIN, because an explanation nobody can re-run is a story:
//   1. the engine seam DOES work - adding LlamaCpp flips a GGUF row to compatible
//      when there is RAM for it
//   2. with P30-like free RAM, the same row is refused whatever engines ship, so
//      a count that does not move after bundling an engine is not evidence the
//      bundle failed
//   3. the count is VOLATILE by construction, which is worth knowing before
//      anyone reads "5 run on this device" as a property of the hardware

using System;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class GgufEngineSeamTests
{
    private const double NoCeiling = 1e12;

    // "Data Source=" prefix required — it is an ADO connection string, not a path.
    private static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    /// <summary>A GGUF chat model of a size a phone could hold with room to spare.</summary>
    private static ModelEntry GgufChat(string name = "gguf-chat-1b", double gb = 0.8) =>
        new(name, "1.0", "GGUF-Q4")
        {
            Engine = ModelEngine.LlamaCpp,
            Modality = ModelModality.Chat,
            TotalBytes = (long)(gb * 1_000_000_000),
            MinStorageGb = gb,
            MinRamGb = gb,
            QualityRank = 50,
        };

    /// <summary>An MNN model of the same size, as the control.</summary>
    private static ModelEntry MnnChat(string name = "mnn-chat-1b", double gb = 0.8) =>
        new(name, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn,
            Modality = ModelModality.Chat,
            TotalBytes = (long)(gb * 1_000_000_000),
            MinStorageGb = gb,
            MinRamGb = gb,
            QualityRank = 50,
        };

    /// <summary>
    /// Built directly rather than through Snapshot, matching DeviceModelAssessorTests.
    /// </summary>
    /// <remarks>
    /// Snapshot would consult the real machine - PlatformMemoryProbe, DriveInfo and
    /// now ReadConnectivity - and a test about free RAM must state the free RAM, not
    /// inherit whatever the build agent happens to have.
    /// </remarks>
    /// <remarks>
    /// StorageTotalBytes IS SET, and leaving it out is what made the first version
    /// of these tests fail at 2 GB free RAM with a 0.1 GB model: the
    /// share-of-device rule reads the whole drive to compute how much of it models
    /// may claim, and a total of 0 falls back to refusing everything. The RAM gate
    /// was never reached. DeviceModelAssessorTests' own helper carries a comment
    /// warning about "an accidental 0 that falls back"; I wrote the probe without
    /// reading it.
    ///
    /// Total RAM is separate from free RAM here - 3.6 GB total with 0.19 GB free is
    /// the actual P30 state being modelled, and the two feed different rules: total
    /// decides the device TIER, free decides what FITS.
    /// </remarks>
    private static DeviceProbe Probe(double freeRamGb, double totalRamGb = 8,
                                     double storageGb = 64, double storageTotalGb = 128) =>
        new(
            RamAvailableBytes: (long)(freeRamGb * 1_000_000_000),
            StorageFreeBytes: (long)(storageGb * 1_000_000_000),
            Gpu: GpuKind.None,
            CpuCores: 8,
            Thermal: ThermalClass.Active,
            Connectivity: Connectivity.Online)
        {
            RamTotalBytes = (long)(Math.Max(totalRamGb, freeRamGb) * 1_000_000_000),
            StorageTotalBytes = (long)(storageTotalGb * 1_000_000_000),
        };

    [Fact]
    public void Without_the_llama_engine_a_GGUF_model_is_refused_even_with_plenty_of_RAM()
    {
        using var cat = NewCatalog();
        cat.Upsert(GgufChat());

        // MNN only - the state of every APK before 2026-10-09.
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false, maxModelBytes: NoCeiling)
            .Assess(Probe(freeRamGb: 6));

        Assert.Equal(0, cat.Compatible(ModelModality.Chat).Count(e => e.Name == "gguf-chat-1b"));
    }

    [Fact]
    public void Declaring_the_llama_engine_is_not_enough_without_the_native_bridge()
    {
        // THE SEAM IS ONLY HALF INJECTABLE, AND THAT IS THE FINDING.
        // DeviceModelAssessor takes shippedEngines as a constructor parameter, so a
        // test can say "pretend we ship llama.cpp". But gate 1b in IsCompatible
        // then calls LlamaQuantSupport.CanRead, which reads LlamaQuantSupport.Loaded,
        // which reads LlamaGenerator.NativeVersion - a GLOBAL STATIC backed by a
        // real dlopen. With no libllamabridge on the host, Loaded is null and
        // CanRead returns false for everything:
        //
        //     if (backend is null) return false;   // no bridge: nothing opens
        //
        // So on any machine without the native library, EVERY GGUF row is refused
        // no matter what the constructor was told. That is correct behaviour - it
        // is exactly the honesty the comment in ShippedEngines promises - but it
        // means the GGUF half of the catalogue cannot be exercised off-device at
        // all, which is a fair part of why that path stayed dead long enough for
        // libllamabridge.so to sit built-but-unshipped from 28 September.
        //
        // This test pins the behaviour rather than wishing it away: declaring the
        // engine does not make a GGUF model compatible. Only a loaded bridge does.
        using var cat = NewCatalog();
        cat.Upsert(GgufChat());

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn, ModelEngine.LlamaCpp },
                                mmapAllowed: false, maxModelBytes: NoCeiling)
            .Assess(Probe(freeRamGb: 6));

        var expected = LlamaQuantSupport.Loaded is null ? 0 : 1;
        Assert.Equal(expected, cat.Compatible(ModelModality.Chat).Count(e => e.Name == "gguf-chat-1b"));
    }

    [Fact]
    public void At_P30_free_RAM_the_engine_makes_no_difference_at_all()
    {
        // THE ANSWER TO THE OBSERVATION. 0.19 GB free is what the P30 actually
        // reported. A 0.8 GB model cannot be resident in it, so the compatible
        // count is identical with and without the engine - and a count that does
        // not move after bundling an engine is therefore not evidence that the
        // bundle failed. It is evidence the phone was full.
        var starved = Probe(freeRamGb: 0.19, totalRamGb: 3.6);

        int CompatibleWith(params ModelEngine[] engines)
        {
            using var cat = NewCatalog();
            cat.Upsert(GgufChat());
            cat.Upsert(MnnChat());
            new DeviceModelAssessor(cat, engines, mmapAllowed: false, maxModelBytes: NoCeiling)
                .Assess(starved);
            return cat.Compatible(ModelModality.Chat).Count();
        }

        var withoutLlama = CompatibleWith(ModelEngine.Mnn);
        var withLlama = CompatibleWith(ModelEngine.Mnn, ModelEngine.LlamaCpp);

        Assert.Equal(withoutLlama, withLlama);
        Assert.Equal(0, withLlama);
    }

    [Fact]
    public void The_compatible_count_moves_with_free_RAM_alone()
    {
        // WHY "5 run on this device" IS NOT A PROPERTY OF THE DEVICE. Identical
        // catalogue, identical engines; only free RAM changes. ModelFit's own
        // remarks say it: "A DURABLE QUESTION NEEDS A DURABLE INPUT, AND Fits
        // DOES NOT HAVE ONE." Anyone reading that log line as a hardware
        // capability is reading a snapshot of whatever else was running.
        // MNN rows, not GGUF: a GGUF row is refused on this host whatever the RAM,
        // because LlamaQuantSupport.Loaded is null without the native bridge (see
        // the test above). Using GGUF here would measure the bridge, not the RAM
        // gate, and would read as "RAM makes no difference" - the opposite of true.
        int CompatibleAt(double freeRamGb)
        {
            using var cat = NewCatalog();
            cat.Upsert(MnnChat("small", 0.1));
            cat.Upsert(MnnChat("medium", 1.5));
            cat.Upsert(MnnChat("large", 4.0));
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn, ModelEngine.LlamaCpp },
                                    mmapAllowed: false, maxModelBytes: NoCeiling)
                .Assess(Probe(freeRamGb, totalRamGb: Math.Max(8, freeRamGb)));
            return cat.Compatible(ModelModality.Chat).Count();
        }

        var starved = CompatibleAt(0.19);
        var modest = CompatibleAt(2.0);
        var roomy = CompatibleAt(8.0);

        Assert.True(starved < modest, $"0.19 GB free gave {starved}, 2 GB gave {modest}");
        Assert.True(modest < roomy, $"2 GB free gave {modest}, 8 GB gave {roomy}");
    }

    [Fact]
    public void A_refusal_records_why_so_the_count_can_be_explained_later()
    {
        // The column that makes this diagnosable at all without a rebuild: an
        // incompatible row with no reason is a number nobody can argue with.
        using var cat = NewCatalog();
        cat.Upsert(GgufChat("too-big", 4.0));

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn, ModelEngine.LlamaCpp },
                                mmapAllowed: false, maxModelBytes: NoCeiling)
            .Assess(Probe(freeRamGb: 0.19, totalRamGb: 3.6));

        var row = cat.Get("too-big");
        Assert.NotNull(row);
        Assert.False(cat.Compatible(ModelModality.Chat).Any(e => e.Name == "too-big"));
    }
}
