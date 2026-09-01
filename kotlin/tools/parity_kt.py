import re, os, glob, sys

# Every public type the Kotlin port declares.
#
# `typealias` counts: a C# type that becomes an alias over a JVM one IS ported —
# the name resolves and callers compile — and a measure that ignored aliases
# would report finished work as missing.
types = set()
kt = re.compile(
    r"^\s*(?:public\s+|internal\s+)?"
    r"(?:sealed\s+|open\s+|abstract\s+|data\s+|value\s+|inline\s+|annotation\s+|fun\s+)*"
    r"(?:class|interface|object|enum\s+class|typealias)\s+([A-Za-z_][A-Za-z0-9_]*)")
for root, _, files in os.walk("kotlin/src/main"):
    for fn in files:
        if not fn.endswith(".kt"):
            continue
        for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
            m = kt.match(line)
            if m:
                types.add(m.group(1))
low = {t.lower() for t in types}

cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")


def candidates(t, module):
    """Every name this type could legitimately have taken in the Kotlin port.

    Kotlin has packages, so it does not need the module-prefix renaming Swift
    does — but it has its own conventions, and a measure that does not know them
    reports finished work as missing, which sends somebody to write a file that
    already exists:

      I prefix     Kotlin drops it on interfaces (`IWavIo` -> `WavIo`).
      EventArgs    There is no EventArgs on the JVM. They became `...Event`, or
                   dropped the suffix where the payload already reads as a noun.
      Exception    A C# exception hierarchy usually folds into one sealed class,
                   so `FooException` may be `FooError` or just `Foo`.
      module word  Where a name collided across packages it took the module as a
                   prefix, the same way Swift's did.
    """
    mod = module.replace(".", "")
    head = module.split(".")[0]

    bases = {t}
    if t.startswith("I") and len(t) > 1 and t[1].isupper():
        bases.add(t[1:])

    for b in list(bases):
        if b.endswith("EventArgs"):
            stem = b[: -len("EventArgs")]
            bases.add(stem + "Event")
            bases.add(stem)
        elif b.endswith("Args"):
            bases.add(b[: -len("Args")])
        if b.endswith("Exception"):
            stem = b[: -len("Exception")]
            bases.add(stem + "Error")
            bases.add(stem)

    # The module word relocated to the front rather than repeated.
    for b in list(bases):
        for word in {mod, head}:
            if word and word in b and not b.startswith(word):
                bases.add(b.replace(word, "", 1))

    out = set(bases)
    for prefix in {mod, head}:
        for b in bases:
            out.add(prefix + b)
            out.add(b + "Kind")
    return {c for c in out if c}


# ─────────────────────────────────────────────────────────────────────────────
# The exclusions file IS this measure's configuration.
#
# A type deliberately absent — an Android binding, a DI registration, something
# that needs a native library — is written down in kotlin/PARITY-EXCLUSIONS.md
# with a reason. Reading it here rather than hard-coding a list means there is
# exactly ONE place to see what was decided, and adding a line is a claim on the
# record rather than a way to make a number go up quietly.
def read_exclusions(path="kotlin/PARITY-EXCLUSIONS.md"):
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
            if "=" in stripped:
                key, value = (p.strip() for p in stripped.split("=", 1))
                renames[key] = value
        else:
            # "Module.TypeName   reason". A line with no module qualifier is
            # ignored: an unqualified name would excuse a same-named type in
            # every other module too.
            parts = stripped.split(None, 1)
            if "." in parts[0]:
                excluded.add(parts[0])
    return excluded, renames


EXCLUDED, RENAMES = read_exclusions()

rows = []
for d in sorted(glob.glob("src/CircleAI.*/")):
    # normpath, not rstrip("/"): glob hands back a trailing os.sep, which is a
    # BACKSLASH on Windows. rstrip("/") leaves it, basename then returns "" and
    # every module is nameless — so every qualified exclusion and rename in
    # PARITY-EXCLUSIONS.md silently stops matching and the same commit measures
    # four points lower on Windows than on macOS. A ruler that reads differently
    # per platform is worse than no ruler.
    mod = os.path.basename(os.path.normpath(d))[len("CircleAI."):]
    ts = {}
    for root, dirs, files in os.walk(d):
        # obj/ and bin/ are BUILD OUTPUT, not API. Scanning them counts the
        # Android resource designer's generated "Resource" class as a public
        # type every port is then failing to provide — a phantom gap that
        # appears only on a machine that has built the solution, and that no
        # amount of porting can ever close.
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
        if qualified in EXCLUDED:
            return True
        renamed = RENAMES.get(qualified)
        if renamed and (renamed in types or renamed.lower() in low):
            return True
        return any(c in types or c.lower() in low for c in candidates(t, mod))

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
print("declared exclusions honoured: %d, renames: %d  (kotlin/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
