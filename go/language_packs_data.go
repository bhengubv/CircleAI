// language_packs_data.go
//
// Ports the 8 per-language packs, each a data-driven ILanguagePack:
//   isiZuluLanguagePack.cs   -> IsiZuluLanguagePack   / IsiZuluLanguagePackInstance
//   SwahiliLanguagePack.cs    -> SwahiliLanguagePack    / SwahiliLanguagePackInstance
//   AmharicLanguagePack.cs    -> AmharicLanguagePack    / AmharicLanguagePackInstance
//   HausaLanguagePack.cs      -> HausaLanguagePack      / HausaLanguagePackInstance
//   AfrikaansLanguagePack.cs  -> AfrikaansLanguagePack  / AfrikaansLanguagePackInstance
//   ArabicLanguagePack.cs     -> ArabicLanguagePack     / ArabicLanguagePackInstance
//   PortugueseLanguagePack.cs -> PortugueseLanguagePack / PortugueseLanguagePackInstance
//   SesothoLanguagePack.cs    -> SesothoLanguagePack    / SesothoLanguagePackInstance
//
// Every C# pack follows the same template (metadata + idiom map + cultural
// notes + adapt-prompt + greeting + locale hints). The Go port encodes each
// pack's data in a staticLanguagePack backing value; the exported concrete
// types embed it so each language keeps a distinct nominal type while the
// behaviour stays byte-identical to the C# original.

package circleai

import "strings"

// ---------------------------------------------------------------------------
// staticLanguagePack — shared data-driven ILanguagePack implementation
// ---------------------------------------------------------------------------

// staticLanguagePack is the data table + behaviour shared by every per-language
// pack. Idiom lookups and cultural-note lookups are case-insensitive, matching
// the C# StringComparer.OrdinalIgnoreCase dictionaries.
type staticLanguagePack struct {
	meta    LanguagePackMetadata
	idioms  map[string]string         // key = lower-cased phrase
	notes   map[string][]CulturalNote // key = lower-cased context
	hints   map[string]string
	adaptFn string // "You are a culturally aware AI assistant for {DisplayName} speakers. ..."
	// greeting
	morningGreeting string
	defaultGreeting string
}

func (p *staticLanguagePack) Metadata() LanguagePackMetadata { return p.meta }

func (p *staticLanguagePack) GetIdiomaticExpression(phrase string) *string {
	if v, ok := p.idioms[strings.ToLower(phrase)]; ok {
		out := v
		return &out
	}
	return nil
}

func (p *staticLanguagePack) AdaptSystemPrompt(basePrompt string) string {
	return "You are a culturally aware AI assistant for " + p.meta.DisplayName + " speakers. " +
		"Respond in " + p.meta.DisplayName + " (" + p.meta.NativeName + ") unless instructed otherwise. " +
		"Use natural, idiomatic expressions. Respect regional customs. " +
		"\n\n" + basePrompt
}

func (p *staticLanguagePack) GetCulturalNotes(context string) []CulturalNote {
	if n, ok := p.notes[strings.ToLower(context)]; ok {
		return n
	}
	return []CulturalNote{}
}

func (p *staticLanguagePack) GetGreeting(timeOfDay string) string {
	switch strings.ToLower(timeOfDay) {
	case "morning", "am":
		return p.morningGreeting
	default:
		return p.defaultGreeting
	}
}

func (p *staticLanguagePack) GetLocaleHints() map[string]string {
	out := make(map[string]string, len(p.hints))
	for k, v := range p.hints {
		out[k] = v
	}
	return out
}

var _ ILanguagePack = (*staticLanguagePack)(nil)

// greetingNote builds the single "greeting" cultural note shared by the packs.
func greetingNote(guidance string, examples ...string) map[string][]CulturalNote {
	return map[string][]CulturalNote{
		"greeting": {{Context: "greeting", Guidance: guidance, Examples: examples}},
	}
}

// ---------------------------------------------------------------------------
// isiZulu
// ---------------------------------------------------------------------------

// IsiZuluLanguagePack is the isiZulu language pack. Ports isiZuluLanguagePack.
type IsiZuluLanguagePack struct{ staticLanguagePack }

// IsiZuluLanguagePackInstance is the shared singleton, mirroring the C#
// isiZuluLanguagePack.Instance field.
var IsiZuluLanguagePackInstance = &IsiZuluLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "zu", DisplayName: "isiZulu", NativeName: "isiZulu",
		PrimaryRegion: "ZA", SpokenInRegions: []string{"ZA"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Sawubona", "hello (plural)": "Sanibonani", "goodbye": "Sala kahle",
		"goodbye (sleep)": "Lala kahle", "thank you": "Ngiyabonga", "thank you (pl)": "Siyabonga",
		"please": "Ngicela", "yes": "Yebo", "no": "Cha", "how are you": "Unjani",
		"i am fine": "Ngikhona", "sorry": "Uxolo", "family": "umndeni", "love": "uthando",
		"water": "amanzi", "food": "ukudla", "mother": "umama", "father": "ubaba",
		"child": "ingane", "friend": "umngani",
	},
	notes:           greetingNote("Use 'Sawubona' in the morning. Show respect to elders.", "Sawubona", "Lala kahle"),
	hints:           map[string]string{"bcp_tag": "zu", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Sawubona", defaultGreeting: "Lala kahle",
}}

// ---------------------------------------------------------------------------
// Swahili
// ---------------------------------------------------------------------------

// SwahiliLanguagePack is the Swahili language pack. Ports SwahiliLanguagePack.
type SwahiliLanguagePack struct{ staticLanguagePack }

// SwahiliLanguagePackInstance is the shared singleton.
var SwahiliLanguagePackInstance = &SwahiliLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "sw", DisplayName: "Swahili", NativeName: "Kiswahili",
		PrimaryRegion: "KE", SpokenInRegions: []string{"KE", "TZ", "UG"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Habari", "hello (informal)": "Mambo", "good morning": "Habari ya asubuhi",
		"good evening": "Habari ya jioni", "goodbye": "Kwaheri", "goodbye (sleep)": "Usiku mwema",
		"thank you": "Asante", "thank you (very)": "Asante sana", "please": "Tafadhali",
		"yes": "Ndio", "no": "Hapana", "how are you": "Habari yako", "i am fine": "Nzuri",
		"sorry": "Pole", "family": "familia", "love": "upendo", "water": "maji",
		"food": "chakula", "mother": "mama", "father": "baba", "child": "mtoto",
		"friend": "rafiki", "no problem": "Hakuna matata",
	},
	notes:           greetingNote("Use 'Habari' in the morning. Show respect to elders.", "Habari", "Usiku mwema"),
	hints:           map[string]string{"bcp_tag": "sw", "region": "KE", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Habari", defaultGreeting: "Usiku mwema",
}}

// ---------------------------------------------------------------------------
// Amharic
// ---------------------------------------------------------------------------

// AmharicLanguagePack is the Amharic language pack. Ports AmharicLanguagePack.
type AmharicLanguagePack struct{ staticLanguagePack }

// AmharicLanguagePackInstance is the shared singleton.
var AmharicLanguagePackInstance = &AmharicLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "am", DisplayName: "Amharic", NativeName: "አማርኛ",
		PrimaryRegion: "ET", SpokenInRegions: []string{"ET"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "ሰላም", "hello (respectful)": "ጤና ይስጥልኝ", "good morning": "እንደምን አደርክ",
		"good evening": "መልካም ምሽት", "goodbye": "ቻው", "thank you": "አመሰግናለሁ",
		"please": "እባክህ", "yes": "አዎ", "no": "አይ", "sorry": "ይቅርታ",
		"how are you": "እንዴት ነህ", "i am fine": "ደህና ነኝ", "water": "ውሃ", "food": "ምግብ",
		"family": "ቤተሰብ", "friend": "ጓደኛ", "love": "ፍቅር", "mother": "እናት",
		"father": "አባት", "child": "ልጅ",
	},
	notes:           greetingNote("Use 'ጤና ይስጥልኝ' in the morning. Show respect to elders.", "ጤና ይስጥልኝ", "መልካም ምሽት"),
	hints:           map[string]string{"bcp_tag": "am", "region": "ET", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "ጤና ይስጥልኝ", defaultGreeting: "መልካም ምሽት",
}}

// ---------------------------------------------------------------------------
// Hausa
// ---------------------------------------------------------------------------

// HausaLanguagePack is the Hausa language pack. Ports HausaLanguagePack.
type HausaLanguagePack struct{ staticLanguagePack }

// HausaLanguagePackInstance is the shared singleton.
var HausaLanguagePackInstance = &HausaLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "ha", DisplayName: "Hausa", NativeName: "Hausa",
		PrimaryRegion: "NG", SpokenInRegions: []string{"NG", "NE", "GH"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Sannu", "good morning": "Barka da safe", "good afternoon": "Barka da rana",
		"good evening": "Barka da yamma", "goodbye": "Sai anjima", "see you later": "Sai gobe",
		"thank you": "Na gode", "please": "Don Allah", "yes": "Eh", "no": "A'a",
		"sorry": "Yi hakuri", "how are you": "Yaya kake", "i am fine": "Lafiya lau",
		"water": "ruwa", "food": "abinci", "family": "iyali", "friend": "aboki",
		"love": "kauna", "mother": "uwa", "father": "uba", "child": "yaro",
	},
	notes:           greetingNote("Use 'Barka da safe' in the morning. Show respect to elders.", "Barka da safe", "Sai anjima"),
	hints:           map[string]string{"bcp_tag": "ha", "region": "NG", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Barka da safe", defaultGreeting: "Sai anjima",
}}

// ---------------------------------------------------------------------------
// Afrikaans
// ---------------------------------------------------------------------------

// AfrikaansLanguagePack is the Afrikaans language pack. Ports AfrikaansLanguagePack.
type AfrikaansLanguagePack struct{ staticLanguagePack }

// AfrikaansLanguagePackInstance is the shared singleton.
var AfrikaansLanguagePackInstance = &AfrikaansLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "af", DisplayName: "Afrikaans", NativeName: "Afrikaans",
		PrimaryRegion: "ZA", SpokenInRegions: []string{"ZA", "NA"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Hallo", "good morning": "Goeie môre", "good afternoon": "Goeie middag",
		"good evening": "Goeie naand", "goodbye": "Totsiens", "thank you": "Dankie",
		"please": "Asseblief", "yes": "Ja", "no": "Nee", "sorry": "Jammer",
		"how are you": "Hoe gaan dit", "i am fine": "Dit gaan goed", "water": "water",
		"food": "kos", "family": "familie", "friend": "vriend", "love": "liefde",
		"mother": "ma", "father": "pa", "child": "kind",
	},
	notes:           greetingNote("Use 'Goeie môre' in the morning. Show respect to elders.", "Goeie môre", "Totsiens"),
	hints:           map[string]string{"bcp_tag": "af", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Goeie môre", defaultGreeting: "Totsiens",
}}

// ---------------------------------------------------------------------------
// Arabic
// ---------------------------------------------------------------------------

// ArabicLanguagePack is the Arabic language pack. Ports ArabicLanguagePack.
type ArabicLanguagePack struct{ staticLanguagePack }

// ArabicLanguagePackInstance is the shared singleton.
var ArabicLanguagePackInstance = &ArabicLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "ar", DisplayName: "Arabic", NativeName: "العربية",
		PrimaryRegion: "SA", SpokenInRegions: []string{"SA", "EG", "MA", "AE"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "مرحبا", "peace be upon you": "السلام عليكم", "good morning": "صباح الخير",
		"good evening": "مساء الخير", "goodbye": "مع السلامة", "thank you": "شكرا",
		"please": "من فضلك", "yes": "نعم", "no": "لا", "sorry": "آسف",
		"how are you": "كيف حالك", "i am fine": "أنا بخير", "water": "ماء", "food": "طعام",
		"family": "عائلة", "friend": "صديق", "love": "حب", "mother": "أم",
		"father": "أب", "child": "طفل",
	},
	notes:           greetingNote("Use 'صباح الخير' in the morning. Show respect to elders.", "صباح الخير", "مساء الخير"),
	hints:           map[string]string{"bcp_tag": "ar", "region": "SA", "rtl": "true", "date_format": "dd/MM/yyyy"},
	morningGreeting: "صباح الخير", defaultGreeting: "مساء الخير",
}}

// ---------------------------------------------------------------------------
// Portuguese
// ---------------------------------------------------------------------------

// PortugueseLanguagePack is the Portuguese language pack. Ports PortugueseLanguagePack.
type PortugueseLanguagePack struct{ staticLanguagePack }

// PortugueseLanguagePackInstance is the shared singleton.
var PortugueseLanguagePackInstance = &PortugueseLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "pt", DisplayName: "Portuguese", NativeName: "Português",
		PrimaryRegion: "PT", SpokenInRegions: []string{"PT", "BR", "MZ", "AO"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Olá", "good morning": "Bom dia", "good afternoon": "Boa tarde",
		"good evening": "Boa noite", "goodbye": "Adeus", "see you later": "Até logo",
		"thank you": "Obrigado", "thank you (f)": "Obrigada", "please": "Por favor",
		"sorry": "Desculpe", "yes": "Sim", "no": "Não", "how are you": "Como está",
		"i am fine": "Estou bem", "water": "água", "food": "comida", "family": "família",
		"friend": "amigo", "love": "amor", "mother": "mãe", "father": "pai",
		"child": "criança",
	},
	notes:           greetingNote("Use 'Bom dia' in the morning. Show respect to elders.", "Bom dia", "Boa noite"),
	hints:           map[string]string{"bcp_tag": "pt", "region": "PT", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Bom dia", defaultGreeting: "Boa noite",
}}

// ---------------------------------------------------------------------------
// Sesotho
// ---------------------------------------------------------------------------

// SesothoLanguagePack is the Sesotho language pack. Ports SesothoLanguagePack.
type SesothoLanguagePack struct{ staticLanguagePack }

// SesothoLanguagePackInstance is the shared singleton.
var SesothoLanguagePackInstance = &SesothoLanguagePack{staticLanguagePack{
	meta: LanguagePackMetadata{
		BcpTag: "st", DisplayName: "Sesotho", NativeName: "Sesotho",
		PrimaryRegion: "ZA", SpokenInRegions: []string{"ZA", "LS"}, PackVersion: "1.0",
	},
	idioms: map[string]string{
		"hello": "Dumela", "hello (plural)": "Dumelang", "goodbye": "Sala hantle",
		"goodbye (sleep)": "Robala hantle", "thank you": "Kea leboha", "please": "Ka kopo",
		"yes": "E", "no": "Che", "how are you": "O phela joang", "i am fine": "Ke phela hantle",
		"sorry": "Tshwarelo", "family": "lelapa", "love": "lerato", "water": "metsi",
		"food": "dijo", "mother": "'me", "father": "ntate", "child": "ngwana",
		"friend": "motswalle",
	},
	notes:           greetingNote("Use 'Dumela' in the morning. Show respect to elders.", "Dumela", "Robala hantle"),
	hints:           map[string]string{"bcp_tag": "st", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"},
	morningGreeting: "Dumela", defaultGreeting: "Robala hantle",
}}
