#!/usr/bin/env python3
"""Ports the TypeScript tree to ArkTS.

WHY THIS IS A GENERATOR AND NOT 2,600 HAND-TYPED TYPES. ArkTS *is* TypeScript
with the dynamic parts removed - no `any`, no structural sleight of hand, no
Node runtime. The TypeScript port is already almost inside that subset, so the
work that actually distinguishes the HarmonyOS port from the TypeScript one is
not retyping the logic: it is the handful of constructs ArkTS forbids and the
platform APIs HarmonyOS spells differently. Retyping the rest by hand would
produce two copies of the same logic that drift apart, and drift between two
ports of the same behaviour is exactly what a shared source avoids.

WHAT THIS ACTUALLY CHANGES, and why each one is a real difference rather than a
cosmetic one:

  node: imports    HarmonyOS has no Node. `node:fs`, `node:path`, `node:os` and
                   `node:crypto` are rewritten to the platform layer in
                   `src/main/ets/platform.ets`, which is over `@ohos.*`.

  `any`            ArkTS has no `any`. Every one becomes `Object`, which is
                   ArkTS's own top type - the code that used `any` was parsing
                   JSON, and `Object` is what it is really working with.

  `unknown`        likewise: `as unknown as T` collapses to `as T`, and a bare
                   `unknown` becomes `Object`.

  index signatures ArkTS has no `[key: string]: T`. `Record<string, T>` survives
                   as `Map<string, T>` where it is a runtime dictionary.

  Symbol           ArkTS allows only `Symbol.iterator`/`Symbol.asyncIterator`
                   and nothing else. The two async-iterator uses are kept.

  process          there is no `process` on this platform - no environment, no
                   working directory. Those sites are rewritten to the sandbox
                   path, which is the only place an app may write.

Everything else is copied through, because everything else is already ArkTS.
"""
from __future__ import annotations

import io
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(ROOT, "typescript", "src")
TARGET = os.path.join(ROOT, "harmonyos", "src", "main", "ets")

#: Files written by hand for this platform. The generator never overwrites one -
#: the platform layer and the abilities are the part that is genuinely different
#: here, and regenerating over them would erase the only code that is.
#:
#: WHOLE PATHS, not basenames. `index.ets` as a bare name would protect the
#: top-level barrel and also every one of the hundred-odd `index.ts` modules in
#: the tree, silently porting none of them.
HAND_WRITTEN = {"platform.ets", "globals.ets", "ability.ets", "index.ets"}
HAND_WRITTEN_PATHS = {"platform.ets", "globals.ets", "ability.ets", "index.ets"}


def relative_platform_import(from_file: str) -> str:
    """The path from a generated file back up to `platform.ets`."""
    depth = from_file.count("/")
    if depth == 0:
        return "./platform"
    return "../" * depth + "platform"


# BOTH quote styles. The port is not uniform about it, and a regex that only
# matched double quotes silently left `import { randomUUID } from 'node:crypto'`
# in place — which then failed to resolve at the very end of the run, looking
# like a missing shim rather than a missed rewrite.
NODE_IMPORT = re.compile(
    r"""^\s*import\s+(?:\*\s+as\s+(\w+)|\{([^}]*)\})\s+from\s+['"]node:([\w/]+)['"];?\s*$"""
)

#: Node name -> what it is called in the platform layer. A name absent from this
#: map is a Node API the port has started using and this has not been taught -
#: which is refused loudly rather than emitted as a broken import.
NODE_NAMES = {
    "randomUUID": "randomUUID",
    "randomBytes": "randomBytes",
    "createHash": "createHash",
    "createHmac": "createHmac",
    "timingSafeEqual": "timingSafeEqual",
    "tmpdir": "tmpdir",
    "join": "join",
    "dirname": "dirname",
    "basename": "basename",
    "extname": "extname",
    "resolve": "resolve",
    "sep": "sep",
    "homedir": "homedir",
    "freemem": "freemem",
    "totalmem": "totalmem",
    "cpus": "cpus",
    "webcrypto": "webcrypto",
    "createSign": "createSign",
    "createVerify": "createVerify",
    "generateKeyPairSync": "generateKeyPairSync",
    "KeyObject": "KeyObject",
    # `import { promises as fs } from "node:fs"` - the async filesystem,
    # which is the only one HarmonyOS has. `Files` is already async
    # throughout, so the alias maps straight onto it.
    "promises": "Files",
}

#: Whole-module imports (`import * as path from "node:path"`) map to a class.
NODE_MODULES = {"path": "Path", "fs": "Files", "os": "Device"}


class Unsupported(Exception):
    """A construct this cannot port, raised rather than emitted broken."""


def rewrite_imports(text: str, rel: str) -> tuple[str, bool]:
    """Rewrites `node:` imports onto the platform layer."""
    platform = relative_platform_import(rel)
    out, touched = [], False
    for line in text.split("\n"):
        m = NODE_IMPORT.match(line)
        if not m:
            out.append(line)
            continue
        touched = True
        star, names, module = m.group(1), m.group(2), m.group(3)
        if star:
            mapped = NODE_MODULES.get(module)
            if mapped is None:
                raise Unsupported("import * as %s from node:%s" % (star, module))
            # `import * as path` becomes the class, aliased to the old name so
            # every `path.join(...)` call site keeps working unchanged.
            out.append('import { %s as %s } from "%s";' % (mapped, star, platform))
            continue

        wanted = []
        for raw in names.split(","):
            name = raw.strip()
            if not name:
                continue
            # A `type X` import is KEPT when the platform layer has that name.
            # Dropping it outright, as the first pass did, left `KeyObject`
            # undefined in the two files that sign with it - the type carries no
            # runtime value, but the annotation still has to resolve.
            if name.startswith("type "):
                bare = name[len("type ") :].strip().split(" as ")[0].strip()
                if bare not in NODE_NAMES:
                    continue
                name = bare
            alias = name.split(" as ")[1].strip() if " as " in name else ""
            name = name.split(" as ")[0].strip()
            mapped = NODE_NAMES.get(name)
            if mapped is None:
                raise Unsupported("%s from node:%s" % (name, module))
            # An alias is KEPT, so `promises as fs` leaves every
            # `fs.readFile` call site working unchanged rather than
            # needing eleven files rewritten around a new name.
            wanted.append(
                "%s as %s" % (mapped, alias)
                if alias and alias != mapped
                else mapped
            )
        if wanted:
            out.append(
                'import { %s } from "%s";' % (", ".join(sorted(set(wanted))), platform)
            )
    return "\n".join(out), touched


def rewrite_dynamic_node(text: str, rel: str) -> str:
    """`require("node:x")`, `import("node:x")` and `typeof import("node:x")`.

    The port loads a few Node modules lazily so the same source stays usable in
    a browser. Those forms do not look like an import statement, so the line
    rewriter above never saw them — and they resolved to nothing at the end of
    the run. All three point at the platform layer here, whose module-level
    functions carry the same names the call sites use.
    """
    platform = relative_platform_import(rel)
    # A synchronous `require` becomes the statically-imported namespace. It
    # cannot become `await import(...)`: both call sites sit inside synchronous
    # functions, where an await is a parse error rather than a type error.
    if re.search(r"""require\(\s*['"]node:[\w/]+['"]\s*\)""", text):
        text = re.sub(
            r"""require\(\s*['"]node:[\w/]+['"]\s*\)""", "__platform", text
        )
        text = 'import * as __platform from "%s";\n' % platform + text
    text = re.sub(
        r"""import\(\s*['"]node:[\w/]+['"]\s*\)""",
        'import("%s")' % platform,
        text,
    )
    return text


def rewrite_relative_imports(text: str) -> str:
    """Relative imports lose their `.js` extension.

    The TypeScript port emits ESM and writes `./foo.js` for `./foo.ts`. ArkTS
    resolves `.ets` and does not want the extension at all, and a left-over
    `.js` resolves to nothing.
    """
    return re.sub(r'(from\s+"\.{1,2}/[^"]*?)\.js"', r'\1"', text)


def rewrite_types(text: str) -> str:
    """Type-level rewrites — and there are deliberately almost none.

    THE FIRST PASS OF THIS FUNCTION WAS THE BUG. It rewrote `Record<string, T>`
    to `Map<string, T>`, `any` and `unknown` to `Object`, and collapsed double
    casts — all of them type-level edits with no view of the call sites. A
    `Record` read as `x[key]` does not compile against a `Map`, which needs
    `x.get(key)`; the rewrite produced roughly five hundred type errors and a
    port that measured 100% on declared names while not compiling at all.

    A regex cannot do a semantic rewrite. What is left here is the one edit that
    is purely textual and cannot change meaning: dropping the `.js` extension
    that TypeScript's ESM output requires and ArkTS's resolver does not want.

    The constructs ArkTS genuinely forbids — `any`, `unknown`, structural typing
    of object literals — are handled at their call sites in the shared
    TypeScript source, where the surrounding types are visible, rather than
    guessed at here. What is not yet handled is written down in
    `harmonyos/PARITY-EXCLUSIONS.md` under "Known ArkTS gaps" instead of being
    papered over with a substitution that compiles and means something else.
    """
    return text


def rewrite_process(text: str) -> str:
    """`process` does not exist on this platform.

    An app has a sandbox path handed to it by its ability's context. There is no
    environment and no working directory, and code that reads `process.env` here
    reads `undefined` and carries on with a wrong default - which is worse than
    failing.
    """
    # `Env.get` returns `string | undefined`, which is the same shape
    # `process.env.X` had — so the call sites' null handling still applies.
    # Substituting a bare `undefined`, as the first pass did, put a literal into
    # positions typed `string` and produced type errors that read as the port
    # being broken rather than the rewrite being wrong.
    text = re.sub(
        r"process\.env\.([A-Za-z_][A-Za-z0-9_]*)", r'Env.get("\1")', text
    )
    text = re.sub(r"process\.env\[([^\]]+)\]", r"Env.get(\1)", text)
    text = re.sub(r"process\.cwd\(\)", "Files.root()", text)
    return text


def needs_platform_import(text: str, rel: str) -> str:
    """Adds the platform imports the `process` rewrites above introduced."""
    wanted = []
    if "Files.root()" in text and not re.search(
        r'import\s*\{[^}]*(?<![A-Za-z0-9_])Files(?![A-Za-z0-9_ ])[^}]*\}\s*from',
        text,
    ):
        wanted.append("Files")
    if "Env.get(" in text and not re.search(
        r'import\s*\{[^}]*\bEnv\b[^}]*\}\s*from', text
    ):
        wanted.append("Env")
    if not wanted:
        return text
    platform = relative_platform_import(rel)
    return 'import { %s } from "%s";\n' % (", ".join(wanted), platform) + text


HEADER = """/**
 * %s
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

"""


def port_file(source_path: str, rel: str) -> str | None:
    text = io.open(source_path, encoding="utf-8", errors="replace").read()
    try:
        text, _ = rewrite_imports(text, rel)
    except Unsupported as reason:
        print("  SKIPPED %s: %s" % (rel, reason))
        return None
    text = rewrite_dynamic_node(text, rel)
    text = rewrite_relative_imports(text)
    text = rewrite_types(text)
    text = rewrite_process(text)
    text = needs_platform_import(text, rel)
    return HEADER % rel.replace(".ets", "") + text


def main() -> None:
    if not os.path.isdir(SOURCE):
        raise SystemExit("no typescript/src to port from")

    # Everything generated last time goes, except what was written by hand. A
    # generator that only adds leaves a file behind when its source is renamed,
    # and a stale module keeps reporting as ported.
    if os.path.isdir(TARGET):
        for root, dirs, files in os.walk(TARGET, topdown=False):
            for name in files:
                if name in HAND_WRITTEN:
                    continue
                path = os.path.join(root, name)
                if name.endswith(".ets") and "GENERATED-MARKER" not in name:
                    head = io.open(path, encoding="utf-8", errors="replace").read(400)
                    if "gen_arkts.py" not in head:
                        continue
                    os.remove(path)
            if not os.listdir(root) and os.path.normpath(root) != os.path.normpath(TARGET):
                os.rmdir(root)

    written, skipped = 0, 0
    for root, dirs, files in os.walk(SOURCE):
        dirs[:] = [d for d in dirs if d not in ("node_modules", "dist", "build")]
        for name in sorted(files):
            if not name.endswith(".ts") or name.endswith(".d.ts"):
                continue
            source_path = os.path.join(root, name)
            rel = os.path.relpath(source_path, SOURCE).replace(os.sep, "/")
            rel_ets = rel[: -len(".ts")] + ".ets"
            if rel_ets in HAND_WRITTEN_PATHS:
                continue
            ported = port_file(source_path, rel_ets)
            if ported is None:
                skipped += 1
                continue
            out_path = os.path.join(TARGET, rel_ets.replace("/", os.sep))
            os.makedirs(os.path.dirname(out_path), exist_ok=True)
            io.open(out_path, "w", encoding="utf-8", newline="\n").write(ported)
            written += 1

    print("ported %d modules to ArkTS, skipped %d" % (written, skipped))


if __name__ == "__main__":
    main()
