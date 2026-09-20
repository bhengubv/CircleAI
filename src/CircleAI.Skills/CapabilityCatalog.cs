#nullable enable

// CapabilityCatalog.cs
//
// The typed answer to "what can Circle AI do, and what not yet?".
//
// capabilities.json is the honest self-catalogue (kept true by CapabilityManifestTests).
// CapabilityManifestSkillStore already serves it to the MODEL, flattened into skill text.
// This serves it to a CONSUMER as structured data — one CapabilityEntry per module, with
// its status — so a harness can discover the surface without parsing JSON itself or
// scraping a skill description.
//
// One source of truth: both readers read the same embedded capabilities.json; neither
// holds its own copy of the data.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CircleAI.Skills;

/// <summary>A real-hardware observation attached to a capability, or none.</summary>
public sealed record CapabilityMeasurement(string Device, string Date, string Result);

/// <summary>One capability from the manifest, typed. The per-entry <see cref="Status"/>
/// is the point: a consumer can tell shipping from planned before it relies on something.</summary>
public sealed record CapabilityEntry(
    string Id,
    string Name,
    string Status,
    string Summary,
    string? Package,
    string? EntryPoint,
    string? Seam,
    IReadOnlyList<string> VerifiedBy,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Limits,
    CapabilityMeasurement? Measured);

/// <summary>Structured discovery over Circle AI's capability manifest.</summary>
public interface ICapabilityCatalog
{
    /// <summary>Every capability, in manifest order.</summary>
    IReadOnlyList<CapabilityEntry> All();

    /// <summary>One capability by id (case-insensitive), or null.</summary>
    CapabilityEntry? Find(string id);
}

/// <summary>Reads the embedded <c>capabilities.json</c> into typed entries.</summary>
public sealed class CapabilityCatalog : ICapabilityCatalog
{
    private const string ResourceName = "CircleAI.Skills.capabilities.json";

    private readonly IReadOnlyList<CapabilityEntry> _entries;

    /// <summary>Shared catalogue over the embedded manifest.</summary>
    public static CapabilityCatalog Default { get; } = new(ReadEmbeddedManifest());

    /// <summary>Builds from manifest JSON (used by <see cref="Default"/> and by tests).</summary>
    public CapabilityCatalog(string manifestJson) => _entries = Parse(manifestJson);

    /// <inheritdoc />
    public IReadOnlyList<CapabilityEntry> All() => _entries;

    /// <inheritdoc />
    public CapabilityEntry? Find(string id)
        => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    // ── loading ──────────────────────────────────────────────────────────

    private static string ReadEmbeddedManifest()
    {
        var asm = typeof(CapabilityCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null) return "{\"Capabilities\":[]}";   // never block on missing self-knowledge
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<CapabilityEntry> Parse(string json)
    {
        var list = new List<CapabilityEntry>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return list; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Capabilities", out var caps) ||
                caps.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var c in caps.EnumerateArray())
            {
                var id = Str(c, "Id");
                var name = Str(c, "Name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new CapabilityEntry(
                    Id:         id!,
                    Name:       name!,
                    Status:     Str(c, "Status") ?? "unknown",
                    Summary:    Str(c, "Summary") ?? "",
                    Package:    Str(c, "Package"),
                    EntryPoint: Str(c, "EntryPoint"),
                    Seam:       Str(c, "Seam"),
                    VerifiedBy: StrList(c, "VerifiedBy"),
                    Requires:   StrList(c, "Requires"),
                    Limits:     StrList(c, "Limits"),
                    Measured:   ReadMeasured(c)));
            }
        }

        return list;
    }

    private static CapabilityMeasurement? ReadMeasured(JsonElement c)
    {
        if (!c.TryGetProperty("Measured", out var m) || m.ValueKind != JsonValueKind.Object) return null;
        var device = Str(m, "Device");
        var date = Str(m, "Date");
        var result = Str(m, "Result");
        if (device is null || date is null || result is null) return null;
        return new CapabilityMeasurement(device, date, result);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static IReadOnlyList<string> StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                list.Add(s);
        return list;
    }
}
