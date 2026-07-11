# portuguese.py
#
# Port of CircleAI.Languages.Language.Portuguese PortugueseLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
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
    "i am fine": "Estou bem",
    "water": "água",
    "food": "comida",
    "family": "família",
    "friend": "amigo",
    "love": "amor",
    "mother": "mãe",
    "father": "pai",
    "child": "criança",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Bom dia' in the morning. Show respect to elders.",
            ["Bom dia", "Boa noite"],
        )
    ]
}


class PortugueseLanguagePack(ILanguagePack):
    """Portuguese language pack for Circle AI."""

    Instance: "PortugueseLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="pt",
        display_name="Portuguese",
        native_name="Português",
        primary_region="PT",
        spoken_in_regions=["PT", "BR", "MZ", "AO"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Portuguese speakers. "
            "Respond in Portuguese (Português) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Bom dia" if tod in ("morning", "am") else "Boa noite"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "pt",
            "region": "PT",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


PortugueseLanguagePack.Instance = PortugueseLanguagePack()  # type: ignore[attr-defined]
