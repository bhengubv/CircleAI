// LanguagePackData.swift
//
// The eight concrete language packs, ported 1:1 from
// src/CircleAI.Languages.Language.<Lang>/<Lang>LanguagePack.cs:
//   Afrikaans · Amharic · Arabic · Hausa · Portuguese · Sesotho · Swahili · isiZulu
//
// Base types (LanguagePackMetadata, CulturalNote, ILanguagePack) live in
// LanguagePacks.swift.
//
// Porting notes:
//   • C# `sealed class … Instance = new()` → `final class … @unchecked Sendable`
//     with a `static let shared` singleton (`Instance` reads as a Swift keyword
//     collision risk when lowercased; `shared` is the Swift convention).
//   • C# `StringComparer.OrdinalIgnoreCase` idiom / notes dictionaries → the
//     stored dictionaries key on lower-cased phrases and lookups lower-case the
//     query, preserving case-insensitive behaviour.
//   • `AdaptSystemPrompt` / `GetGreeting` / `GetLocaleHints` reproduce the exact
//     C# strings and the `"morning"|"am"` greeting switch.
//   • Non-Latin literals (Arabic العربية, Amharic አማርኛ, Ge'ez) are copied
//     verbatim; the Swift source is UTF-8 like the C#.

import Foundation

// MARK: - Shared base

/// Common storage + behaviour for the concrete packs. Not part of the public
/// contract surface — every pack is exposed through `ILanguagePack`.
/// Immutable after construction, hence safe to share.
public class LanguagePackBase: ILanguagePack, @unchecked Sendable {
    public let metadata: LanguagePackMetadata
    private let idioms: [String: String]          // keys already lower-cased
    private let notes: [String: [CulturalNote]]   // keys already lower-cased
    private let systemPromptTemplate: String      // "%@" placeholder for basePrompt
    private let morningGreeting: String
    private let otherGreeting: String
    private let hints: [String: String]

    init(
        metadata: LanguagePackMetadata,
        idioms: [String: String],
        notes: [String: [CulturalNote]],
        systemPromptTemplate: String,
        morningGreeting: String,
        otherGreeting: String,
        hints: [String: String]
    ) {
        self.metadata = metadata
        // Normalise keys to lower-case for OrdinalIgnoreCase parity.
        self.idioms = Dictionary(idioms.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { a, _ in a })
        self.notes = Dictionary(notes.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { a, _ in a })
        self.systemPromptTemplate = systemPromptTemplate
        self.morningGreeting = morningGreeting
        self.otherGreeting = otherGreeting
        self.hints = hints
    }

    public func idiomaticExpression(_ phrase: String) -> String? {
        idioms[phrase.lowercased()]
    }

    public func adaptSystemPrompt(_ basePrompt: String) -> String {
        systemPromptTemplate + "\n\n" + basePrompt
    }

    public func culturalNotes(_ context: String) -> [CulturalNote] {
        notes[context.lowercased()] ?? []
    }

    public func greeting(timeOfDay: String) -> String {
        switch timeOfDay.lowercased() {
        case "morning", "am": return morningGreeting
        default: return otherGreeting
        }
    }

    public func localeHints() -> [String: String] { hints }
}

// MARK: - isiZulu

public final class IsiZuluLanguagePack: LanguagePackBase {
    public static let shared = IsiZuluLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "zu", displayName: "isiZulu", nativeName: "isiZulu",
                primaryRegion: "ZA", spokenInRegions: ["ZA"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Sawubona",
                "hello (plural)": "Sanibonani",
                "goodbye": "Sala kahle",
                "goodbye (sleep)": "Lala kahle",
                "thank you": "Ngiyabonga",
                "thank you (pl)": "Siyabonga",
                "please": "Ngicela",
                "yes": "Yebo",
                "no": "Cha",
                "how are you": "Unjani",
                "I am fine": "Ngikhona",
                "sorry": "Uxolo",
                "family": "umndeni",
                "love": "uthando",
                "water": "amanzi",
                "food": "ukudla",
                "mother": "umama",
                "father": "ubaba",
                "child": "ingane",
                "friend": "umngani",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Sawubona' in the morning. Show respect to elders.",
                        examples: ["Sawubona", "Lala kahle"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for isiZulu speakers. "
                + "Respond in isiZulu (isiZulu) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Sawubona",
            otherGreeting: "Lala kahle",
            hints: ["bcp_tag": "zu", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Afrikaans

public final class AfrikaansLanguagePack: LanguagePackBase {
    public static let shared = AfrikaansLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "af", displayName: "Afrikaans", nativeName: "Afrikaans",
                primaryRegion: "ZA", spokenInRegions: ["ZA", "NA"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Hallo",
                "good morning": "Goeie môre",
                "good afternoon": "Goeie middag",
                "good evening": "Goeie naand",
                "goodbye": "Totsiens",
                "thank you": "Dankie",
                "please": "Asseblief",
                "yes": "Ja",
                "no": "Nee",
                "sorry": "Jammer",
                "how are you": "Hoe gaan dit",
                "I am fine": "Dit gaan goed",
                "water": "water",
                "food": "kos",
                "family": "familie",
                "friend": "vriend",
                "love": "liefde",
                "mother": "ma",
                "father": "pa",
                "child": "kind",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Goeie môre' in the morning. Show respect to elders.",
                        examples: ["Goeie môre", "Totsiens"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Afrikaans speakers. "
                + "Respond in Afrikaans (Afrikaans) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Goeie môre",
            otherGreeting: "Totsiens",
            hints: ["bcp_tag": "af", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Amharic

public final class AmharicLanguagePack: LanguagePackBase {
    public static let shared = AmharicLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "am", displayName: "Amharic", nativeName: "አማርኛ",
                primaryRegion: "ET", spokenInRegions: ["ET"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "ሰላም",
                "hello (respectful)": "ጤና ይስጥልኝ",
                "good morning": "እንደምን አደርክ",
                "good evening": "መልካም ምሽት",
                "goodbye": "ቻው",
                "thank you": "አመሰግናለሁ",
                "please": "እባክህ",
                "yes": "አዎ",
                "no": "አይ",
                "sorry": "ይቅርታ",
                "how are you": "እንዴት ነህ",
                "I am fine": "ደህና ነኝ",
                "water": "ውሃ",
                "food": "ምግብ",
                "family": "ቤተሰብ",
                "friend": "ጓደኛ",
                "love": "ፍቅር",
                "mother": "እናት",
                "father": "አባት",
                "child": "ልጅ",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'ጤና ይስጥልኝ' in the morning. Show respect to elders.",
                        examples: ["ጤና ይስጥልኝ", "መልካም ምሽት"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Amharic speakers. "
                + "Respond in Amharic (አማርኛ) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "ጤና ይስጥልኝ",
            otherGreeting: "መልካም ምሽት",
            hints: ["bcp_tag": "am", "region": "ET", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Arabic

public final class ArabicLanguagePack: LanguagePackBase {
    public static let shared = ArabicLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "ar", displayName: "Arabic", nativeName: "العربية",
                primaryRegion: "SA", spokenInRegions: ["SA", "EG", "MA", "AE"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "مرحبا",
                "peace be upon you": "السلام عليكم",
                "good morning": "صباح الخير",
                "good evening": "مساء الخير",
                "goodbye": "مع السلامة",
                "thank you": "شكرا",
                "please": "من فضلك",
                "yes": "نعم",
                "no": "لا",
                "sorry": "آسف",
                "how are you": "كيف حالك",
                "I am fine": "أنا بخير",
                "water": "ماء",
                "food": "طعام",
                "family": "عائلة",
                "friend": "صديق",
                "love": "حب",
                "mother": "أم",
                "father": "أب",
                "child": "طفل",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'صباح الخير' in the morning. Show respect to elders.",
                        examples: ["صباح الخير", "مساء الخير"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Arabic speakers. "
                + "Respond in Arabic (العربية) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "صباح الخير",
            otherGreeting: "مساء الخير",
            hints: ["bcp_tag": "ar", "region": "SA", "rtl": "true", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Hausa

public final class HausaLanguagePack: LanguagePackBase {
    public static let shared = HausaLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "ha", displayName: "Hausa", nativeName: "Hausa",
                primaryRegion: "NG", spokenInRegions: ["NG", "NE", "GH"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Sannu",
                "good morning": "Barka da safe",
                "good afternoon": "Barka da rana",
                "good evening": "Barka da yamma",
                "goodbye": "Sai anjima",
                "see you later": "Sai gobe",
                "thank you": "Na gode",
                "please": "Don Allah",
                "yes": "Eh",
                "no": "A'a",
                "sorry": "Yi hakuri",
                "how are you": "Yaya kake",
                "I am fine": "Lafiya lau",
                "water": "ruwa",
                "food": "abinci",
                "family": "iyali",
                "friend": "aboki",
                "love": "kauna",
                "mother": "uwa",
                "father": "uba",
                "child": "yaro",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Barka da safe' in the morning. Show respect to elders.",
                        examples: ["Barka da safe", "Sai anjima"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Hausa speakers. "
                + "Respond in Hausa (Hausa) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Barka da safe",
            otherGreeting: "Sai anjima",
            hints: ["bcp_tag": "ha", "region": "NG", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Portuguese

public final class PortugueseLanguagePack: LanguagePackBase {
    public static let shared = PortugueseLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "pt", displayName: "Portuguese", nativeName: "Português",
                primaryRegion: "PT", spokenInRegions: ["PT", "BR", "MZ", "AO"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Olá",
                "good morning": "Bom dia",
                "good afternoon": "Boa tarde",
                "good evening": "Boa noite",
                "goodbye": "Adeus",
                "see you later": "Até logo",
                "thank you": "Obrigado",
                "thank you (f)": "Obrigada",
                "please": "Por favor",
                "sorry": "Desculpe",
                "yes": "Sim",
                "no": "Não",
                "how are you": "Como está",
                "I am fine": "Estou bem",
                "water": "água",
                "food": "comida",
                "family": "família",
                "friend": "amigo",
                "love": "amor",
                "mother": "mãe",
                "father": "pai",
                "child": "criança",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Bom dia' in the morning. Show respect to elders.",
                        examples: ["Bom dia", "Boa noite"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Portuguese speakers. "
                + "Respond in Portuguese (Português) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Bom dia",
            otherGreeting: "Boa noite",
            hints: ["bcp_tag": "pt", "region": "PT", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Sesotho

public final class SesothoLanguagePack: LanguagePackBase {
    public static let shared = SesothoLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "st", displayName: "Sesotho", nativeName: "Sesotho",
                primaryRegion: "ZA", spokenInRegions: ["ZA", "LS"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Dumela",
                "hello (plural)": "Dumelang",
                "goodbye": "Sala hantle",
                "goodbye (sleep)": "Robala hantle",
                "thank you": "Kea leboha",
                "please": "Ka kopo",
                "yes": "E",
                "no": "Che",
                "how are you": "O phela joang",
                "I am fine": "Ke phela hantle",
                "sorry": "Tshwarelo",
                "family": "lelapa",
                "love": "lerato",
                "water": "metsi",
                "food": "dijo",
                "mother": "'me",
                "father": "ntate",
                "child": "ngwana",
                "friend": "motswalle",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Dumela' in the morning. Show respect to elders.",
                        examples: ["Dumela", "Robala hantle"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Sesotho speakers. "
                + "Respond in Sesotho (Sesotho) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Dumela",
            otherGreeting: "Robala hantle",
            hints: ["bcp_tag": "st", "region": "ZA", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Swahili

public final class SwahiliLanguagePack: LanguagePackBase {
    public static let shared = SwahiliLanguagePack()

    public init() {
        super.init(
            metadata: LanguagePackMetadata(
                bcpTag: "sw", displayName: "Swahili", nativeName: "Kiswahili",
                primaryRegion: "KE", spokenInRegions: ["KE", "TZ", "UG"], packVersion: PackVersion(1, 0)),
            idioms: [
                "hello": "Habari",
                "hello (informal)": "Mambo",
                "good morning": "Habari ya asubuhi",
                "good evening": "Habari ya jioni",
                "goodbye": "Kwaheri",
                "goodbye (sleep)": "Usiku mwema",
                "thank you": "Asante",
                "thank you (very)": "Asante sana",
                "please": "Tafadhali",
                "yes": "Ndio",
                "no": "Hapana",
                "how are you": "Habari yako",
                "I am fine": "Nzuri",
                "sorry": "Pole",
                "family": "familia",
                "love": "upendo",
                "water": "maji",
                "food": "chakula",
                "mother": "mama",
                "father": "baba",
                "child": "mtoto",
                "friend": "rafiki",
                "no problem": "Hakuna matata",
            ],
            notes: [
                "greeting": [
                    CulturalNote(
                        context: "greeting",
                        guidance: "Use 'Habari' in the morning. Show respect to elders.",
                        examples: ["Habari", "Usiku mwema"]),
                ],
            ],
            systemPromptTemplate:
                "You are a culturally aware AI assistant for Swahili speakers. "
                + "Respond in Swahili (Kiswahili) unless instructed otherwise. "
                + "Use natural, idiomatic expressions. Respect regional customs. ",
            morningGreeting: "Habari",
            otherGreeting: "Usiku mwema",
            hints: ["bcp_tag": "sw", "region": "KE", "rtl": "false", "date_format": "dd/MM/yyyy"])
    }
}

// MARK: - Built-in registry helper

extension DefaultLanguagePackRegistry {
    /// Registers all eight built-in packs. Convenience for hosts that want the
    /// full CircleAI language surface without wiring each pack by hand.
    public static func withBuiltInPacks() -> DefaultLanguagePackRegistry {
        let registry = DefaultLanguagePackRegistry()
        let packs: [ILanguagePack] = [
            IsiZuluLanguagePack.shared,
            AfrikaansLanguagePack.shared,
            AmharicLanguagePack.shared,
            ArabicLanguagePack.shared,
            HausaLanguagePack.shared,
            PortugueseLanguagePack.shared,
            SesothoLanguagePack.shared,
            SwahiliLanguagePack.shared,
        ]
        for pack in packs { registry.register(pack) }
        return registry
    }
}
