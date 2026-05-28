export declare enum WritingSystem {
    Latin = "Latin",
    Arabic = "Arabic",
    Ethiopic = "Ethiopic",
    Geez = "Geez",
    Devanagari = "Devanagari",
    Han = "Han",
    Cyrillic = "Cyrillic",
    Hebrew = "Hebrew",
    Greek = "Greek",
    Other = "Other"
}
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
export declare const UNKNOWN_LANGUAGE: LanguageTag;
/** Result of a language detection operation. */
export interface DetectionResult {
    readonly language: LanguageTag;
    readonly confidence: number;
    readonly isReliable: boolean;
}
/**
 * Static registry of every language Circle AI ships support for.
 * 20 entries in canonical declaration order.
 * Must match fixtures/language_tags.json exactly.
 */
export declare const KnownLanguages: {
    IsiZulu: LanguageTag;
    Sesotho: LanguageTag;
    Afrikaans: LanguageTag;
    Swahili: LanguageTag;
    Hausa: LanguageTag;
    Amharic: LanguageTag;
    Yoruba: LanguageTag;
    Igbo: LanguageTag;
    Xhosa: LanguageTag;
    Sepedi: LanguageTag;
    Setswana: LanguageTag;
    Somali: LanguageTag;
    Oromo: LanguageTag;
    Arabic: LanguageTag;
    English: LanguageTag;
    Portuguese: LanguageTag;
    French: LanguageTag;
    Spanish: LanguageTag;
    Mandarin: LanguageTag;
    Hindi: LanguageTag;
    /** All languages shipped with Circle AI, in canonical declaration order. */
    ALL: LanguageTag[];
};
