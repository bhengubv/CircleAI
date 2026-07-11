# afrikaans.py
#
# Port of CircleAI.Languages.Language.Afrikaans AfrikaansLanguagePack.cs
# (C# — the EXACT spec).

from __future__ import annotations

from typing import List, Mapping, Optional

from .pack import CulturalNote, ILanguagePack, LanguagePackMetadata

_IDIOMS: Mapping[str, str] = {
    "hello": "Hallo",
    "good morning": "Goeie môre",
    "good afternoon": "Goeie middag",
    "good evening": "Goeie naand",
    "goodbye": "Totsiens",
    "thank you": "Dankie",
    "please": "Asseblief",
    "yes": "Ja",
    "no": "Nee",
    "sorry": "Jammer",
    "how are you": "Hoe gaan dit",
    "i am fine": "Dit gaan goed",
    "water": "water",
    "food": "kos",
    "family": "familie",
    "friend": "vriend",
    "love": "liefde",
    "mother": "ma",
    "father": "pa",
    "child": "kind",
}

_NOTES: Mapping[str, List[CulturalNote]] = {
    "greeting": [
        CulturalNote(
            "greeting",
            "Use 'Goeie môre' in the morning. Show respect to elders.",
            ["Goeie môre", "Totsiens"],
        )
    ]
}


class AfrikaansLanguagePack(ILanguagePack):
    """Afrikaans language pack for Circle AI."""

    Instance: "AfrikaansLanguagePack"

    _METADATA = LanguagePackMetadata(
        bcp_tag="af",
        display_name="Afrikaans",
        native_name="Afrikaans",
        primary_region="ZA",
        spoken_in_regions=["ZA", "NA"],
        pack_version=(1, 0),
    )

    @property
    def metadata(self) -> LanguagePackMetadata:
        return self._METADATA

    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        return _IDIOMS.get(phrase.lower()) if phrase is not None else None

    def adapt_system_prompt(self, base_prompt: str) -> str:
        return (
            "You are a culturally aware AI assistant for Afrikaans speakers. "
            "Respond in Afrikaans (Afrikaans) unless instructed otherwise. "
            "Use natural, idiomatic expressions. Respect regional customs. "
            f"\n\n{base_prompt}"
        )

    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        return list(_NOTES.get(context.lower(), [])) if context is not None else []

    def get_greeting(self, time_of_day: str) -> str:
        tod = (time_of_day or "").lower()
        return "Goeie môre" if tod in ("morning", "am") else "Totsiens"

    def get_locale_hints(self) -> Mapping[str, str]:
        return {
            "bcp_tag": "af",
            "region": "ZA",
            "rtl": "false",
            "date_format": "dd/MM/yyyy",
        }


AfrikaansLanguagePack.Instance = AfrikaansLanguagePack()  # type: ignore[attr-defined]
