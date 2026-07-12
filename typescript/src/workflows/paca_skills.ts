// workflows/paca_skills.ts
//
// (3.3.0) Eleven built-in Claude Code skills ported from paca (PacaSkills.cs):
// paca, paca-epic, paca-breakdown, paca-clarify, paca-sprint, paca-estimate,
// paca-prioritize, paca-do, paca-test, paca-doc, paca-setup. Plus a skill
// installer that strips frontmatter and drops the markdown into
// ~/.claude/commands, and templates for the nine creator skills.
//
// Filesystem seam: the C# installer uses Directory.CreateDirectory /
// File.WriteAllText / File.Delete directly. Following the port's "inject the
// platform seam, keep the logic deterministic" convention (see
// voice/onnx_speaker.ts IEnrollmentStore), the filesystem is injected behind
// ISkillFileStore with a NullSkillFileStore default; the frontmatter/markdown
// logic is ported one-to-one and stays pure.

/** (3.3.0) A skill definition: frontmatter metadata + body. Mirrors C# `PacaSkill`. */
export interface PacaSkill {
  readonly name: string;
  readonly description: string;
  readonly body: string;
}

/** Constructs a {@link PacaSkill}. */
export function pacaSkill(name: string, description: string, body: string): PacaSkill {
  return { name, description, body };
}

/** (3.3.0) Render as a Claude-Code-compatible markdown file with frontmatter. Mirrors C# `PacaSkill.ToMarkdown`. */
export function skillToMarkdown(skill: PacaSkill): string {
  return `---\nname: ${skill.name}\ndescription: ${skill.description}\n---\n\n${skill.body}`;
}

/** (3.3.0) Render as the bare body (frontmatter stripped) for the installer. Mirrors C# `PacaSkill.ToBodyOnly`. */
export function skillToBodyOnly(skill: PacaSkill): string {
  return skill.body;
}

/** (3.3.0) The nine creator-skill templates (markdown body). Mirrors C# `SkillTemplates`. */
export const SkillTemplates = {
  Epic:
    "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks.",
  Breakdown:
    "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria.",
  Clarify:
    "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task.",
  Sprint: "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools.",
  Estimate: "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions.",
  Prioritize: "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning.",
  Do: "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done.",
  Test: "You are running paca-test. Write and run unit + integration tests for the current change.",
  Doc: "You are running paca-doc. Update the living document with the smallest accurate diff.",
} as const;

/** (3.3.0) The eleven built-in paca skills. Mirrors C# `PacaSkillLibrary`. */
export const PacaSkillLibrary = {
  /** (3.3.0) Returns the full set, deduplicated by name. */
  all: [
    pacaSkill("paca", "Run the paca workflow on the current ask.", "Use the paca MCP tools to plan and execute the user's request."),
    pacaSkill("paca-epic", "Capture a large initiative as a paca epic.", SkillTemplates.Epic),
    pacaSkill("paca-breakdown", "Break a paca epic into actionable tasks.", SkillTemplates.Breakdown),
    pacaSkill("paca-clarify", "Ask the right clarifying questions before estimating.", SkillTemplates.Clarify),
    pacaSkill("paca-sprint", "Form / close a sprint with the paca sprint surface.", SkillTemplates.Sprint),
    pacaSkill("paca-estimate", "Estimate story points for a set of tasks.", SkillTemplates.Estimate),
    pacaSkill("paca-prioritize", "Reorder the backlog by importance.", SkillTemplates.Prioritize),
    pacaSkill("paca-do", "Pick the next-best task and start it.", SkillTemplates.Do),
    pacaSkill("paca-test", "Generate and run tests for the current change.", SkillTemplates.Test),
    pacaSkill("paca-doc", "Update the project's living doc to reflect the latest change.", SkillTemplates.Doc),
    pacaSkill(
      "paca-setup",
      "First-run setup: pick project, configure agents, install plugins.",
      "Walk the user through paca first-run setup.",
    ),
  ] as readonly PacaSkill[],

  find(name: string): PacaSkill | null {
    return this.all.find((s) => s.name.toLowerCase() === name.toLowerCase()) ?? null;
  },
} as const;

/**
 * (3.3.0) Filesystem seam for the skill installer. The C# implementation writes
 * UTF-8 markdown files under a commands directory and deletes them on
 * uninstall. Injected so the port needs no filesystem; the default is a
 * {@link NullSkillFileStore}.
 */
export interface ISkillFileStore {
  /** Ensure the commands directory exists (Directory.CreateDirectory). */
  ensureDir(dir: string): void;
  /** Write UTF-8 text to `path` (File.WriteAllText, no BOM). */
  writeText(path: string, contents: string): void;
  /** True if a file exists at `path` (File.Exists). */
  exists(path: string): boolean;
  /** Delete the file at `path` (File.Delete). */
  delete(path: string): void;
  /** Join a directory + filename with the platform separator (Path.Combine). */
  combine(dir: string, file: string): string;
}

/** No-op {@link ISkillFileStore}: nothing is persisted. Fails-closed for `exists`. */
export class NullSkillFileStore implements ISkillFileStore {
  ensureDir(_dir: string): void {
    /* nothing */
  }
  writeText(_path: string, _contents: string): void {
    /* nothing */
  }
  exists(_path: string): boolean {
    return false;
  }
  delete(_path: string): void {
    /* nothing */
  }
  combine(dir: string, file: string): string {
    return dir.endsWith("/") || dir.endsWith("\\") ? `${dir}${file}` : `${dir}/${file}`;
  }
}

// ^\s*---.*?---\s*\n with DOTALL — mirrors the C# compiled FrontmatterPattern
// (RegexOptions.Compiled | RegexOptions.Singleline). `[\s\S]` emulates DOTALL.
const FRONTMATTER_PATTERN = /^\s*---[\s\S]*?---\s*\n/;

/** (3.3.0) Installer that drops bare skill bodies into ~/.claude/commands/. Mirrors C# `PacaSkillInstaller`. */
export class PacaSkillInstaller {
  private readonly commandsDir: string;
  private readonly files: ISkillFileStore;

  constructor(commandsDir: string, files: ISkillFileStore = new NullSkillFileStore()) {
    if (isBlank(commandsDir)) throw new Error("commandsDir required");
    this.commandsDir = commandsDir;
    this.files = files;
  }

  /** (3.3.0) Install all built-in skills. */
  installAll(): readonly string[] {
    return this.installEach(PacaSkillLibrary.all);
  }

  /** (3.3.0) Install a custom set of skills. */
  installEach(skills: Iterable<PacaSkill>): readonly string[] {
    this.files.ensureDir(this.commandsDir);
    const installed: string[] = [];
    for (const skill of skills) {
      const path = this.files.combine(this.commandsDir, `${skill.name}.md`);
      const body = PacaSkillInstaller.stripFrontmatter(skillToMarkdown(skill));
      this.files.writeText(path, body);
      installed.push(path);
    }
    return installed;
  }

  /** (3.3.0) Uninstall a set of skills by name. Returns the count removed. */
  uninstallByName(names: Iterable<string>): number {
    let count = 0;
    for (const name of names) {
      const path = this.files.combine(this.commandsDir, `${name}.md`);
      if (this.files.exists(path)) {
        this.files.delete(path);
        count++;
      }
    }
    return count;
  }

  /** (3.3.0) Strip the frontmatter block from a markdown skill file. Mirrors C# `StripFrontmatter`. */
  static stripFrontmatter(markdown: string): string {
    if (markdown == null || markdown.length === 0) return "";
    const match = FRONTMATTER_PATTERN.exec(markdown);
    if (match === null || match.index !== 0) return markdown.replace(/^\s+/, "");
    return markdown.slice(match[0].length).replace(/^\s+/, "");
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
