# CircleAI

## Ask the memory first

There is a local long-term memory on this machine and it already holds what was
worked out here — decisions, standing rules, and the roads that turned out to be
closed. **Ask it before starting something that might have been done before, and
tell it when something is decided, resolved, or fails.**

```bash
memory recall --doing deploy --to android --brief
```

It answers in about 180 ms, it never blocks, and an empty answer exits zero.

**This repo carries its own memory in `./memory`, and it holds the skills.**
There is a curated skills library on this machine — mobile, UI, accessibility,
testing, security and sixty-odd more — and asking the memory is how you find the
one that applies:

```bash
CIRCLEAI_MEMORY=./memory memory recall "mobile android offline permissions" --brief
```

Read the skill it names before starting. Every one of them was sitting on disk
during the week this app was built and none was opened; the QA axis that would
have caught the same locale bug five times was in one of them.

[MEMORY.md](MEMORY.md) is the short version; [AGENT.md](AGENT.md) is the
contract. If `memory` is not on PATH, the two install lines are in AGENT.md.

## What is NOT done

[docs/OPEN-GAPS.md](docs/OPEN-GAPS.md) is the register of what is built and
unreachable, what is blocked at the native layer, and what has never run on a
phone. **Read it before claiming any capability works.**

The repo's most common defect by a wide margin is a library that was written,
tested, committed, and then had no way in from the app. Vision was catalogued
with two models and full hashes, the bridge could encode an image, the session
method was complete - and the abilities screen offered a 311 MB download to a
screen that did not exist. Translation has an engine with zero consumers while
the app hand-rolls its own prompt. A grep for callers is worth more than a
green test run when the question is "does this work".

## Building and testing

```bash
dotnet build src/CircleAI.Memory/CircleAI.Memory.csproj
```

```bash
dotnet test tests/CircleAI.Tests/CircleAI.Tests.csproj -f net10.0
```

Everything in `src` multi-targets **net9.0 and net10.0** except the two Android
heads, and the test project runs both legs. Run one framework while iterating;
run both before calling it done — a green net10 leg has hidden a net9 break
before.

That sentence was FALSE for a long time and this file asserted it anyway.
Fourteen libraries were net10.0-only, the three `CircleAI.Assistant` projects
among them — so a developer on net9.0 could reference 154 libraries and not the
product. Nothing caught it: a net10-only library builds fine, every consumer
that can see it is also net10, and the net9 test leg stayed green because the
test project could not reference those libraries at all. They turned out to need
nothing from net10; they were net10-only because that is what a new project
defaults to.

`TargetFrameworkTests` now asserts it against the project files, so the claim in
this paragraph is checked rather than believed.

**There are SEVENTEEN test projects. This file said TWO, and that sentence cost a
whole day of verification that sounded complete and was not. It then said SIXTEEN
until 2026-10-28 — right when written, stale eight days later:
`tests/CircleAI.DeviceTests` landed on 2026-10-08 with "the last three android
projects fold in" and nothing here counted it. A number in this file with nothing
asserting it has now gone wrong twice.**

`docs/BUILD.md` says `dotnet test CircleAI.sln` — "everything at once,
recommended" — and the solution does name **15 of the 17**. **But I could not
make that command work on this box**, and this file is not going to tell you it
did: it ran for **85 minutes and produced not one test result** before it was
killed.

Whether that was a stall or just a very slow build of 186 projects, I do not
know. An earlier version of this paragraph called it a stall on the evidence
that the parent `dotnet` process had used only 48 seconds of CPU — **that
inference was wrong.** A Release Android build in its AOT phase shows the same
signature (23 s of parent CPU over 10 minutes) while writing a thousand files a
minute, because the work is in child `clang`/`llvm` processes. If you need to
tell the two apart, watch `obj/` for recent writes, not CPU.

Either way it is unverified, and `never-hog-the-dev-workstation` says a build
that size does not belong on this box in the first place.

**What was verified, project by project, on both legs: sixteen passed.** That
count predates `CircleAI.DeviceTests`, which has never been run that way and
cannot be: it targets `net10.0-android` only and is a MAUI head with a
`MainActivity`, so `dotnet test` on this box has nothing to execute. Run the
others individually — it is also far faster to iterate on:

```bash
dotnet test tests/CircleAI.Tests/CircleAI.Tests.csproj -f net10.0
```

```bash
for p in tests/*/*.csproj; do dotnet test "$p" --nologo -v q; done
```

The loop does not care what any solution file contains, which is the point of it —
but it does not cover everything either, and this file said it did.
`CircleAI.DeviceTests` is an android head: the loop reaches it and `dotnet test`
has nothing to run. **TWO projects are in no solution at all, not one** — that
android head, and the bUnit suite below:

```bash
dotnet test tests/CircleAI.Samples.Ui.Tests/CircleAI.Samples.Ui.Tests.csproj
```

**390** bUnit tests over the screens, and **no solution builds that project** — so
when it once stopped COMPILING it simply stopped running, and stayed that way
with every other suite green. It builds and passes green today (all 390 in 15 s,
run 2026-10-28 via the command above), but nothing *guards* it: it can rot
again the moment a screen change breaks the build unnoticed.

**THREE DOCS HELD THREE COUNTS FOR THIS ONE SUITE** — 344 here, 308 in
`docs/OPEN-GAPS.md` and `ROADMAP.md` — and all three were wrong. The suite nobody's
solution builds was also the one nobody could count, which is the same defect
twice: there is nothing to read the number off, so each file kept its own. Run it
and read the line; do not trust a prose figure for it, including this one. It was found by
accident — no solution would have missed it if any solution contained it.

`tests/CircleAI.Tests` is the big one — **5,107 per leg, 10,214 over both** — and
it is the one people mean by "the tests".

This line said "~3,270 over both legs" until 2026-10-24, which is the same
figure as the "3271 passed" two sentences down: **the count of one incomplete
session had become this file's description of the project.** It was out by three
times, and it is the reason the twenty-minute budget below ever looked plausible.

The other fourteen were holding 527 more when somebody last counted, and that
was not today — read it as a floor, not a figure. And this file's "TWO" is why a
session ran two projects, reported "3271 passed", and believed it.

**IGNORE `CircleAI.slnx`.** Two solution files disagree about what this product
is. Counted 2026-10-28: `CircleAI.sln` lists **186 projects, 15 of them tests**;
`CircleAI.slnx` lists **15 and 2**. Tooling increasingly prefers the newer
`.slnx`, so the file that knows about a twelfth of the product is the one a fresh
checkout is most likely to open.

**THE REASON GIVEN HERE WAS WRONG, AND SO WAS THE EVIDENCE FOR IT.** This said the
`.slnx` "was created in the May rebrand and has not been touched since 24 July",
and called it the stale one. Both files were last written on **2026-10-21, the
same day**. It is not out of date; it is incomplete — which is worse, because a
file somebody keeps editing while it describes a twelfth of the tree reads as
maintained. Do not reach for a modification date to decide which of two files to
trust: count what is in them.

The main suite is **5,107 tests per leg** and takes **fifteen to twenty-nine
minutes per leg** — not the "six to nine" this file claimed, and not the "two to
three" it claimed before that. Measured here on 2026-10-24, cold build included:

| leg | quiet box | box under load |
|---|---|---|
| net9.0 | 14 m 46 s | 28 m 05 s |
| net10.0 | 20 m 11 s | 28 m 58 s |

So budget **forty minutes for both legs on a quiet box, an hour on a busy one**,
plus the UI suite. Do not read a quiet terminal at minute five — or minute
fifteen — as a hang.

**That stale twenty-minute figure was not merely bad advice.**
`tests/CircleAI.Tests/bhengu.runsettings` capped `TestSessionTimeout` at fifteen
minutes to match it, and the project hardcodes that file via
`RunSettingsFilePath` — so the documented command above **aborted at minute
fifteen and printed `Failed!`** with about 54 tests never run. That is
indistinguishable from a real break, and it cost most of a day.

**And an abort does not reliably announce itself.** Dropping that cap to ten
seconds deliberately, to prove the file governs, printed this:

```
Aborting test run: test run timeout of 10000 milliseconds exceeded.
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 9 s
Test Run Aborted.
```

Fifteen tests of 5,107, and the summary line says **`Passed!`**. Only the
`Test Run Aborted` line and the exit code tell the truth. Read the count, not
the word.

The cap is forty-five minutes now and the comment in that file carries these
measurements, so the two numbers can no longer drift apart.

Serialise heavy builds rather than running two at once.

## Where things are

| | |
|---|---|
| `src/CircleAI/` | ONE library, **176 modules as subfolders**; `Core` holds the interfaces the rest build on |
| `src/CircleAI/Memory` | episodes, atoms, recall, the append-only log |
| `src/CircleAI/Memory.Sql` | the same store on PostgreSQL, SQL Server, MySQL, Oracle |
| `tools/` | small runnable programs — `memory`, `voice-audit`, `tts-speak`, `stt-hear` |
| `samples/CircleAI.Samples.Hybrid` | the IT! sample: MAUI Blazor hybrid and web off one shared Razor library |
| `tests/CircleAI.Tests` | one project, both frameworks |

**`src/CircleAI.*` WAS THE PRE-3.8.0 SHAPE AND THIS TABLE KEPT IT FOR TWO
RELEASES**, which is why a reader looking for `src/CircleAI.Voice` finds nothing
and may conclude the feature is gone. There are **three** projects under `src/` —
`CircleAI`, `CircleAIService` and `CircleAI.Inference.Server` — plus
`src/CircleAI.Maui`, an empty directory with **no tracked files** that is in
neither solution and still counts as a module. The libraries the old row meant are
subfolders of `src/CircleAI`.

The count is **176**, and unlike "~150" it is now checked rather than stated:
`tools/gen-services/gen_services.py` counts the tree, and `ServiceCoverageTests`
counts it independently in C# and fails if the generated census disagrees. "~150"
was asserted by nothing — and the generator that was supposed to own the number
had been reading the old `src/CircleAI.*` layout since 3.8.0, so it saw **2
modules of 176** and its "anything unclassified is an ERROR" guard could not fire
for anything. Fixed 2026-10-28.

`tools/` projects are not in the solution. Run them with
`dotnet run --project tools/<name> -- <args>`.

## Two things that cost a day each

**Deploying to Android wipes the app's data — unless you ask for an APK.**
A **Release** `-t:Install` uninstalls first and takes the downloaded models with
it; on the P30 that is 2 GB. Add one flag and it does not:

```bash
dotnet build <proj> -c Release -f net10.0-android -t:Install \
  -p:AndroidPackageFormat=apk -p:AdbTarget="-s <serial>"
```

The cause is the package format, not `EmbedAssembliesIntoApk`. `Install` depends
on `_DeployApk` and `_DeployAppBundle`; the first is `adb install -r` — an
in-place update — and the second uninstalls before installing. Release defaults
`AndroidPackageFormats` to `aab;apk`, which selects the bundle path;
`EmbedAssembliesIntoApk` only chooses the harsher of two uninstalls once you are
already on it. Debug builds an APK and has never wiped anything.

Do NOT use `InstallKeepingData`: **that target does not exist.** It fails with
`MSB4057` and `dotnet build` still exits 0, so nothing installs and nothing says
so. Verify a deploy with `adb shell dumpsys package <pkg>` — `lastUpdateTime`
moving means it landed, `firstInstallTime` NOT moving means the data survived.

### `-t:Install` installs nothing on the current SDK — build, then `adb install -r`

Measured 2026-10-09 on `10.0.400-preview.0.26322.102`, deploying `CircleAIService`
to the P30. The command above **exits 0, writes a fresh signed APK, and does not
install it** — `lastUpdateTime` never moves.

Re-run at `-v n` and the reason is visible: MSBuild logs
`Project "CircleAIService.csproj" on node 1 (Install target(s))` on entry, and
then **no target whose name contains "Install" ever executes**. The build ends at
`_BuildApkEmbed`. There is no `MSB4057`, so the target exists — it simply does
nothing, and there is not one `adb` line in 1817 lines of output.

So the deploy is two steps, and the second one is the install:

```bash
dotnet build <proj> -c Release -f net10.0-android -m:1 -p:AndroidPackageFormat=apk \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=<ks> -p:AndroidSigningKeyAlias=<alias> \
  -p:AndroidSigningKeyPass=<pw> -p:AndroidSigningStorePass=<pw>
```

```bash
adb -s <serial> install -r <proj>/bin/Release/net10.0-android/<pkg>-Signed.apk
```

`adb install -r` is an in-place update: it moves `lastUpdateTime`, leaves
`firstInstallTime` alone, and **keeps the model store** — 270 MB on the P30 that
session, up to 22.8 GB on Circle OS. Sign with the real key or the certificate
will not match the installed app and `-r` is refused; an unsigned Release
silently debug-signs, which is what the `CIRCLEAI012` guard exists to catch.

Do not reach for "uninstall first" to get past this. It works and it costs the
models, and it is never necessary — the install above replaces the app in place.

**One fact with two owners always ends up with two answers.** The language count,
the model choice, the wake phrase and the app language each lived in three or
four places in the sample, and every one of them disagreed. When adding
something a screen displays, find who else displays it and make them read the
same source.
