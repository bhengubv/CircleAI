import re, os, glob, sys

# Every public name the TypeScript port declares.
#
# class, interface, type alias, enum, const, function and namespace — all seven,
# because a C# type has no single TypeScript spelling. A record became an
# interface or a type alias; a static class became a namespace, a const object,
# or a module of exported functions; an enum became an enum or a union type. A
# scan that only looked for `class` would report almost the whole port missing
# while it sits there.
#
# Both exported and top-level declarations are counted. A type that is internal
# to the port is still a type the port HAS, and the question this measures is
# whether the concept was ported — not whether it was re-exported.
types = set()
decl = re.compile(
    r"^\s*(?:export\s+)?(?:declare\s+)?(?:default\s+)?"
    r"(?:abstract\s+)?"
    r"(?:class|interface|enum|namespace|module|function|const|let|var|type)\s+"
    r"([A-Za-z_$][A-Za-z0-9_$]*)"
)
for root, dirs, files in os.walk("typescript/src"):
    dirs[:] = [d for d in dirs if d not in ("node_modules", "dist", "build", ".git")]
    for fn in files:
        # .d.ts is GENERATED from the .ts beside it. Counting both would not
        # change the answer, but a port whose only declaration of a type is in a
        # generated file has not ported it — so the generated ones are skipped
        # and the source has to say it.
        if not fn.endswith(".ts") or fn.endswith(".d.ts"):
            continue
        for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
            m = decl.match(line)
            if m:
                types.add(m.group(1))
low = {t.lower() for t in types}


cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")


def candidates(t, module):
    """Every name this type could legitimately have taken in the TypeScript port.

    What varies, and why each is accepted:

      the I prefix   TypeScript's own convention DROPS it — `IFarmBoard` becomes
                     `FarmBoard` — and that is what most of this port did. Both
                     are accepted rather than picking a winner here, because
                     settling on one across 166 modules is a rename to do
                     deliberately, not a side effect of measuring.

      camelCase      a C# static class that became a module of free functions has
                     its members in camelCase, and the class itself may survive
                     only as a camelCase const object. `DtmfToneGenerator` and
                     `dtmfToneGenerator` are the same thing here.

      EventArgs      TypeScript has no events. Every one became a plain payload
                     interface, sometimes keeping "Event" and sometimes dropping
                     the suffix because the name already reads as a noun.

      Exception      a C# exception becomes an Error subclass, usually taking
                     `Error` in place of `Exception` — which is the TypeScript
                     convention rather than a rename.

      Options/Args   a C# options record often arrives as a plain parameter
                     interface with `Options` intact, and sometimes as the bare
                     noun. Both are tried.
    """
    mod = module.replace(".", "")
    head = module.split(".")[0]

    bases = {t}
    if t.startswith("I") and len(t) > 1 and t[1].isupper():
        bases.add(t[1:])

    for b in list(bases):
        if b.endswith("EventArgs"):
            bases.add(b[: -len("EventArgs")] + "Event")
            bases.add(b[: -len("EventArgs")])
        if b.endswith("Args") and not b.endswith("EventArgs"):
            bases.add(b[: -len("Args")])
        if b.endswith("Exception"):
            stem = b[: -len("Exception")]
            bases.add(stem + "Error")
            bases.add(stem)
        # A C# `XxxServiceCollectionExtensions` has no DI container to extend
        # here; it arrives as a registration module or a `registerXxx` function.
        if b.endswith("ServiceCollectionExtensions"):
            stem = b[: -len("ServiceCollectionExtensions")]
            bases.add(stem + "Registration")
            bases.add(stem + "Module")
            bases.add("register" + stem)
        if b.endswith("Extensions"):
            bases.add(b[: -len("Extensions")])

    # The product name rides on the package path here, so a type that spells
    # CircleAI out in C# drops it.
    for b in list(bases):
        if b[:8].lower() == "circleai" and len(b) > 8:
            bases.add(b[8:])

    out = set(bases)
    for prefix in {mod, head}:
        for b in bases:
            out.add(prefix + b)
            if b.startswith("I") and len(b) > 1 and b[1].isupper():
                out.add("I" + prefix + b[1:])
            else:
                out.add("I" + prefix + b)
    return {c for c in out if c}


def to_camel(name):
    """PascalCase to camelCase, leaving an all-caps acronym head intact.

    `IOOptions` lower-cased naively becomes `iOOptions`, which nothing is called.
    The rule that matches what people actually write: lower the leading run of
    capitals, but stop one short when a lowercase letter follows, so `IOReader`
    becomes `ioReader` and `Reader` becomes `reader`.
    """
    if not name:
        return name
    head = 0
    while head < len(name) and name[head].isupper():
        head += 1
    if head == 0:
        return name
    if head < len(name):
        head = max(1, head - 1)
    return name[:head].lower() + name[head:]


# ─────────────────────────────────────────────────────────────────────────────
# The exclusions file IS this measure's configuration.
#
# A type deliberately absent is written down in typescript/PARITY-EXCLUSIONS.md
# with a reason. Reading it here rather than hard-coding a list means there is
# exactly ONE place to see what was decided, and adding a line is a claim on the
# record rather than a way to make a number go up quietly.
def read_exclusions(path="typescript/PARITY-EXCLUSIONS.md"):
    excluded, renames = set(), {}
    if not os.path.exists(path):
        return excluded, renames

    block = None
    for line in open(path, encoding="utf-8"):
        stripped = line.strip()
        if stripped.startswith("```"):
            tag = stripped[3:].strip()
            block = tag if tag in ("excluded", "renames") else None
            continue
        if not block or not stripped:
            continue

        if block == "renames":
            if "=" not in stripped:
                continue
            key, value = (p.strip() for p in stripped.split("=", 1))
            renames[key] = value
        else:
            # A line with no module qualifier is ignored: an unqualified name
            # would excuse a same-named type in every other module too. A
            # trailing ".*" excludes a whole module, which is what a platform
            # head actually is.
            parts = stripped.split(None, 1)
            if "." in parts[0] or parts[0].endswith(".*"):
                excluded.add(parts[0])
    return excluded, renames


EXCLUDED, RENAMES = read_exclusions()
EXCLUDED_MODULES = {e[:-2] for e in EXCLUDED if e.endswith(".*")}

rows = []
for d in sorted(glob.glob("src/CircleAI.*/")):
    # normpath, not rstrip("/"): glob hands back a trailing os.sep, which is a
    # BACKSLASH on Windows. rstrip("/") leaves it, basename then returns "" and
    # every module is nameless — so every qualified exclusion and rename
    # silently stops matching and the same commit measures lower on Windows than
    # on macOS. A ruler that reads differently per platform is worse than none.
    mod = os.path.basename(os.path.normpath(d))[len("CircleAI."):]
    ts = {}
    for root, dirs, files in os.walk(d):
        # obj/ and bin/ are BUILD OUTPUT, not API. Scanning them counts the
        # Android resource designer's generated "Resource" class as a public
        # type every port is then failing to provide.
        dirs[:] = [x for x in dirs if x not in ("obj", "bin")]
        for fn in files:
            if fn.endswith(".cs"):
                for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
                    m = cs.match(line)
                    if m:
                        ts[m.group(1)] = os.path.relpath(os.path.join(root, fn), d)
    if not ts:
        continue

    def present(t):
        qualified = "%s.%s" % (mod, t)
        if mod in EXCLUDED_MODULES or qualified in EXCLUDED:
            return True
        renamed = RENAMES.get(qualified)
        if renamed and (renamed in types or renamed.lower() in low):
            return True
        for c in candidates(t, mod):
            if c in types or c.lower() in low or to_camel(c) in types:
                return True
        return False

    miss = sorted(t for t in ts if not present(t))
    rows.append((mod, len(ts) - len(miss), len(ts), miss, ts))

if "--full" in sys.argv:
    for mod, have, total, miss, ts in sorted(rows, key=lambda r: -len(r[3])):
        if not miss:
            continue
        print("=== %s: %d/%d missing %d" % (mod, have, total, len(miss)))
        byfile = {}
        for t in miss:
            byfile.setdefault(ts[t], []).append(t)
        for f in sorted(byfile):
            print("   %s: %s" % (f, ", ".join(sorted(byfile[f]))))
    print()

th = sum(r[1] for r in rows)
tt = sum(r[2] for r in rows)
print("declared exclusions honoured: %d, renames: %d  (typescript/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
