# arabic.py
#
# Port of CircleAI.Languages.Language.Arabic ArabicLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "أنا بخير",
    "water": "ماء",
    "food": "طعام",
    "family": "عائلة",
    "friend": "صديق",
    "love": "حب",
    "mother": "أم",
    "father": "أب",
    "child": "طفل",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'صباح الخير' in the morning. Show respect to elders.",
            ["صباح الخير", "مساء الخير"],
        )
    ]
}


class ArabicLanguagePack(ILanguagePack):
    """Arabic language pack for Circle AI."""

    Instance: "ArabicLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="ar",
        display_name="Arabic",
        native_name="العربية",
        primary_region="SA",
        spoken_in_regions=["SA", "EG", "MA", "AE"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Arabic speakers. "
            "Respond in Arabic (العربية) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "صباح الخير" if tod in ("morning", "am") else "مساء الخير"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "ar",
            "region": "SA",
            "rtl": "true",
            "date_format": "dd/MM/yyyy",
        }


ArabicLanguagePack.Instance = ArabicLanguagePack()  # type: ignore[attr-defined]
