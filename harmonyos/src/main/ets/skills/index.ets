/**
 * skills/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// skills/index.ts
// Full-parity port of the scoped CircleAI.Skills surface (C#). C# is the exact
// spec.
//
// B!-skill primitives in scope: the SkillSource enum, the ISkillStore contract
// (+ SkillSummary / SkillDetail / SkillDraft records), the thread-safe
// InMemorySkillStore, the SkillPackSource declaration + KnownSkillPacks catalog,
// SkillPackSourcesOptions, and the IPackDownloader strategy with a deterministic
// in-memory downloader (no network — the C# HttpPackDownloader / tar-extraction
// path is external and injected behind this contract).
//
// Type mappings (C# → TS):
//   IReadOnlyList<string> Tags          → readonly string[]
//   DateTimeOffset LastModified         → Date
//   Task<IReadOnlyList<SkillSummary>>   → Promise<readonly SkillSummary[]>
//   ConcurrentDictionary (Ordinal)      → Map<string, SkillDetail>
//   TimeSpan CacheTtl                   → number (milliseconds)
//   Guid.NewGuid().ToString("N")        → randomUUID() with dashes stripped

import { randomUUID } from "../platform";

/** Where a {@link SkillDetail} originated. Mirrors C# `SkillSource`. */
export enum SkillSource {
  File = 0,
  InMemory = 1,
  Remote = 2,
}

/** Lightweight projection of a skill for list/search results. Mirrors C# `SkillSummary`. */
export interface SkillSummary {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly tags: readonly string[];
  readonly source: SkillSource;
}

/** Constructs a {@link SkillSummary}. */
export function skillSummary(
  id: string,
  name: string,
  description: string,
  tags: readonly string[],
  source: SkillSource,
): SkillSummary {
  return { id, name, description, tags, source };
}

/** Full skill record. Mirrors C# `SkillDetail`. */
export interface SkillDetail {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  readonly tags: readonly string[];
  readonly source: SkillSource;
  readonly lastModified: Date;
}

/** Constructs a {@link SkillDetail}. */
export function skillDetail(
  id: string,
  name: string,
  description: string,
  instructions: string,
  tags: readonly string[],
  source: SkillSource,
  lastModified: Date,
): SkillDetail {
  return { id, name, description, instructions, tags, source, lastModified };
}

/** Input model for creating/updating a skill. Mirrors C# `SkillDraft`. */
export interface SkillDraft {
  readonly name: string;
  readonly description: string;
  readonly instructions: string;
  readonly tags: readonly string[];
}

/** Constructs a {@link SkillDraft}. */
export function skillDraft(
  name: string,
  description: string,
  instructions: string,
  tags: readonly string[],
): SkillDraft {
  return { name, description, instructions, tags };
}

/** Persistent store for B! skills. Mirrors C# `ISkillStore`. */
export interface ISkillStore {
  listAsync(signal?: AbortSignal): Promise<readonly SkillSummary[]>;
  getAsync(id: string, signal?: AbortSignal): Promise<SkillDetail | null>;
  searchAsync(query: string, signal?: AbortSignal): Promise<readonly SkillSummary[]>;
  upsertAsync(id: string | null, draft: SkillDraft, signal?: AbortSignal): Promise<SkillDetail>;
  deleteAsync(id: string, signal?: AbortSignal): Promise<void>;
}

/**
 * Thread-safe in-memory {@link ISkillStore}. Mirrors C# `InMemorySkillStore`.
 * List/search results are ordered by name (case-insensitive).
 */
export class InMemorySkillStore implements ISkillStore {
  private readonly skills = new Map<string, SkillDetail>();

  listAsync(_signal?: AbortSignal): Promise<readonly SkillSummary[]> {
    const results = [...this.skills.values()]
      .map(toSummary)
      .sort((a, b) => compareCaseInsensitive(a.name, b.name));
    return Promise.resolve(results);
  }

  getAsync(id: string, _signal?: AbortSignal): Promise<SkillDetail | null> {
    if (isBlank(id)) throw new Error("id required");
    return Promise.resolve(this.skills.get(id) ?? null);
  }

  searchAsync(query: string, _signal?: AbortSignal): Promise<readonly SkillSummary[]> {
    if (isBlank(query)) return Promise.resolve([]);
    const q = query.trim();
    const results = [...this.skills.values()]
      .filter((s) => matchesQuery(s, q))
      .map(toSummary)
      .sort((a, b) => compareCaseInsensitive(a.name, b.name));
    return Promise.resolve(results);
  }

  upsertAsync(id: string | null, draft: SkillDraft, _signal?: AbortSignal): Promise<SkillDetail> {
    if (draft == null) throw new Error("draft required");
    const effectiveId = isBlank(id) ? InMemorySkillStore.generateSlug(draft.name) : (id as string).trim();
    const detail = skillDetail(
      effectiveId,
      draft.name,
      draft.description,
      draft.instructions,
      draft.tags ?? [],
      SkillSource.InMemory,
      new Date(),
    );
    this.skills.set(effectiveId, detail);
    return Promise.resolve(detail);
  }

  deleteAsync(id: string, _signal?: AbortSignal): Promise<void> {
    if (isBlank(id)) throw new Error("id required");
    this.skills.delete(id);
    return Promise.resolve();
  }

  /** Converts a display name to a URL-safe lowercase slug. Mirrors C# `GenerateSlug`. */
  static generateSlug(name: string): string {
    if (isBlank(name)) return uuidN();
    let slug = name.trim().toLowerCase();
    slug = slug.replace(/\s+/g, "-");
    slug = slug.replace(/[^a-z0-9-]/g, "");
    slug = slug.replace(/-{2,}/g, "-").replace(/^-+|-+$/g, "");
    return slug.length === 0 ? uuidN() : slug;
  }
}

function toSummary(d: SkillDetail): SkillSummary {
  return skillSummary(d.id, d.name, d.description, d.tags, d.source);
}

function matchesQuery(s: SkillDetail, query: string): boolean {
  const q = query.toLowerCase();
  return (
    s.name.toLowerCase().includes(q) ||
    s.description.toLowerCase().includes(q) ||
    s.tags.some((t) => t.toLowerCase().includes(q))
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Skill-pack sources + downloader
// ─────────────────────────────────────────────────────────────────────────────

/** Source declaration for a single skill pack. Mirrors C# `SkillPackSource` record. */
export interface SkillPackSource {
  readonly name: string;
  readonly repoUrl: string;
  readonly gitRef: string;
  readonly license: string;
  readonly skillSubdir: string;
  readonly estimatedSkillCount: number;
  readonly isDefaultEnabled: boolean;
  readonly defaultTags: readonly string[] | null;
}

/** Constructs a {@link SkillPackSource} mirroring the C# record's defaults. */
export function skillPackSource(
  name: string,
  repoUrl: string,
  opts: {
    gitRef?: string;
    license?: string;
    skillSubdir?: string;
    estimatedSkillCount?: number;
    isDefaultEnabled?: boolean;
    defaultTags?: readonly string[] | null;
  } = {},
): SkillPackSource {
  return {
    name,
    repoUrl,
    gitRef: opts.gitRef ?? "main",
    license: opts.license ?? "unknown",
    skillSubdir: opts.skillSubdir ?? "",
    estimatedSkillCount: opts.estimatedSkillCount ?? 0,
    isDefaultEnabled: opts.isDefaultEnabled ?? true,
    defaultTags: opts.defaultTags ?? null,
  };
}

/** Default catalogue of skill packs. Mirrors C# `KnownSkillPacks`. */
export const KnownSkillPacks = (() => {
  const awesomeAgentSkills = skillPackSource(
    "awesome-agent-skills",
    "https://github.com/bhengubv/awesome-agent-skills",
    { license: "Apache-2.0", skillSubdir: "skills", estimatedSkillCount: 1000, defaultTags: ["community"] },
  );
  const anthropicCybersecurity = skillPackSource(
    "Anthropic-Cybersecurity-Skills",
    "https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
    { license: "Apache-2.0", skillSubdir: "skills", estimatedSkillCount: 754, defaultTags: ["security", "mitre"] },
  );
  const privacyDataProtection = skillPackSource(
    "Privacy-Data-Protection-Skills",
    "https://github.com/mukul975/Privacy-Data-Protection-Skills",
    { license: "Apache-2.0", skillSubdir: "skills", estimatedSkillCount: 282, defaultTags: ["privacy", "compliance"] },
  );
  const claudeBugHunter = skillPackSource(
    "Claude-BugHunter",
    "https://github.com/bhengubv/Claude-BugHunter",
    { license: "Apache-2.0", skillSubdir: "skills", estimatedSkillCount: 51, defaultTags: ["security", "bug-bounty"] },
  );
  const last30Days = skillPackSource("last30days-skill", "https://github.com/bhengubv/last30days-skill", {
    license: "MIT",
    estimatedSkillCount: 1,
    defaultTags: ["research"],
  });
  const edubaBrand = skillPackSource("eduba-brand", "https://github.com/bhengubv/eduba-brand", {
    license: "n/a (pattern-port)",
    skillSubdir: ".agents/skills/eduba-brand",
    estimatedSkillCount: 1,
    defaultTags: ["branding", "eduba"],
  });
  const careerOps = skillPackSource("career-ops", "https://github.com/bhengubv/career-ops", {
    license: "MIT",
    estimatedSkillCount: 14,
    isDefaultEnabled: false,
    defaultTags: ["job-search", "career", "thejobcenter"],
  });
  const buildYourOwnX = skillPackSource("build-your-own-x", "https://github.com/bhengubv/build-your-own-x", {
    license: "MIT",
    estimatedSkillCount: 0,
    isDefaultEnabled: false,
    defaultTags: ["education", "tutorial"],
  });

  const all: readonly SkillPackSource[] = [
    awesomeAgentSkills,
    anthropicCybersecurity,
    privacyDataProtection,
    claudeBugHunter,
    last30Days,
    edubaBrand,
    careerOps,
    buildYourOwnX,
  ];

  return {
    awesomeAgentSkills,
    anthropicCybersecurity,
    privacyDataProtection,
    claudeBugHunter,
    last30Days,
    edubaBrand,
    careerOps,
    buildYourOwnX,
    all,
  } as const;
})();

/** Settings for a skill-pack importer. Mirrors C# `SkillPackSourcesOptions`. */
export interface SkillPackSourcesOptions {
  sources: SkillPackSource[];
  /** Root directory for cached pack downloads. */
  cacheDirectory: string;
  importDefaultEnabledPacks: boolean;
  explicitlyEnabled: string[];
  /** Cache TTL in milliseconds (C# `TimeSpan CacheTtl`; default 7 days). */
  cacheTtlMs: number;
}

/** Builds {@link SkillPackSourcesOptions} with the C# defaults. */
export function skillPackSourcesOptions(
  overrides: Partial<SkillPackSourcesOptions> = {},
): SkillPackSourcesOptions {
  return {
    sources: overrides.sources ?? [...KnownSkillPacks.all],
    cacheDirectory: overrides.cacheDirectory ?? "circleai/skill-packs",
    importDefaultEnabledPacks: overrides.importDefaultEnabledPacks ?? true,
    explicitlyEnabled: overrides.explicitlyEnabled ?? [],
    cacheTtlMs: overrides.cacheTtlMs ?? 7 * 24 * 60 * 60 * 1000,
  };
}

/**
 * Strategy for materialising a remote pack into a local directory. Mirrors C#
 * `IPackDownloader`. The default HTTP + tar downloader is external; hosts and
 * tests inject an implementation (e.g. {@link InMemoryPackDownloader}).
 */
export interface IPackDownloader {
  /**
   * Ensure `source` is materialised under `cacheRoot`; returns the local path
   * containing the extracted repo (the caller then appends `skillSubdir`).
   */
  ensureAsync(
    source: SkillPackSource,
    cacheRoot: string,
    cacheTtlMs: number,
    signal?: AbortSignal,
  ): Promise<string>;
}

/**
 * Deterministic in-memory {@link IPackDownloader}. Resolves each pack to a stable
 * local path under `cacheRoot` (keyed by a sanitised pack name) and records the
 * ensured packs. No network I/O — the host's real downloader is swapped in
 * behind the same contract. Mirrors the injectable seam the C# tests use.
 */
export class InMemoryPackDownloader implements IPackDownloader {
  private readonly ensuredList: SkillPackSource[] = [];

  ensureAsync(
    source: SkillPackSource,
    cacheRoot: string,
    _cacheTtlMs: number,
    signal?: AbortSignal,
  ): Promise<string> {
    if (source == null) throw new Error("source required");
    if (isBlank(cacheRoot)) throw new Error("cacheRoot required");
    if (signal?.aborted) return Promise.reject(new Error("ensure aborted"));
    this.ensuredList.push(source);
    return Promise.resolve(`${cacheRoot}/${sanitize(source.name)}`);
  }

  /** Packs ensured so far, in call order. */
  get ensured(): readonly SkillPackSource[] {
    return [...this.ensuredList];
  }
}

function sanitize(name: string): string {
  // Replace characters that are invalid in file names on the common platforms.
  return name.replace(/[<>:"/\\|?* -]/g, "_");
}

function uuidN(): string {
  return randomUUID().replace(/-/g, "");
}

function compareCaseInsensitive(a: string, b: string): number {
  const la = a.toLowerCase();
  const lb = b.toLowerCase();
  return la < lb ? -1 : la > lb ? 1 : 0;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
