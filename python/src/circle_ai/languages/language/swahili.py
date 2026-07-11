# swahili.py
#
# Port of CircleAI.Languages.Language.Swahili SwahiliLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "Nzuri",
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
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Habari' in the morning. Show respect to elders.",
            ["Habari", "Usiku mwema"],
        )
    ]
}


class SwahiliLanguagePack(ILanguagePack):
    """Swahili language pack for Circle AI."""

    Instance: "SwahiliLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="sw",
        display_name="Swahili",
        native_name="Kiswahili",
        primary_region="KE",
        spoken_in_regions=["KE", "TZ", "UG"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Swahili speakers. "
            "Respond in Swahili (Kiswahili) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Habari" if tod in ("morning", "am") else "Usiku mwema"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "sw",
            "region": "KE",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


SwahiliLanguagePack.Instance = SwahiliLanguagePack()  # type: ignore[attr-defined]
