// languages/language/packs.ts
//
// The eight concrete language packs, ported 1:1 from
// src/CircleAI.Languages.Language.<Lang>/<Lang>LanguagePack.cs:
//   Afrikaans · Amharic · Arabic · Hausa · Portuguese · Sesotho · Swahili · isiZulu
//
// The contract surface (ILanguagePack, LanguagePackMetadata, CulturalNote,
// DefaultLanguagePackRegistry) lives in ./index.ts and is re-exported alongside
// this module.
//
// Porting notes:
//   • C# `sealed class … Instance = new()` → a class with a `static readonly
//     instance` singleton. A shared `LanguagePackBase` holds the common storage
//     and behaviour; each pack is a subclass that supplies the data.
//   • C# `StringComparer.OrdinalIgnoreCase` idiom / notes dictionaries → the
//     stored maps key on lower-cased phrases and lookups lower-case the query,
//     preserving the case-insensitive behaviour.
//   • `adaptSystemPrompt` / `getGreeting` / `getLocaleHints` reproduce the exact
//     C# strings and the `"morning" | "am"` greeting switch.
//   • C# `new Version(1, 0)` → the "1.0" packVersion string (see index.ts).
//   • Non-Latin literals (Arabic العربية, Amharic አማርኛ / Ge'ez) are copied
//     verbatim; this source is UTF-8 like the C#.

import type { CulturalNote, ILanguagePack, LanguagePackMetadata } from "./index.js";
import { DefaultLanguagePackRegistry } from "./index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Shared base
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Common storage + behaviour for the concrete packs. Not part of the public
 * contract surface — every pack is consumed through {@link ILanguagePack}.
 * Immutable after construction, hence safe to share.
 */
export abstract class LanguagePackBase implements ILanguagePack {
  readonly metadata: LanguagePackMetadata;
  private readonly idioms: ReadonlyMap<string, string>; // keys already lower-cased
  private readonly notes: ReadonlyMap<string, readonly CulturalNote[]>; // keys lower-cased
  private readonly systemPromptTemplate: string;
  private readonly morningGreeting: string;
  private readonly otherGreeting: string;
  private readonly hints: ReadonlyMap<string, string>;

  protected constructor(
    metadata: LanguagePackMetadata,
    idioms: Readonly<Record<string, string>>,
    notes: Readonly<Record<string, readonly CulturalNote[]>>,
    systemPromptTemplate: string,
    morningGreeting: string,
    otherGreeting: string,
    hints: Readonly<Record<string, string>>,
  ) {
    this.metadata = metadata;
    // Normalise keys to lower-case for OrdinalIgnoreCase parity.
    const idiomMap = new Map<string, string>();
    for (const [k, v] of Object.entries(idioms)) idiomMap.set(k.toLowerCase(), v);
    this.idioms = idiomMap;

    const noteMap = new Map<string, readonly CulturalNote[]>();
    for (const [k, v] of Object.entries(notes)) noteMap.set(k.toLowerCase(), v);
    this.notes = noteMap;

    this.systemPromptTemplate = systemPromptTemplate;
    this.morningGreeting = morningGreeting;
    this.otherGreeting = otherGreeting;

    const hintMap = new Map<string, string>();
    for (const [k, v] of Object.entries(hints)) hintMap.set(k, v);
    this.hints = hintMap;
  }

  getIdiomaticExpression(phrase: string): string | null {
    return this.idioms.get(phrase.toLowerCase()) ?? null;
  }

  adaptSystemPrompt(basePrompt: string): string {
    return `${this.systemPromptTemplate}\n\n${basePrompt}`;
  }

  getCulturalNotes(context: string): readonly CulturalNote[] {
    return this.notes.get(context.toLowerCase()) ?? [];
  }

  getGreeting(timeOfDay: string): string {
    switch (timeOfDay.toLowerCase()) {
      case "morning":
      case "am":
        return this.morningGreeting;
      default:
        return this.otherGreeting;
    }
  }

  getLocaleHints(): ReadonlyMap<string, string> {
    return this.hints;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Afrikaans
// ─────────────────────────────────────────────────────────────────────────────

/** Afrikaans language pack for Circle AI. Mirrors C# `AfrikaansLanguagePack`. */
export class AfrikaansLanguagePack extends LanguagePackBase {
  static readonly instance = new AfrikaansLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "af",
        displayName: "Afrikaans",
        nativeName: "Afrikaans",
        primaryRegion: "ZA",
        spokenInRegions: ["ZA", "NA"],
        packVersion: "1.0",
      },
      {
        hello: "Hallo",
        "good morning": "Goeie môre",
        "good afternoon": "Goeie middag",
        "good evening": "Goeie naand",
        goodbye: "Totsiens",
        "thank you": "Dankie",
        please: "Asseblief",
        yes: "Ja",
        no: "Nee",
        sorry: "Jammer",
        "how are you": "Hoe gaan dit",
        "I am fine": "Dit gaan goed",
        water: "water",
        food: "kos",
        family: "familie",
        friend: "vriend",
        love: "liefde",
        mother: "ma",
        father: "pa",
        child: "kind",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Goeie môre' in the morning. Show respect to elders.",
            examples: ["Goeie môre", "Totsiens"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Afrikaans speakers. " +
        "Respond in Afrikaans (Afrikaans) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Goeie môre",
      "Totsiens",
      { bcp_tag: "af", region: "ZA", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Amharic
// ─────────────────────────────────────────────────────────────────────────────

/** Amharic language pack for Circle AI. Mirrors C# `AmharicLanguagePack`. */
export class AmharicLanguagePack extends LanguagePackBase {
  static readonly instance = new AmharicLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "am",
        displayName: "Amharic",
        nativeName: "አማርኛ",
        primaryRegion: "ET",
        spokenInRegions: ["ET"],
        packVersion: "1.0",
      },
      {
        hello: "ሰላም",
        "hello (respectful)": "ጤና ይስጥልኝ",
        "good morning": "እንደምን አደርክ",
        "good evening": "መልካም ምሽት",
        goodbye: "ቻው",
        "thank you": "አመሰግናለሁ",
        please: "እባክህ",
        yes: "አዎ",
        no: "አይ",
        sorry: "ይቅርታ",
        "how are you": "እንዴት ነህ",
        "I am fine": "ደህና ነኝ",
        water: "ውሃ",
        food: "ምግብ",
        family: "ቤተሰብ",
        friend: "ጓደኛ",
        love: "ፍቅር",
        mother: "እናት",
        father: "አባት",
        child: "ልጅ",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'ጤና ይስጥልኝ' in the morning. Show respect to elders.",
            examples: ["ጤና ይስጥልኝ", "መልካም ምሽት"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Amharic speakers. " +
        "Respond in Amharic (አማርኛ) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "ጤና ይስጥልኝ",
      "መልካም ምሽት",
      { bcp_tag: "am", region: "ET", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Arabic
// ─────────────────────────────────────────────────────────────────────────────

/** Arabic language pack for Circle AI. Mirrors C# `ArabicLanguagePack`. */
export class ArabicLanguagePack extends LanguagePackBase {
  static readonly instance = new ArabicLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "ar",
        displayName: "Arabic",
        nativeName: "العربية",
        primaryRegion: "SA",
        spokenInRegions: ["SA", "EG", "MA", "AE"],
        packVersion: "1.0",
      },
      {
        hello: "مرحبا",
        "peace be upon you": "السلام عليكم",
        "good morning": "صباح الخير",
        "good evening": "مساء الخير",
        goodbye: "مع السلامة",
        "thank you": "شكرا",
        please: "من فضلك",
        yes: "نعم",
        no: "لا",
        sorry: "آسف",
        "how are you": "كيف حالك",
        "I am fine": "أنا بخير",
        water: "ماء",
        food: "طعام",
        family: "عائلة",
        friend: "صديق",
        love: "حب",
        mother: "أم",
        father: "أب",
        child: "طفل",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'صباح الخير' in the morning. Show respect to elders.",
            examples: ["صباح الخير", "مساء الخير"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Arabic speakers. " +
        "Respond in Arabic (العربية) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "صباح الخير",
      "مساء الخير",
      { bcp_tag: "ar", region: "SA", rtl: "true", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Hausa
// ─────────────────────────────────────────────────────────────────────────────

/** Hausa language pack for Circle AI. Mirrors C# `HausaLanguagePack`. */
export class HausaLanguagePack extends LanguagePackBase {
  static readonly instance = new HausaLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "ha",
        displayName: "Hausa",
        nativeName: "Hausa",
        primaryRegion: "NG",
        spokenInRegions: ["NG", "NE", "GH"],
        packVersion: "1.0",
      },
      {
        hello: "Sannu",
        "good morning": "Barka da safe",
        "good afternoon": "Barka da rana",
        "good evening": "Barka da yamma",
        goodbye: "Sai anjima",
        "see you later": "Sai gobe",
        "thank you": "Na gode",
        please: "Don Allah",
        yes: "Eh",
        no: "A'a",
        sorry: "Yi hakuri",
        "how are you": "Yaya kake",
        "I am fine": "Lafiya lau",
        water: "ruwa",
        food: "abinci",
        family: "iyali",
        friend: "aboki",
        love: "kauna",
        mother: "uwa",
        father: "uba",
        child: "yaro",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Barka da safe' in the morning. Show respect to elders.",
            examples: ["Barka da safe", "Sai anjima"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Hausa speakers. " +
        "Respond in Hausa (Hausa) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Barka da safe",
      "Sai anjima",
      { bcp_tag: "ha", region: "NG", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Portuguese
// ─────────────────────────────────────────────────────────────────────────────

/** Portuguese language pack for Circle AI. Mirrors C# `PortugueseLanguagePack`. */
export class PortugueseLanguagePack extends LanguagePackBase {
  static readonly instance = new PortugueseLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "pt",
        displayName: "Portuguese",
        nativeName: "Português",
        primaryRegion: "PT",
        spokenInRegions: ["PT", "BR", "MZ", "AO"],
        packVersion: "1.0",
      },
      {
        hello: "Olá",
        "good morning": "Bom dia",
        "good afternoon": "Boa tarde",
        "good evening": "Boa noite",
        goodbye: "Adeus",
        "see you later": "Até logo",
        "thank you": "Obrigado",
        "thank you (f)": "Obrigada",
        please: "Por favor",
        sorry: "Desculpe",
        yes: "Sim",
        no: "Não",
        "how are you": "Como está",
        "I am fine": "Estou bem",
        water: "água",
        food: "comida",
        family: "família",
        friend: "amigo",
        love: "amor",
        mother: "mãe",
        father: "pai",
        child: "criança",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Bom dia' in the morning. Show respect to elders.",
            examples: ["Bom dia", "Boa noite"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Portuguese speakers. " +
        "Respond in Portuguese (Português) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Bom dia",
      "Boa noite",
      { bcp_tag: "pt", region: "PT", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Sesotho
// ─────────────────────────────────────────────────────────────────────────────

/** Sesotho language pack for Circle AI. Mirrors C# `SesothoLanguagePack`. */
export class SesothoLanguagePack extends LanguagePackBase {
  static readonly instance = new SesothoLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "st",
        displayName: "Sesotho",
        nativeName: "Sesotho",
        primaryRegion: "ZA",
        spokenInRegions: ["ZA", "LS"],
        packVersion: "1.0",
      },
      {
        hello: "Dumela",
        "hello (plural)": "Dumelang",
        goodbye: "Sala hantle",
        "goodbye (sleep)": "Robala hantle",
        "thank you": "Kea leboha",
        please: "Ka kopo",
        yes: "E",
        no: "Che",
        "how are you": "O phela joang",
        "I am fine": "Ke phela hantle",
        sorry: "Tshwarelo",
        family: "lelapa",
        love: "lerato",
        water: "metsi",
        food: "dijo",
        mother: "'me",
        father: "ntate",
        child: "ngwana",
        friend: "motswalle",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Dumela' in the morning. Show respect to elders.",
            examples: ["Dumela", "Robala hantle"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Sesotho speakers. " +
        "Respond in Sesotho (Sesotho) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Dumela",
      "Robala hantle",
      { bcp_tag: "st", region: "ZA", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Swahili
// ─────────────────────────────────────────────────────────────────────────────

/** Swahili language pack for Circle AI. Mirrors C# `SwahiliLanguagePack`. */
export class SwahiliLanguagePack extends LanguagePackBase {
  static readonly instance = new SwahiliLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "sw",
        displayName: "Swahili",
        nativeName: "Kiswahili",
        primaryRegion: "KE",
        spokenInRegions: ["KE", "TZ", "UG"],
        packVersion: "1.0",
      },
      {
        hello: "Habari",
        "hello (informal)": "Mambo",
        "good morning": "Habari ya asubuhi",
        "good evening": "Habari ya jioni",
        goodbye: "Kwaheri",
        "goodbye (sleep)": "Usiku mwema",
        "thank you": "Asante",
        "thank you (very)": "Asante sana",
        please: "Tafadhali",
        yes: "Ndio",
        no: "Hapana",
        "how are you": "Habari yako",
        "I am fine": "Nzuri",
        sorry: "Pole",
        family: "familia",
        love: "upendo",
        water: "maji",
        food: "chakula",
        mother: "mama",
        father: "baba",
        child: "mtoto",
        friend: "rafiki",
        "no problem": "Hakuna matata",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Habari' in the morning. Show respect to elders.",
            examples: ["Habari", "Usiku mwema"],
          },
        ],
      },
      "You are a culturally aware AI assistant for Swahili speakers. " +
        "Respond in Swahili (Kiswahili) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Habari",
      "Usiku mwema",
      { bcp_tag: "sw", region: "KE", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// isiZulu
// ─────────────────────────────────────────────────────────────────────────────

/** isiZulu language pack for Circle AI. Mirrors C# `isiZuluLanguagePack`. */
export class IsiZuluLanguagePack extends LanguagePackBase {
  static readonly instance = new IsiZuluLanguagePack();

  constructor() {
    super(
      {
        bcpTag: "zu",
        displayName: "isiZulu",
        nativeName: "isiZulu",
        primaryRegion: "ZA",
        spokenInRegions: ["ZA"],
        packVersion: "1.0",
      },
      {
        hello: "Sawubona",
        "hello (plural)": "Sanibonani",
        goodbye: "Sala kahle",
        "goodbye (sleep)": "Lala kahle",
        "thank you": "Ngiyabonga",
        "thank you (pl)": "Siyabonga",
        please: "Ngicela",
        yes: "Yebo",
        no: "Cha",
        "how are you": "Unjani",
        "I am fine": "Ngikhona",
        sorry: "Uxolo",
        family: "umndeni",
        love: "uthando",
        water: "amanzi",
        food: "ukudla",
        mother: "umama",
        father: "ubaba",
        child: "ingane",
        friend: "umngani",
      },
      {
        greeting: [
          {
            context: "greeting",
            guidance: "Use 'Sawubona' in the morning. Show respect to elders.",
            examples: ["Sawubona", "Lala kahle"],
          },
        ],
      },
      "You are a culturally aware AI assistant for isiZulu speakers. " +
        "Respond in isiZulu (isiZulu) unless instructed otherwise. " +
        "Use natural, idiomatic expressions. Respect regional customs. ",
      "Sawubona",
      "Lala kahle",
      { bcp_tag: "zu", region: "ZA", rtl: "false", date_format: "dd/MM/yyyy" },
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Built-in registry helpers
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The eight built-in CircleAI language packs, in the same order the Swift
 * reference registers them.
 */
export const builtInLanguagePacks: readonly ILanguagePack[] = [
  IsiZuluLanguagePack.instance,
  AfrikaansLanguagePack.instance,
  AmharicLanguagePack.instance,
  ArabicLanguagePack.instance,
  HausaLanguagePack.instance,
  PortugueseLanguagePack.instance,
  SesothoLanguagePack.instance,
  SwahiliLanguagePack.instance,
];

/** Registers all eight built-in packs into an existing registry. */
export function registerBuiltInPacks(registry: DefaultLanguagePackRegistry): void {
  for (const pack of builtInLanguagePacks) registry.register(pack);
}

/**
 * Builds a {@link DefaultLanguagePackRegistry} pre-populated with all eight
 * built-in packs. Convenience for hosts that want the full CircleAI language
 * surface without wiring each pack by hand. Mirrors the Swift
 * `DefaultLanguagePackRegistry.withBuiltInPacks()` factory.
 */
export function withBuiltInPacks(): DefaultLanguagePackRegistry {
  const registry = new DefaultLanguagePackRegistry();
  registerBuiltInPacks(registry);
  return registry;
}
