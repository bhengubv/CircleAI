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
// A PREBUILT SQLite DATABASE, WHICH IS ALSO THE SOURCE OF TRUTH. The pack ships as
// consumer.db - an embedded SqliteSkillStore (FTS5 + identifying match). The
// runtime unpacks it once and opens it, the same store the shipped library and
// long-term memory use; no Markdown is walked on the turn path, which is the I/O
// win. The SKILL.md are NOT kept in the repo - one fact, one owner, so nothing can
// drift from the database. To edit a skill, round-trip through Markdown in a
// scratch dir and rebuild the db:
//
//   dotnet run --project tools/skills-db -- --export   src/CircleAI.Skills/consumer.db <scratch>
//   # edit the SKILL.md under <scratch>, then:
//   dotnet run --project tools/skills-db -- --consumer <scratch> src/CircleAI.Skills/consumer.db
//
// A person can also add their own skill at runtime through the upload flow
// (SkillPackLoader.ImportMarkdownAsync -> a writable SQLite store).
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

    private static volatile ISkillStore? _shared;
    private static readonly object _sharedGate = new();
    private static readonly ConsumerSkillStore _emptyFallback = new();

    /// <summary>The pack, opened once and shared.</summary>
    /// <remarks>
    /// A transient open failure serves an EMPTY pack for that ONE call and is
    /// RETRIED on the next access - never cached. One hiccup must not disable the
    /// Services for the life of the process, which is exactly what the old cached
    /// Lazy did when a warm-up storm made the first open lose a construction race.
    /// SqliteSkillStore now serialises construction so the race is gone; this
    /// retry is the belt to that braces.
    /// </remarks>
    public static ISkillStore Shared
    {
        get
        {
            var cached = _shared;
            if (cached is not null) return cached;
            lock (_sharedGate)
            {
                if (_shared is not null) return _shared;
                var db = TryOpen();
                if (db is not null) return _shared = db;   // cache ONLY a real load
                return _emptyFallback;                     // transient: retry next access
            }
        }
    }

    /// <summary>
    /// Open a fresh store over the pack database, or an empty in-memory store if it
    /// cannot be opened - a missing pack costs "how do I…" answers, and the
    /// capability manifest still answers "what can you do" (the degradation
    /// <c>SkillLibrary.Open</c> also takes). A fresh instance each call;
    /// <see cref="Shared"/> caches a successful one.
    /// </summary>
    public static ISkillStore Load() => (ISkillStore?)TryOpen() ?? new ConsumerSkillStore();

    // IDENTIFYING MATCH ONLY, LIKE THE LIBRARY. A consumer skill earns its place in
    // the prompt by being ABOUT what the tile asked - its name and tags - not a
    // word buried in its body. Every skill carries its domain in its tags: work,
    // rights, money, grant, bank. See SqliteSkillStore's constructor.
    // SERIALIZE THE WHOLE CONSUMER OPEN across the process. Shared and Load both
    // land here, and on a COLD start they otherwise raced writing the same temp
    // file in ExtractDb (Load, from a test, is not under _sharedGate) - which is
    // what actually broke the pack under full-suite load. One opener at a time:
    // the first writes and opens the db, the rest see it already extracted.
    private static readonly object _openGate = new();

    private static SqliteSkillStore? TryOpen()
    {
        lock (_openGate)
        {
            // RETRY a transient construction failure. Under a cold, heavily
            // concurrent start the first open of the pack database can fail where
            // the same open a moment later, or warm, succeeds; a few short retries
            // absorb that so one hiccup does not leave the pack empty. If every
            // attempt fails, Shared serves empty for that one call and retries on
            // the next access - so a failure is never cached for the process.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { return new SqliteSkillStore(ExtractDb(), identifyingMatchOnly: true); }
                catch { if (attempt < 2) System.Threading.Thread.Sleep(40 * (attempt + 1)); }
            }
            return null;
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
