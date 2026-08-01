// RegistryDriftTests.cs
//
// TWO registry files ship, and they must agree on the facts that break users.
//
//   src/CircleAI.Core/registry.json                 — keyed by model name.
//                                                     The OUTPUT FORMAT of
//                                                     tools/recalibrate-registry-sha.
//   src/CircleAI.Core/Models/embedded_registry.json — array form, plus runtime
//                                                     fields (QualityRank,
//                                                     FallbackModelId,
//                                                     Capabilities, MinRamGb).
//                                                     The ONLY one
//                                                     ModelRegistryService reads.
//
// Both are <EmbeddedResource> in CircleAI.Core.csproj, whose comment already
// concedes registry.json is "legacy, kept for callers that load it by name".
//
// The hazard is concrete, not cosmetic: the TOOL writes registry.json and a
// human transcribes into embedded_registry.json. If a re-run refreshes hashes
// in one and nobody copies them across, the runtime downloads a bundle and
// fails SHA-256 verification — after spending the user's data. On the measured
// Huawei run that was 429 MB before anything could have gone wrong.
//
// (Today is a live example of the transcription gap: Capabilities / MinRamGb /
// MinStorageGb were added to embedded_registry.json only.)
//
// So: do not compare the files wholesale — they legitimately differ in shape
// and in runtime-only fields. Compare the facts that must never diverge.
//
// And note there are now TWO ways a model gets catalogued: the ModelScope tool
// that writes registry.json, and the HuggingFace voice bucket
// (thegeekco/circleai-voices) that only ever lands in embedded_registry.json.
// "Present in both files" is therefore no longer a usable stand-in for "someone
// checked this hash" — see EveryRuntimeModelDeclaresAVerifiableSourceAndRealHashes,
// which asserts that property directly instead of inferring it from provenance.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CircleAI.Tests;

public sealed class RegistryDriftTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "capabilities.json")))
                dir = dir.Parent;
            Assert.True(dir is not null, "repo root not found from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }

    private static string LegacyPath   => Path.Combine(RepoRoot, "src", "CircleAI.Core", "registry.json");
    private static string EmbeddedPath => Path.Combine(RepoRoot, "src", "CircleAI.Core", "Models", "embedded_registry.json");

    private sealed record FileFact(string Name, string Sha, long Size);

    /// <summary>modelName → (fileName → fact), from the keyed legacy file.</summary>
    private static Dictionary<string, Dictionary<string, FileFact>> LegacyBundles()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LegacyPath));
        var result = new Dictionary<string, Dictionary<string, FileFact>>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;          // skip "Notes"
            if (!prop.Value.TryGetProperty("BundleFiles", out var files)) continue;

            result[prop.Name] = files.EnumerateArray()
                .Select(f => new FileFact(
                    f.GetProperty("Name").GetString() ?? "",
                    f.GetProperty("Sha256").GetString() ?? "",
                    f.GetProperty("SizeBytes").GetInt64()))
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>modelName → (fileName → fact), from the array-form runtime file.</summary>
    private static Dictionary<string, Dictionary<string, FileFact>> EmbeddedBundles()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(EmbeddedPath));
        var result = new Dictionary<string, Dictionary<string, FileFact>>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in doc.RootElement.GetProperty("Models").EnumerateArray())
        {
            var name = m.GetProperty("Name").GetString() ?? "";
            if (!m.TryGetProperty("BundleFiles", out var files)) continue;

            result[name] = files.EnumerateArray()
                .Select(f => new FileFact(
                    f.GetProperty("Name").GetString() ?? "",
                    f.GetProperty("Sha256").GetString() ?? "",
                    f.GetProperty("SizeBytes").GetInt64()))
                .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    [Fact]
    public void BothRegistriesExist()
    {
        Assert.True(File.Exists(LegacyPath),   "legacy registry.json is missing: " + LegacyPath);
        Assert.True(File.Exists(EmbeddedPath), "embedded_registry.json is missing: " + EmbeddedPath);
    }

    [Fact]
    public void EveryModelInBothFiles_AgreesOnFileHashesAndSizes()
    {
        var legacy   = LegacyBundles();
        var embedded = EmbeddedBundles();

        var shared = legacy.Keys.Intersect(embedded.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(shared.Count > 0, "The two registries share no model names at all — that is drift by itself.");

        var problems = new List<string>();

        foreach (var model in shared)
        {
            foreach (var (fileName, legacyFact) in legacy[model])
            {
                if (!embedded[model].TryGetValue(fileName, out var embFact))
                {
                    problems.Add($"{model}/{fileName}: present in registry.json, missing from embedded_registry.json");
                    continue;
                }

                if (!string.Equals(legacyFact.Sha, embFact.Sha, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{model}/{fileName}: SHA-256 differs (legacy {legacyFact.Sha[..12]}… vs embedded {embFact.Sha[..12]}…)");

                if (legacyFact.Size != embFact.Size)
                    problems.Add($"{model}/{fileName}: SizeBytes differs ({legacyFact.Size} vs {embFact.Size})");
            }
        }

        Assert.True(problems.Count == 0,
            "Registry drift — the runtime would download bundles and fail SHA verification " +
            "AFTER spending the user's data:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void ModelsInTheLegacyFileAreAllVisibleToTheRuntime()
    {
        var onlyLegacy = LegacyBundles().Keys
            .Except(EmbeddedBundles().Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(onlyLegacy.Count == 0,
            "In registry.json but never transcribed into embedded_registry.json, " +
            "so the runtime cannot see it: " + string.Join(", ", onlyLegacy));
    }

    [Fact]
    public void EveryRuntimeModelDeclaresAVerifiableSourceAndRealHashes()
    {
        // This replaces "every runtime model must also appear in registry.json".
        //
        // That rule was a PROXY for the thing that matters — no model ships with a
        // hash nobody checked — and it worked while ModelScope, via
        // recalibrate-registry-sha, was the only way a model got catalogued. The 58
        // voices now come from a HuggingFace bucket instead, so the proxy started
        // reporting a provenance it simply did not know about, and a green build
        // would have required either hand-writing them into the tool's own output
        // file or deleting the check.
        //
        // So assert the real invariant directly: every bundled file names a source
        // and carries a hash that is actually a SHA-256. That still catches the
        // hazard the original was written for — a hand-added entry with an empty,
        // truncated or placeholder digest, which the runtime only discovers AFTER
        // spending the user's data. On the measured Huawei run that was 429 MB.
        using var doc = JsonDocument.Parse(File.ReadAllText(EmbeddedPath));
        var problems = new List<string>();

        foreach (var m in doc.RootElement.GetProperty("Models").EnumerateArray())
        {
            var name = m.GetProperty("Name").GetString() ?? "(unnamed)";

            if (!m.TryGetProperty("Repo", out var repo) || string.IsNullOrWhiteSpace(repo.GetString()))
                problems.Add($"{name}: no Repo — nothing says where this came from or how to re-verify it");

            if (!m.TryGetProperty("BundleFiles", out var files) || files.GetArrayLength() == 0)
            {
                problems.Add($"{name}: no BundleFiles — nothing to download or verify");
                continue;
            }

            foreach (var f in files.EnumerateArray())
            {
                var fileName = f.GetProperty("Name").GetString() ?? "(unnamed)";
                var sha      = f.GetProperty("Sha256").GetString() ?? "";

                if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                    problems.Add($"{name}/{fileName}: Sha256 is not a SHA-256 ('{sha}')");
                else if (sha.All(c => c == '0'))
                    problems.Add($"{name}/{fileName}: Sha256 is all zeroes — a placeholder, not a digest");

                if (f.GetProperty("SizeBytes").GetInt64() <= 0)
                    problems.Add($"{name}/{fileName}: SizeBytes is not positive");
            }
        }

        Assert.True(problems.Count == 0,
            "Models that would fail verification only after the download:\n  " +
            string.Join("\n  ", problems));
    }
}
