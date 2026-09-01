#!/usr/bin/env python3
"""Generates the Rust vertical domain contexts and companion adapters.

ONE TABLE, NOW THREE LANGUAGES. The Python and TypeScript generators read the
same `DOMAINS` map; this one imports it rather than restating it, so a refusal
reworded once is reworded everywhere. A copy of the table here would drift, and
drift in a refusal is the kind that matters - one port declining to give tax
advice while another quietly does.

GENERATED BECAUSE THEY ARE THE SAME SHAPE, not because they are unimportant.
What is per-vertical and real lives in the table: the topics, and the one thing
each must refuse however it is asked, in its own words.
"""
from __future__ import annotations

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "python", "tools"))

from gen_domains import DOMAINS  # noqa: E402  (path is set above)

OUT_DIR = os.path.join(ROOT, "rust", "src", "domains")


def to_snake(name: str) -> str:
    """`HRDomainContext` -> `hr_domain_context`, acronym runs kept together."""
    s = re.sub(r"(.)([A-Z][a-z]+)", r"\1_\2", name)
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", s)
    return s.lower()


def type_prefix(name: str) -> str:
    """The C# type prefix for a vertical: `Commerce.Finance` -> `CommerceFinance`."""
    return "".join(part for part in name.split("."))


def rust_str(value: str) -> str:
    """A Rust string literal.

    NOT `repr()`. Python's repr prefers SINGLE quotes, which Rust reads as a
    character literal — `'making a device usable'` is a parse error, not a
    string. Every generated constant carried one until the crate was first
    compiled.
    """
    escaped = value
    for raw, rendered in (
        (chr(92), chr(92) * 2),
        (chr(34), chr(92) + chr(34)),
        (chr(10), chr(92) + 'n'),
        (chr(13), chr(92) + 'r'),
        (chr(9), chr(92) + 't'),
    ):
        escaped = escaped.replace(raw, rendered)
    return chr(34) + escaped + chr(34)


def wrap(text: str, indent: str = "/// ", width: int = 76) -> str:
    words, line, lines = text.split(), "", []
    for w in words:
        if len(line) + len(w) + 1 > width - len(indent):
            lines.append(line)
            line = w
        else:
            line = (line + " " + w).strip()
    if line:
        lines.append(line)
    return "\n".join(indent + l for l in lines)


TEMPLATE = '''//! {title} - what this vertical knows and what it will not do.
//!
//! GENERATED from the shared domain table. The refusal below is this vertical's
//! own, in its own words, and is the reason the table exists rather than a
//! single generic decline.

use std::collections::HashMap;

{context_doc}
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct {prefix}DomainContext {{
    /// What the person is working on right now, in their words.
    pub focus: String,
    /// Facts this vertical has been given for this conversation. Held HERE and
    /// not in a model prompt, so what was supplied can be shown back and
    /// cleared.
    pub facts: HashMap<String, String>,
    /// The language to answer in. Empty means the device's.
    pub language: String,
}}

impl {prefix}DomainContext {{
    /// What this vertical is for.
    pub const PURPOSE: &'static str = {purpose};

    /// What it will speak to. A topic list rather than a classifier, because a
    /// list can be read by the person it applies to.
    pub const TOPICS: &'static [&'static str] = &[{topics}];

    /// The one thing it will NOT do, however it is asked.
    pub const REFUSES: &'static str = {refuses};

    /// Why - in words for the person asking, not a policy identifier.
    pub const REFUSAL: &'static str = {refusal};

    pub fn new() -> Self {{
        Self::default()
    }}

    pub fn with_fact(mut self, key: &str, value: &str) -> Self {{
        self.facts.insert(key.to_string(), value.to_string());
        self
    }}

    /// Whether a request is in scope. Matched against the topic words, so an
    /// unrelated question is not answered by this vertical with false
    /// confidence.
    pub fn covers(&self, request: &str) -> bool {{
        let request = request.to_lowercase();
        Self::TOPICS.iter().any(|t| request.contains(&t.to_lowercase()))
    }}

    /// Whether this is the thing it refuses.
    ///
    /// Matched on the ACTION words rather than the whole phrase - somebody does
    /// not ask in the wording of a policy, and a refusal that only triggers on
    /// an exact phrase is a refusal that never triggers.
    pub fn is_refused(&self, request: &str) -> bool {{
        let request = request.to_lowercase();
        Self::REFUSES
            .split_whitespace()
            .filter(|w| w.len() > 3)
            .all(|w| request.contains(&w.to_lowercase()))
    }}

    /// Everything it has been told, for showing back.
    pub fn describe(&self) -> String {{
        if self.facts.is_empty() {{
            return format!("{{}} - nothing supplied yet", Self::PURPOSE);
        }}
        let mut keys: Vec<&String> = self.facts.keys().collect();
        keys.sort();
        format!(
            "{{}} - {{}}",
            Self::PURPOSE,
            keys.iter()
                .map(|k| format!("{{k}}: {{}}", self.facts[*k]))
                .collect::<Vec<_>>()
                .join(", ")
        )
    }}

    /// Forgets everything supplied. What a "clear" control calls.
    pub fn clear(&mut self) {{
        self.facts.clear();
        self.focus.clear();
    }}
}}

{adapter_doc}
pub struct {prefix}CompanionAdapter {{
    context: {prefix}DomainContext,
    answer: Option<Box<dyn Fn(&str, &{prefix}DomainContext) -> String + Send + Sync>>,
}}

impl {prefix}CompanionAdapter {{
    pub fn new(
        context: {prefix}DomainContext,
        answer: Option<Box<dyn Fn(&str, &{prefix}DomainContext) -> String + Send + Sync>>,
    ) -> Self {{
        Self {{ context, answer }}
    }}

    pub fn context(&self) -> &{prefix}DomainContext {{
        &self.context
    }}

    pub fn context_mut(&mut self) -> &mut {prefix}DomainContext {{
        &mut self.context
    }}

    pub fn is_available(&self) -> bool {{
        self.answer.is_some()
    }}

    /// The refusal is checked BEFORE the model sees the request.
    ///
    /// Checking afterwards means the model has already produced the thing that
    /// should not have been produced, and the only remaining option is to hide
    /// it - which is not the same as not doing it.
    pub fn handle(&self, request: &str) -> String {{
        if self.context.is_refused(request) {{
            return format!(
                "I will not {{}} - {{}}.",
                {prefix}DomainContext::REFUSES,
                {prefix}DomainContext::REFUSAL
            );
        }}
        match &self.answer {{
            Some(answer) => answer(request, &self.context),
            None => format!(
                "{{}} is not set up on this device yet.",
                {prefix}DomainContext::PURPOSE
            ),
        }}
    }}
}}

impl std::fmt::Debug for {prefix}CompanionAdapter {{
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {{
        f.debug_struct("{prefix}CompanionAdapter")
            .field("purpose", &{prefix}DomainContext::PURPOSE)
            .field("available", &self.is_available())
            .finish()
    }}
}}
'''


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    modules = []
    for name, (suffix, purpose, topics, (refuses, refusal)) in sorted(DOMAINS.items()):
        prefix = type_prefix(name)
        body = TEMPLATE.format(
            title=name,
            prefix=prefix,
            purpose=rust_str(purpose),
            refuses=rust_str(refuses),
            refusal=rust_str(refusal),
            topics=", ".join(rust_str(t) for t in topics),
            context_doc=wrap(
                "What the %s vertical is working with. Held on the device, "
                "shown back on request, and cleared when asked." % purpose
            ),
            adapter_doc=wrap(
                "The %s companion. Answers within its topics and refuses the "
                "one thing it must, before anything else runs." % purpose
            ),
        )
        path = os.path.join(OUT_DIR, suffix + ".rs")
        io.open(path, "w", encoding="utf-8", newline="\n").write(body)
        modules.append(suffix)

    index = [
        "//! The verticals.",
        "//!",
        "//! GENERATED by `rust/tools/gen_domains_rs.py` from the shared domain",
        "//! table in `python/tools/gen_domains.py` - one table, three languages,",
        "//! so a refusal reworded once is reworded everywhere.",
        "",
    ]
    index += ["pub mod %s;" % m for m in modules]
    io.open(
        os.path.join(OUT_DIR, "mod.rs"), "w", encoding="utf-8", newline="\n"
    ).write("\n".join(index) + "\n")
    print("wrote %d domain modules" % len(modules))


if __name__ == "__main__":
    main()
