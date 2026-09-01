#!/usr/bin/env python3
"""Type-checks the ported ArkTS tree.

WHAT THIS PROVES AND WHAT IT DOES NOT. `tsc --strict` is a WEAKER gate than the
ArkTS compiler: ArkTS additionally bans structural typing of object literals,
restricts `Object`, and forbids several things TypeScript accepts. Passing here
is necessary and not sufficient - it says the port is type-correct TypeScript,
not that DevEco will build it. That remains unverified until the HarmonyOS SDK
is on a machine here, and this script says so rather than letting a green run
read as more than it is.

WHAT IT DOES CATCH is the whole class of defect a mechanical port introduces: a
type rewritten without its call sites, a substituted literal flowing into a
typed position, an import that resolves to nothing. Those are the ones that made
the first pass wrong.

`.ets` is copied to `.ts` in a scratch tree because `tsc` does not read `.ets`.
Imports are extensionless in ArkTS, so nothing has to be rewritten - the copy is
byte-for-byte and the errors map back line for line.
"""
from __future__ import annotations

import io
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
ETS = os.path.join(ROOT, "harmonyos", "src", "main", "ets")
SCRATCH = os.path.join(ROOT, "harmonyos", ".typecheck")

TSCONFIG = {
    "compilerOptions": {
        "target": "ES2021",
        "module": "ES2022",
        "moduleResolution": "bundler",
        # ArkTS has no `any` at all, so `noImplicitAny` is not optional here -
        # an implicit `any` that TypeScript tolerates is a hard error there.
        "strict": True,
        "noImplicitAny": True,
        "strictNullChecks": True,
        "noEmit": True,
        "skipLibCheck": True,
        # No DOM and no Node: the port runs on a phone, and a check that hands
        # it `window` or `process` passes code that will fail on the device.
        "lib": ["ES2021"],
        "types": [],
    },
    "include": ["**/*.ts"],
}


def stage() -> int:
    if os.path.isdir(SCRATCH):
        shutil.rmtree(SCRATCH)
    count = 0
    for root, dirs, files in os.walk(ETS):
        for name in files:
            if not name.endswith(".ets"):
                continue
            rel = os.path.relpath(os.path.join(root, name), ETS)
            out = os.path.join(SCRATCH, rel[: -len(".ets")] + ".ts")
            os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copyfile(os.path.join(root, name), out)
            count += 1
    io.open(
        os.path.join(SCRATCH, "tsconfig.json"), "w", encoding="utf-8", newline="\n"
    ).write(json.dumps(TSCONFIG, indent=2) + "\n")
    return count


def main() -> None:
    staged = stage()
    print("staged %d modules" % staged)
    tsc = os.path.join(ROOT, "harmonyos", "node_modules", ".bin", "tsc")
    result = subprocess.run(
        [tsc, "-p", SCRATCH, "--pretty", "false"],
        capture_output=True,
        text=True,
    )
    lines = [l for l in (result.stdout + result.stderr).split("\n") if l.strip()]
    errors = [l for l in lines if ": error TS" in l]

    if "--summary" in sys.argv:
        # The error CODES, most frequent first. A mechanical port fails the same
        # way thousands of times, and the shape of the failure is what tells you
        # which rewrite was wrong - the individual lines do not.
        counts: dict[str, int] = {}
        for line in errors:
            code = line.split(": error ")[1].split(":")[0]
            counts[code] = counts.get(code, 0) + 1
        for code, n in sorted(counts.items(), key=lambda kv: -kv[1])[:20]:
            sample = next(l for l in errors if ": error %s:" % code in l)
            print("%6d  %s  %s" % (n, code, sample.split(": error ")[1][:110]))
    else:
        for line in errors[:60]:
            print(line)

    print()
    print("%d type errors in %d modules" % (len(errors), staged))
    print(
        "NOTE: tsc --strict is weaker than the ArkTS compiler. Zero here means "
        "type-correct TypeScript, NOT that DevEco will build it."
    )


if __name__ == "__main__":
    main()
