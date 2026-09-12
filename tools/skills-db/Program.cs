// skills-db — build the shipped skill database from a directory of SKILL.md files.
//
// WHY A TOOL AND NOT A BUILD STEP. The corpus is 1,405 SKILL.md files living
// OUTSIDE this repo, on whichever machine curated them. A build that reached for
// them would fail on every other machine and in every clone - the repo's own
// housekeeping rule is that a fresh checkout must be enough to work. So this runs
// by hand when the library changes, and its OUTPUT is committed.
//
// NO IMPORTER HERE, DELIBERATELY. SkillPackLoader already walks a pack
// recursively, parses exactly this frontmatter, yields lazily because it was
// written for 1000+ community skills, and stamps a pack: tag on every skill it
// imports. Writing a second one would be writing a second answer.
//
//   dotnet run --project tools/skills-db -- <skills-root> [output.db]
//
// <skills-root> is a directory holding skill packs. Every immediate subdirectory
// is treated as one pack and named after itself; the root is also scanned in case
// it holds SKILL.md files directly.

using CircleAI.Skills;

var root = args.Length > 0 ? args[0] : null;
var output = args.Length > 1
    ? args[1]
    : Path.Combine(Directory.GetCurrentDirectory(), "skills.db");

if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
{
    Console.Error.WriteLine("usage: dotnet run --project tools/skills-db -- <skills-root> [output.db]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  <skills-root>  a directory of skill packs, each holding SKILL.md files");
    Console.Error.WriteLine("  [output.db]    where to write (default: ./skills.db)");
    return 2;
}

// A FRESH DATABASE EVERY TIME. This is a derived artefact built from a directory
// of files - rebuilding it from a previous run would carry forward skills that
// have since been deleted upstream, which is the one failure a generator can have
// that nobody notices until a phone recites something that no longer exists.
foreach (var stale in new[] { output, output + "-wal", output + "-shm" })
    if (File.Exists(stale)) File.Delete(stale);

using var store = new SqliteSkillStore(output);

Console.WriteLine($"index: {(store.FullTextAvailable ? "FTS5" : "LIKE fallback - NO FTS5 IN THIS BUILD")}");
Console.WriteLine($"root:  {Path.GetFullPath(root)}");
Console.WriteLine();

var warnings = 0;
void OnWarning(string file, Exception ex)
{
    warnings++;
    // FIRST TEN ONLY. A malformed pack can produce hundreds, and a wall of
    // identical parse errors buries the one that is actually different.
    if (warnings <= 10)
        Console.Error.WriteLine($"  ! {Path.GetFileName(file)}: {ex.Message}");
}

var total = 0;
var packs = 0;

// WHAT COUNTS AS A PACK, from the shape the library actually has rather than a
// rule invented first. Measured over 1,405 SKILL.md files:
//
//   01 - Concierge Curated Skills/<skill>/SKILL.md          70   the folder IS the pack
//   02 - Downloaded Skill Sources/<repo>/.../SKILL.md     1335   each REPO is a pack
//
// So: a directory is a pack when EVERY child of it that has skills at all has
// one sitting directly inside; otherwise each child is its own pack.
//
// THE "EVERY" IS NOT PEDANTRY - AN EARLIER VERSION SAID "ANY" AND WAS WRONG ON
// THE REAL LIBRARY. bhengubv-loki-mode is a single-skill repo with SKILL.md at
// its own root, so "any child holds one directly" was true of the DOWNLOADED
// folder, which then imported as ONE pack and collapsed eleven separate
// upstream repos into a single unattributable name. One outlier, and a rule
// drawn from the majority shape swallowed the other ten.
foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
{
    if (HasSkillsDirectlyBelow(dir))
    {
        total += await Import(dir, Name(dir));
        continue;
    }

    foreach (var inner in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        total += await Import(inner, Name(inner));
}

Console.WriteLine();
Console.WriteLine($"{total} skills from {packs} pack(s) -> {output}");
if (warnings > 0)
    Console.WriteLine($"{warnings} file(s) could not be parsed and were skipped");

// SAY WHAT IT WEIGHS. This file is about to be committed and shipped inside an
// APK, so its size is a number somebody should see before that happens rather
// than discover in a build log.
var size = new FileInfo(output).Length;
Console.WriteLine($"{size / 1_000_000.0:0.#} MB on disk");

return total > 0 ? 0 : 1;

static bool HasSkillsDirectlyBelow(string dir)
{
    var withSkills = Directory.EnumerateDirectories(dir)
        .Where(HasAnySkill)
        .ToList();

    return withSkills.Count > 0
        && withSkills.All(c => File.Exists(Path.Combine(c, SkillPackLoader.DefaultSkillFile)));
}

static bool HasAnySkill(string dir)
{
    try
    {
        return Directory
            .EnumerateFiles(dir, SkillPackLoader.DefaultSkillFile, SearchOption.AllDirectories)
            .Any();
    }
    catch { return false; }   // an unreadable directory is not a pack
}

async Task<int> Import(string dir, string pack)
{
    var before = (await store.ListAsync()).Count;
    var found = 0;

    // LoadAsync, NOT ImportAsync, AND FOR ONE REASON: THE ID.
    //
    // ImportAsync upserts under the skill's own slug, and the first run of this
    // tool reported "1335 skills from 1405 files - 70 replaced an earlier
    // skill". Eleven community repos independently ship a skill called
    // "brand-guidelines", or "code-review", or "pdf"; under a bare slug the
    // eleventh silently overwrites the other ten and FIVE PER CENT OF THE
    // LIBRARY DISAPPEARS with nothing in the output to say so.
    //
    // Qualifying the id by pack keeps all of them and makes provenance a
    // property of the key rather than only a tag. Within one pack a repeated
    // name still collapses, which is correct - that is the same skill twice.
    //
    // The PARSER is still SkillPackLoader's, which is what was worth reusing;
    // what is not reused is four lines of upsert loop that had the wrong key.
    await foreach (var parsed in SkillPackLoader.LoadAsync(
                       dir, SkillPackLoader.DefaultSkillFile, OnWarning))
    {
        var draft = new SkillDraft(
            Name:         parsed.Name,
            Description:  parsed.Description,
            Instructions: parsed.Instructions,
            Tags:         parsed.Tags.Concat([$"pack:{pack}"])
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToArray());

        await store.UpsertAsync($"{pack}/{parsed.Id}", draft);
        found++;
    }

    if (found == 0) return 0;
    packs++;

    var added = (await store.ListAsync()).Count - before;

    // FOUND VERSUS ADDED, STILL PRINTED. The gap is now only ever a duplicate
    // WITHIN one pack, which is a real duplicate - but it is the number that
    // caught the cross-pack collision, so it stays.
    var note = added == found ? "" : $"  ({found - added} duplicate name(s) within the pack)";
    Console.WriteLine($"  {pack,-34} {added,5}{note}");

    return added;
}

static string Name(string dir)
{
    var raw = new DirectoryInfo(dir.TrimEnd(Path.DirectorySeparatorChar)).Name;

    // "01 - Concierge Curated Skills" -> "concierge-curated-skills". The ordering
    // prefix is how a person keeps a folder list tidy; it is not part of the
    // pack's name, and it would end up on every skill as pack:01-….
    var trimmed = raw;
    var dash = raw.IndexOf(" - ", StringComparison.Ordinal);
    if (dash > 0 && raw[..dash].All(char.IsDigit)) trimmed = raw[(dash + 3)..];

    var slug = new string(trimmed.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    while (slug.Contains("--", StringComparison.Ordinal))
        slug = slug.Replace("--", "-", StringComparison.Ordinal);
    return slug.Trim('-');
}
