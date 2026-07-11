# sesotho.py
#
# Port of CircleAI.Languages.Language.Sesotho SesothoLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
    "hello": "Dumela",
    "hello (plural)": "Dumelang",
    "goodbye": "Sala hantle",
    "goodbye (sleep)": "Robala hantle",
    "thank you": "Kea leboha",
    "please": "Ka kopo",
    "yes": "E",
    "no": "Che",
    "how are you": "O phela joang",
    "i am fine": "Ke phela hantle",
    "sorry": "Tshwarelo",
    "family": "lelapa",
    "love": "lerato",
    "water": "metsi",
    "food": "dijo",
    "mother": "'me",
    "father": "ntate",
    "child": "ngwana",
    "friend": "motswalle",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Dumela' in the morning. Show respect to elders.",
            ["Dumela", "Robala hantle"],
        )
    ]
}


class SesothoLanguagePack(ILanguagePack):
    """Sesotho language pack for Circle AI."""

    Instance: "SesothoLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="st",
        display_name="Sesotho",
        native_name="Sesotho",
        primary_region="ZA",
        spoken_in_regions=["ZA", "LS"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Sesotho speakers. "
            "Respond in Sesotho (Sesotho) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Dumela" if tod in ("morning", "am") else "Robala hantle"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "st",
            "region": "ZA",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


SesothoLanguagePack.Instance = SesothoLanguagePack()  # type: ignore[attr-defined]
