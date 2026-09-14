// MemoryBudgetTests.cs
//
// The Memory Manager's brain, tested on bytes alone - no phone.
//
// The rule under test is "prevent, do not cure": the decision to download
// something a device cannot hold is made BEFORE the download, against the
// device's own health floor (a tenth free), reclaiming only what costs the
// person nothing to get back.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class MemoryBudgetTests
{
    private const long GB = 1024L * 1024 * 1024;

    // A 32 GB phone, the cheap-handset case this is for. Floor = 3.2 GB.
    private static DeviceResources Phone(double freeGb) =>
        new(TotalDiskBytes: 32 * GB,
            FreeDiskBytes: (long)(freeGb * GB),
            TotalRamBytes: 3 * GB,
            AvailableRamBytes: 1 * GB);

    [Fact]
    public void The_floor_is_a_tenth_of_total()
        => Assert.Equal((long)(32 * GB * 0.10), MemoryBudget.FreeFloorBytes(Phone(10)));

    [Fact]
    public void The_swap_cap_is_a_tenth_of_total_disk()
        => Assert.Equal((long)(32 * GB * 0.10), MemoryBudget.SwapCapBytes(Phone(10)));

    [Fact]
    public void Plenty_of_room_just_proceeds()
    {
        // 5 GB free, wants 1 GB: 4 GB left, well above the 3.2 GB floor.
        var d = MemoryBudget.ForDownload(Phone(5), 1 * GB);
        Assert.Equal(FootprintVerdict.Fits, d.Verdict);
        Assert.Equal("", d.Message);
    }

    [Fact]
    public void Tight_but_reclaimable_frees_space_first()
    {
        // 3.5 GB free, wants 1 GB -> 2.5 GB left, below the floor. But 1 GB of
        // duplicate models / temp audio can be freed at no cost -> 3.5 GB, above.
        var d = MemoryBudget.ForDownload(Phone(3.5), 1 * GB, reclaimableBytes: 1 * GB);
        Assert.Equal(FootprintVerdict.ReclaimFirst, d.Verdict);
    }

    [Fact]
    public void No_room_and_nothing_to_reclaim_is_refused_with_a_number()
    {
        // 3.5 GB free, wants 1 GB, nothing reclaimable. 2.5 GB left vs 3.2 floor.
        var d = MemoryBudget.ForDownload(Phone(3.5), 1 * GB, reclaimableBytes: 0);
        Assert.Equal(FootprintVerdict.WontFit, d.Verdict);

        // Shortfall = floor - freeAfter = 3.2 - 2.5 = ~0.7 GB.
        Assert.Equal((long)(3.2 * GB) - (long)(2.5 * GB), d.ShortfallBytes);

        // The message names a number to act on, never just "storage full".
        Assert.Contains("Free about", d.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Reclaiming_that_still_falls_short_refuses()
    {
        // 1 GB free, wants 2 GB, only 0.5 GB reclaimable. Nowhere near the floor.
        var d = MemoryBudget.ForDownload(Phone(1), 2 * GB, reclaimableBytes: (long)(0.5 * GB));
        Assert.Equal(FootprintVerdict.WontFit, d.Verdict);
        Assert.True(d.ShortfallBytes > 0);
    }

    [Fact]
    public void A_device_it_cannot_read_yields_no_decision_not_a_guess()
    {
        var d = MemoryBudget.ForDownload(DeviceResources.Unknown, 1 * GB);
        Assert.Equal(FootprintVerdict.Unknown, d.Verdict);
    }

    [Fact]
    public void Downloading_nothing_always_fits()
        => Assert.Equal(FootprintVerdict.Fits, MemoryBudget.ForDownload(Phone(1), 0).Verdict);

    [Theory]
    [InlineData(500L * 1024 * 1024, "500 MB")]
    [InlineData(1300L * 1024 * 1024, "1.3 GB")]
    [InlineData(-1, "0 MB")]
    public void Sizes_read_like_a_person_wrote_them(long bytes, string shown)
        => Assert.Equal(shown, MemoryBudget.Human(bytes));

    [Fact]
    public void Unknown_device_has_no_floor_and_no_cap()
    {
        Assert.Equal(0, MemoryBudget.FreeFloorBytes(DeviceResources.Unknown));
        Assert.Equal(0, MemoryBudget.SwapCapBytes(DeviceResources.Unknown));
    }
}
