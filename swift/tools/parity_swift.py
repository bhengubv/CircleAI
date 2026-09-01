import re, os, glob, sys

# Every public type the Swift port declares.
types = set()
decl = re.compile(
    r"^\s*(?:public\s+|open\s+)?(?:final\s+)?(?:indirect\s+)?"
    r"(?:struct|class|enum|protocol|actor|typealias)\s+([A-Za-z_][A-Za-z0-9_]*)")
for root, _, files in os.walk("swift/Sources"):
    for fn in files:
        if not fn.endswith(".swift"):
            continue
        for line in open(os.path.join(root, fn), encoding="utf-8", errors="ignore"):
            m = decl.match(line)
            if m:
                types.add(m.group(1))
low = {t.lower() for t in types}

cs = re.compile(r"^\s*public\s+(?:sealed\s+|abstract\s+|static\s+|readonly\s+|partial\s+|ref\s+)*"
                r"(?:record\s+struct|record|class|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")


def candidates(t, module):
    """Every name this type could legitimately have taken in the Swift port.

    Swift has ONE namespace where C# has many, so the port renames on collision:
    a type keeps its name where it can, and takes its module as a prefix where
    it cannot (WavIo -> VoiceWavIo, SentencePieceUnigram ->
    VoiceSentencePieceUnigram). A measure that does not know that reports work
    as missing when it is done and renamed, which is worse than useless - it
    sends somebody to write it twice.

    Three conventions the first version of this measure did not know, each of
    which cost a false negative that would have sent me to write a file that
    already existed:

      EventArgs        Swift has no EventArgs. Every one of them was ported as
                       plain `...Event` - TranscribedEventArgs is
                       TranscribedEvent, and the measure called it missing.

      module RELOCATED Where the module word is already inside the C# name, the
                       port moves it to the front rather than saying it twice:
                       PiperVoiceConfig is VoicePiperConfig, not
                       VoicePiperVoiceConfig.

      dotted modules   `head` was computed from the already-dot-stripped name,
                       so for Inference.Server it was "InferenceServer" and the
                       plain "Inference" prefix was never tried at all.
    """
    mod = module.replace(".", "")
    head = module.split(".")[0]

    # Base forms: the name with a C#-ism stripped, in every combination.
    bases = {t}
    if t.startswith("I") and len(t) > 1 and t[1].isupper():
        bases.add(t[1:])
    for b in list(bases):
        if b.endswith("EventArgs"):
            # Both shapes are in the port: some became `...Event`, and some
            # dropped the suffix entirely because the payload already reads as a
            # noun (VoiceExchangeEventArgs is just VoiceExchange).
            bases.add(b[: -len("EventArgs")] + "Event")
            bases.add(b[: -len("EventArgs")])
        if b.endswith("Args") and not b.endswith("EventArgs"):
            bases.add(b[: -len("Args")])
        # Swift has no exception classes; every one of them became an Error
        # enum, usually with the C# subclasses folded in as CASES
        # (CastException + CastControlException are both CastError).
        if b.endswith("Exception"):
            stem = b[: -len("Exception")]
            bases.add(stem + "Error")
            bases.add(stem)

    # A C# name that would read as a Swift PROTOCOL takes a suffix instead:
    # CastProtocol is CastProtocolKind, because `CastProtocol` in Swift says
    # "something you conform to" and this is an enumeration of wire protocols.
    for b in list(bases):
        bases.add(b + "Kind")

    # The module word relocated to the front rather than repeated.
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
# A type that is deliberately not a one-to-one Swift type - an enum case, an
# extension, a DI registration, something that needs onnxruntime - is written
# down in swift/PARITY-EXCLUSIONS.md with a reason. Reading it here rather than
# hard-coding a list means there is exactly ONE place a person can look to see
# what was decided, and adding a line is a claim on the record rather than a way
# to make a number go up quietly.
def read_exclusions(path="swift/PARITY-EXCLUSIONS.md"):
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
            # "Module.TypeName   reason" - the reason is for a person, not for
            # this. A line with no reason is still honoured; a line with no
            # module qualifier is not, because an unqualified name would exclude
            # a same-named type in every other module too.
            parts = stripped.split(None, 1)
            if "." in parts[0]:
                excluded.add(parts[0])
    return excluded, renames


EXCLUDED, RENAMES = read_exclusions()

rows = []
for d in sorted(glob.glob("src/CircleAI.*/")):
    mod = os.path.basename(d.rstrip("/"))[len("CircleAI."):]
    ts = {}
    for root, _, files in os.walk(d):
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
print("declared exclusions honoured: %d, renames: %d  (swift/PARITY-EXCLUSIONS.md)"
      % (len(EXCLUDED), len(RENAMES)))
print("modules with NOTHING missing: %d of %d" % (sum(1 for r in rows if not r[3]), len(rows)))
print("overall type coverage: %d/%d = %.1f%%  (%d types still missing)"
      % (th, tt, 100.0 * th / tt, tt - th))
