import re, os, glob, sys

# Every public name the HarmonyOS port declares.
#
# BOTH `.ets` AND `.ts` ARE SCANNED. ArkTS is TypeScript with the dynamic parts
# taken out, and the port is split by what each file needs: anything touching
# ArkUI or an `@ohos.*` API is `.ets` because only `.ets` may carry decorators
# and the UI DSL, and everything else is plain `.ts` so it can be type-checked
# and run by a normal TypeScript toolchain. A ruler that read only one extension
# would report the half it did not read as missing.
#
# `struct` is counted alongside `class`. In ArkTS a `struct` is not a value
# type - it is the declaration form of an ArkUI component, and a C# view model
# or page that arrived here arrived as one. Leaving it out would report every
# ported screen as absent.
#
# Both exported and top-level declarations are counted. A type internal to the
# port is still a type the port HAS, and the question this measures is whether
# the concept was ported - not whether it was re-exported.
types = set()
decl = re.compile(
    r"^\s*(?:export\s+)?(?:declare\s+)?(?:default\s+)?"
    r"(?:abstract\s+)?"
    r"(?:@\w+(?:\([^)]*\))?\s+)*"
    r"(?:class|struct|interface|enum|namespace|module|function|const|let|var|type)\s+"
    r"([A-Za-z_$][A-Za-z0-9_$]*)"
)
# A decorator on its own line, with the declaration on the next - which is how
# ArkUI components are actually written. Without this every `@Component`/
# `@Entry` struct reads as undeclared.
decorated = re.compile(r"^\s*@[A-Za-z_$][A-Za-z0-9_$]*(?:\([^)]*\))?\s*$")

for root, dirs, files in os.walk(os.path.join("harmonyos", "src")):
    dirs[:] = [
        d
        for d in dirs
        if d not in ("node_modules", "dist", "build", "oh_modules", ".git", ".preview")
    ]
    for fn in files:
        # .d.ts and .d.ets are GENERATED from the source beside them. A port
        # whose only declaration of a type is in a generated file has not ported
        # it, so the generated ones are skipped and the source has to say it.
        if not fn.endswith((".ts", ".ets")) or fn.endswith((".d.ts", ".d.ets")):
            continue
        pending = False
        for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
            m = decl.match(line)
            if m:
                types.add(m.group(1))
                pending = False
                continue
            if pending:
                # The line after a bare decorator: the declaration may have lost
                # its own leading keyword match only because the decorator ate
                # the line above it.
                m = decl.match(line)
                if m:
                    types.add(m.group(1))
            pending = bool(decorated.match(line))
low = {t.lower() for t in types}


cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")


def candidates(t, module):
    """Every name this type could legitimately have taken in the HarmonyOS port.

    Mostly the TypeScript conventions, because ArkTS IS TypeScript for naming
    purposes. What is different here:

      the I prefix   ArkTS follows TypeScript and drops it. Both are accepted
                     rather than picking a winner, because settling on one
                     across 166 modules is a rename to do deliberately, not a
                     side effect of measuring.

      camelCase      a C# static class that became a module of free functions
                     has its members in camelCase, and the class itself may
                     survive only as a camelCase const object.

      EventArgs      ArkTS has no events either. Every one became a plain
                     payload interface.

      Exception      becomes an Error subclass, taking `Error` in place of
                     `Exception` - the convention rather than a rename.

      Ability        a C# background service or activity arrives as an ArkTS
                     Ability - `UIAbility` for something with a screen,
                     `ExtensionAbility` for something without. That is the
                     HarmonyOS spelling of the same concept and is accepted as
                     one, because refusing it would push the port towards a name
                     that fits nothing on the platform.
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
        # The platform's own word for a long-running or screen-owning component.
        if b.endswith("Service") or b.endswith("Activity"):
            stem = re.sub(r"(Service|Activity)$", "", b)
            bases.add(stem + "Ability")
            bases.add(stem + "UIAbility")
            bases.add(stem + "ExtensionAbility")

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
# A type deliberately absent is written down in harmonyos/PARITY-EXCLUSIONS.md
# with a reason. Reading it here rather than hard-coding a list means there is
# exactly ONE place to see what was decided, and adding a line is a claim on the
# record rather than a way to make a number go up quietly.
def read_exclusions(path=os.path.join("harmonyos", "PARITY-EXCLUSIONS.md")):
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
    # every module is nameless - so every qualified exclusion and rename
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
print("declared exclusions honoured: %d, renames: %d  (harmonyos/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
