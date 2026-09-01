import re, os, glob, sys

# Every identifier the C headers declare — types AND functions, because a C#
# class of static helpers legitimately becomes a set of free functions here.
symbols = set()
for root, _, files in os.walk("c/include"):
    for fn in files:
        if not fn.endswith(".h"):
            continue
        text = open(os.path.join(root, fn), encoding="utf-8", errors="ignore").read()
        symbols.update(re.findall(r"\b(ca_[a-z0-9_]+)", text))

# A symbol is its set of words, so ca_commerce_board_add_line covers the words
# {commerce, board, add, line} whatever order a C# name puts them in.
# ─────────────────────────────────────────────────────────────────────────────
# A C# class is a NOUN and a C function is a VERB, so the same thing is
# GeezRomanizer on one side and ca_geez_romanize on the other. Matching raw
# words calls that MISSING and sends somebody to write a file that already
# exists — the exact failure this measure is here to prevent.
#
# C-only. The Swift and Kotlin rulers match real type names, because those
# languages have classes to put the noun on; C is where a static class becomes
# free functions and the name has to change word class to stay idiomatic.
#
# Applied to BOTH sides, so it can only ever merge words that share a stem — it
# cannot invent a match between two unrelated ones. Deliberately small: plural,
# agent noun, doubled consonant, silent e. A real stemmer starts collapsing
# words that mean different things, and a measure that OVER-reports is worse
# than one that under-reports: it claims work nobody did.
def stem(w):
    if len(w) > 3 and w.endswith("s") and not w.endswith("ss"):
        w = w[:-1]                       # languages -> language
    if len(w) > 3 and w.endswith(("er", "or")):
        w = w[:-2]                       # romanizer -> romaniz, detector -> detect
    if len(w) > 2 and w[-1] == w[-2] and w[-1] not in "aeiou":
        w = w[:-1]                       # splitt -> split, formatt -> format
    if len(w) > 3 and w.endswith("e"):
        w = w[:-1]                       # romanize -> romaniz
    return w


sym_words = [{stem(w) for w in s.split("_")[1:]} for s in symbols]

# Prefixes the C port drops on purpose: there is one implementation and it is
# named for the thing, not for how it stores it.
IMPL = ("InMemory", "Null", "Json", "FileSystem", "Filesystem", "Adjacency",
        "Channel", "Http", "Managed", "Default", "Simple", "Basic", "Local",
        "TextRewrite", "IndexBacked", "Deterministic", "LineDiff", "Sqlite",
        "Ado", "Heuristic", "Keyword", "Energy", "Tf", "Topic")

# Words that carry no meaning for matching — every port spells them differently
# or leaves them out entirely.
NOISE = {"i", "in", "memory", "null", "json", "file", "system", "filesystem",
         "the", "a", "item", "info", "data", "type", "kind", "options", "option",
         "result", "service", "services", "provider", "store", "impl", "args",
         "event", "eventargs", "dto", "base", "class", "attribute"}


def words(name):
    out, cur = [], ""
    for i, c in enumerate(name):
        if c.isupper() and cur and (not name[i - 1].isupper() or
                                    (i + 1 < len(name) and name[i + 1].islower())):
            out.append(cur.lower()); cur = c
        else:
            cur += c
    if cur:
        out.append(cur.lower())
    return out


def candidates(t):
    yield t
    if t.startswith("I") and len(t) > 1 and t[1].isupper():
        yield t[1:]
    for p in IMPL:
        if t.startswith(p) and len(t) > len(p):
            yield t[len(p):]
            if t[len(p):].startswith("I"):
                yield t[len(p) + 1:]
    # C has no exceptions: a C# exception class becomes an error CODE, so the
    # "Exception" word never appears in a symbol.
    if t.endswith("Exception"):
        yield t[: -len("Exception")]
        yield t[: -len("Exception")] + "Error"


# ─────────────────────────────────────────────────────────────────────────────
# The exclusions file IS this measure's configuration.
#
# A type deliberately absent — something that needs a runtime C does not have, a
# DI container, a managed-language construct — is written down in
# c/PARITY-EXCLUSIONS.md with a reason. Reading it here rather than hard-coding
# a list means there is exactly ONE place to see what was decided, and adding a
# line is a claim on the record rather than a way to make a number go up
# quietly.
def read_exclusions(path="c/PARITY-EXCLUSIONS.md"):
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
            parts = stripped.split(None, 1)
            # A line with no module qualifier is ignored: an unqualified name
            # would excuse a same-named type in every other module too. A
            # trailing "*" excludes a whole module, which is what a platform
            # head or an ASP.NET project actually is.
            if "." in parts[0] or parts[0].endswith(".*"):
                excluded.add(parts[0])
    return excluded, renames


EXCLUDED, RENAMES = read_exclusions()
EXCLUDED_MODULES = {e[:-2] for e in EXCLUDED if e.endswith(".*")}

cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")

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
        if mod in EXCLUDED_MODULES or qualified in EXCLUDED:
            return True
        renamed = RENAMES.get(qualified)
        if renamed and renamed in symbols:
            return True
        for cand in set(candidates(t)):
            want = {stem(w) for w in words(cand) if w not in NOISE}
            if not want:
                continue
            for sw in sym_words:
                if want <= sw:
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
print("declared exclusions honoured: %d, renames: %d  (c/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
