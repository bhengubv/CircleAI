# hausa.py
#
# Port of CircleAI.Languages.Language.Hausa HausaLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "Lafiya lau",
    "water": "ruwa",
    "food": "abinci",
    "family": "iyali",
    "friend": "aboki",
    "love": "kauna",
    "mother": "uwa",
    "father": "uba",
    "child": "yaro",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Barka da safe' in the morning. Show respect to elders.",
            ["Barka da safe", "Sai anjima"],
        )
    ]
}


class HausaLanguagePack(ILanguagePack):
    """Hausa language pack for Circle AI."""

    Instance: "HausaLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="ha",
        display_name="Hausa",
        native_name="Hausa",
        primary_region="NG",
        spoken_in_regions=["NG", "NE", "GH"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Hausa speakers. "
            "Respond in Hausa (Hausa) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Barka da safe" if tod in ("morning", "am") else "Sai anjima"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "ha",
            "region": "NG",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


HausaLanguagePack.Instance = HausaLanguagePack()  # type: ignore[attr-defined]
