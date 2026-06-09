// ModelUpgradeTests.cs
//
// Proves ModelRegistryService.CheckForUpgradesAsync correctly classifies
// upgrades by comparing installed.json on disk against the active registry.
// Covers all four UpgradeReason buckets (VersionChanged, SHAChanged, Both,
// Unknown) plus the no-upgrade-needed case.
//
// Also exercises ModelDownloadService.WriteInstalledManifestAsync (the
// producer side) so the round-trip is end-to-end.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelUpgradeTests
{
    [Fact]
    public async Task CheckForUpgrades_ModelNotInstalled_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.0.0",
                    new BundleFile("config.json", "abc", 100),
                    new BundleFile("llm.mnn",     "def", 200))
            });

            var upgrades = await registry.CheckForUpgradesAsync(dir);
            Assert.Empty(upgrades);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CheckForUpgrades_NoManifestButFilesExist_ReturnsUnknown()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            // Simulate a pre-feature install: the model directory exists, files
            // are present, but there's no installed.json manifest.
            var modelDir = Path.Combine(dir, "Qwen3-0.6B-MNN");
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "config.json"), "stub");

            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.0.0",
                    new BundleFile("config.json", "abc", 100))
            });

            var upgrades = await registry.CheckForUpgradesAsync(dir);

            var u = Assert.Single(upgrades);
            Assert.Equal("Qwen3-0.6B-MNN", u.ModelId);
            Assert.Null(u.InstalledVersion);
            Assert.Equal("1.0.0", u.AvailableVersion);
            Assert.Equal(UpgradeReason.Unknown, u.Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CheckForUpgrades_AllShasMatch_ReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            WriteManifest(dir, "Qwen3-0.6B-MNN", "1.0.0",
                new BundleFile("config.json", "abc", 100),
                new BundleFile("llm.mnn",     "def", 200));

            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.0.0",
                    new BundleFile("config.json", "abc", 100),
                    new BundleFile("llm.mnn",     "def", 200))
            });

            var upgrades = await registry.CheckForUpgradesAsync(dir);
            Assert.Empty(upgrades);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CheckForUpgrades_VersionDifferentSameSha_ReturnsVersionChanged()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            WriteManifest(dir, "Qwen3-0.6B-MNN", "1.0.0",
                new BundleFile("config.json", "abc", 100),
                new BundleFile("llm.mnn",     "def", 200));

            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.1.0", // version bumped, SHAs same
                    new BundleFile("config.json", "abc", 100),
                    new BundleFile("llm.mnn",     "def", 200))
            });

            var u = Assert.Single(await registry.CheckForUpgradesAsync(dir));
            Assert.Equal(UpgradeReason.VersionChanged, u.Reason);
            Assert.Equal("1.0.0", u.InstalledVersion);
            Assert.Equal("1.1.0", u.AvailableVersion);
            Assert.Equal(0, u.EstimatedDownloadBytes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CheckForUpgrades_SameVersionDifferentSha_ReturnsSHAChanged()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            WriteManifest(dir, "Qwen3-0.6B-MNN", "1.0.0",
                new BundleFile("config.json", "abc", 100),
                new BundleFile("llm.mnn",     "OLD", 200));

            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.0.0",
                    new BundleFile("config.json", "abc", 100),
                    new BundleFile("llm.mnn",     "NEW", 200)) // SHA drift
            });

            var u = Assert.Single(await registry.CheckForUpgradesAsync(dir));
            Assert.Equal(UpgradeReason.SHAChanged, u.Reason);
            Assert.Equal(200, u.EstimatedDownloadBytes); // only the drifted file
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task CheckForUpgrades_VersionAndShaBothChanged_ReturnsBoth()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            WriteManifest(dir, "Qwen3-0.6B-MNN", "1.0.0",
                new BundleFile("config.json", "abc", 100),
                new BundleFile("llm.mnn",     "OLD", 200));

            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "2.0.0",
                    new BundleFile("config.json", "abc2", 100),
                    new BundleFile("llm.mnn",     "NEW",  200))
            });

            var u = Assert.Single(await registry.CheckForUpgradesAsync(dir));
            Assert.Equal(UpgradeReason.Both, u.Reason);
            Assert.Equal(300, u.EstimatedDownloadBytes); // both files re-download
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task WriteInstalledManifest_RoundTrip_MatchesRegistry()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-upgrade-").FullName;
        try
        {
            var modelDir = Path.Combine(dir, "Qwen3-0.6B-MNN");
            Directory.CreateDirectory(modelDir);

            using var downloader = new ModelDownloadService(dir);
            var specs = new[]
            {
                new BundleFileSpec("config.json", "abc", 100),
                new BundleFileSpec("llm.mnn",     "def", 200),
            };

            await downloader.WriteInstalledManifestAsync(
                modelDir, "Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN", specs);

            // installed.json should now exist with the right shape.
            var manifestPath = Path.Combine(modelDir, "installed.json");
            Assert.True(File.Exists(manifestPath));

            // CheckForUpgradesAsync against an identical registry returns empty.
            var registry = new InMemoryRegistryWithEntries(new[]
            {
                MakeEntry("Qwen3-0.6B-MNN", "1.0.0",
                    new BundleFile("config.json", "abc", 100),
                    new BundleFile("llm.mnn",     "def", 200))
            });

            var upgrades = await registry.CheckForUpgradesAsync(dir);
            Assert.Empty(upgrades);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void WriteManifest(string storageDir, string modelId, string version, params BundleFile[] files)
    {
        var modelDir = Path.Combine(storageDir, modelId);
        Directory.CreateDirectory(modelDir);

        var manifest = new InstalledManifest(
            ModelId:        modelId,
            Version:        version,
            Repo:           "MNN/" + modelId,
            TotalBytes:     files.Sum(f => f.SizeBytes),
            Files:          files,
            InstalledAtUtc: DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(modelDir, "installed.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static ModelEntry MakeEntry(string name, string version, params BundleFile[] files) =>
        new ModelEntry(name, version, "Q4")
        {
            Repo        = "MNN/" + name,
            TotalBytes  = files.Sum(f => f.SizeBytes),
            BundleFiles = files,
        };

    /// <summary>
    /// In-memory registry that exposes an arbitrary entry list — used so the
    /// tests don't depend on the real embedded registry's contents.
    /// </summary>
    private sealed class InMemoryRegistryWithEntries : ModelRegistryService
    {
        private readonly IReadOnlyList<ModelEntry> _entries;
        public InMemoryRegistryWithEntries(IReadOnlyList<ModelEntry> entries) : base()
        {
            _entries = entries;
        }
        public override IReadOnlyList<ModelEntry> AllModels => _entries;
    }
}
