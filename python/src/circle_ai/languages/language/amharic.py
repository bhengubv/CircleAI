# amharic.py
#
# Port of CircleAI.Languages.Language.Amharic AmharicLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "ደህና ነኝ",
    "water": "ውሃ",
    "food": "ምግብ",
    "family": "ቤተሰብ",
    "friend": "ጓደኛ",
    "love": "ፍቅር",
    "mother": "እናት",
    "father": "አባት",
    "child": "ልጅ",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'ጤና ይስጥልኝ' in the morning. Show respect to elders.",
            ["ጤና ይስጥልኝ", "መልካም ምሽት"],
        )
    ]
}


class AmharicLanguagePack(ILanguagePack):
    """Amharic language pack for Circle AI."""

    Instance: "AmharicLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="am",
        display_name="Amharic",
        native_name="አማርኛ",
        primary_region="ET",
        spoken_in_regions=["ET"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Amharic speakers. "
            "Respond in Amharic (አማርኛ) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "ጤና ይስጥልኝ" if tod in ("morning", "am") else "መልካም ምሽት"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "am",
            "region": "ET",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


AmharicLanguagePack.Instance = AmharicLanguagePack()  # type: ignore[attr-defined]
