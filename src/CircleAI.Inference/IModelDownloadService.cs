#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>
/// Downloads and manages model files on disk. Supports both the legacy
/// single-file shape (one URL → one cached weight) and the new bundle shape
/// (a per-model directory with every file MNN-LLM needs to load).
/// </summary>
public interface IModelDownloadService
{
    /// <summary>
    /// Ensures a single model file is present on disk and matches
    /// <paramref name="expectedSha256"/>.
    /// </summary>
    /// <param name="modelId">Logical identifier for the model (used as filename stem).</param>
    /// <param name="downloadUri">Where to download the file from.</param>
    /// <param name="expectedSha256">
    /// Optional SHA-256 in either <c>sha256:&lt;hex&gt;</c> or bare-hex form. When
    /// provided the downloaded file is verified; a mismatch throws and the
    /// partial file is deleted.
    /// </param>
    /// <param name="progress">Optional 0-1 progress callback.</param>
    /// <returns>Absolute path to the cached file.</returns>
    Task<string> EnsureModelAsync(
        string modelId,
        Uri downloadUri,
        string? expectedSha256,
        IProgress<double>? progress,
        CancellationToken ct);

    /// <summary>
    /// Ensures every file listed in <paramref name="bundleFiles"/> is present
    /// on disk under a per-model directory and matches its pinned SHA-256.
    /// Files that already exist with the correct hash are kept; mismatches
    /// trigger a fresh download.
    /// </summary>
    /// <param name="modelId">Logical model identifier — used as the directory name.</param>
    /// <param name="repo">
    /// ModelScope repository path (e.g. <c>MNN/Qwen3-0.6B-MNN</c>). Used to
    /// build the per-file Primary + Fallback URLs.
    /// </param>
    /// <param name="bundleFiles">
    /// Sequence of (Name, Sha256, SizeBytes) tuples describing every file the
    /// model needs. Order is irrelevant.
    /// </param>
    /// <param name="progress">Optional 0-1 overall progress callback.</param>
    /// <returns>Absolute path to the model directory containing every file.</returns>
    Task<string> EnsureBundleAsync(
        string modelId,
        string repo,
        IReadOnlyList<BundleFileSpec> bundleFiles,
        IProgress<double>? progress,
        CancellationToken ct);

    /// <summary>
    /// Returns <see langword="true"/> if the model file (single-file shape) exists on disk.
    /// </summary>
    Task<bool> IsModelCachedAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Deletes the model file or directory if it exists. No-op when absent.
    /// </summary>
    Task DeleteModelAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Returns the number of free bytes available on the drive that hosts the storage directory.
    /// </summary>
    ValueTask<long> GetAvailableDiskSpaceBytesAsync(CancellationToken ct);
}

/// <summary>
/// One file in a model bundle (compatible shape with CircleAI.Core.Models.BundleFile).
/// </summary>
/// <param name="Name">Filename relative to the model directory (e.g. <c>config.json</c>).</param>
/// <param name="Sha256">
/// SHA-256 in <c>sha256:&lt;hex&gt;</c> or bare-hex form. The downloader's verify
/// path strips the optional <c>sha256:</c> prefix before comparing.
/// </param>
/// <param name="SizeBytes">Expected file size for diagnostics.</param>
public sealed record BundleFileSpec(string Name, string Sha256, long SizeBytes);
