// ConsumerSkillPack.cs
//
// The consumer skill pack that makes the everyday-life Services real.
//
// THE GAP THIS FILLS. The Services grid promises money, work, grants, health -
// but the only skills the app shipped were developer/security packs (zero
// consumer). So a person tapping "Work rights" reached a generic 0.6B improvising
// from base knowledge, because nothing behind the tile could deliver. A service
// with no skill behind it is a label.
//
// A PREBUILT SQLite DATABASE, NOT MARKDOWN PARSED AT LAUNCH. The pack ships as
// consumer.db - an embedded, prebuilt SqliteSkillStore (FTS5 + identifying match)
// generated from the SKILL.md by tools/skills-db. The runtime unpacks it once and
// opens it, the same store the shipped library and long-term memory already use;
// no Markdown is walked on the turn path, which is the I/O win. The SKILL.md stay
// on disk under ConsumerPack/ as the human-authored SOURCE the .db is built from -
// they are no longer embedded in the assembly.
//
//   dotnet run --project tools/skills-db -- --consumer src/CircleAI.Skills/ConsumerPack src/CircleAI.Skills/consumer.db
//
// IN-REPO, NOT DOWNLOADED. The auto-importer pulls packs from GitHub at runtime;
// that is a network dependency this product must not have (decentralisation and
// offline first). consumer.db is embedded and unpacked locally, present on every
// head that loads this project and in tests, and a fresh clone builds it in.
//
// COMPOSED, NOT SHIPPED IN THE 20 MB LIBRARY DB. CircleAISession composes this
// ahead of the community library: manifest -> consumer -> user -> library. See
// CircleAISession.Skills().

namespace CircleAI.Skills;

/// <summary>
/// The in-repo, SA-relevant consumer skill pack (work, money, grants, banking…),
/// shipped as a prebuilt SQLite database and opened once as a
/// <see cref="SqliteSkillStore"/>.
/// </summary>
public static class ConsumerSkillPack
{
    /// <summary>Tag stamped on every skill in this pack, e.g. <c>pack:consumer-sa</c>.</summary>
    public const string PackName = "consumer-sa";

    /// <summary>The embedded prebuilt database, pinned by LogicalName in the csproj.</summary>
    private const string DbResource = "CircleAI.Skills.consumer.db";

    private static readonly Lazy<ISkillStore> _shared =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The pack, opened once and shared.</summary>
    public static ISkillStore Shared => _shared.Value;

    /// <summary>
    /// Unpack the embedded <c>consumer.db</c> and open it as a
    /// <see cref="SqliteSkillStore"/>. A fresh instance each call; <see cref="Shared"/>
    /// caches one. On any failure returns an empty in-memory store rather than
    /// throwing - a missing pack costs "how do I…" answers, and the capability
    /// manifest still answers "what can you do" (the same degradation
    /// <c>SkillLibrary.Open</c> takes).
    /// </summary>
    public static ISkillStore Load()
    {
        try
        {
            // IDENTIFYING MATCH ONLY, LIKE THE LIBRARY. A consumer skill earns its
            // place in the prompt by being ABOUT what the tile asked - its name and
            // its tags - not by a word buried in its body. That is why every
            // consumer SKILL.md carries the domain in its tags: work, rights, money,
            // grant, bank. See SqliteSkillStore's constructor.
            return new SqliteSkillStore(ExtractDb(), identifyingMatchOnly: true);
        }
        catch
        {
            return new ConsumerSkillStore();
        }
    }

    /// <summary>
    /// Write the embedded database to a per-content temp path and return it.
    /// </summary>
    /// <remarks>
    /// NAMED BY THE RESOURCE'S BYTE LENGTH, so a new build (new content, almost
    /// always a new length) lands in a new file and the previous one is simply left
    /// behind - the regenerable-cache stance the rest of the app takes. The write is
    /// unique-temp-then-move, so two processes racing on a cold cache (net9 and
    /// net10 test hosts, say) cannot tear each other's copy: the loser's move fails
    /// onto the winner's already-valid file and it opens that instead.
    /// </remarks>
    private static string ExtractDb()
    {
        var asm = typeof(ConsumerSkillPack).Assembly;
        using var stream = asm.GetManifestResourceStream(DbResource)
            ?? throw new InvalidOperationException($"embedded {DbResource} not found");

        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        var bytes = mem.ToArray();

        var dir = Path.Combine(Path.GetTempPath(), "circleai-consumer", bytes.Length.ToString());
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "consumer.db");

        if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
        {
            var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, target, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { /* a concurrent writer won */ } }
        }

        return target;
    }
}
