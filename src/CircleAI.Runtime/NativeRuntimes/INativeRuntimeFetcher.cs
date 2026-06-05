// INativeRuntimeFetcher.cs
//
// The runtime-binary analogue of CircleAI.Inference.IModelDownloadService.
// Resolves the right pre-built MNN runtime for (Os, Arch, Backend), fetches
// it from ModelScope/GitHub, verifies SHA-256, extracts to a cache directory,
// and returns the absolute paths the caller P/Invokes against.

using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.NativeRuntimes;

/// <summary>
/// Pre-built MNN native runtime fetcher. Single source of truth for where
/// the on-disk runtime tree lives and how to bring it up to date.
/// </summary>
public interface INativeRuntimeFetcher
{
    /// <summary>
    /// Ensure the runtime archive matching (<paramref name="os"/>,
    /// <paramref name="arch"/>, <paramref name="backend"/>) is present and
    /// extracted under the configured cache root. Returns the install
    /// pointing at the on-disk native libraries.
    /// </summary>
    /// <param name="os">Target OS family. Pass the host's profile OS.</param>
    /// <param name="arch">Target architecture.</param>
    /// <param name="backend">Backend the caller intends to run.</param>
    /// <param name="progress">
    /// Optional download-progress reporter in the range [0.0, 1.0]. Reports
    /// 1.0 on completion. <c>null</c> to disable.
    /// </param>
    /// <param name="ct">Cancellation token. Honoured during download and SHA verify.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no registry entry exists for the requested tuple, or
    /// when SHA-256 verification fails after download.
    /// </exception>
    Task<NativeRuntimeInstall> EnsureRuntimeAsync(
        OperatingSystemKind os,
        ArchitectureKind arch,
        BackendKind backend,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns true when the runtime for the requested tuple is already
    /// extracted in the cache. Does not perform any I/O against the network.
    /// </summary>
    Task<bool> IsRuntimeCachedAsync(
        OperatingSystemKind os,
        ArchitectureKind arch,
        BackendKind backend,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the runtime bundles known to the registry for diagnostics
    /// (which platforms can be auto-fetched, what versions are pinned).
    /// </summary>
    IReadOnlyList<NativeRuntimeBundle> ListAvailableBundles();
}
