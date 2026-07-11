# isizulu.py
#
# Port of CircleAI.Languages.Language.isiZulu isiZuluLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

# Idioms — case-insensitive lookup (C# StringComparer.OrdinalIgnoreCase).
_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "Ngikhona",
    "sorry": "Uxolo",
    "family": "umndeni",
    "love": "uthando",
    "water": "amanzi",
    "food": "ukudla",
    "mother": "umama",
    "father": "ubaba",
    "child": "ingane",
    "friend": "umngani",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Sawubona' in the morning. Show respect to elders.",
            ["Sawubona", "Lala kahle"],
        )
    ]
}


class isiZuluLanguagePack(ILanguagePack):
    """isiZulu language pack for Circle AI."""

    Instance: "isiZuluLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="zu",
        display_name="isiZulu",
        native_name="isiZulu",
        primary_region="ZA",
        spoken_in_regions=["ZA"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for isiZulu speakers. "
            "Respond in isiZulu (isiZulu) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Sawubona" if tod in ("morning", "am") else "Lala kahle"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "zu",
            "region": "ZA",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


isiZuluLanguagePack.Instance = isiZuluLanguagePack()  # type: ignore[attr-defined]
