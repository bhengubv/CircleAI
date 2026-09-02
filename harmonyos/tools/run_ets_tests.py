#!/usr/bin/env python3
"""Runs the ArkTS (`.ets`) tests.

WHY THEY WERE NOT RUNNING. `npm test` globs `tests/*.test.ts`, and these are
`.ets`. Node cannot resolve a `.ets` import either, so even naming them
differently would not have been enough — importing
`../src/main/ets/memory/index` fails because the file on disk is `index.ets`.

So seven test files sat in the tree and had never run once. They are not
duplicates of the `.ts` tests: `AffectStateTest`, `CompanionTypesTest` and
`LanguageRegistryTest` are each roughly twice the size of their `.ts`
namesakes, and `AffectVadTest`, `AnomalySignalTest`, `BiometricMatcherTest` and
`GoalProgressTest` have no `.ts` counterpart at all.

HOW THIS RUNS THEM. The `.ets` sources and tests are staged as `.ts` into
`.ets-run/`, keeping the same relative depth so every `../src/main/ets/...`
import still resolves — to the staged copy. Each test is then executed with
`tsx`.

The tests need no framework: each is a series of self-executing blocks that
throw on failure and print `PASS:` lines, so running the module IS the test and
a non-zero exit is a failure.
"""
from __future__ import annotations

import io
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                      # harmonyos/
STAGE = os.path.join(ROOT, ".ets-run")
TSX = os.path.join(ROOT, "node_modules", ".bin", "tsx")


#: Wires the platform the way an ability does, using Node's own primitives.
#:
#: The shims REFUSE when nothing is bound - a digest or a random source that
#: quietly returned something weak is the failure they exist to prevent - so a
#: test touching either needs this. Node's crypto is the same class of primitive
#: the device provides, so the code under test runs for real.
PLATFORM_PREAMBLE = """\
import { createHash, randomUUID as nodeRandomUUID, randomFillSync } from 'node:crypto';
import { bindPlatform } from '../src/main/ets/platform';

bindPlatform({
  sha256: (text: string) => createHash('sha256').update(text, 'utf8').digest('hex'),
  randomBytes: (count: number) => {
    const out = new Uint8Array(count);
    randomFillSync(out);
    return out;
  },
  sandboxDirectory: () => '/tmp/circleai-ets-tests',
  cpuCount: () => 1,
});
"""


def stage() -> list[tuple[str, str]]:
    """Copies `.ets` to `.ts` under .ets-run, preserving relative layout."""
    if os.path.isdir(STAGE):
        shutil.rmtree(STAGE)

    tests: list[tuple[str, str]] = []
    for sub in ("src/main/ets", "tests"):
        source_root = os.path.join(ROOT, sub)
        if not os.path.isdir(source_root):
            continue
        for base, dirs, files in os.walk(source_root):
            dirs[:] = [d for d in dirs if d not in ("node_modules", "oh_modules")]
            for name in files:
                if not name.endswith(".ets") or name.endswith(".d.ets"):
                    continue
                rel = os.path.relpath(os.path.join(base, name), ROOT)
                out = os.path.join(STAGE, rel[: -len(".ets")] + ".ts")
                os.makedirs(os.path.dirname(out), exist_ok=True)
                shutil.copyfile(os.path.join(base, name), out)
                if sub == "tests":
                    # The platform is bound BEFORE the test body, which is a
                    # series of self-executing blocks - anything appended after
                    # them would run too late.
                    body = io.open(out, encoding="utf-8").read()
                    io.open(out, "w", encoding="utf-8", newline="\n").write(
                        PLATFORM_PREAMBLE + body)
                    tests.append((name, out))
    return sorted(tests)


def main() -> int:
    if not os.path.exists(TSX):
        print("tsx is not installed; run npm install in harmonyos/")
        return 1

    tests = stage()
    if not tests:
        print("no .ets tests found")
        return 1

    failed: list[str] = []
    passed = 0
    for name, path in tests:
        result = subprocess.run(
            [TSX, path], capture_output=True, text=True, cwd=ROOT
        )
        # Each file prints one PASS line per case and throws on the first
        # failure, so the count is what actually ran rather than what exists.
        cases = sum(1 for line in result.stdout.split("\n") if line.startswith("PASS"))
        if result.returncode == 0:
            passed += cases
            print("  ok   %-28s %d checks" % (name, cases))
        else:
            failed.append(name)
            tail = (result.stderr or result.stdout).strip().split("\n")
            print("  FAIL %-28s %s" % (name, tail[0] if tail else "no output"))
            for line in tail[1:6]:
                print("       %s" % line)

    shutil.rmtree(STAGE, ignore_errors=True)

    print()
    print("ets tests: %d files, %d checks passed, %d files failed"
          % (len(tests), passed, len(failed)))
    if failed:
        print("failed: %s" % ", ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
