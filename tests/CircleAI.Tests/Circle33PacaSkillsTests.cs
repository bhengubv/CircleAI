// Circle33PacaSkillsTests.cs
//
// (3.3.0) Tests for paca built-in skills + installer.

using System.IO;
using System.Linq;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaSkillsTests
{
    [Fact]
    public void Library_HasElevenSkills()
    {
        Assert.Equal(11, PacaSkillLibrary.All.Count);
    }

    [Fact]
    public void Library_HasUniqueNames()
    {
        var names = PacaSkillLibrary.All.Select(s => s.Name).ToArray();
        Assert.Equal(names.Distinct().Count(), names.Length);
    }

    [Fact]
    public void Find_KnownSkill_ReturnsIt()
    {
        var s = PacaSkillLibrary.Find("paca-epic");
        Assert.NotNull(s);
        Assert.Equal("paca-epic", s!.Name);
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
    {
        Assert.Null(PacaSkillLibrary.Find("ghost"));
    }

    [Fact]
    public void ToMarkdown_IncludesFrontmatter()
    {
        var s = PacaSkillLibrary.Find("paca-epic")!;
        var md = s.ToMarkdown();
        Assert.StartsWith("---\nname: paca-epic", md);
        Assert.Contains("description:", md);
    }

    [Fact]
    public void StripFrontmatter_RemovesYamlBlock()
    {
        var md   = "---\nname: paca\ndescription: x\n---\n\nBody here.";
        var body = PacaSkillInstaller.StripFrontmatter(md);
        Assert.Equal("Body here.", body);
    }

    [Fact]
    public void StripFrontmatter_NoFrontmatter_ReturnsAsIs()
    {
        Assert.Equal("body", PacaSkillInstaller.StripFrontmatter("body"));
    }

    [Fact]
    public void InstallAll_DropsFilesIntoTargetDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "paca-skills-test-" + System.Guid.NewGuid().ToString("n"));
        try
        {
            var installer = new PacaSkillInstaller(tempDir);
            var written   = installer.InstallAll();

            Assert.Equal(11, written.Count);
            Assert.All(written, p => Assert.True(File.Exists(p)));

            // Files should NOT contain "---" frontmatter.
            foreach (var p in written)
            {
                var content = File.ReadAllText(p);
                Assert.DoesNotContain("name: paca", content); // i.e. no YAML frontmatter
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void UninstallByName_RemovesNamedSkillFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "paca-skills-test-" + System.Guid.NewGuid().ToString("n"));
        try
        {
            var installer = new PacaSkillInstaller(tempDir);
            installer.InstallAll();
            var removed = installer.UninstallByName(new[] { "paca-epic", "paca-do" });

            Assert.Equal(2, removed);
            Assert.False(File.Exists(Path.Combine(tempDir, "paca-epic.md")));
            Assert.False(File.Exists(Path.Combine(tempDir, "paca-do.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "paca-clarify.md")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SkillTemplates_CoverNineCreatorSkills()
    {
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Epic));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Breakdown));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Clarify));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Sprint));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Estimate));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Prioritize));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Do));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Test));
        Assert.False(string.IsNullOrEmpty(SkillTemplates.Doc));
    }

    [Fact]
    public void Installer_NullDir_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new PacaSkillInstaller(""));
    }
}
