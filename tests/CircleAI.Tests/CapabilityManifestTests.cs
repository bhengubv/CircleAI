// CapabilityManifestTests.cs
//
// Makes capabilities.json LOAD-BEARING.
//
// Why this exists
// ─────────────────────────────────────────────────────────────────────────
// capabilities.json is the answer to "what can CircleAI actually do?" — read
// by developers, by agents navigating the repo, and eventually by the SDK so
// the assistant can describe itself from fact. An unverified manifest is worse
// than none: it is a confident, machine-readable source of wrong answers.
//
// On 2026-07-20 a single on-device run falsified FOUR claims in that file,
// including "Native MNN libraries DO ship" (they never reached an APK, and
// inference died with DllNotFoundException after a perfect 433 MB download).
// Every one of those claims was written from source-reading and believed.
//
// So this suite asserts the manifest cannot drift from the repo:
//   • every EntryPoint resolves to a real file, and its #Symbol is really there
//   • every VerifiedBy names a test class that actually EXISTS in this assembly
//   • 'shipping' is not claimable without both of the above
//   • Measured blocks carry Device + Date + Result — evidence, not adjectives
//
// The VerifiedBy check is the one that would have caught today's drift: when
// DeviceAwareModelSelectorTests was split, three entries still pointed at a
// name that no longer covered what they claimed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CircleAI.Tests;

public sealed class CapabilityManifestTests
{
    // ── loading ──────────────────────────────────────────────────────────

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "capabilities.json")))
                dir = dir.Parent;

            Assert.True(dir is not null,
                "capabilities.json not found walking up from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }

    private static string ManifestPath => Path.Combine(RepoRoot, "capabilities.json");

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(ManifestPath));

    private static IEnumerable<JsonElement> Entries()
    {
        using var doc = Load();
        // Clone so elements outlive the document.
        return doc.RootElement.GetProperty("Capabilities")
                  .EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    /// <summary>Every capability Id — drives the per-entry theories.</summary>
    public static IEnumerable<object[]> AllIds()
        => Entries().Select(e => new object[] { Str(e, "Id") ?? "(missing Id)" });

    private static JsonElement ById(string id)
        => Entries().Single(e => Str(e, "Id") == id);

    // ── whole-file invariants ────────────────────────────────────────────

    [Fact]
    public void Manifest_IsValidJson_AndNonEmpty()
    {
        var entries = Entries().ToList();
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Ids_AreUnique()
    {
        var ids = Entries().Select(e => Str(e, "Id")).ToList();
        var dupes = ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "Duplicate capability Ids: " + string.Join(", ", dupes));
    }

    [Fact]
    public void EveryStatus_IsADeclaredStatus()
    {
        using var doc = Load();
        var declared = doc.RootElement.GetProperty("StatusDefinitions")
                          .EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var bad = Entries()
            .Where(e => !declared.Contains(Str(e, "Status") ?? ""))
            .Select(e => $"{Str(e, "Id")}={Str(e, "Status")}")
            .ToList();

        Assert.True(bad.Count == 0,
            "Status not in StatusDefinitions: " + string.Join(", ", bad));
    }

    // ── per-entry invariants ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Entry_HasTheRequiredFields(string id)
    {
        var e = ById(id);

        Assert.False(string.IsNullOrWhiteSpace(Str(e, "Name")),    $"{id}: Name is required.");
        Assert.False(string.IsNullOrWhiteSpace(Str(e, "Summary")), $"{id}: Summary is required.");
        Assert.False(string.IsNullOrWhiteSpace(Str(e, "Status")),  $"{id}: Status is required.");

        // "Absence of limits is itself a claim" — so it must be deliberate.
        Assert.True(e.TryGetProperty("Limits", out var limits)
                    && limits.ValueKind == JsonValueKind.Array
                    && limits.GetArrayLength() > 0,
            $"{id}: Limits must list at least one honest limitation.");
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void EntryPoint_ResolvesToARealFileAndSymbol(string id)
    {
        var e = ById(id);
        var entryPoint = Str(e, "EntryPoint");

        // null is legitimate — e.g. a 'rejected' capability with no code.
        if (string.IsNullOrWhiteSpace(entryPoint)) return;

        var hash     = entryPoint!.IndexOf('#');
        var relPath  = hash >= 0 ? entryPoint[..hash] : entryPoint;
        var symbol   = hash >= 0 ? entryPoint[(hash + 1)..] : null;
        var fullPath = Path.Combine(RepoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(fullPath),
            $"{id}: EntryPoint file does not exist: {relPath}");

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var text = File.ReadAllText(fullPath);
            Assert.True(text.Contains(symbol!, StringComparison.Ordinal),
                $"{id}: EntryPoint symbol '{symbol}' not found in {relPath} — renamed or removed?");
        }
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void VerifiedBy_NamesTestClassesThatExist(string id)
    {
        var e = ById(id);
        if (!e.TryGetProperty("VerifiedBy", out var vb) || vb.ValueKind != JsonValueKind.Array)
            return;

        // Every test type in THIS assembly, by simple name.
        var known = typeof(CapabilityManifestTests).Assembly
            .GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var missing = vb.EnumerateArray()
            .Select(x => x.GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n) && !known.Contains(n!))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{id}: VerifiedBy names test class(es) that do not exist: {string.Join(", ", missing)}. " +
            "Renamed or deleted? A manifest citing a phantom test is worse than citing none.");
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Shipping_RequiresEvidence(string id)
    {
        var e = ById(id);
        if (Str(e, "Status") != "shipping") return;

        Assert.False(string.IsNullOrWhiteSpace(Str(e, "EntryPoint")),
            $"{id}: 'shipping' requires a resolvable EntryPoint.");

        var hasTests = e.TryGetProperty("VerifiedBy", out var vb)
                       && vb.ValueKind == JsonValueKind.Array
                       && vb.GetArrayLength() > 0;

        Assert.True(hasTests,
            $"{id}: 'shipping' means a test exercises the real behaviour. " +
            "Name at least one, or downgrade the status to what is true.");
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Measured_IsEvidenceNotAdjectives(string id)
    {
        var e = ById(id);
        if (!e.TryGetProperty("Measured", out var m) || m.ValueKind != JsonValueKind.Object) return;

        foreach (var field in new[] { "Device", "Date", "Result" })
        {
            Assert.True(
                m.TryGetProperty(field, out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()),
                $"{id}: Measured.{field} is required — a Measured block without it is a claim, not evidence.");
        }
    }

    [Theory]
    [MemberData(nameof(AllIds))]
    public void Package_ResolvesToARealProject(string id)
    {
        var e = ById(id);
        var pkg = Str(e, "Package");

        // "(none)" is the deliberate marker for a capability with no project.
        if (string.IsNullOrWhiteSpace(pkg) || pkg == "(none)") return;

        var dir = Path.Combine(RepoRoot, "src", pkg!);
        Assert.True(Directory.Exists(dir), $"{id}: Package '{pkg}' has no src/{pkg} directory.");
    }
}
