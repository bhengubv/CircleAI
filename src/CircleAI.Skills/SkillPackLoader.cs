// SkillPackLoader.cs
//
// (2.0.1) Loads Claude Code-style skill packs into the existing
// ISkillStore. Each skill lives in a directory containing SKILL.md
// with YAML frontmatter (name + description) and a markdown body
// that becomes the skill's Instructions.
//
// Compatible with:
//   - bhengubv/Claude-BugHunter (51 skills, 681 disclosed-report patterns)
//   - bhengubv/awesome-agent-skills (1000+ community skills)
//   - any Claude Code-format skill pack on disk

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Skills;

/// <summary>
/// (2.0.1) Description of a skill pack — name, version, where it
/// came from. Persisted alongside imported skills so a consumer can
/// see which pack a given skill originated from.
/// </summary>
/// <param name="Name">e.g. "Claude-BugHunter".</param>
/// <param name="Version">Pack version or short commit; "unknown" when unset.</param>
/// <param name="SourceUrl">Canonical repo URL.</param>
/// <param name="License">SPDX identifier, e.g. "MIT" or "Apache-2.0".</param>
/// <param name="SkillCount">How many skills loaded from this pack.</param>
public sealed record SkillPackManifest(
    string Name,
    string Version,
    string SourceUrl,
    string License,
    int    SkillCount);

/// <summary>
/// One parsed skill straight from a SKILL.md file.
/// </summary>
public sealed record ParsedSkill(
    string                Id,
    string                Name,
    string                Description,
    string                Instructions,
    IReadOnlyList<string> Tags,
    string                SourceFilePath);

/// <summary>
/// (2.0.1) Walks a skill-pack directory, reads each SKILL.md file,
/// parses YAML frontmatter + markdown body, and returns the loaded
/// skills. Lazily yields so big packs (1000+ skills) don't have to
/// materialise into a single list when the caller wants to stream.
/// </summary>
public static class SkillPackLoader
{
    /// <summary>
    /// Default file name the loader searches for. Override via
    /// <see cref="LoadAsync"/>'s parameter for non-standard packs.
    /// </summary>
    public const string DefaultSkillFile = "SKILL.md";

    /// <summary>
    /// Scan <paramref name="root"/> recursively for files matching
    /// <paramref name="skillFile"/>, parse each, and yield the
    /// resulting <see cref="ParsedSkill"/> records. Skips files that
    /// fail to parse, with the failure raised on the optional
    /// <paramref name="onWarning"/> callback.
    /// </summary>
    public static async IAsyncEnumerable<ParsedSkill> LoadAsync(
        string root,
        string skillFile = DefaultSkillFile,
        Action<string, Exception>? onWarning = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Skill pack root not found: {root}");

        foreach (var file in Directory.EnumerateFiles(root, skillFile, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            ParsedSkill? skill = null;
            try
            {
                var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                skill = Parse(text, file);
            }
            catch (Exception ex)
            {
                onWarning?.Invoke(file, ex);
            }
            if (skill is not null) yield return skill;
        }
    }

    /// <summary>
    /// Import every parsed skill into <paramref name="store"/> via
    /// <see cref="ISkillStore.UpsertAsync"/>. Returns a manifest with
    /// the count of skills imported.
    /// </summary>
    public static async Task<SkillPackManifest> ImportAsync(
        ISkillStore       store,
        string            root,
        string            packName,
        string            packVersion  = "unknown",
        string            sourceUrl    = "",
        string            license      = "unknown",
        string            skillFile    = DefaultSkillFile,
        Action<string, Exception>? onWarning = null,
        CancellationToken ct           = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(packName);

        var count = 0;
        await foreach (var parsed in LoadAsync(root, skillFile, onWarning, ct))
        {
            ct.ThrowIfCancellationRequested();
            var draft = new SkillDraft(
                Name:         parsed.Name,
                Description:  parsed.Description,
                Instructions: parsed.Instructions,
                Tags:         parsed.Tags.Concat(new[] { $"pack:{packName.ToLowerInvariant()}" })
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToArray());
            await store.UpsertAsync(parsed.Id, draft, ct).ConfigureAwait(false);
            count++;
        }
        return new SkillPackManifest(packName, packVersion, sourceUrl, license, count);
    }

    // ─────────────────────────────────────────────────────────────────────
    // YAML-frontmatter parser. Lenient — accepts the small subset Claude
    // Code skills use (name, description, optional metadata block). We
    // don't need a full YAML library for that.
    // ─────────────────────────────────────────────────────────────────────

    private static readonly Regex FrontmatterRegex = new(
        @"^\s*---\s*\r?\n(?<body>[\s\S]*?)\r?\n---\s*\r?\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse a single SKILL.md file's text. <paramref name="sourceFilePath"/>
    /// is informational — used as a fallback when no name/heading can be
    /// extracted. Throws <see cref="ArgumentException"/> when content is
    /// null/empty.
    /// </summary>
    public static ParsedSkill Parse(string content, string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        var fmMatch = FrontmatterRegex.Match(content);
        string fmBody;
        string mdBody;
        if (fmMatch.Success)
        {
            fmBody = fmMatch.Groups["body"].Value;
            mdBody = content[fmMatch.Length..].TrimStart('\r', '\n');
        }
        else
        {
            // No frontmatter — derive name from first heading.
            fmBody = "";
            mdBody = content;
        }

        var name        = ExtractField(fmBody, "name")
                          ?? ExtractFirstHeading(mdBody)
                          ?? Path.GetFileNameWithoutExtension(sourceFilePath);
        var description = ExtractField(fmBody, "description")
                          ?? Truncate(mdBody, 280);
        var tags        = ExtractTags(fmBody);

        // Stable id from name (slug-cased).
        var id = Slugify(name);

        return new ParsedSkill(
            Id:             id,
            Name:           name,
            Description:    description,
            Instructions:   mdBody.Trim(),
            Tags:           tags,
            SourceFilePath: sourceFilePath);
    }

    private static string? ExtractField(string fmBody, string field)
    {
        if (string.IsNullOrEmpty(fmBody)) return null;
        // Accept "field: value" or "field: >\n  value lines"
        var simple = Regex.Match(
            fmBody,
            $@"^\s*{Regex.Escape(field)}\s*:\s*(?<v>.*)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!simple.Success) return null;
        var value = simple.Groups["v"].Value.Trim();
        // Trim outer quotes if present.
        if (value.Length >= 2 &&
            ((value[0] == '"'  && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static IReadOnlyList<string> ExtractTags(string fmBody)
    {
        if (string.IsNullOrEmpty(fmBody)) return Array.Empty<string>();
        // Look for "tags: [a, b, c]" or "tags:\n  - a\n  - b".
        var inline = Regex.Match(fmBody, @"^\s*tags\s*:\s*\[(?<v>[^\]]*)\]",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (inline.Success)
        {
            return inline.Groups["v"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('\'', '"'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }
        var block = Regex.Match(fmBody,
            @"^\s*tags\s*:\s*\r?\n(?<v>(?:\s+-\s+\S+\s*\r?\n?)+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (block.Success)
        {
            return block.Groups["v"].Value
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('-').Trim().Trim('\'', '"'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }
        return Array.Empty<string>();
    }

    private static string? ExtractFirstHeading(string mdBody)
    {
        var m = Regex.Match(mdBody, @"^#\s+(?<v>.+)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        var slug = sb.ToString().TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "unnamed" : slug;
    }
}
