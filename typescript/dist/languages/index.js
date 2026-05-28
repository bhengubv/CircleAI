"use strict";
// languages/index.ts
// Circle AI language registry: writing systems, language tags, detection results.
// Ported from Circle.AI.Languages (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.KnownLanguages = exports.UNKNOWN_LANGUAGE = exports.WritingSystem = void 0;
// ─────────────────────────────────────────────────────────────────────────────
// WritingSystem enum
// ─────────────────────────────────────────────────────────────────────────────
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
/** Sentinel for an unrecognised or undetermined language. */
exports.UNKNOWN_LANGUAGE = {
    bcpTag: "und",
    englishName: "Unknown",
    nativeName: "Unknown",
    writingSystem: WritingSystem.Latin,
    isRtl: false,
    primaryRegion: "",
};
// ─────────────────────────────────────────────────────────────────────────────
// KnownLanguages
// ─────────────────────────────────────────────────────────────────────────────
/**
 * Static registry of every language Circle AI ships support for.
 * 20 entries in canonical declaration order.
 * Must match fixtures/language_tags.json exactly.
 */
exports.KnownLanguages = {
    // ── Africa ──────────────────────────────────────────────────────────────────
    IsiZulu: {
        bcpTag: "zu", englishName: "isiZulu", nativeName: "isiZulu",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Sesotho: {
        bcpTag: "st", englishName: "Sesotho", nativeName: "Sesotho",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Afrikaans: {
        bcpTag: "af", englishName: "Afrikaans", nativeName: "Afrikaans",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Swahili: {
        bcpTag: "sw", englishName: "Swahili", nativeName: "Kiswahili",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "KE",
    },
    Hausa: {
        bcpTag: "ha", englishName: "Hausa", nativeName: "Hausa",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
    },
    Amharic: {
        bcpTag: "am", englishName: "Amharic", nativeName: "አማርኛ",
        writingSystem: WritingSystem.Ethiopic, isRtl: false, primaryRegion: "ET",
    },
    Yoruba: {
        bcpTag: "yo", englishName: "Yoruba", nativeName: "Yorùbá",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
    },
    Igbo: {
        bcpTag: "ig", englishName: "Igbo", nativeName: "Igbo",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "NG",
    },
    Xhosa: {
        bcpTag: "xh", englishName: "isiXhosa", nativeName: "isiXhosa",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Sepedi: {
        bcpTag: "nso", englishName: "Sepedi", nativeName: "Sepedi",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Setswana: {
        bcpTag: "tn", englishName: "Setswana", nativeName: "Setswana",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ZA",
    },
    Somali: {
        bcpTag: "so", englishName: "Somali", nativeName: "Soomaali",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "SO",
    },
    Oromo: {
        bcpTag: "om", englishName: "Oromo", nativeName: "Afaan Oromoo",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ET",
    },
    // ── Middle East & North Africa ─────────────────────────────────────────────
    Arabic: {
        bcpTag: "ar", englishName: "Arabic", nativeName: "العربية",
        writingSystem: WritingSystem.Arabic, isRtl: true, primaryRegion: "SA",
    },
    // ── Europe & Americas ──────────────────────────────────────────────────────
    English: {
        bcpTag: "en", englishName: "English", nativeName: "English",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "GB",
    },
    Portuguese: {
        bcpTag: "pt", englishName: "Portuguese", nativeName: "Português",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "PT",
    },
    French: {
        bcpTag: "fr", englishName: "French", nativeName: "Français",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "FR",
    },
    Spanish: {
        bcpTag: "es", englishName: "Spanish", nativeName: "Español",
        writingSystem: WritingSystem.Latin, isRtl: false, primaryRegion: "ES",
    },
    // ── Asia ───────────────────────────────────────────────────────────────────
    Mandarin: {
        bcpTag: "zh", englishName: "Mandarin", nativeName: "中文",
        writingSystem: WritingSystem.Han, isRtl: false, primaryRegion: "CN",
    },
    Hindi: {
        bcpTag: "hi", englishName: "Hindi", nativeName: "हिन्दी",
        writingSystem: WritingSystem.Devanagari, isRtl: false, primaryRegion: "IN",
    },
    /** All languages shipped with Circle AI, in canonical declaration order. */
    ALL: [],
};
// Populate ALL after all members are declared (avoids forward-reference issues).
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
