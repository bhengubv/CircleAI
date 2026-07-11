# prompt_variable_resolver.py
#
# Port of CircleAI.Telephony PromptVariableResolver.cs (C# — the EXACT spec).
#
# (3.3.0) Substitute {{variables}} in a system prompt before sending to the LLM.
# Sources: static dictionary, resolved providers, or per-call context. Variables
# can come from CRM look-ups, time-of-day, user identity, knowledge-base hits, etc.
#
# C# delegate PromptVariableProvider (ValueTask<string?>(string, CancellationToken))
# -> an async Callable. C# Regex (compiled, culture-invariant) -> a module-level
# compiled re.Pattern with the same expression. Static/provider name maps use
# casefold() keys to mirror StringComparer.OrdinalIgnoreCase. The two-pass
# design (collect distinct names, resolve once each, then substitute) is
# preserved so a provider is invoked at most once per render.

from __future__ import annotations

import re
from typing import Awaitable, Callable, Dict, Optional

# (3.3.0) Resolves the value for one prompt variable.
PromptVariableProvider = Callable[[str, Optional[object]], Awaitable[Optional[str]]]

_VARIABLE_PATTERN = re.compile(r"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}")


class PromptVariableResolver:
    """(3.3.0) Render a template with ``{{var}}`` placeholders against a set of
    providers."""

    def __init__(self, default_missing: str = "") -> None:
        self._default_missing = default_missing if default_missing is not None else ""
        self._providers: Dict[str, PromptVariableProvider] = {}
        self._statics: Dict[str, str] = {}

    def set(self, name: str, value: str) -> "PromptVariableResolver":
        """Register a static value."""
        if not name or name.isspace():
            raise ValueError("name required")
        self._statics[name.casefold()] = value if value is not None else ""
        return self

    def set_provider(self, name: str, provider: PromptVariableProvider) -> "PromptVariableResolver":
        """Register a dynamic value provider (e.g. CRM lookup)."""
        if not name or name.isspace():
            raise ValueError("name required")
        if provider is None:
            raise ValueError("provider must not be None")
        self._providers[name.casefold()] = provider
        return self

    async def render_async(self, template: str, *, ct: Optional[object] = None) -> str:
        """Render ``template`` by substituting every ``{{var}}``."""
        if not template:
            return ""

        matches = list(_VARIABLE_PATTERN.finditer(template))
        if not matches:
            return template

        replacements: Dict[str, str] = {}  # keyed by casefold(name)
        for m in matches:
            name = m.group(1)
            key = name.casefold()
            if key in replacements:
                continue

            if key in self._statics:
                replacements[key] = self._statics[key]
                continue
            provider = self._providers.get(key)
            if provider is not None:
                resolved = await provider(name, ct)
                replacements[key] = resolved if resolved is not None else self._default_missing
                continue
            replacements[key] = self._default_missing

        def _sub(match: "re.Match[str]") -> str:
            return replacements[match.group(1).casefold()]

        return _VARIABLE_PATTERN.sub(_sub, template)
