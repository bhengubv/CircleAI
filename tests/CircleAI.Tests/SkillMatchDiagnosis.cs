// SkillMatchDiagnosis.cs
//
// WHY does an SSRF skill match "What is the capital of France"?
//
// v16 on a P30: memory recall is now clean (0 offered), but the skill block
// still injects hunt-ssrf for a geography question - enrichment=1520 - and the
// answer stays poor. Before touching the skill ranker (OPEN-GAPS E15 is the scar
// from doing that blind), this finds the exact term that matched and measures
// the bm25 gap between this spurious hit and a real one, so any threshold is set
// on evidence rather than taste.
//
// Pure measurement. No assertion beyond "it ran".

using CircleAI.Skills;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public class SkillMatchDiagnosis : IClassFixture<SkillLibraryFixture>
{
    private readonly ITestOutputHelper _out;
    private readonly SkillLibraryFixture _lib;

    public SkillMatchDiagnosis(ITestOutputHelper output, SkillLibraryFixture library)
    {
        _out = output;
        _lib = library;
    }

    [Fact]
    public void Which_term_drags_in_the_ssrf_skill()
    {
        Assert.True(_lib.Available, "skills.db.zip not found");
        using var store = new SqliteSkillStore(_lib.DatabasePath!, identifyingMatchOnly: true);

        foreach (var q in new[] { "capital", "France", "capital France", "What is the capital of France" })
        {
            var hits = store.SearchAsync(q).GetAwaiter().GetResult();
            _out.WriteLine($"\"{q}\" -> {hits.Count} hits");
            foreach (var h in hits.Take(6)) _out.WriteLine($"    {h.Id}");
        }
    }
}
