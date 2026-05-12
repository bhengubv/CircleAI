// languages.ts
//
// Circle AI language layer — BCP-47 language tags, writing systems, detection,
// and the static registry of all 20 supported languages.

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/** Writing system (script) used by a language. */
export enum WritingSystem {
  Latin      = 'Latin',
  Arabic     = 'Arabic',
  Ethiopic   = 'Ethiopic',
  Geez       = 'Geez',
  Devanagari = 'Devanagari',
  Han        = 'Han',
  Cyrillic   = 'Cyrillic',
  Hebrew     = 'Hebrew',
  Greek      = 'Greek',
  Other      = 'Other',
}

// ---------------------------------------------------------------------------
// Core types
// ---------------------------------------------------------------------------

/** A BCP-47 language tag enriched with display metadata. */
export interface LanguageTag {
  /** IETF BCP-47 tag (e.g. "en", "zu", "ar"). */
  readonly bcpTag: string;
  /** English display name (e.g. "English", "isiZulu"). */
  readonly englishName: string;
  /** Native name of the language (e.g. "isiZulu", "العربية"). */
  readonly nativeName: string;
  /** Writing system / script used by this language. */
  readonly writingSystem: WritingSystem;
  /** true for right-to-left languages (Arabic, Hebrew, etc.). */
  readonly isRtl: boolean;
  /** ISO 3166-1 alpha-2 primary region code (e.g. "ZA", "NG"). */
  readonly primaryRegion: string;
}

/** Sentinel value returned when language detection fails. */
export const UNKNOWN_LANGUAGE: LanguageTag = {
  bcpTag:        'und',
  englishName:   'Unknown',
  nativeName:    'Unknown',
  writingSystem: WritingSystem.Latin,
  isRtl:         false,
  primaryRegion: '',
};

/** Result of language detection. */
export interface DetectionResult {
  readonly language:   LanguageTag;
  readonly confidence: number;
  readonly isReliable: boolean;
}

/** Result of script normalisation. */
export interface ScriptNormalisationResult {
  readonly input:            string;
  readonly normalised:       string;
  readonly detectedLanguage: LanguageTag;
}

// ---------------------------------------------------------------------------
// KnownLanguages — static registry (20 languages in declaration order)
// ---------------------------------------------------------------------------

function tag(
  bcpTag: string,
  englishName: string,
  nativeName: string,
  writingSystem: WritingSystem,
  isRtl: boolean,
  primaryRegion: string,
): LanguageTag {
  return { bcpTag, englishName, nativeName, writingSystem, isRtl, primaryRegion };
}

/** Static registry of every language Circle AI ships support for. */
export const KnownLanguages = {
  // ── Africa ────────────────────────────────────────────────────────────────
  IsiZulu:   tag('zu',  'isiZulu',    'isiZulu',       WritingSystem.Latin,      false, 'ZA'),
  Sesotho:   tag('st',  'Sesotho',    'Sesotho',       WritingSystem.Latin,      false, 'ZA'),
  Afrikaans: tag('af',  'Afrikaans',  'Afrikaans',     WritingSystem.Latin,      false, 'ZA'),
  Swahili:   tag('sw',  'Swahili',    'Kiswahili',     WritingSystem.Latin,      false, 'KE'),
  Hausa:     tag('ha',  'Hausa',      'Hausa',         WritingSystem.Latin,      false, 'NG'),
  Amharic:   tag('am',  'Amharic',    'አማርኛ',          WritingSystem.Ethiopic,   false, 'ET'),
  Yoruba:    tag('yo',  'Yoruba',     'Yorùbá',        WritingSystem.Latin,      false, 'NG'),
  Igbo:      tag('ig',  'Igbo',       'Igbo',          WritingSystem.Latin,      false, 'NG'),
  Xhosa:     tag('xh',  'isiXhosa',   'isiXhosa',      WritingSystem.Latin,      false, 'ZA'),
  Sepedi:    tag('nso', 'Sepedi',     'Sepedi',        WritingSystem.Latin,      false, 'ZA'),
  Setswana:  tag('tn',  'Setswana',   'Setswana',      WritingSystem.Latin,      false, 'ZA'),
  Somali:    tag('so',  'Somali',     'Soomaali',      WritingSystem.Latin,      false, 'SO'),
  Oromo:     tag('om',  'Oromo',      'Afaan Oromoo',  WritingSystem.Latin,      false, 'ET'),

  // ── Middle East & North Africa ────────────────────────────────────────────
  Arabic:    tag('ar',  'Arabic',     'العربية',       WritingSystem.Arabic,     true,  'SA'),

  // ── Europe & Americas ─────────────────────────────────────────────────────
  English:   tag('en',  'English',    'English',       WritingSystem.Latin,      false, 'GB'),
  Portuguese:tag('pt',  'Portuguese', 'Português',     WritingSystem.Latin,      false, 'PT'),
  French:    tag('fr',  'French',     'Français',      WritingSystem.Latin,      false, 'FR'),
  Spanish:   tag('es',  'Spanish',    'Español',       WritingSystem.Latin,      false, 'ES'),

  // ── Asia ──────────────────────────────────────────────────────────────────
  Mandarin:  tag('zh',  'Mandarin',   '中文',           WritingSystem.Han,        false, 'CN'),
  Hindi:     tag('hi',  'Hindi',      'हिन्दी',          WritingSystem.Devanagari, false, 'IN'),

  /** All 20 languages shipped with Circle AI, in declaration order. */
  ALL: [] as LanguageTag[],
} as const;

// Populate ALL after the object is defined so we can reference the properties
(KnownLanguages as { ALL: LanguageTag[] }).ALL = [
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

// ---------------------------------------------------------------------------
// Store / service interfaces
// ---------------------------------------------------------------------------

/** Detects the BCP-47 language of a piece of text. */
export abstract class ILanguageDetector {
  /**
   * Detects the most likely language.
   * Returns UNKNOWN_LANGUAGE with confidence=0 when detection fails.
   */
  abstract detect(text: string): Promise<DetectionResult>;

  /** Returns up to maxResults candidates ranked by confidence. */
  abstract detectMultiple(text: string, maxResults?: number): Promise<readonly DetectionResult[]>;
}

/** Registry of all BCP-47 language tags that Circle AI understands. */
export abstract class ILanguageRegistry {
  abstract getByBcpTag(bcpTag: string): LanguageTag | null;
  abstract getAll(): readonly LanguageTag[];
  abstract getForRegion(isoRegion: string): readonly LanguageTag[];
  abstract isSupported(bcpTag: string): boolean;
}
