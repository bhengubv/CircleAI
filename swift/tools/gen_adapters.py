"""Generates the missing Swift *CompanionAdapter types from the C#.

THE PROMPTS ARE GENERATED AND THE SHAPE IS NOT. There are forty-six adapters,
each a decorator over ICompanionSession with the same twelve lines of
forwarding and then N helper methods whose only content is a prompt string.
Hand-copying forty-six sets of prompts is how one loses a clause; the
forwarding is written once, here, and reviewed once.

Run:  python3 gen_adapters.py <repo-root> <out-dir> [--only Name,Name]
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
OUT = sys.argv[2] if len(sys.argv) > 2 else "."
ONLY = None
for a in sys.argv[3:]:
    if a.startswith("--only"):
        ONLY = set(a.split("=", 1)[1].split(",")) if "=" in a else None

CS_TO_SWIFT = {
    "string": "String",
    "string?": "String?",
    "int": "Int",
    "int?": "Int?",
    "double": "Double",
    "double?": "Double?",
    "decimal": "Decimal",
    "decimal?": "Decimal?",
    "bool": "Bool",
    "bool?": "Bool?",
    "long": "Int64",
}


def lower_first(s):
    return s[0].lower() + s[1:] if s else s


def swift_method_name(cs_name):
    name = cs_name[:-5] if cs_name.endswith("Async") else cs_name
    return lower_first(name)


def parse_params(raw):
    """C# parameter list -> [(name, swiftType)], dropping the cancellation token."""
    out = []
    depth = 0
    cur = ""
    for ch in raw:
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur)

    params = []
    for p in out:
        p = p.strip()
        if not p or "CancellationToken" in p:
            continue
        p = re.sub(r"=.*$", "", p).strip()          # drop a default value
        bits = p.split()
        if len(bits) < 2:
            continue
        ctype, cname = " ".join(bits[:-1]), bits[-1]
        stype = CS_TO_SWIFT.get(ctype)
        if stype is None:
            # An unmodelled type: keep the C# spelling so the compiler says so
            # rather than this script guessing silently.
            stype = ctype
        params.append((cname, stype))
    return params


def to_swift_interpolation(cs_literal):
    """C# $"..." body -> Swift "..." body.

    {name}      -> \\(name)
    {name:C}    -> \\(name)   and the caller is told why in the file header
    {{ / }}     -> literal braces
    """
    out = []
    i = 0
    while i < len(cs_literal):
        c = cs_literal[i]
        if c == "{" and i + 1 < len(cs_literal) and cs_literal[i + 1] == "{":
            out.append("{")
            i += 2
            continue
        if c == "}" and i + 1 < len(cs_literal) and cs_literal[i + 1] == "}":
            out.append("}")
            i += 2
            continue
        if c == "{":
            close = cs_literal.index("}", i)
            expr = cs_literal[i + 1:close]
            expr = expr.split(":", 1)[0].split(",", 1)[0].strip()
            out.append("\\(" + expr + ")")
            i = close + 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def join_literals(text):
    """Collapses `$"a" + $"b"` and adjacent literals into one body."""
    parts = re.findall(r'\$?"((?:[^"\\]|\\.)*)"', text)
    return "".join(parts)


METHOD = re.compile(
    r"public\s+Task<string>\s+(\w+)\s*\(([^)]*)\)\s*"
    r"=>\s*_i\.AgentAsync\(\s*(.*?)\s*,\s*ct\s*\)\s*;",
    re.S)


def wrap(literal_body, indent):
    """Splits a long Swift string literal across lines with + concatenation."""
    if len(literal_body) <= 88:
        return '"' + literal_body + '"'
    pieces, cur = [], ""
    for token in re.split(r"(?<= )", literal_body):
        if len(cur) + len(token) > 88 and cur:
            pieces.append(cur)
            cur = ""
        cur += token
    if cur:
        pieces.append(cur)
    pad = " " * indent
    return ("\n" + pad + "+ ").join('"' + p + '"' for p in pieces)


def generate(module, cls, text):
    domain = cls[:-len("CompanionAdapter")]
    L = []
    w = L.append

    w("// %s.swift" % cls)
    w("//")
    w("// Port of CircleAI.%s.%s." % (module, cls))
    w("//")
    w("// AN ADAPTER IS A DECORATOR, NOT A SESSION. Identity, history, context and")
    w("// feedback are forwarded to the inner session untouched; the only thing this")
    w("// type does is put the domain's system prompt in front of every")
    w("// conversational call, and offer the helpers that domain needs.")
    w("//")
    w("// The helper PROMPTS are generated from the C# so that not one clause of")
    w("// forty-six sets of them is lost in transcription; the forwarding above them")
    w("// is written once and reviewed once. See swift/tools/gen_adapters.py.")
    w("//")
    w("// One deliberate difference: C# currency format specifiers ({x:C}) are")
    w("// dropped, because they render against the machine's CURRENT CULTURE. A")
    w("// prompt that says R1 200,00 on one device and $1,200.00 on another is not")
    w("// the same prompt, and the model is being handed a number either way.")
    w("")
    w("import Foundation")
    w("")
    w("/// An `ICompanionSession` decorator that prepends the %s domain" % domain.lower())
    w("/// system prompt to every conversational call.")
    w("public final class %s: ICompanionSession, @unchecked Sendable {" % cls)
    w("")
    w("    private let inner: ICompanionSession")
    w("")
    w("    public init(_ inner: ICompanionSession) { self.inner = inner }")
    w("")
    w("    public var sessionId: String { inner.sessionId }")
    w("    public var identityId: String { inner.identityId }")
    w("    public var interface: InterfaceKind { inner.interface }")
    w("    public var history: [CompanionTurn] { inner.history }")
    w("    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }")
    w("")
    w("    public func getContext() -> CompanionContext { inner.getContext() }")
    w("    public func refreshContext() async throws { try await inner.refreshContext() }")
    w("    public func signalFeedback(positive: Bool, note: String?) async throws {")
    w("        try await inner.signalFeedback(positive: positive, note: note)")
    w("    }")
    w("")
    w("    public func send(_ message: String) async throws -> String {")
    w("        try await inner.send(enrich(message))")
    w("    }")
    w("    public func stream(_ message: String) -> AsyncStream<String> {")
    w("        inner.stream(enrich(message))")
    w("    }")
    w("    public func agent(_ instruction: String) async throws -> String {")
    w("        try await inner.agent(enrich(instruction))")
    w("    }")
    w("")
    w("    private func enrich(_ m: String) -> String {")
    w('        "\\(%sDomainContext.systemPromptSnippet)\\n\\n\\(m)"' % domain)
    w("    }")

    methods = METHOD.findall(text)
    if methods:
        w("")
        w("    // MARK: - %s helpers" % domain)

    # The three FORWARDING methods match the same shape as a helper - they are
    # `Task<string> X(...) => _i.AgentAsync(E(m), ct)` too - and would generate a
    # helper with an empty prompt. They are written out by hand above.
    FORWARDING = {"SendAsync", "StreamAsync", "AgentAsync"}

    seen = set()
    for cs_name, raw_params, body in methods:
        if cs_name in FORWARDING:
            continue
        literal_check = join_literals(body)
        if not literal_check:
            # No string literal in the body: this forwards something rather than
            # asking something, so there is no prompt to port.
            continue
        swift_name = swift_method_name(cs_name)
        if swift_name in seen:
            continue
        seen.add(swift_name)
        params = parse_params(raw_params)
        sig = ", ".join("%s: %s" % (n, t) for n, t in params)
        literal = to_swift_interpolation(join_literals(body))
        w("")
        w("    /// C# `%s`." % cs_name)
        w("    public func %s(%s) async throws -> String {" % (swift_name, sig))
        w("        try await inner.agent(")
        w("            " + wrap(literal, 12) + ")")
        w("    }")

    w("}")
    return "\n".join(L) + "\n"


made = []
for module_dir in sorted(os.listdir(os.path.join(ROOT, "src"))):
    if not module_dir.startswith("CircleAI."):
        continue
    module = module_dir[len("CircleAI."):]
    d = os.path.join(ROOT, "src", module_dir)
    for fn in sorted(os.listdir(d)):
        if not fn.endswith("CompanionAdapter.cs"):
            continue
        cls = fn[:-3]
        if ONLY and cls not in ONLY:
            continue
        text = open(os.path.join(d, fn), encoding="utf-8", errors="ignore").read()
        if "class %s" % cls not in text:
            continue
        out_path = os.path.join(OUT, cls + ".swift")
        with open(out_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(generate(module, cls, text))
        made.append(cls)

print("generated %d adapters" % len(made))
for m in made:
    print("  " + m)
