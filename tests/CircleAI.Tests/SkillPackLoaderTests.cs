// SkillPackLoaderTests.cs
//
// (2.0.1) Tests for the Claude Code-style SKILL.md loader and the
// ISkillStore importer.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class SkillPackLoaderTests
{
    private static string WriteTempPack(params (string subdir, string content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"circleai-skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        foreach (var (subdir, content) in files)
        {
            var dir = Path.Combine(root, subdir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        }
        return root;
    }

    [Fact]
    public void Parse_WithFrontmatter_ExtractsNameAndDescription()
    {
        const string md =
            "---\n" +
            "name: hunt-idor\n" +
            "description: IDOR hunting workflow\n" +
            "---\n\n" +
            "# Hunt IDOR\n\n" +
            "Detailed steps go here.";

        var parsed = SkillPackLoader.Parse(md, "fake/hunt-idor/SKILL.md");
        Assert.Equal("hunt-idor", parsed.Id);
        Assert.Equal("hunt-idor", parsed.Name);
        Assert.Equal("IDOR hunting workflow", parsed.Description);
        Assert.Contains("Detailed steps go here.", parsed.Instructions);
    }

    [Fact]
    public void Parse_WithoutFrontmatter_DerivesNameFromFirstHeading()
    {
        const string md = "# My Skill\n\nBody here";
        var parsed = SkillPackLoader.Parse(md, "x/y/SKILL.md");
        Assert.Equal("My Skill", parsed.Name);
        Assert.Equal("my-skill", parsed.Id);
    }

    [Fact]
    public void Parse_QuotedFrontmatterValues_StripsQuotes()
    {
        const string md = "---\nname: 'quoted'\ndescription: \"also quoted\"\n---\nbody";
        var parsed = SkillPackLoader.Parse(md, "x/SKILL.md");
        Assert.Equal("quoted", parsed.Name);
        Assert.Equal("also quoted", parsed.Description);
    }

    [Fact]
    public void Parse_InlineTagsArray_ParsesCorrectly()
    {
        const string md = "---\nname: x\ntags: [bb, recon, idor]\n---\nbody";
        var parsed = SkillPackLoader.Parse(md, "x/SKILL.md");
        Assert.Equal(new[] { "bb", "recon", "idor" }, parsed.Tags);
    }

    [Fact]
    public async Task LoadAsync_WalksTreeRecursively()
    {
        var root = WriteTempPack(
            ("a", "---\nname: a\ndescription: A\n---\n# A"),
            ("b/nested", "---\nname: b-nested\ndescription: B\n---\n# B"),
            ("c", "---\nname: c\ndescription: C\n---\n# C"));
        try
        {
            var parsed = new System.Collections.Generic.List<ParsedSkill>();
            await foreach (var s in SkillPackLoader.LoadAsync(root))
                parsed.Add(s);
            Assert.Equal(3, parsed.Count);
            Assert.Contains(parsed, s => s.Id == "a");
            Assert.Contains(parsed, s => s.Id == "b-nested");
            Assert.Contains(parsed, s => s.Id == "c");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ImportAsync_PopulatesStoreAndStampsPackTag()
    {
        var root = WriteTempPack(
            ("skill-one", "---\nname: skill-one\ndescription: One\n---\n# One"),
            ("skill-two", "---\nname: skill-two\ndescription: Two\n---\n# Two"));
        try
        {
            var store = new InMemorySkillStore();
            var manifest = await SkillPackLoader.ImportAsync(
                store, root, packName: "Claude-BugHunter",
                packVersion: "v1.0.0", sourceUrl: "https://github.com/bhengubv/Claude-BugHunter",
                license: "MIT");

            Assert.Equal("Claude-BugHunter", manifest.Name);
            Assert.Equal(2, manifest.SkillCount);

            var all = await store.ListAsync();
            Assert.Equal(2, all.Count);
            var byTag = (await store.SearchAsync("pack:claude-bughunter")).ToList();
            Assert.Equal(2, byTag.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadAsync_BadFile_InvokesWarningAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"circleai-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "good"));
        File.WriteAllText(Path.Combine(root, "good", "SKILL.md"), "# Good");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "empty", "SKILL.md"), "");
        try
        {
            var warnings = 0;
            var loaded = new System.Collections.Generic.List<ParsedSkill>();
            await foreach (var s in SkillPackLoader.LoadAsync(root, onWarning: (_, _) => warnings++))
                loaded.Add(s);
            Assert.Single(loaded);
            Assert.Equal(1, warnings);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
