#nullable enable

// CapabilityManifestSkillStore.cs
//
// Lets the assistant answer "what can you do?" from FACT.
//
// Measured on a Huawei MAR-LX1M, 2026-07-20: asked what it could do, IT!
// answered "retrieving battery level or looking up product prices" — accurate,
// but only because the TOOLS block happened to be in the system prompt. It had
// no idea it runs offline, holds memory across turns, picks its own model, or
// speaks over a mesh. ISkillStore existed and AIService already injects skill
// context from it (BuildEnrichmentAsync, section 4) — nothing ever populated it.
//
// This store fills it from capabilities.json, the same manifest
// CapabilityManifestTests keeps honest.
//
// THE POINT IS HONESTY, NOT MARKETING
// ─────────────────────────────────────────────────────────────────────────
// Every entry carries its Status, and non-shipping entries say so in words the
// model cannot miss. A capability catalogue that let the assistant claim
// planned features would be a machine for confident lying — precisely the
// failure this file exists to end. "Can you do voice?" must produce "not yet",
// not an enthusiastic yes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Skills;

/// <summary>
/// Read-only <see cref="ISkillStore"/> backed by the embedded capabilities
/// manifest. Describes what the SDK can actually do, including what it cannot.
/// </summary>
public sealed class CapabilityManifestSkillStore : ISkillStore
{
    private const string ResourceName = "CircleAI.Skills.capabilities.json";

    private readonly IReadOnlyList<SkillDetail> _skills;

    /// <summary>Shared instance over the embedded manifest.</summary>
    public static CapabilityManifestSkillStore Default { get; } = new();

    /// <summary>Loads from the embedded manifest.</summary>
    public CapabilityManifestSkillStore()
        : this(ReadEmbeddedManifest()) { }

    /// <summary>Loads from caller-supplied manifest JSON. Used by tests.</summary>
    public CapabilityManifestSkillStore(string manifestJson)
        => _skills = Parse(manifestJson);

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SkillSummary>>(
            _skills.Select(ToSummary).ToList());

    /// <inheritdoc />
    public Task<SkillDetail?> GetAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(_skills.FirstOrDefault(
            s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SkillSummary>>(Array.Empty<SkillSummary>());

        var q = query.Trim();

        // ID first — it is the handle the compact listing hands out, so a lookup by
        // id has to resolve. Kept identical to the other stores.
        bool Hit(SkillDetail s)
            => s.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<IReadOnlyList<SkillSummary>>(
            _skills.Where(Hit).Select(ToSummary).ToList());
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always — the manifest is the source of truth.</exception>
    public Task<SkillDetail> UpsertAsync(string? id, SkillDraft draft, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Capabilities come from capabilities.json and are verified by CapabilityManifestTests. " +
            "Editing them at runtime would let the assistant claim things the repo cannot back up. " +
            "Change the manifest instead.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always — see <see cref="UpsertAsync"/>.</exception>
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Capabilities come from capabilities.json. Change the manifest instead.");

    // ── loading ──────────────────────────────────────────────────────────

    private static string ReadEmbeddedManifest()
    {
        var asm = typeof(CapabilityManifestSkillStore).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            // Empty rather than throwing: missing self-knowledge must never stop
            // the assistant from answering ordinary questions.
            return "{\"Capabilities\":[]}";
        }

        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<SkillDetail> Parse(string json)
    {
        var list = new List<SkillDetail>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return list; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Capabilities", out var caps) ||
                caps.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var c in caps.EnumerateArray())
            {
                var id      = Str(c, "Id");
                var name    = Str(c, "Name");
                var status  = Str(c, "Status") ?? "unknown";
                var summary = Str(c, "Summary") ?? "";

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new SkillDetail(
                    Id:           id!,
                    Name:         name!,
                    Description:  $"[{status}] {summary}",
                    Instructions: BuildInstructions(c, status, summary),
                    Tags:         BuildTags(c, status),
                    Source:       SkillSource.InMemory,
                    LastModified: DateTimeOffset.UnixEpoch));
            }
        }

        return list;
    }

    /// <summary>
    /// The text the model actually reads. Status leads, and anything not
    /// shipping says plainly that it must not be claimed.
    /// </summary>
    private static string BuildInstructions(JsonElement c, string status, string summary)
    {
        var sb = new StringBuilder();

        sb.Append("Status: ").Append(status).AppendLine();

        switch (status)
        {
            case "shipping":
                sb.AppendLine("This works and is covered by tests. You may state it plainly.");
                break;
            case "partial":
                sb.AppendLine("This works WITH LIMITS. State the limits when they are relevant; do not oversell it.");
                break;
            case "scaffold":
                sb.AppendLine("NOT USABLE YET — contracts exist but there is no working implementation. Do NOT claim you can do this.");
                break;
            case "planned":
                sb.AppendLine("DOES NOT EXIST YET. Do NOT claim you can do this. Say it is planned.");
                break;
            case "rejected":
                sb.AppendLine("DELIBERATELY NOT BUILT. Do NOT claim you can do this, and do not offer to add it.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine().AppendLine(summary);

        AppendList(sb, c, "Requires", "Requires:");
        AppendList(sb, c, "Limits",   "Limits:");

        if (c.TryGetProperty("Measured", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine().Append("Measured on ").Append(Str(m, "Device"))
              .Append(" (").Append(Str(m, "Date")).Append("): ")
              .AppendLine(Str(m, "Result"));
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendList(StringBuilder sb, JsonElement c, string prop, string heading)
    {
        if (!c.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        if (arr.GetArrayLength() == 0) return;

        sb.AppendLine().AppendLine(heading);
        foreach (var item in arr.EnumerateArray())
            sb.Append(" - ").AppendLine(item.GetString());
    }

    /// <summary>
    /// Tags drive SearchAsync, which is how a user question reaches the right
    /// entry. Splitting the dotted Id ("voice.ondevice" → voice, ondevice) is
    /// what makes "can you do voice?" match at all.
    /// </summary>
    private static IReadOnlyList<string> BuildTags(JsonElement c, string status)
    {
        var tags = new List<string> { status };

        var id = Str(c, "Id");
        if (!string.IsNullOrWhiteSpace(id))
            tags.AddRange(id!.Split('.', StringSplitOptions.RemoveEmptyEntries));

        var pkg = Str(c, "Package");
        if (!string.IsNullOrWhiteSpace(pkg) && pkg != "(none)")
            tags.Add(pkg!);

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SkillSummary ToSummary(SkillDetail s)
        => new(s.Id, s.Name, s.Description, s.Tags, s.Source);

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
