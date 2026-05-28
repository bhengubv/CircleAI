// languages/index.ets — Circle AI language layer
// ArkTS port: enum-based WritingSystem, no `any`, explicit types throughout.
// All 20 languages must exactly match fixtures/language_tags.json.

// ---------------------------------------------------------------------------
// WritingSystem enum
// ---------------------------------------------------------------------------

/** Writing system (script) used by a language. */
export enum WritingSystem {
  Latin      = 0,
  Arabic     = 1,
  Ethiopic   = 2,
  Han        = 3,
  Devanagari = 4,
}

// ---------------------------------------------------------------------------
// Core types
// ---------------------------------------------------------------------------

/** A BCP-47 language tag enriched with display metadata. */
export interface LanguageTag {
  /** IETF BCP-47 tag (e.g. "en", "zu", "ar"). */
  bcp47Tag: string;
  /** English display name (e.g. "English", "isiZulu"). */
  englishName: string;
  /** Native name of the language (e.g. "isiZulu", "العربية"). */
  nativeName: string;
  /** Writing system / script used by this language. */
  writingSystem: WritingSystem;
  /** true for right-to-left languages (Arabic, etc.). */
  isRtl: boolean;
  /** ISO 3166-1 alpha-2 primary region code (e.g. "ZA", "NG"). */
  primaryRegion: string;
}

/** Sentinel value returned when language detection fails. */
export const UNKNOWN_LANGUAGE: LanguageTag = {
  bcp47Tag:      'und',
  englishName:   'Unknown',
  nativeName:    'Unknown',
  writingSystem: WritingSystem.Latin,
  isRtl:         false,
  primaryRegion: '',
};

// ---------------------------------------------------------------------------
// KnownLanguages — static registry (20 languages, declaration order fixed)
// Must match fixtures/language_tags.json exactly.
// ---------------------------------------------------------------------------

/** Static registry of every language Circle AI ships support for. */
export const KnownLanguages = {
  // ── Africa ────────────────────────────────────────────────────────────────
  IsiZulu:    {
    bcp47Tag: 'zu',  englishName: 'isiZulu',    nativeName: 'isiZulu',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Sesotho:    {
    bcp47Tag: 'st',  englishName: 'Sesotho',    nativeName: 'Sesotho',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Afrikaans:  {
    bcp47Tag: 'af',  englishName: 'Afrikaans',  nativeName: 'Afrikaans',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Swahili:    {
    bcp47Tag: 'sw',  englishName: 'Swahili',    nativeName: 'Kiswahili',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'KE',
  } as LanguageTag,
  Hausa:      {
    bcp47Tag: 'ha',  englishName: 'Hausa',      nativeName: 'Hausa',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'NG',
  } as LanguageTag,
  Amharic:    {
    bcp47Tag: 'am',  englishName: 'Amharic',    nativeName: 'አማርኛ',
    writingSystem: WritingSystem.Ethiopic,   isRtl: false, primaryRegion: 'ET',
  } as LanguageTag,
  Yoruba:     {
    bcp47Tag: 'yo',  englishName: 'Yoruba',     nativeName: 'Yorùbá',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'NG',
  } as LanguageTag,
  Igbo:       {
    bcp47Tag: 'ig',  englishName: 'Igbo',       nativeName: 'Igbo',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'NG',
  } as LanguageTag,
  Xhosa:      {
    bcp47Tag: 'xh',  englishName: 'isiXhosa',   nativeName: 'isiXhosa',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Sepedi:     {
    bcp47Tag: 'nso', englishName: 'Sepedi',     nativeName: 'Sepedi',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Setswana:   {
    bcp47Tag: 'tn',  englishName: 'Setswana',   nativeName: 'Setswana',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ZA',
  } as LanguageTag,
  Somali:     {
    bcp47Tag: 'so',  englishName: 'Somali',     nativeName: 'Soomaali',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'SO',
  } as LanguageTag,
  Oromo:      {
    bcp47Tag: 'om',  englishName: 'Oromo',      nativeName: 'Afaan Oromoo',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ET',
  } as LanguageTag,

  // ── Middle East & North Africa ────────────────────────────────────────────
  Arabic:     {
    bcp47Tag: 'ar',  englishName: 'Arabic',     nativeName: 'العربية',
    writingSystem: WritingSystem.Arabic,     isRtl: true,  primaryRegion: 'SA',
  } as LanguageTag,

  // ── Europe & Americas ─────────────────────────────────────────────────────
  English:    {
    bcp47Tag: 'en',  englishName: 'English',    nativeName: 'English',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'GB',
  } as LanguageTag,
  Portuguese: {
    bcp47Tag: 'pt',  englishName: 'Portuguese', nativeName: 'Português',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'PT',
  } as LanguageTag,
  French:     {
    bcp47Tag: 'fr',  englishName: 'French',     nativeName: 'Français',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'FR',
  } as LanguageTag,
  Spanish:    {
    bcp47Tag: 'es',  englishName: 'Spanish',    nativeName: 'Español',
    writingSystem: WritingSystem.Latin,      isRtl: false, primaryRegion: 'ES',
  } as LanguageTag,

  // ── Asia ──────────────────────────────────────────────────────────────────
  Mandarin:   {
    bcp47Tag: 'zh',  englishName: 'Mandarin',   nativeName: '中文',
    writingSystem: WritingSystem.Han,        isRtl: false, primaryRegion: 'CN',
  } as LanguageTag,
  Hindi:      {
    bcp47Tag: 'hi',  englishName: 'Hindi',      nativeName: 'हिन्दी',
    writingSystem: WritingSystem.Devanagari, isRtl: false, primaryRegion: 'IN',
  } as LanguageTag,
};

/**
 * All 20 Circle AI languages in canonical declaration order.
 * Must match fixtures/language_tags.json order exactly.
 */
export const ALL_LANGUAGES: LanguageTag[] = [
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
