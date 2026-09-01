"""Hoist `try await` out of XCTAssert autoclosures.

XCTAssert's arguments are @autoclosure, which is not an async context, so
`XCTAssertEqual(try await f(), x)` does not compile. The fix is always the same
- bind the call to a `let` on the line before - and doing it by hand across a
few hundred assertions is how a typo gets in.

Only the FIRST argument is hoisted, which is where the call always sits; a
trailing message argument is left alone.
"""
import io, re, sys


def split_top_level(argstring):
    """Split on the first top-level comma, respecting parens, brackets and
    strings. A naive split breaks `XCTAssertEqual(f(a, b), c)` on the wrong
    comma and produces something that compiles and tests nothing."""
    depth = 0
    in_string = False
    escaped = False
    for i, ch in enumerate(argstring):
        if in_string:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            continue
        if ch == '"':
            in_string = True
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "," and depth == 0:
            return argstring[:i], argstring[i:]
    return argstring, ""


CALL = re.compile(r"^(\s*)(XCTAssert[A-Za-z]*)\((.*)\)\s*$")


def hoist(path):
    lines = io.open(path, encoding="utf-8").read().split("\n")
    out = []
    n = 0

    for line in lines:
        m = CALL.match(line)
        if not m or "try await" not in line:
            out.append(line)
            continue

        indent, fn, args = m.groups()
        first, rest = split_top_level(args)
        if "try await" not in first:
            out.append(line)
            continue

        n += 1
        name = "hoisted%d" % n
        out.append("%slet %s = %s" % (indent, name, first.strip()))
        out.append("%s%s(%s%s)" % (indent, fn, name, rest))

    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(out))
    return n


for p in sys.argv[1:]:
    print("%s: hoisted %d" % (p, hoist(p)))
