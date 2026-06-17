// SkillPackAutoImporterTests.cs
//
// (2.0.2) Tests for SkillPackAutoImporter — uses a fake IPackDownloader
// that pre-stages SKILL.md content on disk so no network is needed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class SkillPackAutoImporterTests
{
    private sealed class FakeDownloader : IPackDownloader
    {
        private readonly Dictionary<string, string> _staged = new(StringComparer.OrdinalIgnoreCase);

        public FakeDownloader Stage(string sourceName, string preStagedPath)
        {
            _staged[sourceName] = preStagedPath;
            return this;
        }

        public Task<string> EnsureAsync(
            SkillPackSource source, string cacheRoot, TimeSpan cacheTtl, CancellationToken ct)
        {
            if (!_staged.TryGetValue(source.Name, out var path))
                throw new InvalidOperationException($"No staged pack for '{source.Name}'.");
            return Task.FromResult(path);
        }
    }

    private static string WriteSkillPack(string name, params (string subdir, string content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pack-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (subdir, content) in files)
        {
            var dir = Path.Combine(root, subdir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        }
        return root;
    }

    [Fact]
    public async Task KnownSkillPacks_AllRecordsArePresentAndDistinct()
    {
        // Sanity: every entry has a distinct name + URL + license.
        var names = KnownSkillPacks.All.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(KnownSkillPacks.All.Count, names.Count);
        Assert.All(KnownSkillPacks.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.StartsWith("https://github.com/", p.RepoUrl);
            Assert.False(string.IsNullOrWhiteSpace(p.License));
        });
        Assert.Contains(KnownSkillPacks.All, p => p.Name == "Anthropic-Cybersecurity-Skills");
        Assert.Contains(KnownSkillPacks.All, p => p.Name == "Privacy-Data-Protection-Skills");
        Assert.Contains(KnownSkillPacks.All, p => p.Name == "eduba-brand");
    }

    [Fact]
    public async Task ImportEnabledAsync_ImportsDefaultEnabledPacks()
    {
        var pkA = WriteSkillPack("a",
            ("skills/a1", "---\nname: a1\ndescription: A1\n---\n# A1"),
            ("skills/a2", "---\nname: a2\ndescription: A2\n---\n# A2"));
        var pkB = WriteSkillPack("b",
            ("skills/b1", "---\nname: b1\ndescription: B1\n---\n# B1"));
        try
        {
            var store = new InMemorySkillStore();
            var downloader = new FakeDownloader()
                .Stage("pack-a", pkA)
                .Stage("pack-b", pkB);

            var options = new SkillPackSourcesOptions
            {
                Sources = new List<SkillPackSource>
                {
                    new(Name: "pack-a", RepoUrl: "https://github.com/x/a", License: "MIT", SkillSubdir: "skills"),
                    new(Name: "pack-b", RepoUrl: "https://github.com/x/b", License: "MIT", SkillSubdir: "skills"),
                },
            };

            var importer = new SkillPackAutoImporter(store, options, downloader);
            var manifests = await importer.ImportEnabledAsync();

            Assert.Equal(2, manifests.Count);
            Assert.Equal(2, manifests.Single(m => m.Name == "pack-a").SkillCount);
            Assert.Equal(1, manifests.Single(m => m.Name == "pack-b").SkillCount);

            var all = await store.ListAsync();
            Assert.Equal(3, all.Count);
        }
        finally
        {
            if (Directory.Exists(pkA)) Directory.Delete(pkA, recursive: true);
            if (Directory.Exists(pkB)) Directory.Delete(pkB, recursive: true);
        }
    }

    [Fact]
    public async Task ImportEnabledAsync_SkipsDisabledPacksUnlessExplicitlyEnabled()
    {
        var pkDisabled = WriteSkillPack("disabled",
            ("skills/x", "---\nname: x\ndescription: X\n---\n# X"));
        try
        {
            var store = new InMemorySkillStore();
            var downloader = new FakeDownloader().Stage("pack-x", pkDisabled);

            var options = new SkillPackSourcesOptions
            {
                Sources = new List<SkillPackSource>
                {
                    new(Name: "pack-x", RepoUrl: "https://github.com/x/x", License: "MIT", SkillSubdir: "skills",
                        IsDefaultEnabled: false),
                },
            };

            // Default behaviour: skip disabled pack entirely.
            var manifests = await new SkillPackAutoImporter(store, options, downloader)
                .ImportEnabledAsync();
            Assert.Empty(manifests);
            Assert.Empty(await store.ListAsync());

            // Explicit enable: now it imports.
            options.ExplicitlyEnabled.Add("pack-x");
            manifests = await new SkillPackAutoImporter(store, options, downloader)
                .ImportEnabledAsync();
            Assert.Single(manifests);
            Assert.Equal(1, (await store.ListAsync()).Count);
        }
        finally { if (Directory.Exists(pkDisabled)) Directory.Delete(pkDisabled, recursive: true); }
    }

    [Fact]
    public async Task ImportEnabledAsync_PerPackFailureContinues()
    {
        var pkGood = WriteSkillPack("good",
            ("skills/g", "---\nname: g\ndescription: G\n---\n# G"));
        try
        {
            var store = new InMemorySkillStore();
            var downloader = new FakeDownloader().Stage("good", pkGood);
            // "missing" is referenced but never staged -> throws -> reported via onError.

            var options = new SkillPackSourcesOptions
            {
                Sources = new List<SkillPackSource>
                {
                    new(Name: "missing", RepoUrl: "https://github.com/x/missing", License: "MIT", SkillSubdir: "skills"),
                    new(Name: "good",    RepoUrl: "https://github.com/x/good",    License: "MIT", SkillSubdir: "skills"),
                },
            };

            var errors = new List<string>();
            var manifests = await new SkillPackAutoImporter(store, options, downloader)
                .ImportEnabledAsync(onError: (name, ex) => errors.Add($"{name}: {ex.GetType().Name}"));

            Assert.Single(manifests);
            Assert.Equal("good", manifests[0].Name);
            Assert.Contains(errors, e => e.StartsWith("missing:"));
        }
        finally { if (Directory.Exists(pkGood)) Directory.Delete(pkGood, recursive: true); }
    }

    [Fact]
    public async Task SkillPackSourcesOptions_CacheDirectoryDefaultsToLocalAppData()
    {
        var opts = new SkillPackSourcesOptions();
        Assert.False(string.IsNullOrWhiteSpace(opts.CacheDirectory));
        Assert.Contains("CircleAI", opts.CacheDirectory);
        Assert.Contains("skill-packs", opts.CacheDirectory);
    }
}
