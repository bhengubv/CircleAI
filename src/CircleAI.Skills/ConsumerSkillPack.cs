// ConsumerSkillPack.cs
//
// The consumer skill pack that makes the everyday-life Services real.
//
// THE GAP THIS FILLS. The Services grid promises money, work, grants, health -
// but the only skills the app shipped were six developer/security packs (1,378
// skills, zero consumer). So a person tapping "Work rights" reached a generic
// 0.6B improvising from base knowledge, because nothing behind the tile could
// deliver. A service with no skill behind it is a label.
//
// IN-REPO, NOT DOWNLOADED. The auto-importer pulls packs from GitHub at runtime;
// that is a network dependency this product must not have (decentralisation and
// offline first). These SKILL.md files are embedded in the assembly and parsed
// into a ConsumerSkillStore at first use - present on every head (Android AND
// the browser, which ships no SQLite library) and in tests, and a fresh clone
// builds it in.
//
// COMPOSED, NOT SHIPPED IN THE 20 MB DB. CircleAISession composes this ahead of
// the community library: manifest -> consumer -> library. See CircleAISession.Skills().

namespace CircleAI.Skills;

/// <summary>
/// The in-repo, SA-relevant consumer skill pack (work, money, grants, banking…),
/// loaded once from embedded SKILL.md resources.
/// </summary>
public static class ConsumerSkillPack
{
    /// <summary>Tag stamped on every skill in this pack, e.g. <c>pack:consumer-sa</c>.</summary>
    public const string PackName = "consumer-sa";

    private static readonly Lazy<ISkillStore> _shared =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The pack, loaded once and shared.</summary>
    public static ISkillStore Shared => _shared.Value;

    /// <summary>
    /// Parse every embedded <c>SKILL.md</c> into a fresh
    /// <see cref="ConsumerSkillStore"/>. Enumerated by the "SKILL.md" suffix so
    /// MSBuild's mangling of resource ids (folder names to identifiers) does not
    /// matter. A file that fails to parse costs its own skill, not the pack.
    /// </summary>
    public static ISkillStore Load()
    {
        var store = new ConsumerSkillStore();
        var asm = typeof(ConsumerSkillPack).Assembly;

        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = asm.GetManifestResourceStream(res);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            ParsedSkill parsed;
            try { parsed = SkillPackLoader.Parse(text, res); }
            catch { continue; }

            var tags = parsed.Tags
                .Concat(new[] { $"pack:{PackName}" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            store.Add(parsed.Id, parsed.Name, parsed.Description, parsed.Instructions, tags);
        }

        return store;
    }
}
