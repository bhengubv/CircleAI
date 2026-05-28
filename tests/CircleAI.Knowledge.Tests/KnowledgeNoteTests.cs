// KnowledgeNoteTests.cs
//
// KnowledgeNote.ToFileText must round-trip through KnowledgeNote.ParseFile
// for arbitrary content — including 10 randomised cases.

using CircleAI.Knowledge;
using Xunit;

namespace CircleAI.Knowledge.Tests;

public sealed class KnowledgeNoteTests
{
    [Fact]
    public void ToFileText_RoundTripsThroughParseFile_TenRandomCases()
    {
        // Deterministic-ish randomisation so a failure is reproducible.
        var rng = new Random(0xC0FFEE);

        for (int i = 0; i < 10; i++)
        {
            var id = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow.AddMinutes(-rng.Next(0, 100000));
            var updated = created.AddSeconds(rng.Next(0, 600));

            var frontmatter = new Dictionary<string, string>
            {
                ["mood"] = RandomScalar(rng),
                ["source"] = RandomScalar(rng),
                ["case_index"] = i.ToString(),
            };
            var tags = new[] { "case-" + i, "random", RandomScalar(rng) };

            var body = $"# Random case {i}\n\nSome **markdown** body with {RandomScalar(rng)}.\n\nLine 2.";

            var original = new KnowledgeNote(
                Id: id,
                Title: "Case " + i,
                BodyMarkdown: body,
                Frontmatter: frontmatter,
                Tags: tags,
                CreatedAt: created,
                UpdatedAt: updated);

            var text = original.ToFileText();
            var parsed = KnowledgeNote.ParseFile(text);

            Assert.Equal(original.Id, parsed.Id);
            Assert.Equal(original.Title, parsed.Title);
            Assert.Equal(original.BodyMarkdown, parsed.BodyMarkdown);
            Assert.Equal(original.CreatedAt, parsed.CreatedAt);
            Assert.Equal(original.UpdatedAt, parsed.UpdatedAt);
            Assert.Equal(original.Tags, parsed.Tags);
            Assert.Equal(frontmatter["mood"], parsed.Frontmatter["mood"]);
            Assert.Equal(frontmatter["source"], parsed.Frontmatter["source"]);
            Assert.Equal(frontmatter["case_index"], parsed.Frontmatter["case_index"]);
        }
    }

    [Fact]
    public void ParseFile_ThrowsOnMissingId()
    {
        // Frontmatter without an 'id' is malformed for KnowledgeNote.
        const string raw = "---\ntitle: x\n---\nbody";
        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile(raw));
    }

    private static string RandomScalar(Random rng)
    {
        // Mix in characters that require YAML quoting.
        var palette = new[]
        {
            "plain value", "with: colon", "with \"quotes\"", "tab\there", "newline\nhere",
            "leading space", "weird # comment-like", "back\\slash", "[brackets]",
            "{braces}",
        };
        return palette[rng.Next(palette.Length)];
    }
}
