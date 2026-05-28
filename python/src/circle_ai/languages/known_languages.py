from __future__ import annotations

from .language_types import LanguageTag, WritingSystem


class KnownLanguages:
    """Static registry of every language Circle AI ships support for.

    ALL contains exactly 20 entries in canonical declaration order, matching
    the C# KnownLanguages class and the fixtures/language_tags.json fixture.
    """

    # Africa
    IsiZulu   = LanguageTag("zu",  "isiZulu",    "isiZulu",       WritingSystem.LATIN,      False, "ZA")
    Sesotho   = LanguageTag("st",  "Sesotho",    "Sesotho",       WritingSystem.LATIN,      False, "ZA")
    Afrikaans = LanguageTag("af",  "Afrikaans",  "Afrikaans",     WritingSystem.LATIN,      False, "ZA")
    Swahili   = LanguageTag("sw",  "Swahili",    "Kiswahili",     WritingSystem.LATIN,      False, "KE")
    Hausa     = LanguageTag("ha",  "Hausa",      "Hausa",         WritingSystem.LATIN,      False, "NG")
    Amharic   = LanguageTag("am",  "Amharic",    "አማርኛ",
                             WritingSystem.ETHIOPIC,   False, "ET")
    Yoruba    = LanguageTag("yo",  "Yoruba",     "Yorùbá", WritingSystem.LATIN,   False, "NG")
    Igbo      = LanguageTag("ig",  "Igbo",       "Igbo",          WritingSystem.LATIN,      False, "NG")
    Xhosa     = LanguageTag("xh",  "isiXhosa",   "isiXhosa",      WritingSystem.LATIN,      False, "ZA")
    Sepedi    = LanguageTag("nso", "Sepedi",     "Sepedi",        WritingSystem.LATIN,      False, "ZA")
    Setswana  = LanguageTag("tn",  "Setswana",   "Setswana",      WritingSystem.LATIN,      False, "ZA")
    Somali    = LanguageTag("so",  "Somali",     "Soomaali",      WritingSystem.LATIN,      False, "SO")
    Oromo     = LanguageTag("om",  "Oromo",      "Afaan Oromoo",  WritingSystem.LATIN,      False, "ET")

    # Middle East & North Africa
    Arabic    = LanguageTag("ar",  "Arabic",
                             "العربية",
                             WritingSystem.ARABIC, True, "SA")

    # Europe & Americas
    English    = LanguageTag("en", "English",    "English",       WritingSystem.LATIN,      False, "GB")
    Portuguese = LanguageTag("pt", "Portuguese", "Português", WritingSystem.LATIN,     False, "PT")
    French     = LanguageTag("fr", "French",     "Français", WritingSystem.LATIN,      False, "FR")
    Spanish    = LanguageTag("es", "Spanish",    "Español",  WritingSystem.LATIN,      False, "ES")

    # Asia
    Mandarin  = LanguageTag("zh",  "Mandarin",   "中文",  WritingSystem.HAN,        False, "CN")
    Hindi     = LanguageTag("hi",  "Hindi",
                             "हिन्दी",
                             WritingSystem.DEVANAGARI, False, "IN")

    ALL: list[LanguageTag] = []  # populated below


KnownLanguages.ALL = [
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
]


class DefaultLanguageRegistry:
    """Registry backed by KnownLanguages.ALL.

    Provides the same contract as ILanguageRegistry without requiring subclassing.
    """

    def __init__(self) -> None:
        self._by_tag: dict[str, LanguageTag] = {
            lang.bcp_tag: lang for lang in KnownLanguages.ALL
        }

    def get_by_bcp_tag(self, bcp_tag: str) -> LanguageTag | None:
        return self._by_tag.get(bcp_tag)

    def get_all(self) -> list[LanguageTag]:
        return list(KnownLanguages.ALL)

    def get_for_region(self, iso_region: str) -> list[LanguageTag]:
        return [lang for lang in KnownLanguages.ALL if lang.iso_region == iso_region]

    def is_supported(self, bcp_tag: str) -> bool:
        return bcp_tag in self._by_tag
