/**
 * languages/language/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// languages/language/index.ts
//
// Port of CircleAI.Languages.Language — the language-pack contract surface:
// ILanguagePack / ILanguagePackRegistry, the LanguagePackMetadata / CulturalNote
// records, the thread-safe DefaultLanguagePackRegistry, and the shared
// LanguagePackRegistry / LocaleHintMerge helpers (LanguagePackHelpers.cs).
//
// The 8 shipped per-language packs live in ./packs.js and are re-exported here.

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** Metadata for a language pack. Mirrors C# `LanguagePackMetadata`. */
export interface LanguagePackMetadata {
  readonly bcpTag: string;
  readonly displayName: string;
  readonly nativeName: string;
  readonly primaryRegion: string;
  readonly spokenInRegions: readonly string[];
  /** Pack version as "{major}.{minor}" — mirrors C# System.Version. */
  readonly packVersion: string;
}

/** Cultural/contextual note for a specific topic. Mirrors C# `CulturalNote`. */
export interface CulturalNote {
  readonly context: string;
  readonly guidance: string;
  readonly examples: readonly string[];
}

// ─────────────────────────────────────────────────────────────────────────────
// ILanguagePack
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A language-specific knowledge pack.
 * Provides idiomatic expressions, cultural context, and prompt tuning
 * for the on-device LLM to reason correctly in this language.
 */
export interface ILanguagePack {
  readonly metadata: LanguagePackMetadata;

  /** Returns the idiomatic translation of a common phrase, or null if not mapped. */
  getIdiomaticExpression(phrase: string): string | null;

  /** Adapts a base system prompt for this language and culture. */
  adaptSystemPrompt(basePrompt: string): string;

  /** Cultural notes for a given context (e.g. "greeting", "business", "medical"). */
  getCulturalNotes(context: string): readonly CulturalNote[];

  /** Returns a locale-appropriate greeting for the given time of day. */
  getGreeting(timeOfDay: string): string;

  /** Returns locale-specific number/date/currency formatting hints. */
  getLocaleHints(): ReadonlyMap<string, string>;
}

// ─────────────────────────────────────────────────────────────────────────────
// ILanguagePackRegistry
// ─────────────────────────────────────────────────────────────────────────────

/** Registry of all installed language packs. */
export interface ILanguagePackRegistry {
  register(pack: ILanguagePack): void;
  getByBcpTag(bcpTag: string): ILanguagePack | null;
  getAvailablePacks(): readonly LanguagePackMetadata[];
  hasPack(bcpTag: string): boolean;
}

/**
 * In-memory {@link ILanguagePackRegistry}. The C# original guards a Dictionary
 * with a Lock; JavaScript is single-threaded so the map access is already
 * atomic — the observable contract is identical.
 */
export class DefaultLanguagePackRegistry implements ILanguagePackRegistry {
  private readonly packs = new Map<string, ILanguagePack>();

  register(pack: ILanguagePack): void {
    if (pack === null || pack === undefined) {
      throw new Error("pack must not be null");
    }
    this.packs.set(pack.metadata.bcpTag, pack);
  }

  getByBcpTag(bcpTag: string): ILanguagePack | null {
    return this.packs.get(bcpTag) ?? null;
  }

  getAvailablePacks(): readonly LanguagePackMetadata[] {
    return [...this.packs.values()].map((p) => p.metadata);
  }

  hasPack(bcpTag: string): boolean {
    return this.packs.has(bcpTag);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// LanguagePackHelpers — LanguagePackRegistry + LocaleHintMerge
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Registry helper with BCP-47 matching. The C# original is keyed
 * case-insensitively (OrdinalIgnoreCase); this port lower-cases the tag for
 * lookup to reproduce that behaviour.
 */
export class LanguagePackRegistry {
  private readonly byTag = new Map<string, ILanguagePack>();

  register(pack: ILanguagePack): void {
    if (pack === null || pack === undefined) {
      throw new Error("pack must not be null");
    }
    this.byTag.set(pack.metadata.bcpTag.toLowerCase(), pack);
  }

  getByExactTag(bcpTag: string): ILanguagePack | null {
    if (bcpTag === null || bcpTag === undefined || bcpTag.trim().length === 0) {
      return null;
    }
    return this.byTag.get(bcpTag.toLowerCase()) ?? null;
  }

  getByLanguage(langPrefix: string): ILanguagePack | null {
    if (
      langPrefix === null ||
      langPrefix === undefined ||
      langPrefix.trim().length === 0
    ) {
      return null;
    }
    const prefix = langPrefix.split("-")[0].toLowerCase();
    for (const pack of this.byTag.values()) {
      if (pack.metadata.bcpTag.toLowerCase().startsWith(prefix)) {
        return pack;
      }
    }
    return null;
  }

  forRegion(region: string): readonly ILanguagePack[] {
    if (region === null || region === undefined || region.trim().length === 0) {
      throw new Error("region required");
    }
    const target = region.toLowerCase();
    return [...this.byTag.values()].filter((p) =>
      p.metadata.spokenInRegions.some((r) => r.toLowerCase() === target),
    );
  }

  allTags(): readonly string[] {
    return [...this.byTag.values()]
      .map((p) => p.metadata.bcpTag)
      .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  }
}

/** Locale-hint merge helper. Mirrors C# `LocaleHintMerge`. */
export const LocaleHintMerge = {
  /**
   * Merges two hint maps. `primary` wins on key collisions. Keys are compared
   * case-insensitively (OrdinalIgnoreCase in C#): the last write for a given
   * lower-cased key wins.
   */
  merge(
    primary: ReadonlyMap<string, string>,
    secondary: ReadonlyMap<string, string>,
  ): ReadonlyMap<string, string> {
    if (primary === null || primary === undefined) {
      throw new Error("primary must not be null");
    }
    if (secondary === null || secondary === undefined) {
      throw new Error("secondary must not be null");
    }
    // Case-insensitive merge: seed with secondary, then overlay primary.
    const byLowerKey = new Map<string, [string, string]>();
    for (const [k, v] of secondary) byLowerKey.set(k.toLowerCase(), [k, v]);
    for (const [k, v] of primary) byLowerKey.set(k.toLowerCase(), [k, v]);
    const merged = new Map<string, string>();
    for (const [origKey, val] of byLowerKey.values()) merged.set(origKey, val);
    return merged;
  },
} as const;

export * from "./packs";
