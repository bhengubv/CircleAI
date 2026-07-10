"""Generative UI — port of CircleAI.Hosting.GenerativeUI.

(2.0.2) The AI emits JSON constrained to a typed component catalog; the host
renders it. Pattern adopted from bhengubv/json-render (no npm dependency — just
the "typed catalog + strict parse" contract).

Ports:
  * record ``UiComponent`` (recursive),
  * record ``UiCatalogEntry``,
  * ``UiCatalogs.default`` (the built-in card/list/button/textBlock/image set),
  * interface ``IGenerativeUIRenderer`` + ``RecordingGenerativeUIRenderer``,
  * ``JsonRenderParser`` (strict JSON→UiComponent parser + prompt describer).
"""
from __future__ import annotations

import json as _json
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any, Dict, List, Mapping, Optional, Sequence

__all__ = [
    "UiComponent",
    "UiCatalogEntry",
    "UiCatalogs",
    "IGenerativeUIRenderer",
    "RecordingGenerativeUIRenderer",
    "JsonRenderParser",
]


@dataclass(frozen=True, slots=True)
class UiComponent:
    """One UI element produced by a generative-UI model. Mirrors ``UiComponent``.
    ``children`` is ``None`` when the component has no nested components.
    """

    kind: str
    properties: Mapping[str, Any]
    children: Optional[Sequence["UiComponent"]] = None


@dataclass(frozen=True, slots=True)
class UiCatalogEntry:
    """Catalog entry — declares an allowed kind + its properties. Mirrors
    ``UiCatalogEntry``.
    """

    kind: str
    description: str
    allowed_properties: Mapping[str, str]
    allows_children: bool = False


class UiCatalogs:
    """Pre-canned component catalogs. Mirrors ``UiCatalogs``."""

    default: List[UiCatalogEntry] = [
        UiCatalogEntry(
            "card",
            "A bordered container with a title and body. May contain children.",
            {"title": "string", "caption": "string?"},
            allows_children=True,
        ),
        UiCatalogEntry(
            "list",
            "An ordered or unordered list. Children are the list items.",
            {"ordered": "boolean"},
            allows_children=True,
        ),
        UiCatalogEntry(
            "button",
            "A tappable button. Emit an action identifier when clicked.",
            {"label": "string", "action": "string", "style": "string?"},
        ),
        UiCatalogEntry(
            "textBlock",
            "Inline text content, optionally markdown.",
            {"text": "string", "markdown": "boolean?"},
        ),
        UiCatalogEntry(
            "image",
            "An image displayed from a URL or data-URI.",
            {"src": "string", "alt": "string?"},
        ),
    ]


class IGenerativeUIRenderer(ABC):
    """(2.0.2) Renderer contract — hosts materialise :class:`UiComponent`
    records into native UI. Mirrors ``IGenerativeUIRenderer``.
    """

    @abstractmethod
    async def render_async(self, root: UiComponent, ct: object = None) -> None:
        """Render a single root component."""
        ...


class RecordingGenerativeUIRenderer(IGenerativeUIRenderer):
    """Default no-op renderer for tests / headless server scenarios. Holds the
    last rendered component for assertion. Mirrors ``RecordingGenerativeUIRenderer``.
    """

    __slots__ = ("last_rendered", "render_count")

    def __init__(self) -> None:
        self.last_rendered: Optional[UiComponent] = None
        self.render_count = 0

    async def render_async(self, root: UiComponent, ct: object = None) -> None:
        self.last_rendered = root
        self.render_count += 1


class JsonRenderParser:
    """(2.0.2) Strict JSON→:class:`UiComponent` parser. Rejects any kind not in
    the catalog and any property not declared on its kind. Mirrors
    ``JsonRenderParser``.
    """

    @staticmethod
    def parse(
        json_text: str,
        catalog: Sequence[UiCatalogEntry],
        strict: bool = True,
    ) -> UiComponent:
        """Parse one JSON document into a :class:`UiComponent` tree. Mirrors
        ``Parse``. Raises ``ValueError`` on empty input and validation errors.
        """
        if json_text is None or json_text == "":
            raise ValueError("json is required")
        if catalog is None:
            raise ValueError("catalog is required")

        try:
            root = _json.loads(json_text)
        except _json.JSONDecodeError as ex:
            raise ValueError(f"Invalid JSON: {ex}") from ex

        index = {c.kind.lower(): c for c in catalog}
        return JsonRenderParser._parse_element(root, index, strict)

    @staticmethod
    def _parse_element(
        el: Any,
        catalog: Dict[str, UiCatalogEntry],
        strict: bool,
    ) -> UiComponent:
        if not isinstance(el, dict):
            raise ValueError(f"Expected JSON object, got {type(el).__name__}.")

        kind = el.get("kind")
        if not isinstance(kind, str) or kind == "":
            raise ValueError("Component missing required 'kind' field.")

        entry = catalog.get(kind.lower())
        if entry is None:
            if strict:
                raise ValueError(f"Unknown component kind '{kind}'.")
            return UiComponent(
                kind="textBlock",
                properties={
                    "text": f"[unknown kind '{kind}']",
                    "markdown": False,
                },
            )

        props: Dict[str, Any] = {}
        props_el = el.get("properties")
        if isinstance(props_el, dict):
            for p_name, p_val in props_el.items():
                if strict and p_name not in entry.allowed_properties:
                    raise ValueError(
                        f"Component '{kind}' does not allow property '{p_name}'."
                    )
                props[p_name] = _to_managed(p_val)

        children: Optional[List[UiComponent]] = None
        child_el = el.get("children")
        if isinstance(child_el, list):
            if not entry.allows_children:
                if strict:
                    raise ValueError(f"Component '{kind}' does not allow children.")
            else:
                children = [
                    JsonRenderParser._parse_element(c, catalog, strict) for c in child_el
                ]

        return UiComponent(kind, props, children)

    @staticmethod
    def describe_catalog_for_prompt(catalog: Sequence[UiCatalogEntry]) -> str:
        """Build a system-prompt snippet describing the catalog to the model.
        Mirrors ``DescribeCatalogForPrompt`` line-for-line (each line ends in
        ``\\n`` as ``AppendLine`` produces).
        """
        if catalog is None:
            raise ValueError("catalog is required")
        lines: List[str] = []
        lines.append(
            "You may respond with a single JSON object describing one UI component."
        )
        lines.append(
            'Allowed shape: { "kind": string, "properties": { ... }, "children"?: [ ... ] }'
        )
        lines.append("")
        lines.append("Allowed kinds:")
        for e in catalog:
            lines.append(f"- {e.kind} — {e.description}")
            for name, type_str in e.allowed_properties.items():
                lines.append(f"    - {name}: {type_str}")
            if e.allows_children:
                lines.append("    - children: array of components")
        # AppendLine adds a trailing newline after every line.
        return "".join(line + "\n" for line in lines)


def _to_managed(v: Any) -> Any:
    """Convert a parsed-JSON value to the managed shape the C# ``ToManaged``
    produces: strings stay strings, whole numbers become ints (C# int64),
    fractional numbers become floats, bools/None pass through, arrays/objects
    recurse.
    """
    if isinstance(v, bool):
        return v
    if v is None:
        return None
    if isinstance(v, int):
        return v
    if isinstance(v, float):
        # C#: TryGetInt64 first, else GetDouble. json already gives int for
        # whole numbers, so a float here is genuinely fractional. But mirror the
        # "integral double -> int64" behaviour for values like 3.0.
        if v.is_integer():
            return int(v)
        return v
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        return [_to_managed(x) for x in v]
    if isinstance(v, dict):
        return {k: _to_managed(val) for k, val in v.items()}
    return None
