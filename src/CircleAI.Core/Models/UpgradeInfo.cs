// UpgradeInfo.cs
//
// Types describing a detected model upgrade — the result of comparing
// what's currently installed on disk against what the catalog (embedded
// registry + ModelScope refresh) says is available.
//
// Surfaces three things to hosts:
//   1. UpgradeInfo record — one per detected upgrade
//   2. UpgradeReason enum — why we flagged it (Version drift / SHA drift / both / unknown)
//   3. InstalledManifest record — the on-disk metadata the detector reads
//
// Written by ModelDownloadService.EnsureBundleAsync on every successful
// install. Read by ModelRegistryService.CheckForUpgradesAsync at the
// next refresh. Hosts subscribe via IAIObserver.OnUpgradeAvailableAsync.

using System;
using System.Collections.Generic;

namespace CircleAI.Core.Models;

/// <summary>
/// One detected upgrade for a locally-installed model. Compared from the
/// <see cref="InstalledManifest"/> on disk against the current
/// <see cref="ModelEntry"/> in the registry.
/// </summary>
/// <param name="ModelId">The model identifier (matches <see cref="ModelEntry.Name"/>).</param>
/// <param name="InstalledVersion">
/// Version string from <c>installed.json</c>. <c>null</c> when no
/// manifest was found — see <see cref="UpgradeReason.Unknown"/>.
/// </param>
/// <param name="AvailableVersion">Version string from the registry entry.</param>
/// <param name="Reason">Why this was flagged — Version drift, SHA drift, or both.</param>
/// <param name="EstimatedDownloadBytes">
/// Sum of <see cref="BundleFile.SizeBytes"/> for files whose SHA differs
/// from the installed manifest (or whose entry is new). 0 when the
/// upgrade is Version-only and all SHAs still match.
/// </param>
/// <param name="DetectedAt">UTC timestamp the detector ran.</param>
public sealed record UpgradeInfo(
    string         ModelId,
    string?        InstalledVersion,
    string         AvailableVersion,
    UpgradeReason  Reason,
    long           EstimatedDownloadBytes,
    DateTimeOffset DetectedAt);

/// <summary>
/// Why <see cref="ModelRegistryService.CheckForUpgradesAsync"/> flagged a
/// model. Helps hosts decide how urgent the upgrade is and whether to
/// download in the background.
/// </summary>
public enum UpgradeReason
{
    /// <summary>
    /// Registry's <see cref="ModelEntry.Version"/> differs from the
    /// installed manifest's version. File SHAs may still match (the
    /// upstream model was re-versioned without a byte change — usually
    /// metadata-only).
    /// </summary>
    VersionChanged = 0,

    /// <summary>
    /// One or more file SHAs in the registry differ from the installed
    /// manifest, but the Version string is identical. Indicates a silent
    /// hotfix on the upstream side — treat as a real upgrade.
    /// </summary>
    SHAChanged = 1,

    /// <summary>
    /// Both the Version string and at least one file SHA differ. The
    /// common case for a real release.
    /// </summary>
    Both = 2,

    /// <summary>
    /// No local <c>installed.json</c> manifest was found, but the model
    /// directory exists on disk. Older installs that pre-date the
    /// upgrade-tracking feature land here. Treat as "upgrade probably
    /// available — re-download to be safe."
    /// </summary>
    Unknown = 3,
}

/// <summary>
/// On-disk record of what was installed for a given model. Written by
/// <c>ModelDownloadService.EnsureBundleAsync</c> after every successful
/// bundle download. Read by
/// <see cref="ModelRegistryService.CheckForUpgradesAsync"/>.
/// </summary>
/// <param name="ModelId">Model identifier — must match <see cref="ModelEntry.Name"/>.</param>
/// <param name="Version">Version string at install time.</param>
/// <param name="Repo">
/// ModelScope repo path (e.g. <c>MNN/Qwen3-0.6B-MNN</c>) if installed from
/// the catalog; <c>null</c> for legacy single-file installs.
/// </param>
/// <param name="TotalBytes">Sum of bundle file sizes at install time.</param>
/// <param name="Files">
/// Per-file SHA snapshot. Compared element-by-element by
/// <see cref="ModelRegistryService.CheckForUpgradesAsync"/>.
/// </param>
/// <param name="InstalledAtUtc">UTC timestamp of the install.</param>
public sealed record InstalledManifest(
    string                       ModelId,
    string                       Version,
    string?                      Repo,
    long                         TotalBytes,
    IReadOnlyList<BundleFile>    Files,
    DateTimeOffset               InstalledAtUtc);
