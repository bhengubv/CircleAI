// DownloadSidecarTests.cs
//
// THE 8.6 GB THESE COME FROM. A Circle OS device spent three days running a 2 B
// model while the 35 B it had been told to fetch sat at 1.1 GB of 22.8 GB. Three
// separate pieces of code each held one true fact and none of them met:
//
//   the downloader   wrote llm.mnn.weight.tmp + a .parts marker of eight positions
//   housekeeping     deleted the idle .tmp as litter and kept the marker
//   the census       read neither, and reported the model absent
//
// So the sweep destroyed the progress, the marker outlived the bytes it described,
// and the one surface that could have said so said nothing. Each of those is a test
// below.
//
// AND THE LENGTH TRAP, which is the subtle one: a segmented download PREALLOCATES
// its temp file to the full size so eight workers can write at their own offsets.
// Anything that answers "how far along is it?" by measuring the file reports 100%
// the moment the first socket opens.

using System;
using System.IO;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class DownloadSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "circleai-sidecar-" + Guid.NewGuid().ToString("N"));

    public DownloadSidecarTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private void Write(string name, long length)
    {
        using var fs = new FileStream(Path_(name), FileMode.Create);
        fs.SetLength(length);
    }

    [Fact]
    public void A_finished_file_counts_its_own_length()
    {
        Write("llm.mnn.weight", 2048);

        Assert.Equal(2048, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void A_file_longer_than_the_catalogue_says_is_capped()
    {
        // A different file, not progress. Counting it would report more downloaded
        // than there is to download, and a census above 100% is not a census.
        Write("llm.mnn.weight", 9000);

        Assert.Equal(2048, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void Nothing_on_disk_is_zero_not_an_error()
        => Assert.Equal(0, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));

    [Fact]
    public void A_sequential_download_in_flight_counts_its_temp_file()
    {
        // No marker: the sequential path appends, so the temp file's length IS the
        // progress. This is the 1.1 GB that used to read as zero.
        Write("llm.mnn.weight.tmp", 600);

        Assert.Equal(600, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void A_segmented_download_counts_the_marker_not_the_preallocated_length()
    {
        // THE LENGTH TRAP. The temp file is already full-size and almost entirely
        // zeroes; the marker says two of the four segments are done.
        Write("llm.mnn.weight.tmp", 2048);
        File.WriteAllLines(Path_("llm.mnn.weight.tmp.parts"),
        [
            "0,512,511",        // done   (current = end + 1)
            "512,1024,1023",    // done
            "1024,1024,1535",   // not started
            "1536,1536,2047",   // not started
        ]);

        Assert.Equal(1024, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void Bytes_laid_down_before_the_segments_were_planned_still_count()
    {
        // The real marker off the device started at 1 304 821 760: a sequential pass
        // had already put 1.3 GB down and only the REMAINDER was split. Counting
        // just the segments would lose every byte below the first one's start.
        Write("llm.mnn.weight.tmp", 2048);
        File.WriteAllLines(Path_("llm.mnn.weight.tmp.parts"),
        [
            "1024,1536,1535",   // the first 1024 were already there, this one is done
            "1536,1536,2047",
        ]);

        Assert.Equal(1536, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void A_marker_nothing_can_parse_is_not_half_believed()
    {
        // A partially-read marker under-reports, and a resume that under-reports
        // re-fetches bytes it already has. Unusable means unusable.
        Write("llm.mnn.weight.tmp", 2048);
        File.WriteAllLines(Path_("llm.mnn.weight.tmp.parts"), ["0,512,511", "not a line at all"]);

        Assert.Equal(-1, DownloadSidecar.Written(Path_("llm.mnn.weight.tmp.parts")));
        // Falls back to the file's own length rather than to a wrong number.
        Assert.Equal(2048, DownloadSidecar.Fetched(Path_("llm.mnn.weight"), 2048));
    }

    [Fact]
    public void No_marker_reads_as_no_marker_rather_than_as_zero_progress()
        => Assert.Equal(-1, DownloadSidecar.Written(Path_("nothing.tmp.parts")));

    [Fact]
    public void A_marker_whose_file_is_gone_is_not_backed()
    {
        // THE EXACT STATE OFF THE DEVICE: 278 bytes of marker, no .tmp. Resuming
        // from it writes at offsets up to 20 GB into a file that starts empty, and
        // the SHA-256 fails after hours of downloading.
        File.WriteAllText(Path_("llm.mnn.weight.tmp.parts"), "1304821760,1656591935,3809989682\n");

        Assert.False(DownloadSidecar.Backed(Path_("llm.mnn.weight.tmp.parts"), -1));
    }

    [Fact]
    public void A_marker_claiming_more_than_the_file_holds_is_not_backed()
    {
        // Truncated by anything - a full disk, a kill mid-write, a copy that stopped.
        File.WriteAllLines(Path_("w.tmp.parts"), ["0,512,511", "512,1600,2047"]);

        Assert.False(DownloadSidecar.Backed(Path_("w.tmp.parts"), 1024));
    }

    [Fact]
    public void A_marker_its_file_can_hold_is_backed()
    {
        File.WriteAllLines(Path_("w.tmp.parts"), ["0,512,511", "512,900,2047"]);

        Assert.True(DownloadSidecar.Backed(Path_("w.tmp.parts"), 2048));
    }

    [Fact]
    public void A_marker_that_is_not_there_is_not_backed()
        => Assert.False(DownloadSidecar.Backed(Path_("nothing.tmp.parts"), 2048));
}
