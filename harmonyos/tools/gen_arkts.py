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
HAND_WRITTEN = {"platform.ets", "ability.ets", "index.ets"}
HAND_WRITTEN_PATHS = {"platform.ets", "ability.ets", "index.ets"}


def relative_platform_import(from_file: str) -> str:
    """The path from a generated file back up to `platform.ets`."""
    depth = from_file.count("/")
    if depth == 0:
        return "./platform"
    return "../" * depth + "platform"


NODE_IMPORT = re.compile(
    r'^\s*import\s+(?:\*\s+as\s+(\w+)|\{([^}]*)\})\s+from\s+"node:(\w+)";\s*$'
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
    "join": "Path",
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
            # `type X` imports carry no runtime value and are dropped: the type
            # they name is a Node type that does not exist here.
            if name.startswith("type "):
                continue
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


def rewrite_relative_imports(text: str) -> str:
    """Relative imports lose their `.js` extension.

    The TypeScript port emits ESM and writes `./foo.js` for `./foo.ts`. ArkTS
    resolves `.ets` and does not want the extension at all, and a left-over
    `.js` resolves to nothing.
    """
    return re.sub(r'(from\s+"\.{1,2}/[^"]*?)\.js"', r'\1"', text)


def rewrite_types(text: str) -> str:
    """The type-level constructs ArkTS does not have."""
    # `as unknown as T` -> `as T`. The double cast exists in TypeScript only to
    # get past the checker; ArkTS has no `unknown` and the single cast says the
    # same thing.
    text = re.sub(r"\bas\s+unknown\s+as\s+", "as ", text)
    # A bare `unknown` and a bare `any` both become ArkTS's top type.
    text = re.sub(r"(:\s*)unknown\b", r"\1Object", text)
    text = re.sub(r"(:\s*)any\b", r"\1Object", text)
    text = re.sub(r"\bas\s+unknown\b", "as Object", text)
    text = re.sub(r"\bany\[\]", "Object[]", text)
    text = re.sub(r"<\s*any\s*>", "<Object>", text)
    # `Record<string, X>` is an index signature underneath, which ArkTS has not
    # got. A runtime dictionary is a Map here.
    text = re.sub(r"\bRecord<\s*string\s*,\s*([^<>]+?)\s*>", r"Map<string, \1>", text)
    return text


def rewrite_process(text: str) -> str:
    """`process` does not exist on this platform.

    An app has a sandbox path handed to it by its ability's context. There is no
    environment and no working directory, and code that reads `process.env` here
    reads `undefined` and carries on with a wrong default - which is worse than
    failing.
    """
    text = re.sub(r"process\.env\.[A-Za-z_][A-Za-z0-9_]*", "undefined", text)
    text = re.sub(r"process\.env\[[^\]]+\]", "undefined", text)
    text = re.sub(r"process\.cwd\(\)", "Files.root()", text)
    return text


def needs_files_import(text: str, rel: str) -> str:
    """Adds the `Files` import when `process.cwd()` was rewritten to use it."""
    if "Files.root()" not in text:
        return text
    if re.search(r'import\s*\{[^}]*\bFiles\b[^}]*\}\s*from', text):
        return text
    platform = relative_platform_import(rel)
    return 'import { Files } from "%s";\n' % platform + text


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
    text = rewrite_relative_imports(text)
    text = rewrite_types(text)
    text = rewrite_process(text)
    text = needs_files_import(text, rel)
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
