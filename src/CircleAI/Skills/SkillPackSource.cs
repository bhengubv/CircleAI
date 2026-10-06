// SkillPackSource.cs
//
// (2.0.2) Declarative description of a remote skill pack — name, GitHub
// repo, ref, license. KnownSkillPacks below holds the default catalogue
// of packs we import out of the box.

using System.Collections.Generic;

namespace CircleAI.Skills;

/// <summary>
/// (2.0.2) Source declaration for a single skill pack. The
/// <see cref="SkillPackAutoImporter"/> uses these to download + import.
/// </summary>
/// <param name="Name">Display name and tag prefix, e.g. <c>"Claude-BugHunter"</c>.</param>
/// <param name="RepoUrl">Canonical repo URL, e.g. <c>https://github.com/bhengubv/Claude-BugHunter</c>.</param>
/// <param name="GitRef">Branch / tag / commit. <c>"main"</c> by default; pin to a tag for reproducibility.</param>
/// <param name="License">SPDX identifier or descriptive string.</param>
/// <param name="SkillSubdir">Optional path within the repo where SKILL.md files live. <c>""</c> walks the whole tree.</param>
/// <param name="EstimatedSkillCount">Cardinality hint for diagnostics + UI; not enforced.</param>
/// <param name="IsDefaultEnabled">When <c>true</c>, <see cref="SkillPackAutoImporter"/> imports this pack on first run.</param>
/// <param name="DefaultTags">Extra tags merged into every skill imported from this pack.</param>
public sealed record SkillPackSource(
    string                Name,
    string                RepoUrl,
    string                GitRef               = "main",
    string                License              = "unknown",
    string                SkillSubdir          = "",
    int                   EstimatedSkillCount  = 0,
    bool                  IsDefaultEnabled     = true,
    IReadOnlyList<string>? DefaultTags         = null);

/// <summary>
/// (2.0.2) Default catalogue of skill packs CircleAI imports when
/// <see cref="SkillPackSourcesOptions.AutoImportOnStart"/> is set.
/// </summary>
public static class KnownSkillPacks
{
    /// <summary>bhengubv/awesome-agent-skills — 1000+ community skills (VoltAgent curation).</summary>
    public static readonly SkillPackSource AwesomeAgentSkills = new(
        Name:                "awesome-agent-skills",
        RepoUrl:             "https://github.com/bhengubv/awesome-agent-skills",
        License:             "Apache-2.0",
        SkillSubdir:         "skills",
        EstimatedSkillCount: 1000,
        DefaultTags:         new[] { "community" });

    /// <summary>mukul975/Anthropic-Cybersecurity-Skills — 754 skills, MITRE / NIST / ATLAS / D3FEND / AI RMF mapped.</summary>
    public static readonly SkillPackSource AnthropicCybersecurity = new(
        Name:                "Anthropic-Cybersecurity-Skills",
        RepoUrl:             "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
        License:             "Apache-2.0",
        SkillSubdir:         "skills",
        EstimatedSkillCount: 754,
        DefaultTags:         new[] { "security", "mitre" });

    /// <summary>mukul975/Privacy-Data-Protection-Skills — 282+ GDPR / CCPA / EU AI Act / HIPAA / LGPD / PIPL / DPDP.</summary>
    public static readonly SkillPackSource PrivacyDataProtection = new(
        Name:                "Privacy-Data-Protection-Skills",
        RepoUrl:             "https://github.com/mukul975/Privacy-Data-Protection-Skills",
        License:             "Apache-2.0",
        SkillSubdir:         "skills",
        EstimatedSkillCount: 282,
        DefaultTags:         new[] { "privacy", "compliance" });

    /// <summary>bhengubv/Claude-BugHunter — 51 hunting skills + 681 disclosed-report patterns.</summary>
    public static readonly SkillPackSource ClaudeBugHunter = new(
        Name:                "Claude-BugHunter",
        RepoUrl:             "https://github.com/bhengubv/Claude-BugHunter",
        License:             "Apache-2.0",
        SkillSubdir:         "skills",
        EstimatedSkillCount: 51,
        DefaultTags:         new[] { "security", "bug-bounty" });

    /// <summary>bhengubv/last30days-skill — single researcher skill.</summary>
    public static readonly SkillPackSource Last30Days = new(
        Name:                "last30days-skill",
        RepoUrl:             "https://github.com/bhengubv/last30days-skill",
        License:             "MIT",
        EstimatedSkillCount: 1,
        DefaultTags:         new[] { "research" });

    /// <summary>bhengubv/eduba-brand — 1 brand skill (Eduba design tokens / voice / patterns).</summary>
    public static readonly SkillPackSource EdubaBrand = new(
        Name:                "eduba-brand",
        RepoUrl:             "https://github.com/bhengubv/eduba-brand",
        License:             "n/a (pattern-port)",
        SkillSubdir:         ".agents/skills/eduba-brand",
        EstimatedSkillCount: 1,
        DefaultTags:         new[] { "branding", "eduba" });

    /// <summary>
    /// bhengubv/career-ops — non-standard skill format; ships disabled by
    /// default until the 2.0.3 adapter converts it to SKILL.md format.
    /// TheJobCenter integration target.
    /// </summary>
    public static readonly SkillPackSource CareerOps = new(
        Name:                "career-ops",
        RepoUrl:             "https://github.com/bhengubv/career-ops",
        License:             "MIT",
        EstimatedSkillCount: 14,
        IsDefaultEnabled:    false,
        DefaultTags:         new[] { "job-search", "career", "thejobcenter" });

    /// <summary>
    /// bhengubv/build-your-own-x — awesome-list (educational corpus, not
    /// SKILL.md format). Disabled by default; 2.0.3 synthesiser converts
    /// the curated links into skill descriptors.
    /// </summary>
    public static readonly SkillPackSource BuildYourOwnX = new(
        Name:                "build-your-own-x",
        RepoUrl:             "https://github.com/bhengubv/build-your-own-x",
        License:             "MIT",
        EstimatedSkillCount: 0,
        IsDefaultEnabled:    false,
        DefaultTags:         new[] { "education", "tutorial" });

    /// <summary>
    /// Every known pack. <see cref="SkillPackSourcesOptions"/> defaults to
    /// importing the subset where <see cref="SkillPackSource.IsDefaultEnabled"/>
    /// is <c>true</c>.
    /// </summary>
    public static readonly IReadOnlyList<SkillPackSource> All = new[]
    {
        AwesomeAgentSkills,
        AnthropicCybersecurity,
        PrivacyDataProtection,
        ClaudeBugHunter,
        Last30Days,
        EdubaBrand,
        CareerOps,
        BuildYourOwnX,
    };
}
