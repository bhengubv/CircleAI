/** Writing system (script) used by a language. */
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
export declare const UNKNOWN_LANGUAGE: LanguageTag;
/** Result of language detection. */
export interface DetectionResult {
    readonly language: LanguageTag;
    readonly confidence: number;
    readonly isReliable: boolean;
}
/** Result of script normalisation. */
export interface ScriptNormalisationResult {
    readonly input: string;
    readonly normalised: string;
    readonly detectedLanguage: LanguageTag;
}
/** Static registry of every language Circle AI ships support for. */
export declare const KnownLanguages: {
    readonly IsiZulu: LanguageTag;
    readonly Sesotho: LanguageTag;
    readonly Afrikaans: LanguageTag;
    readonly Swahili: LanguageTag;
    readonly Hausa: LanguageTag;
    readonly Amharic: LanguageTag;
    readonly Yoruba: LanguageTag;
    readonly Igbo: LanguageTag;
    readonly Xhosa: LanguageTag;
    readonly Sepedi: LanguageTag;
    readonly Setswana: LanguageTag;
    readonly Somali: LanguageTag;
    readonly Oromo: LanguageTag;
    readonly Arabic: LanguageTag;
    readonly English: LanguageTag;
    readonly Portuguese: LanguageTag;
    readonly French: LanguageTag;
    readonly Spanish: LanguageTag;
    readonly Mandarin: LanguageTag;
    readonly Hindi: LanguageTag;
    /** All 20 languages shipped with Circle AI, in declaration order. */
    readonly ALL: LanguageTag[];
};
/** Detects the BCP-47 language of a piece of text. */
export declare abstract class ILanguageDetector {
    /**
     * Detects the most likely language.
     * Returns UNKNOWN_LANGUAGE with confidence=0 when detection fails.
     */
    abstract detect(text: string): Promise<DetectionResult>;
    /** Returns up to maxResults candidates ranked by confidence. */
    abstract detectMultiple(text: string, maxResults?: number): Promise<readonly DetectionResult[]>;
}
/** Registry of all BCP-47 language tags that Circle AI understands. */
export declare abstract class ILanguageRegistry {
    abstract getByBcpTag(bcpTag: string): LanguageTag | null;
    abstract getAll(): readonly LanguageTag[];
    abstract getForRegion(isoRegion: string): readonly LanguageTag[];
    abstract isSupported(bcpTag: string): boolean;
}
