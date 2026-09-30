// HousekeepingSweepTests.cs
//
// The half of housekeeping that deletes bytes rather than rows.
//
// THE CASE THESE COME FROM. A 21.2 GB model was offered on an unmeasured mmap
// claim, fetched to 2.9 GB, and then refused by the corrected fit rule. Nothing on
// the device would ever load it again, and nothing would ever remove it either: the
// reclaim policy reasons about INSTALLED models and a half-fetched bundle was never
// installed, while the storage census counts downloaded models as precious.
//
// THE DANGEROUS CASE IS THE ONE THAT MUST PASS. This runs at start-up, and a
// download in flight is a .tmp being written to that second. A sweep that cannot
// tell those apart would end a fetch somebody is watching a progress bar for, which
// is far worse than the disk it reclaims.

using System;
using System.IO;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class HousekeepingSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "circleai-sweep-" + Guid.NewGuid().ToString("N"));

    public HousekeepingSweepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Leftover(string name, long bytes, TimeSpan age)
    {
        var model = Path.Combine(_dir, "Qwen3.6-35B-A3B-MNN");
        Directory.CreateDirectory(model);
        var path = Path.Combine(model, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void An_abandoned_download_is_swept()
    {
        var stale = Leftover("llm.mnn.weight.tmp", 4096, TimeSpan.FromHours(6));

        var freed = CatalogueHousekeeping.SweepAbandoned(_dir, TimeSpan.FromHours(1));

        Assert.Equal(4096, freed);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_download_still_in_flight_is_left_alone()
    {
        // THE ONE THAT MATTERS. Written seconds ago, which is what an active fetch
        // looks like: the sweep runs at start-up and must not kill it.
        var live = Leftover("llm.mnn.weight.tmp", 4096, TimeSpan.FromSeconds(5));

        var freed = CatalogueHousekeeping.SweepAbandoned(_dir, TimeSpan.FromHours(1));

        Assert.Equal(0, freed);
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void A_finished_model_file_is_never_touched()
    {
        // Only .tmp is leftovers. The bundle itself is the thing somebody waited for.
        var model = Path.Combine(_dir, "Qwen3.5-2B-MNN");
        Directory.CreateDirectory(model);
        var weight = Path.Combine(model, "llm.mnn.weight");
        File.WriteAllBytes(weight, new byte[2048]);
        File.SetLastWriteTimeUtc(weight, DateTime.UtcNow - TimeSpan.FromDays(30));

        Assert.Equal(0, CatalogueHousekeeping.SweepAbandoned(_dir, TimeSpan.FromHours(1)));
        Assert.True(File.Exists(weight));
    }

    [Fact]
    public void A_store_that_is_not_there_is_not_an_error()
    {
        // Runs on first launch, before anything has been downloaded.
        Assert.Equal(0, CatalogueHousekeeping.SweepAbandoned(
            Path.Combine(_dir, "nope"), TimeSpan.FromHours(1)));
    }
}
