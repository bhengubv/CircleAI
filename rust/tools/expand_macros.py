#!/usr/bin/env python3
"""Expands single-rule `macro_rules!` in a Rust file into written-out code.

WHY THIS EXISTS. A macro-generated struct never appears under its own name, so
the parity ruler cannot see it - and a measure that cannot see the code reports
the work as missing. The alternative would be teaching the ruler about this
codebase's private macros, which is tuning the measure to the code and makes it
stop meaning what it says.

So the macro stays as the way the code was WRITTEN - one shape, stated once -
and this expands it before the code is committed. Substitution is textual:
`$name` becomes the argument everywhere it appears. `stringify!` and `concat!`
are left alone because they are ordinary Rust macros and work outside a
`macro_rules!` body exactly as they do inside one.

Handles only what this codebase writes: one rule per macro, `$x:ident`,
`$x:expr` and `$x:ty` parameters, no repetition operators. Anything else is
refused loudly rather than mangled quietly.
"""
from __future__ import annotations

import io
import re
import sys


def balanced(text: str, start: int, opener: str, closer: str) -> int:
    """Index just past the delimiter matching the one at `start`."""
    depth, i, in_str, in_char = 0, start, False, False
    while i < len(text):
        c = text[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
        elif in_char:
            if c == "\\":
                i += 2
                continue
            if c == "'":
                in_char = False
        elif c == '"':
            in_str = True
        elif c == "'" and i + 2 < len(text) and text[i + 2] == "'":
            # A char literal. A lifetime (`&'static`) has no closing quote and
            # must not start one, which is why this looks ahead.
            in_char = True
        elif c == opener:
            depth += 1
        elif c == closer:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    raise ValueError("unbalanced %s at %d" % (opener, start))


def split_args(text: str) -> list[str]:
    """Top-level comma-separated arguments of a macro call."""
    args, cur, depth, i = [], "", 0, 0
    in_str = False
    while i < len(text):
        c = text[i]
        if in_str:
            cur += c
            if c == "\\":
                cur += text[i + 1]
                i += 2
                continue
            if c == '"':
                in_str = False
        elif c == '"':
            in_str = True
            cur += c
        elif c in "([{":
            depth += 1
            cur += c
        elif c in ")]}":
            depth -= 1
            cur += c
        elif c == "," and depth == 0:
            args.append(cur.strip())
            cur = ""
        else:
            cur += c
        i += 1
    if cur.strip():
        args.append(cur.strip())
    return args


def expand(path: str) -> int:
    src = io.open(path, encoding="utf-8").read()
    macros: dict[str, tuple[list[str], str]] = {}

    # Collect definitions, removing each from the source as it is read.
    while True:
        m = re.search(r"macro_rules! (\w+) \{", src)
        if not m:
            break
        name = m.group(1)
        body_start = src.index("{", m.end() - 1)
        body_end = balanced(src, body_start, "{", "}")
        body = src[body_start + 1 : body_end - 1]

        rule = re.search(r"\(", body)
        if not rule:
            raise ValueError("no rule in macro %s" % name)
        params_end = balanced(body, rule.start(), "(", ")")
        params_text = body[rule.start() + 1 : params_end - 1]
        params = re.findall(r"\$(\w+)\s*:\s*(?:ident|expr|ty|literal)", params_text)
        if params_text.count("$") != len(params):
            raise ValueError("macro %s uses a fragment kind this cannot expand" % name)

        arrow = body.index("=>", params_end)
        tmpl_start = body.index("{", arrow)
        tmpl_end = balanced(body, tmpl_start, "{", "}")
        template = body[tmpl_start + 1 : tmpl_end - 1]

        macros[name] = (params, template)
        # Keep a note in place of the definition so the file still says how it
        # was written and why the expansion below exists.
        src = (
            src[: m.start()]
            + "// `%s` was written once as a macro over the table below and\n"
            "// expanded here, so each type appears under its own name.\n" % name
            + src[body_end:]
        )

    if not macros:
        return 0

    expanded = 0
    for name, (params, template) in macros.items():
        while True:
            call = re.search(re.escape(name) + r"!\(", src)
            if not call:
                break
            end = balanced(src, call.end() - 1, "(", ")")
            args = split_args(src[call.end() : end - 1])
            if len(args) != len(params):
                raise ValueError(
                    "%s takes %d arguments, given %d" % (name, len(params), len(args))
                )
            body = template
            # Longest parameter names first, so `$name` does not eat `$name2`.
            for param, arg in sorted(
                zip(params, args), key=lambda p: -len(p[0])
            ):
                body = body.replace("$" + param, arg)
            if "$" in re.sub(r"\$crate", "", body):
                raise ValueError("unsubstituted parameter left in %s" % name)
            tail = end
            while tail < len(src) and src[tail] in ";\n":
                tail += 1
                if src[tail - 1] == ";":
                    break
            src = src[: call.start()] + body.strip() + "\n" + src[tail:]
            expanded += 1

    io.open(path, "w", encoding="utf-8", newline="\n").write(src)
    return expanded


if __name__ == "__main__":
    for target in sys.argv[1:]:
        print("%s: expanded %d" % (target, expand(target)))
