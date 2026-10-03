// DownloadSidecar.cs
//
// THE ONE OWNER OF "HOW FAR DID THAT DOWNLOAD GET".
//
// Three pieces of code needed that answer and none of them agreed, because the
// marker that holds it was a private detail of the downloader:
//
//   - the downloader itself, resuming eight sockets where they stopped;
//   - start-up housekeeping, deciding whether an idle .tmp is progress or litter;
//   - the storage census, telling somebody how much of a 22.8 GB model is here.
//
// Measured on a Circle OS device, 2026-10-04: housekeeping deleted an 8.6 GB .tmp
// and kept its marker, because the marker does not match *.tmp and nothing told
// housekeeping it existed. The census read neither and reported the model absent.
// One suffix in three places, two of which could not see it, is the whole fault.
//
// AND THE FILE LENGTH IS NOT THE ANSWER. A segmented download PREALLOCATES its .tmp
// to the full length so eight workers can write at their own offsets, so a 21.3 GB
// .tmp can be 40% fetched and look finished. Asking the file is how a census ends up
// claiming a model is complete while a third of it is still zeroes. The marker is
// the only honest source while a segmented fetch is in flight.

using System;
using System.IO;
using System.Linq;

namespace CircleAI.Inference;

/// <summary>
/// The resume marker a segmented download writes beside its temp file, read by
/// everything that needs to know how far a download actually got.
/// </summary>
public static class DownloadSidecar
{
    /// <summary>What a download in progress calls its temp file.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>
    /// What a segmented download calls its resume marker, appended to the temp file's
    /// name. Shared rather than spelled out per caller: two spellings of this string
    /// is exactly the defect this type exists to close.
    /// </summary>
    public const string Suffix = ".parts";

    /// <summary>The marker that belongs to <paramref name="tempPath"/>.</summary>
    public static string For(string tempPath) => tempPath + Suffix;

    /// <summary>
    /// How many bytes of <paramref name="finalPath"/> are really on disk, counting a
    /// download still in flight.
    /// </summary>
    /// <param name="finalPath">Where the finished file will live.</param>
    /// <param name="wantBytes">
    /// What the catalogue says the file should be, used as a cap: a file longer than
    /// that is a different file, not progress.
    /// </param>
    /// <remarks>
    /// IN THIS ORDER, AND THE ORDER IS THE POINT. The finished file wins outright. A
    /// marker beats the temp file's length, because a segmented temp file is
    /// preallocated to the full size and its length means nothing. The temp file's
    /// length is used only when there is no marker, which is what the sequential
    /// path leaves behind. Never throws - this is asked on a loading screen.
    /// </remarks>
    public static long Fetched(string finalPath, long wantBytes)
    {
        try
        {
            if (File.Exists(finalPath))
                return Math.Min(new FileInfo(finalPath).Length, wantBytes);

            var temp = finalPath + TempSuffix;
            if (!File.Exists(temp)) return 0;

            var written = Written(For(temp));
            if (written >= 0) return Math.Min(written, wantBytes);

            return Math.Min(new FileInfo(temp).Length, wantBytes);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Bytes recorded as written across every segment in the marker at
    /// <paramref name="sidecarPath"/>, or -1 when there is no usable marker.
    /// </summary>
    /// <remarks>
    /// The lines are <c>start,current,end</c>. A segment that finished records a
    /// current of end+1, so its contribution is its full width. Anything
    /// unparseable makes the whole marker unusable: a partially-read marker would
    /// under-report, and a resume that under-reports re-fetches bytes it has.
    /// </remarks>
    public static long Written(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return -1;

            long sum = 0;
            foreach (var line in File.ReadAllLines(sidecarPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length != 3) return -1;
                if (!long.TryParse(parts[0], out var start) ||
                    !long.TryParse(parts[1], out var current) ||
                    !long.TryParse(parts[2], out var end)) return -1;
                if (end < start) return -1;

                // Clamped, because a marker claiming more than its own segment holds
                // would inflate the total past the file it describes.
                sum += Math.Clamp(current, start, end + 1) - start;
            }

            // The marker only ever covers the segmented REMAINDER; whatever a
            // sequential pass laid down first sits below the first segment's start.
            var floor = Floor(sidecarPath);
            return floor < 0 ? sum : sum + floor;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Can the file the marker describes actually hold what the marker claims?
    /// </summary>
    /// <param name="sidecarPath">The marker.</param>
    /// <param name="fileLength">
    /// The length of the temp file it belongs to, or -1 when it is not there.
    /// </param>
    /// <remarks>
    /// THE QUESTION NOBODY ASKED. The resume path checked the segment plan, the
    /// offsets and the server's Content-Range, and never whether the bytes it was
    /// resuming from still existed. They did not: start-up housekeeping had deleted
    /// the 8.6 GB temp file and kept the marker. The only reason that run did not
    /// resume into 21.3 GB of zeroes - and fail its SHA-256 hours later - is that
    /// the segment plan had changed as well, which is luck, not a guard.
    ///
    /// IT MUST BE ASKED BEFORE THE FILE IS PREALLOCATED. A segmented download
    /// extends its temp file to the full length so workers can write at their own
    /// offsets, so after that runs this can only ever answer yes.
    /// </remarks>
    public static bool Backed(string sidecarPath, long fileLength)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return false;
            if (fileLength < 0) return false;          // the bytes it describes are gone

            foreach (var line in File.ReadAllLines(sidecarPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length != 3) return false;
                if (!long.TryParse(parts[1], out var current)) return false;
                if (current > fileLength) return false; // a segment resuming over a gap
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>Where the first segment begins: bytes already down before it was planned.</summary>
    private static long Floor(string sidecarPath)
    {
        try
        {
            var first = File.ReadLines(sidecarPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (first is null) return -1;
            var parts = first.Split(',');
            return parts.Length == 3 && long.TryParse(parts[0], out var start) ? start : -1;
        }
        catch { return -1; }
    }
}
