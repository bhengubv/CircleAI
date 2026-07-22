using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Core
{
    /// <summary>
    /// Abstraction for model file sources. Allows fallback chains for sanctions resilience
    /// (e.g. ModelScope API primary, ModelScope CDN fallback).
    /// </summary>
    public interface IModelSource
    {
        /// <summary>
        /// Friendly name of the source (e.g. "ModelScope", "HuggingFace"). Used in logs.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Quick reachability check for this source. Implementations should perform a
        /// lightweight HEAD/GET probe and return false on any failure rather than throw.
        /// </summary>
        Task<bool> IsAvailableAsync(CancellationToken ct = default);

        /// <summary>
        /// Download a single file from the given URL to the local path. Implementations
        /// should support resume (Range requests) where possible and report progress.
        /// </summary>
        Task DownloadAsync(
            string url,
            string localPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken ct = default);
    }

    /// <summary>What a download is doing right now — not all of it is transfer.</summary>
    /// <remarks>
    /// A 433 MB bundle spends real time hashing and, on a bad link, retrying.
    /// Without a phase, those look identical to a stalled download and the user
    /// concludes the app has hung.
    /// </remarks>
    public enum DownloadPhase
    {
        /// <summary>Bytes are moving.</summary>
        Downloading = 0,

        /// <summary>Continuing a partial file rather than starting over.</summary>
        Resuming,

        /// <summary>Waiting out a backoff before another attempt.</summary>
        Retrying,

        /// <summary>Checking SHA-256. No bytes move; can take seconds on a phone.</summary>
        Verifying,

        /// <summary>Already on disk and valid — skipped.</summary>
        Cached,

        /// <summary>Every file present and verified.</summary>
        Complete,
    }

    /// <summary>
    /// Snapshot of an in-flight download, suitable for UI/logging consumers.
    /// </summary>
    /// <remarks>
    /// All members are <c>init</c> and optional, so adding to this type does not
    /// break existing producers or consumers.
    /// </remarks>
    public sealed class DownloadProgress
    {
        public string FileName { get; init; } = "";
        public long BytesReceived { get; init; }
        public long TotalBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan EstimatedTimeRemaining { get; init; }

        /// <summary>What is happening — see <see cref="DownloadPhase"/>.</summary>
        public DownloadPhase Phase { get; init; } = DownloadPhase.Downloading;

        /// <summary>1-based index of the file in a bundle. 0 when not a bundle.</summary>
        public int FileIndex { get; init; }

        /// <summary>Number of files in the bundle. 0 when not a bundle.</summary>
        public int FileCount { get; init; }

        /// <summary>
        /// Current attempt, 1-based. Greater than 1 means we are recovering from
        /// a failure — worth showing, because a silent retry looks like a stall.
        /// </summary>
        public int Attempt { get; init; } = 1;

        /// <summary>Overall completion 0..1, or 0 when the total is unknown.</summary>
        public double Ratio =>
            TotalBytes > 0 ? Math.Min((double)BytesReceived / TotalBytes, 1.0) : 0.0;

        /// <summary>
        /// A line fit to put straight on screen, e.g.
        /// <c>"llm.mnn (2/7)  198,3 MB / 433,0 MB  1,7 MB/s  ETA 02:18"</c>.
        /// </summary>
        /// <remarks>
        /// Formatting lives here so every host renders it the same way, and so a
        /// caller cannot accidentally show raw byte counts to a user.
        /// </remarks>
        public string Describe()
        {
            var where = FileCount > 0 ? $"{FileName} ({FileIndex}/{FileCount})" : FileName;

            return Phase switch
            {
                DownloadPhase.Cached   => $"{where}  already downloaded",
                DownloadPhase.Verifying => $"{where}  verifying…",
                DownloadPhase.Retrying => $"{where}  retrying (attempt {Attempt})…",
                DownloadPhase.Complete => "download complete",
                _ => Sized(),
            };

            string Sized()
            {
                var moved = $"{Mb(BytesReceived)} / {Mb(TotalBytes)}";
                var rate  = BytesPerSecond > 0 ? $"  {Mb((long)BytesPerSecond)}/s" : "";
                var eta   = EstimatedTimeRemaining > TimeSpan.Zero
                    ? $"  ETA {EstimatedTimeRemaining:mm\\:ss}"
                    : "";
                var resumed = Phase == DownloadPhase.Resuming ? "  (resumed)" : "";
                return $"{where}  {moved}{rate}{eta}{resumed}";
            }

            static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.#} MB";
        }
    }
}
