// YamlFrontmatterTests.cs
//
// Tests for the internal YAML parser. Exercised indirectly via KnowledgeNote
// round-tripping in KnowledgeNoteTests; this file targets the edge cases
// (nested rejection, escape sequence handling, etc.).

using Circle.AI.Knowledge;
using Xunit;

namespace Circle.AI.Knowledge.Tests;

public sealed class YamlFrontmatterTests
{
    // Nested YAML rejection is tested via KnowledgeNote which delegates to
    // YamlFrontmatter. Anything indented after a key looks like a nested
    // mapping and must throw.

    [Fact]
    public void Parse_RejectsNestedYaml()
    {
        // Knowledge note frontmatter with indented (nested) content must
        // throw FormatException.
        const string raw =
            "---\n" +
            "id: 11111111-1111-1111-1111-111111111111\n" +
            "outer:\n" +
            "  inner: value\n" +
            "---\nbody";

        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile(raw));
    }

    [Fact]
    public void Parse_RejectsFlowStyleSequence()
    {
        const string raw =
            "---\n" +
            "id: 22222222-2222-2222-2222-222222222222\n" +
            "tags_inline: [a, b, c]\n" +
            "---\nbody";

        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile(raw));
    }

    [Fact]
    public void Parse_RejectsListMarker()
    {
        const string raw =
            "---\n" +
            "id: 33333333-3333-3333-3333-333333333333\n" +
            "- item\n" +
            "---\nbody";

        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile(raw));
    }

    [Fact]
    public void Parse_RoundTripsEscapeSequences()
    {
        // Round-trip values containing backslash, double-quote, newline,
        // carriage-return, and tab. The encoder must emit these escaped so
        // the file form does not break the parser.
        var note = new KnowledgeNote(
            Id: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Title: "escapes",
            BodyMarkdown: "body content",
            Frontmatter: new Dictionary<string, string>
            {
                ["with_quote"] = "she said \"hi\"",
                ["with_newline"] = "line1\nline2",
                ["with_tab"] = "a\tb",
                ["with_backslash"] = "C:\\Path\\To\\File",
                ["with_cr"] = "x\ry",
            },
            Tags: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var text = note.ToFileText();
        var parsed = KnowledgeNote.ParseFile(text);

        Assert.Equal("she said \"hi\"", parsed.Frontmatter["with_quote"]);
        Assert.Equal("line1\nline2", parsed.Frontmatter["with_newline"]);
        Assert.Equal("a\tb", parsed.Frontmatter["with_tab"]);
        Assert.Equal("C:\\Path\\To\\File", parsed.Frontmatter["with_backslash"]);
        Assert.Equal("x\ry", parsed.Frontmatter["with_cr"]);
    }

    [Fact]
    public void Parse_ThrowsOnMissingOpeningDelimiter()
    {
        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile("no frontmatter at all"));
    }

    [Fact]
    public void Parse_ThrowsOnMissingClosingDelimiter()
    {
        const string raw = "---\nid: 55555555-5555-5555-5555-555555555555\ntitle: x\nbody but no closer";
        Assert.Throws<FormatException>(() => KnowledgeNote.ParseFile(raw));
    }
}
