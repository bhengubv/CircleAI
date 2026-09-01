//! Languages, translation, the outside-world connectors, telephony carriers,
//! search primitives, media, decks, web and native runtimes.
//!
//! THE LANGUAGE PACKS ARE THE PART THAT MATTERS. Eight of them, each carrying
//! what a speaker of that language actually needs from a device: the script it
//! is written in, the direction it runs, how a number is said aloud, and the
//! handful of cultural notes that make an assistant sound like it has met
//! somebody rather than been translated at them.
//!
//! EVERY CONNECTOR HERE IS A SEAM. Reaching a mailbox or a calendar means an
//! HTTP client, an OAuth flow and a platform credential store, none of which
//! belong in a core that has to compile for a small chip. What is written here
//! is the shape, the refusal, and the things that are easy to get wrong -
//! wired by the head that has the network.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Languages

/// Which way text runs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TextDirection {
    #[default]
    LeftToRight,
    /// Arabic and Hebrew. NOT just a CSS property: a right-to-left string mixed
    /// with a left-to-right one needs isolation marks or the punctuation ends up
    /// on the wrong end of the sentence.
    RightToLeft,
}

/// Something worth knowing about speaking to people in this language.
///
/// NOT TRIVIA. Each note changes what an assistant should say - a greeting that
/// is wrong for the time of day, or a form of address that is too familiar for a
/// first exchange, lands as rudeness rather than as a translation error.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CulturalNote {
    pub topic: String,
    pub note: String,
    /// What to do differently. A note with no consequence is trivia.
    pub applies_to: String,
}

/// What a language pack says about itself.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct LanguagePackMetadata {
    /// BCP 47, so `zu-ZA` rather than `zulu`. A tag a platform will accept
    /// without a lookup table.
    pub tag: String,
    /// What speakers call it, in it. `isiZulu`, not `Zulu` - a language named in
    /// somebody else's language on their own device is a small daily insult.
    pub endonym: String,
    pub english_name: String,
    /// ISO 15924, so `Latn`, `Ethi`, `Arab`.
    pub script: String,
    pub direction: TextDirection,
    /// Roughly how many people speak it. Held to order a picker by usefulness
    /// rather than alphabetically, which buries the widely spoken ones.
    pub speakers_millions: u32,
}

/// Everything the device needs to speak one language.
pub trait LanguagePack {
    fn metadata(&self) -> LanguagePackMetadata;
    /// A greeting for the hour. Many of these languages greet by time of day,
    /// and using the wrong one is immediately noticeable.
    fn greeting(&self, hour_of_day: u8) -> String;
    /// How a number is said. Not formatted - SAID, which is a different thing:
    /// "1500" reads as "one thousand five hundred" and a voice that says
    /// "one five zero zero" has not spoken the language.
    fn say_number(&self, value: i64) -> String;
    fn cultural_notes(&self) -> Vec<CulturalNote>;
    /// Yes and no, since a voice interface asks constantly.
    fn yes_no(&self) -> (String, String);
}

/// Writes a language pack from a small table of its particulars.
// `language_pack` was written once as a macro over the table below and
// expanded here, so each type appears under its own name.


#[doc = concat!("The ", "Afrikaans", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct AfrikaansLanguagePack;

        impl AfrikaansLanguagePack {
            pub const TAG: &'static str = "af-ZA";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["nul", "een", "twee", "drie", "vier", "vyf", "ses", "sewe", "ag", "nege"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["tien", "twintig", "dertig", "veertig", "vyftig", "sestig", "sewentig",
     "tagtig", "negentig"];
        }

        impl LanguagePack for AfrikaansLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "af-ZA".into(),
                    endonym: "Afrikaans".into(),
                    english_name: "Afrikaans".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 7,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Goeiemôre".into(),
                    12..=16 => "Goeiemiddag".into(),
                    _ => "Goeienaand".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "honderd");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "duisend");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("address", "u for a stranger or an elder, jy for a friend",
      "the pronoun in every generated sentence"),
     ("numbers", "units are said before tens - 'een-en-twintig' for 21",
      "spoken number formatting")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("Ja".into(), "Nee".into())
            }
        }


#[doc = concat!("The ", "Zulu", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct isiZuluLanguagePack;

        impl isiZuluLanguagePack {
            pub const TAG: &'static str = "zu-ZA";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["iqanda", "kunye", "kubili", "kuthathu", "kune", "kuhlanu", "isithupha",
     "isikhombisa", "isishiyagalombili", "isishiyagalolunye"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["ishumi", "amashumi amabili", "amashumi amathathu", "amashumi amane",
     "amashumi amahlanu", "amashumi ayisithupha", "amashumi ayisikhombisa",
     "amashumi ayisishiyagalombili", "amashumi ayisishiyagalolunye"];
        }

        impl LanguagePack for isiZuluLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "zu-ZA".into(),
                    endonym: "isiZulu".into(),
                    english_name: "Zulu".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 12,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Sawubona".into(),
                    12..=16 => "Sawubona".into(),
                    _ => "Sawubona".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "ikhulu");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "inkulungwane");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("greeting", "Sawubona to one person, Sanibonani to several - it is a verb, not a word",
      "any greeting where the number of listeners is known"),
     ("respect", "an elder is addressed as Baba or Mama, never by first name",
      "how a person is named in a reply")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("Yebo".into(), "Cha".into())
            }
        }


#[doc = concat!("The ", "Sesotho", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct SesothoLanguagePack;

        impl SesothoLanguagePack {
            pub const TAG: &'static str = "st-ZA";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["lefeela", "nngwe", "pedi", "tharo", "nne", "hlano", "tshelela",
     "supa", "robedi", "robong"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["leshome", "mashome a mabedi", "mashome a mararo", "mashome a mane",
     "mashome a mahlano", "mashome a tshelelang", "mashome a supileng",
     "mashome a robedi", "mashome a robong"];
        }

        impl LanguagePack for SesothoLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "st-ZA".into(),
                    endonym: "Sesotho".into(),
                    english_name: "Sesotho".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 6,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Dumela".into(),
                    12..=16 => "Dumela".into(),
                    _ => "Dumela".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "lekgolo");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "sekete");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("greeting", "Dumela to one, Dumelang to several",
      "any greeting where the number of listeners is known"),
     ("courtesy", "a greeting is followed by asking after the person before business",
      "the opening of a first exchange")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("E".into(), "Tjhe".into())
            }
        }


#[doc = concat!("The ", "Swahili", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct SwahiliLanguagePack;

        impl SwahiliLanguagePack {
            pub const TAG: &'static str = "sw-KE";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["sifuri", "moja", "mbili", "tatu", "nne", "tano", "sita", "saba", "nane",
     "tisa"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["kumi", "ishirini", "thelathini", "arobaini", "hamsini", "sitini",
     "sabini", "themanini", "tisini"];
        }

        impl LanguagePack for SwahiliLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "sw-KE".into(),
                    endonym: "Kiswahili".into(),
                    english_name: "Swahili".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 80,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Habari za asubuhi".into(),
                    12..=16 => "Habari za mchana".into(),
                    _ => "Habari za jioni".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "mia");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "elfu");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("time", "the day is counted from sunrise - saa moja is seven in the morning",
      "any spoken time, which is six hours off a clock reading"),
     ("greeting", "greetings are exchanged at length before anything else",
      "the opening of a first exchange")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("Ndiyo".into(), "Hapana".into())
            }
        }


#[doc = concat!("The ", "Hausa", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct HausaLanguagePack;

        impl HausaLanguagePack {
            pub const TAG: &'static str = "ha-NG";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["sifili", "daya", "biyu", "uku", "hudu", "biyar", "shida", "bakwai",
     "takwas", "tara"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["goma", "ashirin", "talatin", "arba'in", "hamsin", "sittin", "saba'in",
     "tamanin", "casa'in"];
        }

        impl LanguagePack for HausaLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "ha-NG".into(),
                    endonym: "Hausa".into(),
                    english_name: "Hausa".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 80,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Ina kwana".into(),
                    12..=16 => "Ina wuni".into(),
                    _ => "Ina yamma".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "dari");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "dubu");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("greeting", "the greeting asks how the night or day passed and expects an answer",
      "the opening of a first exchange"),
     ("script", "also written in Ajami, an Arabic script, which runs right to left",
      "any assumption that the language is Latin-script only")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("Eh".into(), "A'a".into())
            }
        }


#[doc = concat!("The ", "Amharic", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct AmharicLanguagePack;

        impl AmharicLanguagePack {
            pub const TAG: &'static str = "am-ET";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["ዜሮ", "አንድ", "ሁለት", "ሶስት", "አራት", "አምስት", "ስድስት", "ሰባት", "ስምንት", "ዘጠኝ"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["አስር", "ሃያ", "ሰላሳ", "አርባ", "ሃምሳ", "ስድሳ", "ሰባ", "ሰማንያ", "ዘጠና"];
        }

        impl LanguagePack for AmharicLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "am-ET".into(),
                    endonym: "አማርኛ".into(),
                    english_name: "Amharic".into(),
                    script: "Ethi".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 35,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "እንደምን አደሩ".into(),
                    12..=16 => "እንደምን ዋሉ".into(),
                    _ => "እንደምን አመሹ".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "መቶ");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "ሺህ");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("script", "Ge'ez is a syllabary - each character is a consonant and a vowel, so it cannot be transliterated letter by letter",
      "any romanisation, which needs the syllable table not a character map"),
     ("calendar", "the Ethiopian calendar runs seven or eight years behind and has thirteen months",
      "any date shown or spoken")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("አዎ".into(), "አይደለም".into())
            }
        }


#[doc = concat!("The ", "Arabic", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct ArabicLanguagePack;

        impl ArabicLanguagePack {
            pub const TAG: &'static str = "ar-EG";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["صفر", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية",
     "تسعة"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["عشرة", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون",
     "تسعون"];
        }

        impl LanguagePack for ArabicLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "ar-EG".into(),
                    endonym: "العربية".into(),
                    english_name: "Arabic".into(),
                    script: "Arab".into(),
                    direction: TextDirection::RightToLeft,
                    speakers_millions: 310,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "صباح الخير".into(),
                    12..=16 => "مساء الخير".into(),
                    _ => "مساء الخير".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "مائة");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "ألف");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("direction", "text runs right to left, but numbers within it run left to right",
      "any mixed string, which needs isolation marks or the punctuation lands wrongly"),
     ("register", "written Arabic and spoken Arabic differ by dialect; a reply in formal Arabic to a colloquial question reads as stiff",
      "the register of any generated sentence")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("نعم".into(), "لا".into())
            }
        }


#[doc = concat!("The ", "Portuguese", " pack.")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct PortugueseLanguagePack;

        impl PortugueseLanguagePack {
            pub const TAG: &'static str = "pt-BR";

            /// One through nine. Index 0 is zero.
            pub const UNITS: &'static [&'static str] = &["zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito",
     "nove"];
            /// Ten, twenty, ... ninety.
            pub const TENS: &'static [&'static str] = &["dez", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta",
     "oitenta", "noventa"];
        }

        impl LanguagePack for PortugueseLanguagePack {
            fn metadata(&self) -> LanguagePackMetadata {
                LanguagePackMetadata {
                    tag: "pt-BR".into(),
                    endonym: "Português".into(),
                    english_name: "Portuguese".into(),
                    script: "Latn".into(),
                    direction: TextDirection::LeftToRight,
                    speakers_millions: 260,
                }
            }

            /// Boundaries at 12 and 17, which is where these languages put them.
            /// A greeting is not a clock reading, and "good morning" at half past
            /// twelve is wrong in every one of them.
            fn greeting(&self, hour_of_day: u8) -> String {
                match hour_of_day {
                    0..=11 => "Bom dia".into(),
                    12..=16 => "Boa tarde".into(),
                    _ => "Boa noite".into(),
                }
            }

            fn say_number(&self, value: i64) -> String {
                if value < 0 {
                    return format!("-{}", self.say_number(-value));
                }
                match value {
                    0..=9 => Self::UNITS[value as usize].to_string(),
                    10..=99 => {
                        let (tens, units) = (value / 10, value % 10);
                        let head = Self::TENS[(tens - 1) as usize].to_string();
                        if units == 0 {
                            head
                        } else {
                            format!("{head} {}", Self::UNITS[units as usize])
                        }
                    }
                    100..=999 => {
                        let head =
                            format!("{} {}", Self::UNITS[(value / 100) as usize], "cem");
                        let rest = value % 100;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                    _ => {
                        // Above a thousand it composes rather than reaching for a
                        // bigger table: a table has to stop somewhere and a
                        // number does not.
                        let head =
                            format!("{} {}", self.say_number(value / 1000), "mil");
                        let rest = value % 1000;
                        if rest == 0 {
                            head
                        } else {
                            format!("{head} {}", self.say_number(rest))
                        }
                    }
                }
            }

            fn cultural_notes(&self) -> Vec<CulturalNote> {
                [("variety", "Brazilian and European Portuguese differ in vocabulary and in the second person; picking the wrong one is immediately audible",
      "the tag, which must carry the region not only the language"),
     ("numbers", "the decimal separator is a comma and the thousands separator a full stop",
      "any number formatted for display")]
                    .iter()
                    .map(|(topic, note, applies): &(&str, &str, &str)| CulturalNote {
                        topic: topic.to_string(),
                        note: note.to_string(),
                        applies_to: applies.to_string(),
                    })
                    .collect()
            }

            fn yes_no(&self) -> (String, String) {
                ("Sim".into(), "Não".into())
            }
        }


/// Finds a language pack.
pub trait LanguagePackRegistry {
    fn get(&self, tag: &str) -> Option<Box<dyn LanguagePack + Send + Sync>>;
    fn tags(&self) -> Vec<String>;
    fn metadata(&self) -> Vec<LanguagePackMetadata>;
}

/// The packs that ship.
#[derive(Debug, Default, Clone, Copy)]
pub struct DefaultLanguagePackRegistry;

impl DefaultLanguagePackRegistry {
    pub const TAGS: &'static [&'static str] = &[
        "af-ZA", "am-ET", "ar-EG", "ha-NG", "pt-BR", "st-ZA", "sw-KE", "zu-ZA",
    ];

    /// Matches on the LANGUAGE subtag when the full tag is unknown.
    ///
    /// `zu-ZW` should find the isiZulu pack rather than nothing - a speaker in a
    /// different country speaks the same language, and refusing over a region
    /// code leaves them with English.
    pub fn resolve_tag(tag: &str) -> Option<&'static str> {
        let wanted = tag.replace('_', "-").to_lowercase();
        if let Some(exact) = Self::TAGS.iter().find(|t| t.to_lowercase() == wanted) {
            return Some(exact);
        }
        let language = wanted.split('-').next()?;
        Self::TAGS
            .iter()
            .find(|t| t.split('-').next().unwrap_or("").eq_ignore_ascii_case(language))
            .copied()
    }
}

impl LanguagePackRegistry for DefaultLanguagePackRegistry {
    fn get(&self, tag: &str) -> Option<Box<dyn LanguagePack + Send + Sync>> {
        match Self::resolve_tag(tag)? {
            "af-ZA" => Some(Box::new(AfrikaansLanguagePack)),
            "am-ET" => Some(Box::new(AmharicLanguagePack)),
            "ar-EG" => Some(Box::new(ArabicLanguagePack)),
            "ha-NG" => Some(Box::new(HausaLanguagePack)),
            "pt-BR" => Some(Box::new(PortugueseLanguagePack)),
            "st-ZA" => Some(Box::new(SesothoLanguagePack)),
            "sw-KE" => Some(Box::new(SwahiliLanguagePack)),
            "zu-ZA" => Some(Box::new(isiZuluLanguagePack)),
            _ => None,
        }
    }

    fn tags(&self) -> Vec<String> {
        Self::TAGS.iter().map(|t| t.to_string()).collect()
    }

    /// Ordered by speakers, most first. Alphabetical order buries the widely
    /// spoken languages under the ones that sort early.
    fn metadata(&self) -> Vec<LanguagePackMetadata> {
        let mut out: Vec<LanguagePackMetadata> = Self::TAGS
            .iter()
            .filter_map(|t| self.get(t).map(|p| p.metadata()))
            .collect();
        out.sort_by(|a, b| b.speakers_millions.cmp(&a.speakers_millions));
        out
    }
}

/// Combines what the device, the app and the person each said about language.
///
/// THE PERSON WINS. A device set to English and somebody typing isiZulu means
/// they want isiZulu, and a merge that lets the system setting override that is
/// a merge that ignores them.
#[derive(Debug, Default, Clone)]
pub struct LocaleHintMerge {
    pub device: String,
    pub app: String,
    pub explicit: String,
    pub detected: String,
}

impl LocaleHintMerge {
    /// In order: what they asked for, what was detected in their words, the
    /// app's setting, the device's.
    pub fn resolve(&self) -> String {
        for candidate in [&self.explicit, &self.detected, &self.app, &self.device] {
            if !candidate.trim().is_empty() {
                return candidate.trim().to_string();
            }
        }
        String::new()
    }

    /// Why that one. Shown when somebody asks why the assistant answered in a
    /// language they did not expect.
    pub fn explain(&self) -> &'static str {
        if !self.explicit.trim().is_empty() {
            "you asked for it"
        } else if !self.detected.trim().is_empty() {
            "it is what you wrote in"
        } else if !self.app.trim().is_empty() {
            "the app is set to it"
        } else if !self.device.trim().is_empty() {
            "the device is set to it"
        } else {
            "nothing said which language to use"
        }
    }
}

/// Normalises a script so two spellings of a word compare equal.
pub trait ScriptNormaliser {
    fn script(&self) -> &'static str;
    fn normalise(&self, text: &str) -> String;
}

/// Guesses a language.
pub trait LanguageDetector {
    fn is_available(&self) -> bool;
    /// `(tag, confidence)`. `None` rather than a guess: answering in the wrong
    /// language is worse than asking which one.
    fn detect(&self, text: &str) -> Option<(String, f32)>;
}

/// Guesses nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullLanguageDetector;

impl LanguageDetector for NullLanguageDetector {
    fn is_available(&self) -> bool {
        false
    }
    fn detect(&self, _text: &str) -> Option<(String, f32)> {
        None
    }
}

/// What languages this device knows about at all.
#[derive(Debug, Default)]
pub struct DefaultLanguageRegistry {
    packs: DefaultLanguagePackRegistry,
    extra: Vec<LanguagePackMetadata>,
}

impl DefaultLanguageRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds a language the build did not ship a pack for.
    ///
    /// Metadata WITHOUT a pack is honest: the device can name the language and
    /// tag text with it, and cannot greet in it. Pretending otherwise produces
    /// an English greeting labelled as Yoruba.
    pub fn add_metadata(&mut self, metadata: LanguagePackMetadata) {
        if !metadata.tag.is_empty() && !self.extra.iter().any(|m| m.tag == metadata.tag) {
            self.extra.push(metadata);
        }
    }

    pub fn has_pack(&self, tag: &str) -> bool {
        DefaultLanguagePackRegistry::resolve_tag(tag).is_some()
    }

    pub fn all(&self) -> Vec<LanguagePackMetadata> {
        let mut out = self.packs.metadata();
        out.extend(self.extra.clone());
        out
    }

    pub fn right_to_left_tags(&self) -> Vec<String> {
        self.all()
            .into_iter()
            .filter(|m| m.direction == TextDirection::RightToLeft)
            .map(|m| m.tag)
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Translation

/// How a translation is being used.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TranslationMode {
    /// One block, translated once.
    #[default]
    Document,
    /// A conversation, where each turn depends on the ones before. THE CONTEXT
    /// IS THE POINT - a pronoun in turn four refers to a noun in turn one, and a
    /// per-turn translation gets it wrong every time.
    Conversation,
    /// As somebody speaks. Latency beats polish; a perfect translation arriving
    /// after the moment has passed is not a translation of a conversation.
    Live,
}

/// One turn in a translated conversation.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ConversationTurn {
    pub speaker: String,
    pub text: String,
    pub language: String,
    pub at_ms: u64,
}

/// A request to translate.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TranslationRequest {
    pub text: String,
    /// Empty means detect. Detection can be wrong, which is why the result says
    /// what it decided.
    pub from_language: String,
    pub to_language: String,
    pub mode: TranslationMode,
    /// Earlier turns, for conversation mode.
    pub history: Vec<ConversationTurn>,
}

/// What came back.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranslationResult {
    pub text: String,
    /// What the source was taken to be. ALWAYS reported, because a translation
    /// from a wrongly-detected language is confidently wrong.
    pub detected_from: String,
    pub to_language: String,
    pub confidence: Option<f32>,
    /// Words it did not translate, and why. Names and places stay as they are,
    /// and saying so stops somebody thinking the translation broke.
    pub left_untranslated: Vec<String>,
    pub error: String,
}

impl TranslationResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.text.is_empty()
    }
}

/// Translates.
pub trait TranslationEngine {
    fn is_available(&self) -> bool;
    fn supported_pairs(&self) -> Vec<(String, String)>;
    fn translate(&self, request: &TranslationRequest) -> TranslationResult;
}

/// Translation through a language model.
///
/// A MODEL, NOT A TRANSLATION SERVICE. That is a real difference: a model can be
/// told the register and the context and will keep a conversation coherent, and
/// it will also invent a plausible sentence when it does not know - which is
/// why the confidence and the detected language are always reported back.
pub struct LlmTranslationEngine {
    #[allow(clippy::type_complexity)]
    complete: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    /// How many earlier turns to carry. Bounded, because a long conversation
    /// otherwise grows the prompt until it will not fit.
    pub history_turns: usize,
}

impl LlmTranslationEngine {
    #[allow(clippy::type_complexity)]
    pub fn new(
        complete: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        history_turns: usize,
    ) -> Self {
        Self {
            complete,
            history_turns: if history_turns == 0 { 6 } else { history_turns },
        }
    }

    /// The instruction sent to the model.
    ///
    /// It says explicitly to leave names alone and to say nothing else. Without
    /// that, a model returns "Here is the translation:" and the caller shows it.
    pub fn prompt(&self, request: &TranslationRequest) -> String {
        let mut prompt = format!(
            "Translate into {}. Keep names, places and numbers as they are. \
Return only the translation and nothing else.",
            request.to_language
        );
        if !request.from_language.is_empty() {
            prompt.push_str(&format!(" The source is {}.", request.from_language));
        }
        if request.mode == TranslationMode::Conversation && !request.history.is_empty() {
            prompt.push_str("\n\nEarlier in this conversation:\n");
            let start = request.history.len().saturating_sub(self.history_turns);
            for turn in &request.history[start..] {
                prompt.push_str(&format!("{}: {}\n", turn.speaker, turn.text));
            }
        }
        prompt.push_str(&format!("\n\n{}", request.text));
        prompt
    }
}

impl TranslationEngine for LlmTranslationEngine {
    fn is_available(&self) -> bool {
        self.complete.is_some()
    }

    /// EMPTY, and that is honest: a model will attempt any pair, so claiming a
    /// list would be claiming a competence nobody measured.
    fn supported_pairs(&self) -> Vec<(String, String)> {
        Vec::new()
    }

    fn translate(&self, request: &TranslationRequest) -> TranslationResult {
        let base = TranslationResult {
            detected_from: request.from_language.clone(),
            to_language: request.to_language.clone(),
            ..Default::default()
        };
        if request.to_language.trim().is_empty() {
            return TranslationResult {
                error: "no target language was given".into(),
                ..base
            };
        }
        let Some(complete) = &self.complete else {
            return TranslationResult {
                error: "no model on this device can translate".into(),
                ..base
            };
        };
        match complete(&self.prompt(request)) {
            Ok(text) => TranslationResult { text: text.trim().to_string(), ..base },
            Err(error) => TranslationResult { error, ..base },
        }
    }
}

/// Translates a conversation as it happens.
pub trait LiveTranslator {
    fn is_available(&self) -> bool;
    /// A partial translation of a partial utterance. Marked as such, because a
    /// consumer that treats it as settled shows a sentence that then changes.
    fn interim(&mut self, partial: &str, to_language: &str) -> Option<String>;
    fn settle(&mut self, final_text: &str, to_language: &str) -> TranslationResult;
    fn history(&self) -> Vec<ConversationTurn>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Connectors to the outside world

/// What a connector needs before it will work.
#[derive(Clone, Default)]
pub struct ConnectorCredentials {
    pub token: String,
    pub refresh_token: String,
    pub account: String,
    pub expires_at_ms: u64,
}

impl ConnectorCredentials {
    pub fn is_set(&self) -> bool {
        !self.token.is_empty()
    }

    /// Expired a minute EARLY on purpose. A token that expires mid-request fails
    /// in a way that looks like a permission error.
    pub fn is_live(&self, now_ms: u64) -> bool {
        self.is_set() && (self.expires_at_ms == 0 || now_ms + 60_000 < self.expires_at_ms)
    }
}

/// Never prints a token.
impl std::fmt::Debug for ConnectorCredentials {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ConnectorCredentials")
            .field("token", &if self.is_set() { "<set>" } else { "<unset>" })
            .field("account", &self.account)
            .finish()
    }
}

/// Writes one connector's options and its seam.
// `connector` was written once as a macro over the table below and
// expanded here, so each type appears under its own name.


#[doc = "Gmail. Its read scope covers the whole mailbox - there is no narrower one \
     that also lets a message be found, which is why the write half is separate \
     and off."]
        #[derive(Clone, Default)]
        pub struct GmailOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl GmailOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://gmail.googleapis.com/gmail/v1";
            pub const KIND: &'static str = "Gmail";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "message ids are per-mailbox and are not stable across accounts";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for GmailOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(GmailOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "Gmail", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct GmailEmailConnector {
            options: GmailOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl GmailEmailConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: GmailOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &GmailOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        GmailOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        GmailOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        GmailOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        GmailOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        GmailOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "IMAP. The one connector here that works against a server somebody runs \
     themselves, which is why it is worth carrying alongside two vendor APIs."]
        #[derive(Clone, Default)]
        pub struct ImapOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl ImapOptions {
            pub const DEFAULT_BASE_URL: &'static str = "imaps://";
            pub const KIND: &'static str = "an IMAP mailbox";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "UIDs are per-folder and reset when UIDVALIDITY changes - caching by UID \
     alone silently mixes up messages";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for ImapOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(ImapOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "an IMAP mailbox", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct ImapEmailConnector {
            options: ImapOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl ImapEmailConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: ImapOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &ImapOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        ImapOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        ImapOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        ImapOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        ImapOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        ImapOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "Microsoft Graph mail."]
        #[derive(Clone, Default)]
        pub struct MsGraphEmailOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl MsGraphEmailOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://graph.microsoft.com/v1.0";
            pub const KIND: &'static str = "Microsoft mail";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "delta tokens expire, and a request with a stale one must restart the sync \
     rather than treating the failure as an error";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for MsGraphEmailOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(MsGraphEmailOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "Microsoft mail", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct MsGraphEmailConnector {
            options: MsGraphEmailOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl MsGraphEmailConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: MsGraphEmailOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &MsGraphEmailOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        MsGraphEmailOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        MsGraphEmailOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        MsGraphEmailOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        MsGraphEmailOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        MsGraphEmailOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "Google Calendar."]
        #[derive(Clone, Default)]
        pub struct GoogleCalendarOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl GoogleCalendarOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://www.googleapis.com/calendar/v3";
            pub const KIND: &'static str = "Google Calendar";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "recurring events return as a master plus exceptions; expanding them \
     client-side without honouring the exceptions shows meetings that were \
     cancelled";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for GoogleCalendarOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(GoogleCalendarOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "Google Calendar", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct GoogleCalendarConnector {
            options: GoogleCalendarOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl GoogleCalendarConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: GoogleCalendarOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &GoogleCalendarOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        GoogleCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        GoogleCalendarOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        GoogleCalendarOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        GoogleCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        GoogleCalendarOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "CalDAV. The open one, and the one that works against a server somebody \
     runs themselves."]
        #[derive(Clone, Default)]
        pub struct CalDavCalendarOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl CalDavCalendarOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://";
            pub const KIND: &'static str = "a CalDAV calendar";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "times come as floating, UTC or zoned, and treating a floating time as UTC \
     moves every all-day event by the offset";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for CalDavCalendarOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(CalDavCalendarOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "a CalDAV calendar", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct CalDavCalendarConnector {
            options: CalDavCalendarOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl CalDavCalendarConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: CalDavCalendarOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &CalDavCalendarOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        CalDavCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        CalDavCalendarOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        CalDavCalendarOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        CalDavCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        CalDavCalendarOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "Microsoft Graph calendar."]
        #[derive(Clone, Default)]
        pub struct MsGraphCalendarOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl MsGraphCalendarOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://graph.microsoft.com/v1.0";
            pub const KIND: &'static str = "Microsoft calendar";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "the time zone is a header, not a field - omitting it returns everything in \
     UTC and the day boundaries land wrongly";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for MsGraphCalendarOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(MsGraphCalendarOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "Microsoft calendar", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct MsGraphCalendarConnector {
            options: MsGraphCalendarOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl MsGraphCalendarConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: MsGraphCalendarOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &MsGraphCalendarOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        MsGraphCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        MsGraphCalendarOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        MsGraphCalendarOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        MsGraphCalendarOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        MsGraphCalendarOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "Home Assistant. Runs on the person's own hardware, on their own network, \
     which is why it is the one home connector here."]
        #[derive(Clone, Default)]
        pub struct HomeAssistantOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl HomeAssistantOptions {
            pub const DEFAULT_BASE_URL: &'static str = "http://homeassistant.local:8123/api";
            pub const KIND: &'static str = "Home Assistant";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "an entity id is not a name - it changes when a device is renamed, so \
     anything cached against it goes stale silently";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for HomeAssistantOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(HomeAssistantOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "Home Assistant", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct HomeAssistantConnector {
            options: HomeAssistantOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl HomeAssistantConnector {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: HomeAssistantOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &HomeAssistantOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        HomeAssistantOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        HomeAssistantOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        HomeAssistantOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        HomeAssistantOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        HomeAssistantOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }

#[doc = "OSRM. Open data and self-hostable, which is why it is here rather than a \
     mapping service whose terms forbid storing what it returns."]
        #[derive(Clone, Default)]
        pub struct OsrmOptions {
            pub credentials: ConnectorCredentials,
            pub base_url: String,
            /// What this connector may do. READ ONLY unless somebody widened it,
            /// because the write half of every one of these is the half that
            /// cannot be undone.
            pub allow_write: bool,
        }

        impl OsrmOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://router.project-osrm.org";
            pub const KIND: &'static str = "OSRM routing";
            /// The thing about this service that is easy to get wrong.
            pub const NOTE: &'static str = "coordinates are LONGITUDE,LATITUDE - the opposite order from almost \
     everything else, and swapping them puts a route in the wrong hemisphere \
     without any error";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self, now_ms: u64) -> bool {
                self.credentials.is_live(now_ms)
            }
        }

        impl std::fmt::Debug for OsrmOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(OsrmOptions))
                    .field("credentials", &self.credentials)
                    .field("base_url", &self.resolved_base_url())
                    .field("allow_write", &self.allow_write)
                    .finish()
            }
        }

        #[doc = concat!("Reaches ", "OSRM routing", ".")]
        ///
        /// The request itself belongs to the head: this needs an HTTP client, a
        /// token refresh and a platform credential store, none of which belong
        /// in a core that compiles for a small chip.
        pub struct OsrmRoutingProvider {
            options: OsrmOptions,
            #[allow(clippy::type_complexity)]
            call: Option<
                Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
            >,
        }

        impl OsrmRoutingProvider {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: OsrmOptions,
                call: Option<
                    Box<dyn Fn(&str, &str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, call }
            }

            pub fn options(&self) -> &OsrmOptions {
                &self.options
            }

            pub fn is_available(&self, now_ms: u64) -> bool {
                self.options.is_configured(now_ms) && self.call.is_some()
            }

            /// A read. Fails with a sentence a person can act on rather than a
            /// status code.
            pub fn get(&self, path: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        OsrmOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        OsrmOptions::KIND
                    ));
                };
                call("GET", &format!("{}{path}", self.options.resolved_base_url()), "")
            }

            /// A write. REFUSED unless it was explicitly allowed - the default
            /// for every one of these is to look and not touch.
            pub fn post(&self, path: &str, body: &str, now_ms: u64) -> Result<String, String> {
                if !self.options.allow_write {
                    return Err(format!(
                        "this {} connection is read-only",
                        OsrmOptions::KIND
                    ));
                }
                if !self.options.is_configured(now_ms) {
                    return Err(format!(
                        "{} is not connected on this device",
                        OsrmOptions::KIND
                    ));
                }
                let Some(call) = &self.call else {
                    return Err(format!(
                        "{} cannot be reached from this build",
                        OsrmOptions::KIND
                    ));
                };
                call(
                    "POST",
                    &format!("{}{path}", self.options.resolved_base_url()),
                    body,
                )
            }
        }


/// Weather from Open-Meteo.
///
/// Open data, no key, and no account - which means using it tells nobody who
/// asked. That is the reason it is the weather provider here.
pub struct OpenMeteoWeatherProvider {
    #[allow(clippy::type_complexity)]
    fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    pub base_url: String,
}

impl OpenMeteoWeatherProvider {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.open-meteo.com/v1/forecast";

    #[allow(clippy::type_complexity)]
    pub fn new(fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>) -> Self {
        Self { fetch, base_url: Self::DEFAULT_BASE_URL.into() }
    }

    pub fn is_available(&self) -> bool {
        self.fetch.is_some()
    }

    /// Coordinates are ROUNDED to two decimals - about a kilometre.
    ///
    /// Full precision in a weather request is a person's exact location sent to
    /// a third party, and the forecast for a kilometre away is the same
    /// forecast.
    pub fn url(&self, latitude: f64, longitude: f64) -> String {
        format!(
            "{}?latitude={:.2}&longitude={:.2}&current=temperature_2m,precipitation",
            self.base_url, latitude, longitude
        )
    }

    pub fn current(&self, latitude: f64, longitude: f64) -> Result<String, String> {
        let Some(fetch) = &self.fetch else {
            return Err("this device cannot reach a weather service".into());
        };
        fetch(&self.url(latitude, longitude))
    }
}

/// A story from somewhere.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NewsItem {
    pub title: String,
    pub summary: String,
    pub url: String,
    pub source: String,
    pub published_at_ms: u64,
    pub author: String,
}

/// Writes a news source and its options.
// `news_source` was written once as a macro over the table below and
// expanded here, so each type appears under its own name.


#[doc = "RSS. NO ACCOUNT, NO KEY AND NO SERVICE IN THE MIDDLE - a feed is fetched \
     straight from whoever publishes it, which is the only arrangement here \
     where reading the news tells nobody but the publisher."]
        #[derive(Clone, Default)]
        pub struct RssOptions {
            pub key: String,
            pub base_url: String,
            pub feeds: Vec<String>,
            /// How many to take. Bounded, because a feed with a thousand entries
            /// on a phone is a scroll nobody reaches the end of.
            pub limit: usize,
        }

        impl RssOptions {
            pub const DEFAULT_BASE_URL: &'static str = "";
            pub const LABEL: &'static str = "RSS feeds";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn resolved_limit(&self) -> usize {
                if self.limit == 0 { 20 } else { self.limit.min(100) }
            }
        }

        impl std::fmt::Debug for RssOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(RssOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("feeds", &self.feeds.len())
                    .finish()
            }
        }

        #[doc = concat!("Reads ", "RSS feeds", ".")]
        pub struct RssNewsSource {
            options: RssOptions,
            #[allow(clippy::type_complexity)]
            fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        }

        impl RssNewsSource {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: RssOptions,
                fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
            ) -> Self {
                Self { options, fetch }
            }

            pub fn options(&self) -> &RssOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.fetch.is_some()
            }

            pub fn fetch_raw(&self, url: &str) -> Result<String, String> {
                let Some(fetch) = &self.fetch else {
                    return Err(format!("{} cannot be reached from this build", "RSS feeds"));
                };
                fetch(url)
            }
        }

#[doc = "NewsAPI. An aggregator, so it knows every query - which is why the RSS \
     source exists alongside it."]
        #[derive(Clone, Default)]
        pub struct NewsApiOptions {
            pub key: String,
            pub base_url: String,
            pub feeds: Vec<String>,
            /// How many to take. Bounded, because a feed with a thousand entries
            /// on a phone is a scroll nobody reaches the end of.
            pub limit: usize,
        }

        impl NewsApiOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://newsapi.org/v2";
            pub const LABEL: &'static str = "NewsAPI";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn resolved_limit(&self) -> usize {
                if self.limit == 0 { 20 } else { self.limit.min(100) }
            }
        }

        impl std::fmt::Debug for NewsApiOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(NewsApiOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("feeds", &self.feeds.len())
                    .finish()
            }
        }

        #[doc = concat!("Reads ", "NewsAPI", ".")]
        pub struct NewsApiSource {
            options: NewsApiOptions,
            #[allow(clippy::type_complexity)]
            fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        }

        impl NewsApiSource {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: NewsApiOptions,
                fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
            ) -> Self {
                Self { options, fetch }
            }

            pub fn options(&self) -> &NewsApiOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.fetch.is_some()
            }

            pub fn fetch_raw(&self, url: &str) -> Result<String, String> {
                let Some(fetch) = &self.fetch else {
                    return Err(format!("{} cannot be reached from this build", "NewsAPI"));
                };
                fetch(url)
            }
        }

#[doc = "Mastodon. Federated, so the instance is part of the configuration and \
     there is no single company to ask."]
        #[derive(Clone, Default)]
        pub struct MastodonOptions {
            pub key: String,
            pub base_url: String,
            pub feeds: Vec<String>,
            /// How many to take. Bounded, because a feed with a thousand entries
            /// on a phone is a scroll nobody reaches the end of.
            pub limit: usize,
        }

        impl MastodonOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://mastodon.social/api/v1";
            pub const LABEL: &'static str = "Mastodon";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn resolved_limit(&self) -> usize {
                if self.limit == 0 { 20 } else { self.limit.min(100) }
            }
        }

        impl std::fmt::Debug for MastodonOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(MastodonOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("feeds", &self.feeds.len())
                    .finish()
            }
        }

        #[doc = concat!("Reads ", "Mastodon", ".")]
        pub struct MastodonSource {
            options: MastodonOptions,
            #[allow(clippy::type_complexity)]
            fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        }

        impl MastodonSource {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: MastodonOptions,
                fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
            ) -> Self {
                Self { options, fetch }
            }

            pub fn options(&self) -> &MastodonOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.fetch.is_some()
            }

            pub fn fetch_raw(&self, url: &str) -> Result<String, String> {
                let Some(fetch) = &self.fetch else {
                    return Err(format!("{} cannot be reached from this build", "Mastodon"));
                };
                fetch(url)
            }
        }

#[doc = "Bluesky. The public read endpoint needs no account, so following a feed \
     does not require handing over an identity."]
        #[derive(Clone, Default)]
        pub struct BlueskyOptions {
            pub key: String,
            pub base_url: String,
            pub feeds: Vec<String>,
            /// How many to take. Bounded, because a feed with a thousand entries
            /// on a phone is a scroll nobody reaches the end of.
            pub limit: usize,
        }

        impl BlueskyOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://public.api.bsky.app/xrpc";
            pub const LABEL: &'static str = "Bluesky";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn resolved_limit(&self) -> usize {
                if self.limit == 0 { 20 } else { self.limit.min(100) }
            }
        }

        impl std::fmt::Debug for BlueskyOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(BlueskyOptions))
                    .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
                    .field("base_url", &self.resolved_base_url())
                    .field("feeds", &self.feeds.len())
                    .finish()
            }
        }

        #[doc = concat!("Reads ", "Bluesky", ".")]
        pub struct BlueskySource {
            options: BlueskyOptions,
            #[allow(clippy::type_complexity)]
            fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        }

        impl BlueskySource {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: BlueskyOptions,
                fetch: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
            ) -> Self {
                Self { options, fetch }
            }

            pub fn options(&self) -> &BlueskyOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.fetch.is_some()
            }

            pub fn fetch_raw(&self, url: &str) -> Result<String, String> {
                let Some(fetch) = &self.fetch else {
                    return Err(format!("{} cannot be reached from this build", "Bluesky"));
                };
                fetch(url)
            }
        }


// ─────────────────────────────────────────────────────────────────────────────
// Telephony carriers

/// Where a call is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CallState {
    #[default]
    Idle,
    Ringing,
    Answered,
    /// The other end hung up. Distinct from `Ended`, which is us.
    RemoteHungUp,
    Ended,
    Failed,
}

/// Writes one carrier's options, session and seam.
// `carrier` was written once as a macro over the table below and
// expanded here, so each type appears under its own name.


#[doc = "Twilio."]
        #[derive(Clone, Default)]
        pub struct TwilioOptions {
            pub account: String,
            pub token: String,
            pub base_url: String,
            /// The number calls come from, in E.164. A carrier will reject
            /// anything else, and a national-format number looks correct.
            pub from_number_e164: String,
            /// Where the carrier calls back. HTTPS only - a webhook over plain
            /// HTTP puts call content on the wire in clear.
            pub webhook_url: String,
        }

        impl TwilioOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://api.twilio.com/2010-04-01";
            pub const LABEL: &'static str = "Twilio";
            /// What is easy to get wrong with this carrier.
            pub const NOTE: &'static str = "the account SID doubles as the username, so a leaked SID is half a \
     credential rather than a harmless identifier";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self) -> bool {
                !self.account.is_empty()
                    && !self.token.is_empty()
                    && Self::is_e164(&self.from_number_e164)
            }

            /// A plus, then up to fifteen digits, first not zero.
            ///
            /// Checked HERE because every carrier rejects anything else, and a
            /// number in national format looks right to a person and is not.
            pub fn is_e164(number: &str) -> bool {
                let Some(digits) = number.strip_prefix('+') else { return false };
                (1..=15).contains(&digits.len())
                    && digits.chars().all(|c| c.is_ascii_digit())
                    && !digits.starts_with('0')
            }

            /// The webhook must be HTTPS. A carrier posting call audio and
            /// transcripts to a plain HTTP endpoint puts them on the wire in
            /// clear, and it is the carrier that chooses when to post.
            pub fn webhook_is_safe(&self) -> bool {
                self.webhook_url.starts_with("https://")
            }
        }

        impl std::fmt::Debug for TwilioOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(TwilioOptions))
                    .field("account", &self.account)
                    .field("token", &if self.token.is_empty() { "<unset>" } else { "<set>" })
                    .field("from", &self.from_number_e164)
                    .finish()
            }
        }

        #[doc = concat!("One call on ", "Twilio", ".")]
        #[derive(Debug, Clone, PartialEq, Eq, Default)]
        pub struct TwilioCallSession {
            pub call_id: String,
            pub to_number_e164: String,
            pub state: CallState,
            pub started_at_ms: u64,
            pub ended_at_ms: u64,
            /// Why it ended, in words. A carrier code means nothing to the
            /// person who was on the call.
            pub reason: String,
        }

        impl TwilioCallSession {
            pub fn is_live(&self) -> bool {
                matches!(self.state, CallState::Ringing | CallState::Answered)
            }

            pub fn duration_ms(&self, now_ms: u64) -> u64 {
                let end = if self.ended_at_ms > 0 { self.ended_at_ms } else { now_ms };
                end.saturating_sub(self.started_at_ms)
            }
        }

        #[doc = concat!("Places and answers calls through ", "Twilio", ".")]
        pub struct TwilioCarrier {
            options: TwilioOptions,
            #[allow(clippy::type_complexity)]
            place: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
            sessions: HashMap<String, TwilioCallSession>,
        }

        impl TwilioCarrier {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: TwilioOptions,
                place: Option<
                    Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, place, sessions: HashMap::new() }
            }

            pub fn options(&self) -> &TwilioOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.place.is_some()
            }

            /// Places a call.
            ///
            /// REFUSES over an unsafe webhook. A carrier that will post call
            /// audio to a plain HTTP endpoint should not be given a call to
            /// make, and finding out afterwards is finding out too late.
            pub fn dial(&mut self, to_number_e164: &str, now_ms: u64) -> Result<TwilioCallSession, String> {
                if !TwilioOptions::is_e164(to_number_e164) {
                    return Err(format!(
                        "{to_number_e164} is not a full international number"
                    ));
                }
                if !self.options.is_configured() {
                    return Err(format!("{} is not set up on this device", "Twilio"));
                }
                if !self.options.webhook_is_safe() {
                    return Err(
                        "the callback address is not https, so the call was not placed".into(),
                    );
                }
                let Some(place) = &self.place else {
                    return Err(format!("{} cannot be reached from this build", "Twilio"));
                };
                let call_id = place(&self.options.from_number_e164, to_number_e164)?;
                let session = TwilioCallSession {
                    call_id: call_id.clone(),
                    to_number_e164: to_number_e164.to_string(),
                    state: CallState::Ringing,
                    started_at_ms: now_ms,
                    ..Default::default()
                };
                self.sessions.insert(call_id, session.clone());
                Ok(session)
            }

            pub fn session(&self, call_id: &str) -> Option<TwilioCallSession> {
                self.sessions.get(call_id).cloned()
            }

            /// Moves a call along. A call that has ENDED stays ended - a late
            /// carrier callback must not resurrect it.
            pub fn advance(
                &mut self,
                call_id: &str,
                state: CallState,
                reason: &str,
                now_ms: u64,
            ) -> bool {
                let Some(session) = self.sessions.get_mut(call_id) else { return false };
                if !session.is_live() {
                    return false;
                }
                session.state = state;
                session.reason = reason.to_string();
                if !session.is_live() {
                    session.ended_at_ms = now_ms;
                }
                true
            }

            pub fn live_calls(&self) -> Vec<TwilioCallSession> {
                self.sessions.values().filter(|s| s.is_live()).cloned().collect()
            }
        }

        #[doc = concat!("Wires ", "Twilio", ".")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct TwilioServiceCollectionExtensions;

        impl TwilioServiceCollectionExtensions {
            pub const LABEL: &'static str = "Twilio";

            /// What is missing, so a setup screen can say which part.
            pub fn missing(options: &TwilioOptions) -> Vec<&'static str> {
                let mut out = Vec::new();
                if options.account.is_empty() {
                    out.push("an account identifier");
                }
                if options.token.is_empty() {
                    out.push("a token");
                }
                if !TwilioOptions::is_e164(&options.from_number_e164) {
                    out.push("a number in full international form");
                }
                if !options.webhook_is_safe() {
                    out.push("an https callback address");
                }
                out
            }
        }

#[doc = "Telnyx."]
        #[derive(Clone, Default)]
        pub struct TelnyxOptions {
            pub account: String,
            pub token: String,
            pub base_url: String,
            /// The number calls come from, in E.164. A carrier will reject
            /// anything else, and a national-format number looks correct.
            pub from_number_e164: String,
            /// Where the carrier calls back. HTTPS only - a webhook over plain
            /// HTTP puts call content on the wire in clear.
            pub webhook_url: String,
        }

        impl TelnyxOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://api.telnyx.com/v2";
            pub const LABEL: &'static str = "Telnyx";
            /// What is easy to get wrong with this carrier.
            pub const NOTE: &'static str = "call control events arrive out of order, so a state machine that trusts \
     arrival order will show a call as ringing after it has been answered";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self) -> bool {
                !self.account.is_empty()
                    && !self.token.is_empty()
                    && Self::is_e164(&self.from_number_e164)
            }

            /// A plus, then up to fifteen digits, first not zero.
            ///
            /// Checked HERE because every carrier rejects anything else, and a
            /// number in national format looks right to a person and is not.
            pub fn is_e164(number: &str) -> bool {
                let Some(digits) = number.strip_prefix('+') else { return false };
                (1..=15).contains(&digits.len())
                    && digits.chars().all(|c| c.is_ascii_digit())
                    && !digits.starts_with('0')
            }

            /// The webhook must be HTTPS. A carrier posting call audio and
            /// transcripts to a plain HTTP endpoint puts them on the wire in
            /// clear, and it is the carrier that chooses when to post.
            pub fn webhook_is_safe(&self) -> bool {
                self.webhook_url.starts_with("https://")
            }
        }

        impl std::fmt::Debug for TelnyxOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(TelnyxOptions))
                    .field("account", &self.account)
                    .field("token", &if self.token.is_empty() { "<unset>" } else { "<set>" })
                    .field("from", &self.from_number_e164)
                    .finish()
            }
        }

        #[doc = concat!("One call on ", "Telnyx", ".")]
        #[derive(Debug, Clone, PartialEq, Eq, Default)]
        pub struct TelnyxCallSession {
            pub call_id: String,
            pub to_number_e164: String,
            pub state: CallState,
            pub started_at_ms: u64,
            pub ended_at_ms: u64,
            /// Why it ended, in words. A carrier code means nothing to the
            /// person who was on the call.
            pub reason: String,
        }

        impl TelnyxCallSession {
            pub fn is_live(&self) -> bool {
                matches!(self.state, CallState::Ringing | CallState::Answered)
            }

            pub fn duration_ms(&self, now_ms: u64) -> u64 {
                let end = if self.ended_at_ms > 0 { self.ended_at_ms } else { now_ms };
                end.saturating_sub(self.started_at_ms)
            }
        }

        #[doc = concat!("Places and answers calls through ", "Telnyx", ".")]
        pub struct TelnyxCarrier {
            options: TelnyxOptions,
            #[allow(clippy::type_complexity)]
            place: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
            sessions: HashMap<String, TelnyxCallSession>,
        }

        impl TelnyxCarrier {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: TelnyxOptions,
                place: Option<
                    Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, place, sessions: HashMap::new() }
            }

            pub fn options(&self) -> &TelnyxOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.place.is_some()
            }

            /// Places a call.
            ///
            /// REFUSES over an unsafe webhook. A carrier that will post call
            /// audio to a plain HTTP endpoint should not be given a call to
            /// make, and finding out afterwards is finding out too late.
            pub fn dial(&mut self, to_number_e164: &str, now_ms: u64) -> Result<TelnyxCallSession, String> {
                if !TelnyxOptions::is_e164(to_number_e164) {
                    return Err(format!(
                        "{to_number_e164} is not a full international number"
                    ));
                }
                if !self.options.is_configured() {
                    return Err(format!("{} is not set up on this device", "Telnyx"));
                }
                if !self.options.webhook_is_safe() {
                    return Err(
                        "the callback address is not https, so the call was not placed".into(),
                    );
                }
                let Some(place) = &self.place else {
                    return Err(format!("{} cannot be reached from this build", "Telnyx"));
                };
                let call_id = place(&self.options.from_number_e164, to_number_e164)?;
                let session = TelnyxCallSession {
                    call_id: call_id.clone(),
                    to_number_e164: to_number_e164.to_string(),
                    state: CallState::Ringing,
                    started_at_ms: now_ms,
                    ..Default::default()
                };
                self.sessions.insert(call_id, session.clone());
                Ok(session)
            }

            pub fn session(&self, call_id: &str) -> Option<TelnyxCallSession> {
                self.sessions.get(call_id).cloned()
            }

            /// Moves a call along. A call that has ENDED stays ended - a late
            /// carrier callback must not resurrect it.
            pub fn advance(
                &mut self,
                call_id: &str,
                state: CallState,
                reason: &str,
                now_ms: u64,
            ) -> bool {
                let Some(session) = self.sessions.get_mut(call_id) else { return false };
                if !session.is_live() {
                    return false;
                }
                session.state = state;
                session.reason = reason.to_string();
                if !session.is_live() {
                    session.ended_at_ms = now_ms;
                }
                true
            }

            pub fn live_calls(&self) -> Vec<TelnyxCallSession> {
                self.sessions.values().filter(|s| s.is_live()).cloned().collect()
            }
        }

        #[doc = concat!("Wires ", "Telnyx", ".")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct TelnyxServiceCollectionExtensions;

        impl TelnyxServiceCollectionExtensions {
            pub const LABEL: &'static str = "Telnyx";

            /// What is missing, so a setup screen can say which part.
            pub fn missing(options: &TelnyxOptions) -> Vec<&'static str> {
                let mut out = Vec::new();
                if options.account.is_empty() {
                    out.push("an account identifier");
                }
                if options.token.is_empty() {
                    out.push("a token");
                }
                if !TelnyxOptions::is_e164(&options.from_number_e164) {
                    out.push("a number in full international form");
                }
                if !options.webhook_is_safe() {
                    out.push("an https callback address");
                }
                out
            }
        }

#[doc = "Plivo."]
        #[derive(Clone, Default)]
        pub struct PlivoOptions {
            pub account: String,
            pub token: String,
            pub base_url: String,
            /// The number calls come from, in E.164. A carrier will reject
            /// anything else, and a national-format number looks correct.
            pub from_number_e164: String,
            /// Where the carrier calls back. HTTPS only - a webhook over plain
            /// HTTP puts call content on the wire in clear.
            pub webhook_url: String,
        }

        impl PlivoOptions {
            pub const DEFAULT_BASE_URL: &'static str = "https://api.plivo.com/v1";
            pub const LABEL: &'static str = "Plivo";
            /// What is easy to get wrong with this carrier.
            pub const NOTE: &'static str = "the callback is form-encoded rather than JSON, which parses as empty \
     against a JSON reader without any error";

            pub fn resolved_base_url(&self) -> &str {
                if self.base_url.is_empty() {
                    Self::DEFAULT_BASE_URL
                } else {
                    &self.base_url
                }
            }

            pub fn is_configured(&self) -> bool {
                !self.account.is_empty()
                    && !self.token.is_empty()
                    && Self::is_e164(&self.from_number_e164)
            }

            /// A plus, then up to fifteen digits, first not zero.
            ///
            /// Checked HERE because every carrier rejects anything else, and a
            /// number in national format looks right to a person and is not.
            pub fn is_e164(number: &str) -> bool {
                let Some(digits) = number.strip_prefix('+') else { return false };
                (1..=15).contains(&digits.len())
                    && digits.chars().all(|c| c.is_ascii_digit())
                    && !digits.starts_with('0')
            }

            /// The webhook must be HTTPS. A carrier posting call audio and
            /// transcripts to a plain HTTP endpoint puts them on the wire in
            /// clear, and it is the carrier that chooses when to post.
            pub fn webhook_is_safe(&self) -> bool {
                self.webhook_url.starts_with("https://")
            }
        }

        impl std::fmt::Debug for PlivoOptions {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.debug_struct(stringify!(PlivoOptions))
                    .field("account", &self.account)
                    .field("token", &if self.token.is_empty() { "<unset>" } else { "<set>" })
                    .field("from", &self.from_number_e164)
                    .finish()
            }
        }

        #[doc = concat!("One call on ", "Plivo", ".")]
        #[derive(Debug, Clone, PartialEq, Eq, Default)]
        pub struct PlivoCallSession {
            pub call_id: String,
            pub to_number_e164: String,
            pub state: CallState,
            pub started_at_ms: u64,
            pub ended_at_ms: u64,
            /// Why it ended, in words. A carrier code means nothing to the
            /// person who was on the call.
            pub reason: String,
        }

        impl PlivoCallSession {
            pub fn is_live(&self) -> bool {
                matches!(self.state, CallState::Ringing | CallState::Answered)
            }

            pub fn duration_ms(&self, now_ms: u64) -> u64 {
                let end = if self.ended_at_ms > 0 { self.ended_at_ms } else { now_ms };
                end.saturating_sub(self.started_at_ms)
            }
        }

        #[doc = concat!("Places and answers calls through ", "Plivo", ".")]
        pub struct PlivoCarrier {
            options: PlivoOptions,
            #[allow(clippy::type_complexity)]
            place: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
            sessions: HashMap<String, PlivoCallSession>,
        }

        impl PlivoCarrier {
            #[allow(clippy::type_complexity)]
            pub fn new(
                options: PlivoOptions,
                place: Option<
                    Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, place, sessions: HashMap::new() }
            }

            pub fn options(&self) -> &PlivoOptions {
                &self.options
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.place.is_some()
            }

            /// Places a call.
            ///
            /// REFUSES over an unsafe webhook. A carrier that will post call
            /// audio to a plain HTTP endpoint should not be given a call to
            /// make, and finding out afterwards is finding out too late.
            pub fn dial(&mut self, to_number_e164: &str, now_ms: u64) -> Result<PlivoCallSession, String> {
                if !PlivoOptions::is_e164(to_number_e164) {
                    return Err(format!(
                        "{to_number_e164} is not a full international number"
                    ));
                }
                if !self.options.is_configured() {
                    return Err(format!("{} is not set up on this device", "Plivo"));
                }
                if !self.options.webhook_is_safe() {
                    return Err(
                        "the callback address is not https, so the call was not placed".into(),
                    );
                }
                let Some(place) = &self.place else {
                    return Err(format!("{} cannot be reached from this build", "Plivo"));
                };
                let call_id = place(&self.options.from_number_e164, to_number_e164)?;
                let session = PlivoCallSession {
                    call_id: call_id.clone(),
                    to_number_e164: to_number_e164.to_string(),
                    state: CallState::Ringing,
                    started_at_ms: now_ms,
                    ..Default::default()
                };
                self.sessions.insert(call_id, session.clone());
                Ok(session)
            }

            pub fn session(&self, call_id: &str) -> Option<PlivoCallSession> {
                self.sessions.get(call_id).cloned()
            }

            /// Moves a call along. A call that has ENDED stays ended - a late
            /// carrier callback must not resurrect it.
            pub fn advance(
                &mut self,
                call_id: &str,
                state: CallState,
                reason: &str,
                now_ms: u64,
            ) -> bool {
                let Some(session) = self.sessions.get_mut(call_id) else { return false };
                if !session.is_live() {
                    return false;
                }
                session.state = state;
                session.reason = reason.to_string();
                if !session.is_live() {
                    session.ended_at_ms = now_ms;
                }
                true
            }

            pub fn live_calls(&self) -> Vec<PlivoCallSession> {
                self.sessions.values().filter(|s| s.is_live()).cloned().collect()
            }
        }

        #[doc = concat!("Wires ", "Plivo", ".")]
        #[derive(Debug, Default, Clone, Copy)]
        pub struct PlivoServiceCollectionExtensions;

        impl PlivoServiceCollectionExtensions {
            pub const LABEL: &'static str = "Plivo";

            /// What is missing, so a setup screen can say which part.
            pub fn missing(options: &PlivoOptions) -> Vec<&'static str> {
                let mut out = Vec::new();
                if options.account.is_empty() {
                    out.push("an account identifier");
                }
                if options.token.is_empty() {
                    out.push("a token");
                }
                if !PlivoOptions::is_e164(&options.from_number_e164) {
                    out.push("a number in full international form");
                }
                if !options.webhook_is_safe() {
                    out.push("an https callback address");
                }
                out
            }
        }


// ─────────────────────────────────────────────────────────────────────────────
// Search primitives

/// Splitting text into things to match on.
pub struct SearchTokenisation;

impl SearchTokenisation {
    /// Words, lower-cased, punctuation dropped.
    ///
    /// SPLITS ON UNICODE, not on ASCII whitespace: text in isiZulu or Amharic
    /// tokenised by an ASCII rule comes back as one enormous token, and the
    /// index silently contains nothing useful for those languages.
    pub fn tokens(text: &str) -> Vec<String> {
        text.split(|c: char| !c.is_alphanumeric())
            .filter(|t| !t.is_empty())
            .map(|t| t.to_lowercase())
            .collect()
    }

    /// Overlapping character runs, for matching within words.
    ///
    /// The fallback for languages this has no word rule for, and for matching a
    /// misspelling - which is most of what people type into a search box.
    pub fn character_grams(text: &str, n: usize) -> Vec<String> {
        let chars: Vec<char> = text.to_lowercase().chars().collect();
        if n == 0 || chars.len() < n {
            return vec![chars.into_iter().collect()];
        }
        chars.windows(n).map(|w| w.iter().collect()).collect()
    }

    /// Words too common to be worth indexing. DELIBERATELY SHORT and English
    /// only - a stop word list for a language nobody checked removes meaning.
    pub const ENGLISH_STOP_WORDS: &'static [&'static str] =
        &["a", "an", "and", "are", "as", "at", "be", "for", "in", "is", "it",
          "of", "on", "or", "the", "to", "was"];

    pub fn without_stop_words(tokens: &[String]) -> Vec<String> {
        tokens
            .iter()
            .filter(|t| !Self::ENGLISH_STOP_WORDS.contains(&t.as_str()))
            .cloned()
            .collect()
    }
}

/// How well something matches.
pub struct SearchScoring;

impl SearchScoring {
    /// Okapi BM25, the standard for text relevance.
    ///
    /// The two constants are the usual ones. `k1` controls how fast repeated
    /// terms stop helping and `b` how much a long document is penalised -
    /// without `b`, a long document matches everything simply by containing
    /// more words.
    pub const K1: f64 = 1.2;
    pub const B: f64 = 0.75;

    /// The inverse document frequency of one term.
    ///
    /// The `+ 1.0` inside the logarithm keeps this NON-NEGATIVE. Without it a
    /// term appearing in more than half the documents scores negative, and a
    /// document containing the search term ranks below one that does not.
    pub fn idf(documents: usize, containing: usize) -> f64 {
        if documents == 0 {
            return 0.0;
        }
        let n = documents as f64;
        let df = containing as f64;
        (((n - df + 0.5) / (df + 0.5)) + 1.0).ln()
    }

    pub fn bm25(
        term_frequency: usize,
        document_length: usize,
        average_length: f64,
        idf: f64,
    ) -> f64 {
        if term_frequency == 0 || average_length <= 0.0 {
            return 0.0;
        }
        let tf = term_frequency as f64;
        let normalisation =
            Self::K1 * (1.0 - Self::B + Self::B * document_length as f64 / average_length);
        idf * (tf * (Self::K1 + 1.0)) / (tf + normalisation)
    }
}

/// Vector arithmetic for embeddings.
pub struct VectorMath;

impl VectorMath {
    /// Cosine similarity.
    ///
    /// Returns 0 for a zero vector rather than NaN. A NaN propagates through
    /// every subsequent comparison and sorts unpredictably, so one bad embedding
    /// scrambles a whole result list.
    pub fn cosine(a: &[f32], b: &[f32]) -> f32 {
        if a.len() != b.len() || a.is_empty() {
            return 0.0;
        }
        let (mut dot, mut na, mut nb) = (0f32, 0f32, 0f32);
        for i in 0..a.len() {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if na <= 0.0 || nb <= 0.0 {
            return 0.0;
        }
        dot / (na.sqrt() * nb.sqrt())
    }

    pub fn dot(a: &[f32], b: &[f32]) -> f32 {
        a.iter().zip(b).map(|(x, y)| x * y).sum()
    }

    /// Normalises in place, leaving a zero vector alone.
    ///
    /// Pre-normalising turns every later cosine into a dot product, which is
    /// most of the cost of a search over many vectors.
    pub fn normalise(vector: &mut [f32]) {
        let norm = vector.iter().map(|v| v * v).sum::<f32>().sqrt();
        if norm > 0.0 {
            for v in vector.iter_mut() {
                *v /= norm;
            }
        }
    }

    /// The `count` best, best first.
    pub fn top_k(query: &[f32], vectors: &[Vec<f32>], count: usize) -> Vec<(usize, f32)> {
        let mut scored: Vec<(usize, f32)> = vectors
            .iter()
            .enumerate()
            .map(|(i, v)| (i, Self::cosine(query, v)))
            .collect();
        scored.sort_by(|a, b| {
            b.1.partial_cmp(&a.1)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.0.cmp(&b.0))
        });
        scored.truncate(count);
        scored
    }
}

/// The wide-register operations, written so the compiler can vectorise them.
///
/// NO INTRINSICS AND NO `unsafe`. Hand-written SIMD would be per-architecture -
/// one for x86, one for aarch64, one for RISC-V - and every one of them a place
/// for a bug that only appears on hardware nobody here has. These are shaped as
/// chunked loops over fixed-size arrays, which autovectorises on all three and
/// is correct on a target that has no vector unit at all.
pub struct SimdOps;

impl SimdOps {
    /// The chunk width. Eight f32 is 256 bits - one AVX register, two NEON.
    pub const LANES: usize = 8;

    /// A dot product, accumulated per lane.
    ///
    /// The per-lane accumulators are what lets this vectorise: a single running
    /// sum forces the additions into order, because floating-point addition is
    /// not associative and the compiler may not reorder it.
    pub fn dot(a: &[f32], b: &[f32]) -> f32 {
        let length = a.len().min(b.len());
        let mut lanes = [0f32; Self::LANES];
        let chunks = length / Self::LANES;
        for chunk in 0..chunks {
            let base = chunk * Self::LANES;
            for lane in 0..Self::LANES {
                lanes[lane] += a[base + lane] * b[base + lane];
            }
        }
        let mut total: f32 = lanes.iter().sum();
        for i in chunks * Self::LANES..length {
            total += a[i] * b[i];
        }
        total
    }

    pub fn scale(values: &mut [f32], factor: f32) {
        for v in values.iter_mut() {
            *v *= factor;
        }
    }

    pub fn add_scaled(target: &mut [f32], source: &[f32], factor: f32) {
        for i in 0..target.len().min(source.len()) {
            target[i] += source[i] * factor;
        }
    }

    /// The sum of squares, per lane for the same reason as `dot`.
    pub fn norm_squared(values: &[f32]) -> f32 {
        Self::dot(values, values)
    }
}
