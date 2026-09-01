"""Generates c/src/domain_context.c from the extracted C# domain contexts.

The DATA is generated because there are forty-four of them and every one is a
verbatim string from the reference; hand-copying them is how a compliance flag
goes missing. The BEHAVIOUR - the struct, the lookup, the enrich - is written by
hand in domain_context.h/.c and is not touched by this.
"""
import json
import re
import sys


def snake(name):
    out, cur = [], ""
    for i, c in enumerate(name):
        if c.isupper() and cur and (not name[i - 1].isupper() or
                                    (i + 1 < len(name) and name[i + 1].islower())):
            out.append(cur.lower())
            cur = c
        else:
            cur += c
    if cur:
        out.append(cur.lower())
    return "_".join(out)


def cstr(s):
    """A C string literal. The snippets contain quotes, slashes and non-ASCII."""
    out = []
    for ch in s:
        if ch == '"':
            out.append('\\"')
        elif ch == '\\':
            out.append('\\\\')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\t':
            out.append('\\t')
        elif ord(ch) < 0x20:
            out.append('\\%03o' % ord(ch))
        else:
            out.append(ch)
    return '"' + "".join(out) + '"'


def wrap(literal, indent, width=92):
    """Splits one long literal across lines; adjacent C literals concatenate."""
    body = literal[1:-1]
    parts, cur = [], ""
    i = 0
    while i < len(body):
        # Never split an escape sequence in half.
        take = 2 if body[i] == '\\' else 1
        if body[i] == '\\' and i + 1 < len(body) and body[i + 1] == '0':
            take = 4
        chunk = body[i:i + take]
        if len(cur) + len(chunk) > width and cur:
            parts.append(cur)
            cur = ""
        cur += chunk
        i += take
    if cur:
        parts.append(cur)
    if len(parts) == 1:
        return '"' + parts[0] + '"'
    joiner = "\n" + " " * indent
    return joiner.join('"' + p + '"' for p in parts)


domains = json.load(open(sys.argv[1], encoding="utf-8"))

L = []
w = L.append

w("/*")
w(" * domain_context.c - the forty-four domain contexts, verbatim.")
w(" *")
w(" * THE DATA IS GENERATED AND THE BEHAVIOUR IS NOT. Every string below is a")
w(" * verbatim copy of the C# reference; there are forty-four of them and")
w(" * hand-copying is how a compliance flag goes missing. The struct, the lookup")
w(" * and the enrich live in domain_context.h and the hand-written half of this")
w(" * file, and are the part worth reading.")
w(" *")
w(" * EVERYTHING HERE IS STATIC AND CONST. A domain context is a fact about a")
w(" * domain, not state: it is the same on every device and for every session, so")
w(" * it needs no allocation, cannot be freed by mistake, and costs nothing to")
w(" * hand out a pointer to.")
w(" */")
w("")
w('#include "circle_ai/domain_context.h"')
w("")
w("#include <stdlib.h>")
w("#include <string.h>")
w("")

for d in domains:
    key = snake(d["name"])
    if d["flags"]:
        w("static const char *const %s_flags[] = {" % key)
        for f in d["flags"]:
            w("    %s," % cstr(f))
        w("};")
    if d["tools"]:
        w("static const char *const %s_tools[] = {" % key)
        for t in d["tools"]:
            w("    %s," % cstr(t))
        w("};")
    w("")

w("static const ca_domain_context_t g_domains[] = {")
for d in domains:
    key = snake(d["name"])
    w("    {")
    w('        .domain = %s,' % cstr(key))
    w("        .system_prompt_snippet =")
    w("            " + wrap(cstr(d["snippet"]), 12) + ",")
    w("        .compliance_flags = %s_flags," % key)
    w("        .compliance_flag_count = sizeof(%s_flags) / sizeof(%s_flags[0])," % (key, key))
    w("        .suggested_tools = %s_tools," % key)
    w("        .suggested_tool_count = sizeof(%s_tools) / sizeof(%s_tools[0])," % (key, key))
    w("    },")
w("};")
w("")
w("static const size_t g_domain_count = sizeof(g_domains) / sizeof(g_domains[0]);")
w("")

w("size_t ca_domain_context_count(void) { return g_domain_count; }")
w("")
w("const ca_domain_context_t *ca_domain_context_at(size_t index) {")
w("    return index < g_domain_count ? &g_domains[index] : NULL;")
w("}")
w("")
w("const ca_domain_context_t *ca_domain_context_find(const char *domain) {")
w("    if (!domain) return NULL;")
w("    for (size_t i = 0; i < g_domain_count; i++) {")
w("        if (strcmp(g_domains[i].domain, domain) == 0) return &g_domains[i];")
w("    }")
w("    return NULL;")
w("}")
w("")

# Named accessors, so a caller that knows which domain it wants pays no lookup
# and gets a compile error rather than a NULL if it misspells one.
for i, d in enumerate(domains):
    key = snake(d["name"])
    w("const ca_domain_context_t *ca_%s_domain_context(void) { return &g_domains[%d]; }" % (key, i))
w("")

print("\n".join(L))
