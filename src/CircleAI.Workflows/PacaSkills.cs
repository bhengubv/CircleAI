// PacaSkills.cs
//
// (3.3.0) Eleven built-in Claude Code skills ported from paca:
// paca, paca-epic, paca-breakdown, paca-clarify, paca-sprint,
// paca-estimate, paca-prioritize, paca-do, paca-test, paca-doc,
// paca-setup. Plus a skill installer that strips frontmatter and
// drops the markdown into ~/.claude/commands, and templates for the
// nine creator skills (epic / breakdown / clarify / sprint /
// estimate / prioritize / do / test / doc).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) A skill definition: frontmatter metadata + body.</summary>
public sealed record PacaSkill(string Name, string Description, string Body)
{
    /// <summary>(3.3.0) Render as a Claude-Code-compatible markdown file with frontmatter.</summary>
    public string ToMarkdown()
        => $"---\nname: {Name}\ndescription: {Description}\n---\n\n{Body}";

    /// <summary>(3.3.0) Render as the bare body (frontmatter stripped) for the installer.</summary>
    public string ToBodyOnly() => Body;
}

/// <summary>(3.3.0) The eleven built-in paca skills.</summary>
public static class PacaSkillLibrary
{
    /// <summary>(3.3.0) Returns the full set, deduplicated by name.</summary>
    public static IReadOnlyList<PacaSkill> All { get; } = new[]
    {
        new PacaSkill("paca",           "Run the paca workflow on the current ask.",                        "Use the paca MCP tools to plan and execute the user's request."),
        new PacaSkill("paca-epic",      "Capture a large initiative as a paca epic.",                       SkillTemplates.Epic),
        new PacaSkill("paca-breakdown", "Break a paca epic into actionable tasks.",                         SkillTemplates.Breakdown),
        new PacaSkill("paca-clarify",   "Ask the right clarifying questions before estimating.",            SkillTemplates.Clarify),
        new PacaSkill("paca-sprint",    "Form / close a sprint with the paca sprint surface.",              SkillTemplates.Sprint),
        new PacaSkill("paca-estimate",  "Estimate story points for a set of tasks.",                        SkillTemplates.Estimate),
        new PacaSkill("paca-prioritize","Reorder the backlog by importance.",                               SkillTemplates.Prioritize),
        new PacaSkill("paca-do",        "Pick the next-best task and start it.",                            SkillTemplates.Do),
        new PacaSkill("paca-test",      "Generate and run tests for the current change.",                   SkillTemplates.Test),
        new PacaSkill("paca-doc",       "Update the project's living doc to reflect the latest change.",    SkillTemplates.Doc),
        new PacaSkill("paca-setup",     "First-run setup: pick project, configure agents, install plugins.", "Walk the user through paca first-run setup."),
    };

    public static PacaSkill? Find(string name)
        => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>(3.3.0) The nine creator-skill templates (markdown body).</summary>
public static class SkillTemplates
{
    public const string Epic        = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks.";
    public const string Breakdown   = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria.";
    public const string Clarify     = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task.";
    public const string Sprint      = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools.";
    public const string Estimate    = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions.";
    public const string Prioritize  = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning.";
    public const string Do          = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done.";
    public const string Test        = "You are running paca-test. Write and run unit + integration tests for the current change.";
    public const string Doc         = "You are running paca-doc. Update the living document with the smallest accurate diff.";
}

/// <summary>(3.3.0) Installer that drops bare skill bodies into ~/.claude/commands/.</summary>
public sealed class PacaSkillInstaller
{
    private static readonly Regex FrontmatterPattern = new(@"^\s*---.*?---\s*\n", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly string _commandsDir;

    public PacaSkillInstaller(string commandsDir)
    {
        if (string.IsNullOrWhiteSpace(commandsDir)) throw new ArgumentException("commandsDir required", nameof(commandsDir));
        _commandsDir = commandsDir;
    }

    /// <summary>(3.3.0) Install all built-in skills.</summary>
    public IReadOnlyList<string> InstallAll() => InstallEach(PacaSkillLibrary.All);

    /// <summary>(3.3.0) Install a custom set of skills.</summary>
    public IReadOnlyList<string> InstallEach(IEnumerable<PacaSkill> skills)
    {
        Directory.CreateDirectory(_commandsDir);
        var installed = new List<string>();
        foreach (var skill in skills)
        {
            var path = Path.Combine(_commandsDir, $"{skill.Name}.md");
            var body = StripFrontmatter(skill.ToMarkdown());
            File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            installed.Add(path);
        }
        return installed;
    }

    /// <summary>(3.3.0) Uninstall a set of skills by name.</summary>
    public int UninstallByName(IEnumerable<string> names)
    {
        int count = 0;
        foreach (var name in names)
        {
            var path = Path.Combine(_commandsDir, $"{name}.md");
            if (File.Exists(path)) { File.Delete(path); count++; }
        }
        return count;
    }

    /// <summary>(3.3.0) Strip the frontmatter block from a markdown skill file.</summary>
    public static string StripFrontmatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var match = FrontmatterPattern.Match(markdown);
        if (!match.Success || match.Index != 0) return markdown.TrimStart();
        return markdown[match.Length..].TrimStart();
    }
}
