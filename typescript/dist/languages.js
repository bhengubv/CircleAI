"use strict";
// languages.ts
//
// Circle AI language layer — BCP-47 language tags, writing systems, detection,
// and the static registry of all 20 supported languages.
Object.defineProperty(exports, "__esModule", { value: true });
exports.ILanguageRegistry = exports.ILanguageDetector = exports.KnownLanguages = exports.UNKNOWN_LANGUAGE = exports.WritingSystem = void 0;
// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------
/** Writing system (script) used by a language. */
var WritingSystem;
(function (WritingSystem) {
    WritingSystem["Latin"] = "Latin";
    WritingSystem["Arabic"] = "Arabic";
    WritingSystem["Ethiopic"] = "Ethiopic";
    WritingSystem["Geez"] = "Geez";
    WritingSystem["Devanagari"] = "Devanagari";
    WritingSystem["Han"] = "Han";
    WritingSystem["Cyrillic"] = "Cyrillic";
    WritingSystem["Hebrew"] = "Hebrew";
    WritingSystem["Greek"] = "Greek";
    WritingSystem["Other"] = "Other";
})(WritingSystem || (exports.WritingSystem = WritingSystem = {}));
/** Sentinel value returned when language detection fails. */
exports.UNKNOWN_LANGUAGE = {
    bcpTag: 'und',
    englishName: 'Unknown',
    nativeName: 'Unknown',
    writingSystem: WritingSystem.Latin,
    isRtl: false,
    primaryRegion: '',
};
// ---------------------------------------------------------------------------
// KnownLanguages — static registry (20 languages in declaration order)
// ---------------------------------------------------------------------------
function tag(bcpTag, englishName, nativeName, writingSystem, isRtl, primaryRegion) {
    return { bcpTag, englishName, nativeName, writingSystem, isRtl, primaryRegion };
}
/** Static registry of every language Circle AI ships support for. */
exports.KnownLanguages = {
    // ── Africa ────────────────────────────────────────────────────────────────
    IsiZulu: tag('zu', 'isiZulu', 'isiZulu', WritingSystem.Latin, false, 'ZA'),
    Sesotho: tag('st', 'Sesotho', 'Sesotho', WritingSystem.Latin, false, 'ZA'),
    Afrikaans: tag('af', 'Afrikaans', 'Afrikaans', WritingSystem.Latin, false, 'ZA'),
    Swahili: tag('sw', 'Swahili', 'Kiswahili', WritingSystem.Latin, false, 'KE'),
    Hausa: tag('ha', 'Hausa', 'Hausa', WritingSystem.Latin, false, 'NG'),
    Amharic: tag('am', 'Amharic', 'አማርኛ', WritingSystem.Ethiopic, false, 'ET'),
    Yoruba: tag('yo', 'Yoruba', 'Yorùbá', WritingSystem.Latin, false, 'NG'),
    Igbo: tag('ig', 'Igbo', 'Igbo', WritingSystem.Latin, false, 'NG'),
    Xhosa: tag('xh', 'isiXhosa', 'isiXhosa', WritingSystem.Latin, false, 'ZA'),
    Sepedi: tag('nso', 'Sepedi', 'Sepedi', WritingSystem.Latin, false, 'ZA'),
    Setswana: tag('tn', 'Setswana', 'Setswana', WritingSystem.Latin, false, 'ZA'),
    Somali: tag('so', 'Somali', 'Soomaali', WritingSystem.Latin, false, 'SO'),
    Oromo: tag('om', 'Oromo', 'Afaan Oromoo', WritingSystem.Latin, false, 'ET'),
    // ── Middle East & North Africa ────────────────────────────────────────────
    Arabic: tag('ar', 'Arabic', 'العربية', WritingSystem.Arabic, true, 'SA'),
    // ── Europe & Americas ─────────────────────────────────────────────────────
    English: tag('en', 'English', 'English', WritingSystem.Latin, false, 'GB'),
    Portuguese: tag('pt', 'Portuguese', 'Português', WritingSystem.Latin, false, 'PT'),
    French: tag('fr', 'French', 'Français', WritingSystem.Latin, false, 'FR'),
    Spanish: tag('es', 'Spanish', 'Español', WritingSystem.Latin, false, 'ES'),
    // ── Asia ──────────────────────────────────────────────────────────────────
    Mandarin: tag('zh', 'Mandarin', '中文', WritingSystem.Han, false, 'CN'),
    Hindi: tag('hi', 'Hindi', 'हिन्दी', WritingSystem.Devanagari, false, 'IN'),
    /** All 20 languages shipped with Circle AI, in declaration order. */
    ALL: [],
};
// Populate ALL after the object is defined so we can reference the properties
exports.KnownLanguages.ALL = [
    exports.KnownLanguages.IsiZulu,
    exports.KnownLanguages.Sesotho,
    exports.KnownLanguages.Afrikaans,
    exports.KnownLanguages.Swahili,
    exports.KnownLanguages.Hausa,
    exports.KnownLanguages.Amharic,
    exports.KnownLanguages.Yoruba,
    exports.KnownLanguages.Igbo,
    exports.KnownLanguages.Xhosa,
    exports.KnownLanguages.Sepedi,
    exports.KnownLanguages.Setswana,
    exports.KnownLanguages.Somali,
    exports.KnownLanguages.Oromo,
    exports.KnownLanguages.Arabic,
    exports.KnownLanguages.English,
    exports.KnownLanguages.Portuguese,
    exports.KnownLanguages.French,
    exports.KnownLanguages.Spanish,
    exports.KnownLanguages.Mandarin,
    exports.KnownLanguages.Hindi,
];
// ---------------------------------------------------------------------------
// Store / service interfaces
// ---------------------------------------------------------------------------
/** Detects the BCP-47 language of a piece of text. */
class ILanguageDetector {
}
exports.ILanguageDetector = ILanguageDetector;
/** Registry of all BCP-47 language tags that Circle AI understands. */
class ILanguageRegistry {
}
exports.ILanguageRegistry = ILanguageRegistry;
