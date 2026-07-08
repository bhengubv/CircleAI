// hosting/skills.ts
//
// Port of the CircleAI.Skills contracts that AIService's enrichment pipeline
// depends on: ISkillStore + SkillSummary/SkillDetail/SkillDraft/SkillSource +
// InMemorySkillStore + SkillContextBuilder. This is the skill-injection seam
// consumed by BuildEnrichedSystemPrompt; only the subset AIService needs is
// ported here (the full CircleAI.Skills project is a separate work unit).

import { randomUUID } from "node:crypto";

/** Indicates where a {@link SkillDetail} originated. Mirrors SkillSource. */
export enum SkillSource {
  /** Loaded from a SKILL.md file on disk. */
  File = "File",
  /** Created programmatically and held in memory. */
  InMemory = "InMemory",
  /** Fetched from a remote skill registry. */
  Remote = "Remote",
}

/**
 * Lightweight projection of a {@link SkillDetail} used in list/search results.
 * Does not carry the full instructions text. Mirrors SkillSummary.
 */
export interface SkillSummary {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly tags: readonly string[];
  readonly source: SkillSource;
}

/** Full skill record. Mirrors SkillDetail. */
export interface SkillDetail {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  readonly tags: readonly string[];
  readonly source: SkillSource;
  /** ISO 8601 UTC timestamp of the most recent modification. */
  readonly lastModified: string;
}

/** Input model for {@link ISkillStore.upsertAsync}. Mirrors SkillDraft. */
export interface SkillDraft {
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  readonly tags: readonly string[];
}

/**
 * Persistent store for B! skills. Skills are named, tagged capability
 * definitions injected into the system prompt. Mirrors CircleAI.Skills.ISkillStore.
 */
export interface ISkillStore {
  /** Returns all skills as lightweight summaries. */
  listAsync(): Promise<readonly SkillSummary[]>;
  /** Returns the full detail for a skill by id, or null when unknown. */
  getAsync(id: string): Promise<SkillDetail | null>;
  /**
   * Returns skills whose name/description/tags contain `query`
   * (case-insensitive substring). Empty list when `query` is null/empty.
   */
  searchAsync(query: string): Promise<readonly SkillSummary[]>;
  /** Creates or replaces a skill. Auto-slugs the id from name when id is null/empty. */
  upsertAsync(id: string | null, draft: SkillDraft): Promise<SkillDetail>;
  /** Removes the skill with the given id. No-op if absent. */
  deleteAsync(id: string): Promise<void>;
}

/**
 * Thread-safe in-memory {@link ISkillStore}. Mirrors
 * CircleAI.Skills.InMemorySkillStore.
 */
export class InMemorySkillStore implements ISkillStore {
  private readonly skills = new Map<string, SkillDetail>();

  async listAsync(): Promise<readonly SkillSummary[]> {
    return [...this.skills.values()]
      .map(toSummary)
      .sort((a, b) => ciCompare(a.name, b.name));
  }

  async getAsync(id: string): Promise<SkillDetail | null> {
    if (id == null || id.trim().length === 0) throw new Error("id required");
    return this.skills.get(id) ?? null;
  }

  async searchAsync(query: string): Promise<readonly SkillSummary[]> {
    if (query == null || query.trim().length === 0) return [];
    const q = query.trim();
    return [...this.skills.values()]
      .filter((s) => matchesQuery(s, q))
      .map(toSummary)
      .sort((a, b) => ciCompare(a.name, b.name));
  }

  async upsertAsync(id: string | null, draft: SkillDraft): Promise<SkillDetail> {
    if (!draft) throw new Error("draft required");
    const effectiveId =
      id == null || id.trim().length === 0
        ? InMemorySkillStore.generateSlug(draft.name)
        : id.trim();

    const detail: SkillDetail = {
      id: effectiveId,
      name: draft.name,
      description: draft.description,
      instructions: draft.instructions,
      tags: draft.tags ?? [],
      source: SkillSource.InMemory,
      lastModified: new Date().toISOString(),
    };
    this.skills.set(effectiveId, detail);
    return detail;
  }

  async deleteAsync(id: string): Promise<void> {
    if (id == null || id.trim().length === 0) throw new Error("id required");
    this.skills.delete(id);
  }

  /** "My Skill" → "my-skill". Mirrors InMemorySkillStore.GenerateSlug. */
  static generateSlug(name: string): string {
    if (name == null || name.trim().length === 0) return randomUUID().replace(/-/g, "");
    let slug = name.trim().toLowerCase();
    slug = slug.replace(/\s+/g, "-");
    slug = slug.replace(/[^a-z0-9-]/g, "");
    slug = slug.replace(/-{2,}/g, "-").replace(/^-+|-+$/g, "");
    return slug.length === 0 ? randomUUID().replace(/-/g, "") : slug;
  }
}

/**
 * Selects the most relevant skills for a query and formats them as a
 * system-prompt context block. Mirrors CircleAI.Skills.SkillContextBuilder.
 */
export class SkillContextBuilder {
  private readonly store: ISkillStore;
  private readonly maxSkills: number;

  constructor(store: ISkillStore, maxSkills = 5) {
    if (!store) throw new Error("store required");
    if (maxSkills < 1) throw new Error("maxSkills must be at least 1.");
    this.store = store;
    this.maxSkills = maxSkills;
  }

  /**
   * Returns a formatted block listing the most relevant skills for
   * `userQuery`. Empty string when the store is empty or nothing matches.
   */
  async buildContextAsync(userQuery: string): Promise<string> {
    if (userQuery == null || userQuery.trim().length === 0) return "";

    const matches = await this.store.searchAsync(userQuery);

    let candidates: readonly SkillSummary[];
    if (matches.length > 0) {
      candidates = matches.slice(0, this.maxSkills);
    } else {
      const all = await this.store.listAsync();
      if (all.length === 0) return "";
      candidates = all.slice(0, this.maxSkills);
    }

    const lines: string[] = [];
    lines.push("## Available Skills");

    for (const summary of candidates) {
      const detail = await this.store.getAsync(summary.id);
      if (detail == null) continue;

      lines.push("");
      lines.push(`**${detail.id}** — ${detail.description}`);
      if (detail.instructions != null && detail.instructions.trim().length > 0) {
        for (const line of detail.instructions.split("\n")) lines.push(`  ${line}`);
      }
    }

    // C# joins with AppendLine (trailing "\n" per line) then TrimEnd().
    return (lines.join("\n") + "\n").replace(/\s+$/, "");
  }
}

function toSummary(d: SkillDetail): SkillSummary {
  return {
    id: d.id,
    name: d.name,
    description: d.description,
    tags: d.tags,
    source: d.source,
  };
}

function matchesQuery(s: SkillDetail, query: string): boolean {
  const q = query.toLowerCase();
  return (
    s.name.toLowerCase().includes(q) ||
    s.description.toLowerCase().includes(q) ||
    s.tags.some((t) => t.toLowerCase().includes(q))
  );
}

function ciCompare(a: string, b: string): number {
  const la = a.toLowerCase();
  const lb = b.toLowerCase();
  return la < lb ? -1 : la > lb ? 1 : 0;
}
