// languages.go
//
// WritingSystem, LanguageTag, DetectionResult, KnownLanguages,
// ILanguageDetector, ILanguageRegistry.

package circleai

import "context"

// ---------------------------------------------------------------------------
// WritingSystem
// ---------------------------------------------------------------------------

// WritingSystem is the script used by a language.
type WritingSystem int

const (
	WritingSystemLatin      WritingSystem = iota
	WritingSystemArabic
	WritingSystemEthiopic
	WritingSystemGeez       // alias for Ethiopic in the C# original; kept for completeness
	WritingSystemDevanagari
	WritingSystemHan
	WritingSystemCyrillic
	WritingSystemHebrew
	WritingSystemGreek
	WritingSystemOther
)

// ---------------------------------------------------------------------------
// LanguageTag
// ---------------------------------------------------------------------------

// LanguageTag is a BCP-47 language tag enriched with display metadata.
type LanguageTag struct {
	// BcpTag is the IETF BCP-47 language tag (e.g. "en", "zu", "ar").
	BcpTag string

	// EnglishName is the English display name of the language.
	EnglishName string

	// NativeName is the name of the language in that language.
	NativeName string

	// WritingSystem is the primary script used by this language.
	WritingSystem WritingSystem

	// IsRtl indicates whether the language is written right-to-left.
	IsRtl bool

	// PrimaryRegion is the ISO 3166-1 alpha-2 region code where this
	// language is primarily spoken (e.g. "ZA", "NG").
	PrimaryRegion string
}

// LanguageTagUnknown is the sentinel returned when detection fails.
var LanguageTagUnknown = LanguageTag{
	BcpTag:        "und",
	EnglishName:   "Unknown",
	NativeName:    "Unknown",
	WritingSystem: WritingSystemLatin,
	IsRtl:         false,
	PrimaryRegion: "",
}

// ---------------------------------------------------------------------------
// DetectionResult
// ---------------------------------------------------------------------------

// DetectionResult is the result of a language detection operation.
type DetectionResult struct {
	// Language is the detected language tag.
	Language LanguageTag

	// Confidence is the detector's confidence in [0, 1].
	Confidence float32

	// IsReliable indicates whether the result is considered reliable.
	IsReliable bool
}

// ---------------------------------------------------------------------------
// ScriptNormalisationResult
// ---------------------------------------------------------------------------

// ScriptNormalisationResult is the result of script normalisation.
type ScriptNormalisationResult struct {
	// Input is the original text.
	Input string

	// Normalised is the text after script normalisation.
	Normalised string

	// DetectedLanguage is the language detected during normalisation.
	DetectedLanguage LanguageTag
}

// ---------------------------------------------------------------------------
// KnownLanguages
// ---------------------------------------------------------------------------

// Africa
var (
	LangIsiZulu  = LanguageTag{BcpTag: "zu", EnglishName: "isiZulu", NativeName: "isiZulu", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangSesotho  = LanguageTag{BcpTag: "st", EnglishName: "Sesotho", NativeName: "Sesotho", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangAfrikaans = LanguageTag{BcpTag: "af", EnglishName: "Afrikaans", NativeName: "Afrikaans", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangSwahili  = LanguageTag{BcpTag: "sw", EnglishName: "Swahili", NativeName: "Kiswahili", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "KE"}
	LangHausa    = LanguageTag{BcpTag: "ha", EnglishName: "Hausa", NativeName: "Hausa", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "NG"}
	LangAmharic  = LanguageTag{BcpTag: "am", EnglishName: "Amharic", NativeName: "አማርኛ", WritingSystem: WritingSystemEthiopic, IsRtl: false, PrimaryRegion: "ET"}
	LangYoruba   = LanguageTag{BcpTag: "yo", EnglishName: "Yoruba", NativeName: "Yorùbá", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "NG"}
	LangIgbo     = LanguageTag{BcpTag: "ig", EnglishName: "Igbo", NativeName: "Igbo", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "NG"}
	LangXhosa    = LanguageTag{BcpTag: "xh", EnglishName: "isiXhosa", NativeName: "isiXhosa", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangSepedi   = LanguageTag{BcpTag: "nso", EnglishName: "Sepedi", NativeName: "Sepedi", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangSetswana = LanguageTag{BcpTag: "tn", EnglishName: "Setswana", NativeName: "Setswana", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ZA"}
	LangSomali   = LanguageTag{BcpTag: "so", EnglishName: "Somali", NativeName: "Soomaali", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "SO"}
	LangOromo    = LanguageTag{BcpTag: "om", EnglishName: "Oromo", NativeName: "Afaan Oromoo", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ET"}
)

// Middle East & North Africa
var (
	LangArabic = LanguageTag{BcpTag: "ar", EnglishName: "Arabic", NativeName: "العربية", WritingSystem: WritingSystemArabic, IsRtl: true, PrimaryRegion: "SA"}
)

// Europe & Americas
var (
	LangEnglish    = LanguageTag{BcpTag: "en", EnglishName: "English", NativeName: "English", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "GB"}
	LangPortuguese = LanguageTag{BcpTag: "pt", EnglishName: "Portuguese", NativeName: "Português", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "PT"}
	LangFrench     = LanguageTag{BcpTag: "fr", EnglishName: "French", NativeName: "Français", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "FR"}
	LangSpanish    = LanguageTag{BcpTag: "es", EnglishName: "Spanish", NativeName: "Español", WritingSystem: WritingSystemLatin, IsRtl: false, PrimaryRegion: "ES"}
)

// Asia
var (
	LangMandarin = LanguageTag{BcpTag: "zh", EnglishName: "Mandarin", NativeName: "中文", WritingSystem: WritingSystemHan, IsRtl: false, PrimaryRegion: "CN"}
	LangHindi    = LanguageTag{BcpTag: "hi", EnglishName: "Hindi", NativeName: "हिन्दी", WritingSystem: WritingSystemDevanagari, IsRtl: false, PrimaryRegion: "IN"}
)

// KnownLanguagesAll holds all 20 languages shipped with Circle AI,
// in declaration order.
var KnownLanguagesAll = []LanguageTag{
	LangIsiZulu, LangSesotho, LangAfrikaans, LangSwahili, LangHausa, LangAmharic,
	LangYoruba, LangIgbo, LangXhosa, LangSepedi, LangSetswana, LangSomali, LangOromo,
	LangArabic,
	LangEnglish, LangPortuguese, LangFrench, LangSpanish,
	LangMandarin, LangHindi,
}

// ---------------------------------------------------------------------------
// ILanguageDetector
// ---------------------------------------------------------------------------

// ILanguageDetector detects the BCP-47 language of a piece of text.
type ILanguageDetector interface {
	// Detect detects the most likely language. Returns LanguageTagUnknown
	// with Confidence=0 when detection fails.
	Detect(ctx context.Context, text string) (DetectionResult, error)

	// DetectMultiple returns up to maxResults candidates ranked by confidence.
	DetectMultiple(ctx context.Context, text string, maxResults int) ([]DetectionResult, error)
}

// ---------------------------------------------------------------------------
// ILanguageRegistry
// ---------------------------------------------------------------------------

// ILanguageRegistry is the registry of all BCP-47 language tags that
// Circle AI understands.
type ILanguageRegistry interface {
	// GetByBcpTag returns the LanguageTag for the given BCP-47 tag,
	// or nil if not found.
	GetByBcpTag(bcpTag string) *LanguageTag

	// GetAll returns all language tags in the registry.
	GetAll() []LanguageTag

	// GetForRegion returns all language tags whose primary region matches
	// the given ISO 3166-1 alpha-2 region code.
	GetForRegion(isoRegion string) []LanguageTag

	// IsSupported reports whether the given BCP-47 tag is in the registry.
	IsSupported(bcpTag string) bool
}
