// SkillLibraryDeviceRepro.cs
//
// Reproducing, off the phone, what the phone actually did.
//
// A P30 running v11 answered "What is the weather like today?" with "Object
// reference not set to an instance of an object". The log gave the exact query
// and the exact five skills chosen:
//
//   CIRCLEAI-SKILLS full q="Things you already know about them:
//   What is the weather like today?" hits=1136
//   chosen=anthropic-skills/skill-creator,anthropic-skills/doc-coauthoring,
//          vadim-codex-skills/doc-coauthoring,
//          composio-awesome-codex-skills/connect-apps,secondsky-claude-skills/hugo
//
// so this replays it against the SAME database that ships in the APK rather
// than reasoning about the source - which this repo's own notes record being
// wrong about twice.

using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public class SkillLibraryDeviceRepro : IClassFixture<SkillLibraryFixture>
{
    // The exact query, including the enrichment prefix the head prepends.
    private const string DeviceQuery =
        "Things you already know about them:\nWhat is the weather like today?";

    private readonly SkillLibraryFixture _library;

    public SkillLibraryDeviceRepro(SkillLibraryFixture library) => _library = library;

    private string Db
    {
        get
        {
            Assert.True(_library.Available, "skills.db.zip was not found - the repro needs the shipping asset");
            return _library.DatabasePath!;
        }
    }

    [Fact]
    public void Every_skill_the_search_returns_can_then_be_read_back()
    {
        using var store = new SqliteSkillStore(Db);

        var hits = store.SearchAsync(DeviceQuery).GetAwaiter().GetResult();
        Assert.NotEmpty(hits);

        // THE PHONE TOOK THE FIRST FEW AND CALLED GetAsync ON EACH. If any of
        // them cannot be read back, the turn dies - which is what a person saw.
        foreach (var hit in hits.Take(5))
        {
            var detail = store.GetAsync(hit.Id).GetAwaiter().GetResult();
            Assert.True(detail is not null, $"search returned '{hit.Id}' and GetAsync could not read it back");
        }
    }

    [Fact]
    public void The_composite_the_phone_actually_builds_survives_the_device_query()
    {
        // THE COMBINATION THAT WAS NEVER TESTED TOGETHER. The bare SQLite store
        // passes; the phone does not use the bare store. CircleAISession.Skills()
        // puts CapabilityManifestSkillStore in FRONT of the library, and
        // SkillContextBuilder then asks the composite - so the manifest sees
        // pack-qualified ids it has never heard of, and the library sees a query
        // carrying the manifest's hits.
        using var library = new SqliteSkillStore(Db, identifyingMatchOnly: true);
        using var composite = new CompositeSkillStore(
            CapabilityManifestSkillStore.Default, library);

        var builder = new SkillContextBuilder(composite);

        // Exactly what the P30 logged. No exception is the assertion.
        var block = builder.BuildContextAsync(DeviceQuery).GetAwaiter().GetResult();

        Assert.NotNull(block);
    }

    [Fact]
    public void Reading_back_every_single_skill_in_the_library_survives()
    {
        // THE WHOLE LIBRARY, because "the five the phone happened to pick" is a
        // sample and the next question picks five others. 1,378 rows is seconds.
        using var store = new SqliteSkillStore(Db);

        var all = store.ListAsync().GetAwaiter().GetResult();
        Assert.NotEmpty(all);

        var broken = new List<string>();
        foreach (var summary in all)
        {
            try
            {
                var detail = store.GetAsync(summary.Id).GetAwaiter().GetResult();
                if (detail is null) broken.Add($"{summary.Id}: read back as null");
            }
            catch (Exception ex)
            {
                broken.Add($"{summary.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(broken.Count == 0,
            $"{broken.Count} of {all.Count} skills cannot be read back. First few:\n"
            + string.Join("\n", broken.Take(5)));
    }
}
