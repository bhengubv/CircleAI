# yaml_frontmatter.py
#
# Port of CircleAI.Knowledge YamlFrontmatter.cs (C# — the EXACT spec).
#
# Minimal YAML frontmatter parser/writer. Only flat key-value pairs are
# supported; nested structures, flow-style, anchors and lists are rejected — the
# frontmatter is metadata, not a general YAML surface. This keeps the on-disk
# format predictable across language ports.
#
# ``write(frontmatter, body)`` renders ``---\nkey: value\n---\n(body)``;
# ``read(text)`` parses it back. Malformed input raises ValueError (mirroring the
# C# FormatException). Internal module (the C# type is ``internal static``).

from __future__ import annotations

from typing import Dict, Tuple

_DELIMITER = "---"


def _validate_key(key: str) -> None:
    if key is None or key.strip() == "":
        raise ValueError("YAML key cannot be empty.")
    for ch in key:
        if not (ch.isalnum() or ch in ("_", "-", ".")):
            raise ValueError(f"Invalid character '{ch}' in YAML key '{key}'.")


def _encode_value(value: str) -> str:
    if value is None:
        return ""
    if len(value) == 0:
        return '""'
    needs_quoting = False
    for ch in value:
        if ch in (":", "#", "\n", "\r", "\t", '"', "\\", "'", "{", "["):
            needs_quoting = True
            break
    if not needs_quoting and (value[0] == " " or value[-1] == " "):
        needs_quoting = True
    if not needs_quoting:
        return value
    out = ['"']
    for ch in value:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def _decode_value(raw: str) -> str:
    if len(raw) == 0:
        return ""
    # Strip a single trailing inline comment on unquoted values only.
    if raw[0] != '"' and raw[0] != "'":
        hash_idx = raw.find(" #")
        if hash_idx >= 0:
            raw = raw[:hash_idx].rstrip()
        return raw
    if raw[0] == "'":
        raise ValueError("Single-quoted YAML scalars are not supported.")
    if len(raw) < 2 or raw[-1] != '"':
        raise ValueError("Unterminated double-quoted YAML scalar.")
    inner = raw[1:-1]
    out = []
    i = 0
    while i < len(inner):
        ch = inner[i]
        if ch != "\\":
            out.append(ch)
            i += 1
            continue
        if i + 1 >= len(inner):
            raise ValueError("Trailing backslash in YAML scalar.")
        i += 1
        nxt = inner[i]
        if nxt == "\\":
            out.append("\\")
        elif nxt == '"':
            out.append('"')
        elif nxt == "n":
            out.append("\n")
        elif nxt == "r":
            out.append("\r")
        elif nxt == "t":
            out.append("\t")
        else:
            raise ValueError(f"Unsupported YAML escape '\\{nxt}'.")
        i += 1
    return "".join(out)


def write(frontmatter: Dict[str, str], body: str) -> str:
    """Render ``frontmatter`` into a YAML block followed by ``body``. Mirrors
    ``YamlFrontmatter.Write``."""
    if frontmatter is None:
        raise ValueError("frontmatter")
    if body is None:
        raise ValueError("body")
    parts = [_DELIMITER, "\n"]
    for key, value in frontmatter.items():
        _validate_key(key)
        parts.append(key)
        parts.append(": ")
        parts.append(_encode_value(value))
        parts.append("\n")
    parts.append(_DELIMITER)
    parts.append("\n")
    parts.append(body)
    return "".join(parts)


def read(text: str) -> Tuple[Dict[str, str], str]:
    """Parse ``text`` into a (frontmatter, body) pair. Mirrors
    ``YamlFrontmatter.Read``."""
    if text is None:
        raise ValueError("text")

    text = text.replace("\r\n", "\n").replace("\r", "\n")

    if not text.startswith(_DELIMITER + "\n"):
        raise ValueError("Frontmatter must start with '---' on its own line.")

    search_start = len(_DELIMITER) + 1
    closing_token = "\n" + _DELIMITER + "\n"
    closing_idx = text.find(closing_token, search_start)
    if closing_idx < 0:
        raise ValueError("Missing closing '---' line for frontmatter block.")

    yaml = text[search_start:closing_idx]
    body = text[closing_idx + len(closing_token):]

    d: Dict[str, str] = {}
    for raw_line in yaml.split("\n"):
        if raw_line is None or raw_line.strip() == "":
            continue
        if raw_line[0] == " " or raw_line[0] == "\t":
            raise ValueError("Nested YAML is not supported.")
        if raw_line.startswith("- "):
            raise ValueError("YAML lists are not supported.")
        colon = raw_line.find(":")
        if colon <= 0:
            raise ValueError(f"Malformed YAML line: '{raw_line}'.")
        key = raw_line[:colon].strip()
        rest = raw_line[colon + 1:].lstrip() if colon + 1 < len(raw_line) else ""
        _validate_key(key)
        if rest.startswith("{") or rest.startswith("["):
            raise ValueError("Flow-style YAML structures are not supported.")
        d[key] = _decode_value(rest)

    return d, body
