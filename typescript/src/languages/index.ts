// languages/index.ts
// Circle AI language registry: writing systems, language tags, detection results.
// Ported from Circle.AI.Languages (C#).

// ─────────────────────────────────────────────────────────────────────────────
// WritingSystem enum
// ─────────────────────────────────────────────────────────────────────────────

export enum WritingSystem {
  Latin = "Latin",
  Arabic = "Arabic",
  Ethiopic = "Ethiopic",
  Geez = "Geez",
  Devanagari = "Devanagari",
  Han = "Han",
  Cyrillic = "Cyrillic",
  Hebrew = "Hebrew",
  Greek = "Greek",
  Other = "Other",
}

// ─────────────────────────────────────────────────────────────────────────────
// LanguageTag
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A BCP-47 language tag enriched with display metadata.
 *
 * Field naming matches the canonical fixture format (language_tags.json):
 *   englishName   = human-readable English display name
 *   nativeName    = name in the language itself
 *   writingSystem = WritingSystem enum value
 *   primaryRegion = ISO 3166-1 alpha-2 region code
 */
export interface LanguageTag {
  readonly bcpTag: string;
  readonly englishName: string;
  readonly nativeName: string;
  readonly writingSystem: WritingSystem;
  readonly isRtl: boolean;
  readonly primaryRegion: string;
}

/** Sentinel for an unrecognised or undetermined language. */
export const UNKNOWN_LANGUAGE: LanguageTag = {
  bcpTag: "und",
  englishName: "Unknown",
  nativeName: "Unknown",
  writingSystem: WritingSystem.Latin,
  isRtl: false,
  primaryRegion: "",
};

// ─────────────────────────────────────────────────────────────────────────────
// DetectionResult
// ─────────────────────────────────────────────────────────────────────────────

/** Result of a language detection operation. */
export interface DetectionResult {
  readonly language: LanguageTag;
  readonly confidence: number;
  readonly isReliable: boolean;
}

// ─────────────────────────────────────────────────────────────────────────────
// KnownLanguages
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Static registry of every language Circle AI ships support for.
 * 20 entries in canonical declaration order.
 * Must match fixtures/language_tags.json exactly.
 */
export const KnownLanguages = {
  // ── Africa ──────────────────────────────────────────────────────────────────
  IsiZulu: {
    bcpTag: "zu", englishName: "isiZulu", nativeName: "isiZulu",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Sesotho: {
    bcpTag: "st", englishName: "Sesotho", nativeName: "Sesotho",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Afrikaans: {
    bcpTag: "af", englishName: "Afrikaans", nativeName: "Afrikaans",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Swahili: {
    bcpTag: "sw", englishName: "Swahili", nativeName: "Kiswahili",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "KE",
  } as LanguageTag,

  Hausa: {
    bcpTag: "ha", englishName: "Hausa", nativeName: "Hausa",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
  } as LanguageTag,

  Amharic: {
    bcpTag: "am", englishName: "Amharic", nativeName: "አማርኛ",
    writingSystem: WritingSystem.Ethiopic, isRtl: false, primaryRegion: "ET",
  } as LanguageTag,

  Yoruba: {
    bcpTag: "yo", englishName: "Yoruba", nativeName: "Yorùbá",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
  } as LanguageTag,

  Igbo: {
    bcpTag: "ig", englishName: "Igbo", nativeName: "Igbo",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
  } as LanguageTag,

  Xhosa: {
    bcpTag: "xh", englishName: "isiXhosa", nativeName: "isiXhosa",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Sepedi: {
    bcpTag: "nso", englishName: "Sepedi", nativeName: "Sepedi",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Setswana: {
    bcpTag: "tn", englishName: "Setswana", nativeName: "Setswana",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
  } as LanguageTag,

  Somali: {
    bcpTag: "so", englishName: "Somali", nativeName: "Soomaali",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "SO",
  } as LanguageTag,

  Oromo: {
    bcpTag: "om", englishName: "Oromo", nativeName: "Afaan Oromoo",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ET",
  } as LanguageTag,

  // ── Middle East & North Africa ─────────────────────────────────────────────
  Arabic: {
    bcpTag: "ar", englishName: "Arabic", nativeName: "العربية",
    writingSystem: WritingSystem.Arabic, isRtl: true, primaryRegion: "SA",
  } as LanguageTag,

  // ── Europe & Americas ──────────────────────────────────────────────────────
  English: {
    bcpTag: "en", englishName: "English", nativeName: "English",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "GB",
  } as LanguageTag,

  Portuguese: {
    bcpTag: "pt", englishName: "Portuguese", nativeName: "Português",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "PT",
  } as LanguageTag,

  French: {
    bcpTag: "fr", englishName: "French", nativeName: "Français",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "FR",
  } as LanguageTag,

  Spanish: {
    bcpTag: "es", englishName: "Spanish", nativeName: "Español",
    writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ES",
  } as LanguageTag,

  // ── Asia ───────────────────────────────────────────────────────────────────
  Mandarin: {
    bcpTag: "zh", englishName: "Mandarin", nativeName: "中文",
    writingSystem: WritingSystem.Han, isRtl: false, primaryRegion: "CN",
  } as LanguageTag,

  Hindi: {
    bcpTag: "hi", englishName: "Hindi", nativeName: "हिन्दी",
    writingSystem: WritingSystem.Devanagari, isRtl: false, primaryRegion: "IN",
  } as LanguageTag,

  /** All languages shipped with Circle AI, in canonical declaration order. */
  ALL: [] as LanguageTag[],
};

// Populate ALL after all members are declared (avoids forward-reference issues).
KnownLanguages.ALL = [
  KnownLanguages.IsiZulu,
  KnownLanguages.Sesotho,
  KnownLanguages.Afrikaans,
  KnownLanguages.Swahili,
  KnownLanguages.Hausa,
  KnownLanguages.Amharic,
  KnownLanguages.Yoruba,
  KnownLanguages.Igbo,
  KnownLanguages.Xhosa,
  KnownLanguages.Sepedi,
  KnownLanguages.Setswana,
  KnownLanguages.Somali,
  KnownLanguages.Oromo,
  KnownLanguages.Arabic,
  KnownLanguages.English,
  KnownLanguages.Portuguese,
  KnownLanguages.French,
  KnownLanguages.Spanish,
  KnownLanguages.Mandarin,
  KnownLanguages.Hindi,
];
