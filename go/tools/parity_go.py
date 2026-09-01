import re, os, glob, sys

# Every exported type the Go port declares.
#
# Go has ONE package here — `circleai`, flat — where C# has 166 projects. That
# is the whole reason this measure needs a rename table: two modules with a
# same-named type cannot both keep it, so one of them takes its module as a
# prefix, exactly as the Swift port does for the same reason.
types = set()
# type, var, const and func — all four, because a C# STATIC CLASS has no single
# Go spelling. Some became a type with methods, some a package-level var of an
# anonymous struct (SafetyDomainContext, GrpcDeadline, TcpKnownPorts), some a
# set of free functions. Scanning only `type` reported every one of the latter
# as missing while it was sitting there, which is the same false negative the C
# ruler had for noun-versus-verb naming.
decl = re.compile(r"^(?:type|var|const|func)\s+([A-Z][A-Za-z0-9_]*)\s*[\s({=]")
for root, dirs, files in os.walk("go"):
    # vendor/ is somebody else's code and testdata/ is fixtures; neither is this
    # port's API, and counting either inflates the number with types nobody
    # here wrote.
    dirs[:] = [d for d in dirs if d not in ("vendor", "testdata", ".git")]
    for fn in files:
        if not fn.endswith(".go") or fn.endswith("_test.go"):
            continue
        for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
            m = decl.match(line)
            if m:
                types.add(m.group(1))
low = {t.lower() for t in types}

cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")


def candidates(t, module):
    """Every name this type could legitimately have taken in the Go port.

    Interfaces are spelled BOTH ways in this port — some files keep the C# I
    prefix, some drop it for Go's convention — so both are tried. That
    inconsistency is real and predates this measure; settling on one spelling
    across 166 modules is a rename worth doing deliberately, not as a side
    effect of measuring.

    What it cannot keep is a name another module already used. Those take the
    module as a prefix, and the module word is not repeated when it is already
    inside the name: PiperVoiceConfig becomes VoicePiperConfig, not
    VoicePiperVoiceConfig.
    """
    mod = module.replace(".", "")
    head = module.split(".")[0]

    bases = {t}
    if t.startswith("I") and len(t) > 1 and t[1].isupper():
        bases.add(t[1:])

    for b in list(bases):
        # Go has no EventArgs. Every one of them became a plain payload struct,
        # sometimes keeping "Event" and sometimes dropping the suffix entirely
        # because the name already reads as a noun.
        if b.endswith("EventArgs"):
            bases.add(b[: -len("EventArgs")] + "Event")
            bases.add(b[: -len("EventArgs")])
        if b.endswith("Args") and not b.endswith("EventArgs"):
            bases.add(b[: -len("Args")])
        # Go has no exceptions. A C# exception class becomes an error value or
        # an error type, so the "Exception" word does not survive.
        if b.endswith("Exception"):
            stem = b[: -len("Exception")]
            bases.add(stem + "Error")
            bases.add(stem)

    # `ca_` is the C prefix; here the product name is the module path, so a type
    # that spells CircleAI out in C# drops it.
    for b in list(bases):
        if b[:8].lower() == "circleai" and len(b) > 8:
            bases.add(b[8:])

    # The module word relocated to the front rather than said twice.
    for b in list(bases):
        for word in {mod, head}:
            if word and word in b and not b.startswith(word):
                bases.add(b.replace(word, "", 1))

    out = set(bases)
    for prefix in {mod, head}:
        for b in bases:
            out.add(prefix + b)
            # An interface keeps its I and takes the prefix after it: IWavIo ->
            # IVoiceWavIo.
            if b.startswith("I") and len(b) > 1 and b[1].isupper():
                out.add("I" + prefix + b[1:])
            else:
                out.add("I" + prefix + b)
    return {c for c in out if c}


# ─────────────────────────────────────────────────────────────────────────────
# The exclusions file IS this measure's configuration.
#
# A type deliberately absent — something that needs a runtime Go has not got, a
# DI container, a C#-only construct — is written down in go/PARITY-EXCLUSIONS.md
# with a reason. Reading it here rather than hard-coding a list means there is
# exactly ONE place to see what was decided, and adding a line is a claim on the
# record rather than a way to make a number go up quietly.
def read_exclusions(path="go/PARITY-EXCLUSIONS.md"):
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
            # head or an ASP.NET project actually is.
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
    # silently stops matching and the same commit measures lower on Windows
    # than on macOS. A ruler that reads differently per platform is worse than
    # no ruler.
    mod = os.path.basename(os.path.normpath(d))[len("CircleAI."):]
    ts = {}
    for root, dirs, files in os.walk(d):
        # obj/ and bin/ are BUILD OUTPUT, not API. Scanning them counts the
        # Android resource designer's generated "Resource" class as a public
        # type every port is then failing to provide — a phantom gap that
        # appears only on a machine that has built the solution.
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
print("declared exclusions honoured: %d, renames: %d  (go/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
