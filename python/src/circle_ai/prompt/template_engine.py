"""Prompt template engine — port of CircleAI.Inference.PromptTemplateEngine.

Renders chat history through each model's own chat_template (a Jinja2
string published in tokenizer_config.json alongside the model bundle).
Falls back to the canonical Qwen / ChatML format when no template is
configured. New model family on ModelScope → ZERO Python code needed.

Optional dependency: `jinja2`. When absent, the engine still loads but
the .render() method raises ImportError with a clear hint to install.
Install with:    pip install circle-ai-sdk[prompt]
"""
from __future__ import annotations

import json
import threading
from pathlib import Path
from typing import Optional, Protocol, runtime_checkable

from ..models.models import ChatMessage

try:
    import jinja2 as _jinja2  # type: ignore
except ImportError:  # pragma: no cover - exercised via test env
    _jinja2 = None  # type: ignore


FALLBACK_CHAT_TEMPLATE = (
    "{%- for message in messages -%}\n"
    "<|im_start|>{{ message.role }}\n"
    "{{ message.content }}<|im_end|>\n"
    "{% endfor -%}\n"
    "{%- if add_generation_prompt -%}\n"
    "<|im_start|>assistant\n"
    "{%- endif -%}\n"
)


@runtime_checkable
class IPromptTemplateEngine(Protocol):
    """Render chat messages into the prompt string a model expects."""

    def render(
        self,
        model_directory: str,
        messages: list[ChatMessage],
        add_generation_prompt: bool = True,
    ) -> str:
        ...


class PromptTemplateEngine:
    """Default IPromptTemplateEngine backed by Jinja2.

    Caches compiled templates per model_directory so repeat renders are
    allocation-light.
    """

    def __init__(self) -> None:
        if _jinja2 is None:
            self._env: Optional[object] = None
        else:
            self._env = _jinja2.Environment(
                loader=_jinja2.BaseLoader(),
                autoescape=False,
                undefined=_jinja2.StrictUndefined,
                trim_blocks=False,
                lstrip_blocks=False,
            )
        self._cache: dict[str, tuple[object, dict]] = {}
        self._cache_lock = threading.Lock()

    def render(
        self,
        model_directory: str,
        messages: list[ChatMessage],
        add_generation_prompt: bool = True,
    ) -> str:
        if _jinja2 is None or self._env is None:
            raise ImportError(
                "circle_ai.prompt.PromptTemplateEngine requires `jinja2`. "
                "Install via `pip install circle-ai-sdk[prompt]` or "
                "`pip install jinja2`."
            )
        if not model_directory:
            raise ValueError("model_directory is required")

        template, _cfg = self._get_template(model_directory)
        ctx = {
            "messages": [
                {"role": _normalise_role(m.role), "content": m.content or ""}
                for m in messages
            ],
            "add_generation_prompt": add_generation_prompt,
        }
        return template.render(**ctx)

    def _get_template(self, model_directory: str):
        with self._cache_lock:
            if model_directory in self._cache:
                return self._cache[model_directory]

            cfg = _load_tokenizer_config(model_directory)
            tmpl_src = cfg.get("chat_template") or FALLBACK_CHAT_TEMPLATE
            try:
                template = self._env.from_string(tmpl_src)  # type: ignore[union-attr]
            except Exception:
                # Malformed template — fall back to canonical ChatML rather
                # than raising. Matches the C# parser-error path.
                template = self._env.from_string(  # type: ignore[union-attr]
                    FALLBACK_CHAT_TEMPLATE
                )

            self._cache[model_directory] = (template, cfg)
            return template, cfg


def _normalise_role(role: str) -> str:
    """Remap our role tags onto the Jinja2 vocabulary the template expects.

    role:"tool" / "function" → "user" with the template still seeing the
    content (per directive P3 item 15 in the C# port).
    """
    if not role or not role.strip():
        return "user"
    norm = role.strip().lower()
    if norm in ("tool", "function"):
        return "user"
    return norm


def _load_tokenizer_config(model_directory: str) -> dict:
    path = Path(model_directory) / "tokenizer_config.json"
    if not path.exists():
        return {}
    try:
        with path.open(encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError):
        return {}
