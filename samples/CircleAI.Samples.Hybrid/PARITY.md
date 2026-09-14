# Parity audit — native head vs hybrid

Every user-facing capability in `samples/CircleAI.Samples.Android`, checked
against the hybrid. Taken from the code, not from memory: the control list is
every `Ui.Action`, `Quiet`, `Compact` and `Hint` string in the native activities,
plus the behaviours that have no button.

Status is one of **in**, **missing**, or **deliberate** (absent on purpose, with
the reason stated).

> ## ⚠️ Third pass, 2026-09-13: twenty-three gaps, not zero.
>
> The pass below found five. A method-by-method audit across all 26 native
> files, on the same day, found **twenty-three** - and the five were the easy
> kind: whole screens, which a file-name comparison can see. The rest were the
> kind it cannot.
>
> **A file-name audit passes while behaviour differs.** Both heads "say
> something at startup"; they said different things. Both have a loading screen;
> one of them held you on it for 800 MB. Both stream a reply; one of them read
> the JSON of a tool call out loud in a human voice.
>
> **A control-label audit produces false positives too** - "Swap sides" was read
> as missing and is at `Translate.razor:58` under another label.
>
> All twenty-three are closed. What changed about how that is checked:
>
> - `EveryPageRendersTests` was a **hand-written list of fourteen `[Fact]`s**,
>   and four pages had been added without anybody adding a line - so Find,
>   Seeing, Music and Transcribe had no coverage whatever. It now asks the
>   assembly which components carry a `RouteAttribute`, which is the same
>   question the Router asks at runtime. A page that is reachable is a page that
>   is tested, and adding one is no longer something anybody can forget.
> - `WireEverything` registered neither `IMakesMusic`, `IPlaysMedia` nor
>   `IKeepsTranscripts`, so three of those pages **could not be constructed by
>   the test container at all**. Two ways of not noticing, each looking like the
>   other's job.
> - **The web head had never compiled.** Not "was broken" - had never produced a
>   DLL. Every `Browser*.cs` was missing a `using CircleAI.Assistant`, because
>   the MAUI services sit in `namespace CircleAI.Assistant.Device` and get the
>   SDK types free from enclosing-namespace lookup, while the browser ones sit
>   in `CircleAI.Samples.Web.Client.Services` and got nothing. Thirty-eight
>   CS0246s. `CircleAI.Samples.Web` additionally still referred to itself by its
>   pre-rebrand name in four places and served `_content/CircleAI.Assistant/`
>   for assets that live in `CircleAI.Samples.Shared`.
>
> Prefer the tests to this table. It has now been wrong three times.
>
> ## ⚠️ Re-verified again on 2026-09-13, and it had aged past the app a second time.
>
> This file said **"Nothing is missing"** and was wrong in five ways. Three whole
> screens had no row in it at all - the native head had grown Seeing, Music and
> Search since the last pass, and a table that only lists what it already knew
> about cannot catch a new gap. That is the failure mode of a hand-kept ruler,
> and it is the second time this document has had it.
>
> Corrected below. What was actually missing, and is now ported:
> `Seeing.razor`, `Music.razor`, `Find.razor`, the declined-model memory, and
> `CircleAISpeaker.SideloadFolder`.
>
> **A row that says "in" is only as good as the date beside it.** Prefer
> `AppRoutes` - which is code, is the single owner of every route and surface,
> and has a test - over anything asserted here.

> **Re-verified row by row against the code on 2026-09-02.** Four rows were
> wrong, all in the same direction — the document had aged past the app. The
> screens table was missing three real screens; the battery-exemption row said
> *missing* for something that is implemented AND wired; the wake-phrase row
> described a design that was tried and deliberately reverted; and the Career
> row carried a caveat about the microphone that no code supports. Corrected
> rows are marked ✅ **corrected**, with the evidence named so the next reader
> can re-check in one grep rather than trusting this file.

## Screens

| Screen | Native | Hybrid | Status |
|---|---|---|---|
| Home / the circle | `HomeActivity` | `Home.razor` | in |
| Languages | `LanguagePickerActivity` | `Languages.razor` | in |
| What it can do | `AbilitiesActivity` | `Abilities.razor` | in |
| Typing / chat | `MainActivity` | `Chat.razor` | in, partial — see controls |
| Your CV | `CareerActivity` | `Career.razor` | in |
| Aim at a job | `JobSpecActivity` | `JobSpec.razor` | in |
| Hey B | `WakeWordActivity` | `WakeWord.razor` | in — **verified listening on the P30 2026-09-02**. It had never worked: the APK shipped a glibc `libonnxruntime.so` that the RID graph substituted for the Android one, so `ZipformerKwsSpotter` could not construct. Fixed in `Directory.Build.targets`. |
| Setting it up | `FirstRun` + `SetupTour` | `Setup.razor` | in — `Readiness` drives it; the native `SetupTour` type is not linked, the behaviour is folded in |
| Settings | — | `Settings.razor` | new in hybrid |
| **You** | — | `You.razor` | ✅ **corrected** — new in hybrid, absent from this table until 2026-09-02 |
| **Services** | — | `Services.razor` | ✅ **corrected** — new in hybrid, was absent from this table |
| **Translate** | — | `Translate.razor` | ✅ **corrected** — new in hybrid, was absent from this table |
| Loading / 404 | — | `Loading.razor`, `NotFound.razor` | new in hybrid — routing infrastructure, no native counterpart |
| **Read a picture** | `SeeingActivity` | `Seeing.razor` | ✅ **corrected 2026-09-13** — was **missing** and had no row here at all. Vision existed only as an attach button inside `Chat.razor`. The new page also RESIZES, which the chat path still does not: it asks `IBrain.MaxImageEdge` and shrinks in the browser layer before a byte is read. |
| **Make music** | `MusicActivity` | `Music.razor` | ✅ **corrected 2026-09-13** — was **missing**, no row here, and `CircleAI.Music` had no other consumer in the repo, so retiring the native head would have retired the feature. Behind `IMakesMusic`; a tab answers `Available: false`. |
| **Find** | `SearchActivity` | `Find.razor` | ✅ **corrected 2026-09-13** — was **missing**, no row here. It was the only consumer of the RECALL half of memory anywhere: `LearnAsync` has run on every utterance for months with nothing able to read any of it back. Uses the injected `IRemembers`, NOT `CircleAISession.SearchAsync` — see the note below. |
| Bench | `BenchActivity` | — | **deliberate** — a dev harness, `Exported = true`, launched by adb, reachable from no screen (verified: nothing in the tree navigates to it). Porting it would put a test rig in a shared UI it never appears in. |

No hybrid page is a stub: the smallest real screen is `Services.razor` at 86
lines, and `NotFound.razor` at 16 is a 404 by design.

## Controls

| Control | Where | Status |
|---|---|---|
| Turn on | Abilities | in |
| Next / Say it / Say it or type it | Your CV | ✅ **corrected** — in. This row used to add *"'Say it' reports that the microphone is not wired"*; no such message exists. `Career.razor` renders `"Say it (I will check)"` or `"Say it"` and nothing else. |
| 10 plus languages | Home | in |
| What it can do | Home, chat | in |
| Aim my CV at this job | Aim at a job | in |
| Paste the job advert here | Aim at a job | in |
| Search 10 plus languages | Languages | in |
| Type a message / Send | Chat | in |
| Read an image | Chat | in — `Brain.SeeAsync` at `Chat.razor:344` |
| Speak in 10 plus languages | Chat | in — hero button, its own row |
| Use the mic | Chat | in — `Talk.TurnAsync` at `Chat.razor:318` |
| Read it out | Chat | in — renamed from "TTS", which nobody outside the project could guess at |
| Run the tool check | Chat | **missing** — a diagnostic probe, not a product control. Verified: no `ToolCheck` anywhere in the hybrid. |

## Behaviours with no button

| Behaviour | Native | Status |
|---|---|---|
| Readiness drives the headline | `Readiness` | in — `Home.razor`, `Loading.razor`, `Setup.razor`, `DeviceSetup.cs` |
| Setup tour during the wait | `SetupTour` | in — folded into `Setup.razor`; the native type itself is not linked |
| Choose a language, persistently | `SpokenLanguage.Choose` | in — `StoredSpokenLanguage` |
| Hand control back to detection | `SpokenLanguage.ClearChoice` | in — **the native never calls it** (verified: no call site outside its own declaration); `DeviceSettings.cs:93` does |
| Wake phrase, its own language | `ResidentWakeWord.KeywordsFor` | ✅ **corrected** — in, but **NOT decoupled**. This row claimed the hybrid detached the wake phrase from the answering language. It did, and then reverted: a separate wake language "produced a control that let somebody run the app in English and wake it with ビーさん". The phrase now follows the app's language, and the original bug — choosing a language silently changing the phrase — is fixed by SHOWING the phrase on the settings screen. See the remarks on `DeviceWakeWord`. |
| Speaking replies aloud | `SpokenReply` | in — `IVoiceHost.SayAsync` speaks arbitrary text, not just the checked greeting |
| Voice turn | `VoiceTurn` | in — verified genuinely linked, not reimplemented: `<Compile Include="..\..\CircleAI.Samples.Android\VoiceTurn.cs" />`, so its P30-tuned thresholds carry over |
| Greeting cycle | `HomeActivity.SpeakNext` | in — `Home.razor` cycles zu → af → st → sw → en through `Voice.SpeakAsync` while the brain is still arriving |
| Language read-out line | `HomeActivity._lang` | in — `Home.razor` renders "Answering in …", deliberately empty until a turn has been heard |
| **Battery exemption prompt** | `HomeActivity.AskToKeepRunning` | ✅ **corrected** — **in**, not missing. `DeviceSetup.AllowBackgroundAsync` is ported "vendor list and all" (Huawei, Xiaomi, Oppo, Vivo: standard intent first, vendor screen after, every call wrapped), and it is WIRED — `Setup.razor:202` calls it. This row previously read *missing*, on the step it itself calls the one that decides whether the rest survives. |
| Always-on assistant | `ResidentAssistant` | **in, and verified end to end on the P30 2026-09-02** — `DeviceResidentAssistant`, turned on at Settings › Phone › "Answer to its name". `ResidentWakeWord` is LINKED from the native head; the orchestration is re-expressed against `ISpokenLanguage` rather than copied, because the native file reads its own SharedPreferences store and this app would then have two. Measured: `resident listening: on`, service `isForeground=true` with its notification posted, `capture: VoiceRecognition + 2 effect(s)`, process alive with no ANR. Two defects had to be fixed first — the glibc ONNX substitution above, and a missing `FOREGROUND_SERVICE` permission in this head's manifest that had been refusing the service since it existed. |
| Start on boot | `BootReceiver` | in — `BootReceiver` LINKED from the native head, registered with all three actions (`BOOT_COMPLETED` plus the two `QUICKBOOT_POWERON` variants Huawei and HTC send instead), and `RECEIVE_BOOT_COMPLETED` declared. `DeviceResidentAssistant` writes the `ResidentPrefs` consent flag it reads — verified on the device: `<boolean name="was-running" value="true" />`. **Not yet seen to FIRE**: `BOOT_COMPLETED` is a protected broadcast and the receiver's own `android:permission` refuses a simulated one from adb, so only a real reboot exercises it. Restores the MODELS only — from Android 14 a microphone-typed service may not start from boot, so listening waits for one deliberate tap by design. |
| Earcons | `Earcon` | in — `Earcon` LINKED from the native head. `Woke()` plays in `DeviceResidentAssistant.OnWoke`, so it sounds in the always-on path whether or not a screen is watching; `Heard()` in `TurnAsync` once there are words; `CannotSpeak()` around `SayAsync`. **Not yet HEARD**: both triggers need audio into the microphone, which adb cannot provide. |
| **Turning an ability off stays off** | `SetupPrefs` | ✅ **corrected 2026-09-13** — was **missing and had no row**. `FirstRun.Plan` takes a `declined` predicate and only the native head passed it; `DeviceSetup` called `Plan` twice with no `declined` at all, so removing an ability and reopening the app re-downloaded it. Now `DeclinedModels` in `src/CircleAI.Assistant.Device`, consulted at both call sites, cleared by `DeviceFacts.TurnOnAsync`. |
| **Sideloaded voice found at all** | `CircleAISpeaker.SideloadFolder` | ✅ **corrected 2026-09-13** — set only by the native head. Read at `CircleAISpeaker:841` and twice in `CircleAITtsProbe`, so a bundle the owner copied onto the phone was invisible here and the app offered to download something already on the device. Now set in `MainApplication.OnCreate` — the third such seam after the phonemizer and the memory probe. |
| **Transcripts are kept** | `CircleAISession.Transcripts` | ✅ **corrected 2026-09-13** — was **missing**. `IKeepsTranscripts` is registered in `MauiProgram`; `Transcribe.razor` keeps a recording rather than discarding it on the way out, and can export subtitles. The two web heads register `KeepsNoTranscripts.Instance`, which is the honest answer for a tab with nowhere to put somebody's meeting. |
| Sideloaded bundle import | `CircleAITtsProbe`, `WakeWordActivity` | partial — `DeviceVoiceHost` carries the sideload-before-download path; the wake screen's does not |
| **Tool calls are not read aloud** | `HomeActivity.LooksLikeToolCall` | ✅ **closed 2026-09-13** — was **severe**. Every fragment went to `mouth.Push`, so asking about the weather made the phone recite JSON in a human voice. `ToolCall.Looks` is now in `CircleAI.Assistant` with tests, and `DeviceConversation` suppresses the spoken stream and re-runs the turn through `AskWithToolsAsync`, which had zero callers outside the native head. |
| **The model is told which language to answer in** | `MainActivity:731` | ✅ **closed 2026-09-13** — setting the voice alone gives English words spoken with Zulu phonetics. `DeviceConversation` now appends the instruction; `CircleAISpeaker.NameForLanguage` had one caller in the repo and it was native. |
| **You can talk to it while it downloads** | `HomeActivity:236`, `Readiness:94` | ✅ **closed 2026-09-13** — `Loading.razor` awaited the WHOLE plan before navigating, full screen, no bottom bar: ~800 MB with no way out and the tour unreachable because you never reached Home. It now hands over the moment `Readiness.CanTalk` is true — the plan is ordered voice-then-ears-then-brain precisely so that moment comes early — and `Home.razor` attaches to the run in flight and shows the bar and the tour. `ISetup.RunAsync` was already idempotent and `IsRunning` already existed, for exactly this; nothing had used either. |
| **A wake fallback when the service cannot hold the mic** | `HandsFree.cs` | ✅ **closed 2026-09-13** — Huawei, Xiaomi, Oppo and Vivo stop foreground services whatever the notification says, and on those phones `StartAsync` returned `Failed` and stopped: the headline feature absent on a large share of exactly the handsets this is for. `ScreenUpWakeWord` in `src/CircleAI.Assistant.Device` listens while the app is on screen, tied to `AppLifecycle.Paused`/`Resumed` so the microphone is given back when it goes away. Reported as `ResidentState.ScreenOnly` — on, with the caveat shown, rather than a switch that silently means less than it says. |
| **Personal respellings** | `MainActivity:846-920` | ✅ **closed 2026-09-13** — `CircleAISpeaker.RespellingEngine` had one caller and it was native. `PersonalSpeech` now holds the table for the process: `DeviceConversation` learns from each transcript (free — the words arrive already spelt the way this person says them) and `DeviceVoiceHost` rewrites the text on the way to the synthesiser. Without both halves the learning changes nothing anybody hears. |
| **Pocket-TTS side-load** | `LanguagePickerActivity:478` | ✅ **closed 2026-09-13** — `RunPocketAsync` had one caller and it was native, so the five-second voice cloning this engine exists for was unreachable here. `CircleAITtsProbe.PocketBundleFor` owns the tag list now, with tests. It is **eight tags, six languages, and two of them are not European** — es-MX and pt-BR — which has been written down wrongly more than once. |
| **Music can leave the phone** | `MusicActivity:207-239` | ✅ **closed 2026-09-13** — the screen printed the app-private path and stopped: a real WAV in a folder nothing on the phone can browse to, which for the person holding it is the same as not having made anything. `IMakesMusic.ShareAsync` goes through the share sheet, the same route the CV takes. |
| **Engine log lines are not shown to people** | `LanguagePickerActivity:558` | ✅ **closed 2026-09-13** — somebody choosing a language watched their phone print `voice-under-test=af_ZA-google-nasional` and `phones=41` at them for forty seconds. `PlainProgress.Say` turns each into a phase, with tests including the ordering bug (`voice-under-test` has to be matched before `voice`). |
| **`SetupPhase.Checking` is rendered** | `SetupProgress.Describe` | ✅ **closed 2026-09-13** — the enum was added because a Redmi Note 12 Pro+ sat on "about 20 sec left" for over a minute while it hashed 1.3 GB with the network idle, and **the Blazor screen still rendered the frozen countdown, because nothing read the phase**. `SetupProgressReport.Describe()` now owns the line for both screens that show it. |

## The shape of what is left

Two behaviours, both parts of one capability: **staying alive to listen.**
`ResidentAssistant` — the always-on loop itself — is now written and wired.
What remains is `BootReceiver` (surviving a restart) and `Earcon` (the sound
that says it heard you).

### Two claims in this file were false, beyond the missing rows

**"LINKED from the native head."** Several rows said `VoiceTurn`,
`ResidentWakeWord`, `BootReceiver` and `Earcon` were compiled into this head by
`<Compile Include="..\..\CircleAI.Samples.Android\…">`. There are **no** source
links between the two samples and there were none on 2026-09-13: those files live
in `src/CircleAI.Assistant.Device` and arrive by project reference like anything
else. Verified by grepping every `Include=` in both csproj files.

**"Translate — new in hybrid."** The native head has `TranslateActivity`. It is
not new here; both had it.

### What the two samples still shared, and no longer do

Until 2026-09-13 this head reached into `samples/CircleAI.Samples.Android/` for
three binaries — `libespeak-ng.so`, `espeak-ng-data.zip` and `skills.db.zip` —
by relative path. One sample sourcing binaries for another is not a dependency
with a contract; it is a build depending on a sibling's folder layout, and it
meant the native head could not be deleted without silently removing voice and
skills from this one.

They now live with the libraries that cannot work without them:
`CircleAI.Voice/runtimes/android-arm64/native/`, `CircleAI.Voice/assets/` and
`CircleAI.Skills/assets/`. Neither sample sources anything for the other.

### Do not write "nothing is missing" in this file again

It has been written twice and been false both times — on 2026-09-02, and again
earlier on 2026-09-13, in the pass directly above this one, which then had
twenty-three gaps found after it by a different method on the same day.

The sentence is not wrong because the author was careless. It is wrong because
**a hand-kept table can only list what its author already knew about**, and the
gaps that matter are the ones nobody has thought of yet. A ruler that is taught
what to measure will always agree with itself.

So the honest statement is narrower, and it is this:

- **Every gap found by the 2026-09-13 method-by-method audit is closed**, and
  each has a row above naming the file on both sides.
- **The things that check it are code**: `AppRoutes` owns every route and has a
  test; `EveryPageRendersTests` now scans for `RouteAttribute` rather than
  listing pages by hand; the moved rules — `ToolCall`, `Trouble`, `Welcome`,
  `Greetings`, `PlainProgress`, `AbilityRules`, `SetupProgressReport.Describe`,
  `CircleAITtsProbe.PocketBundleFor` — each have tests written against the
  native original.
- **What no test can tell you** is whether a twenty-fourth gap exists. Three
  audits have now each found things the previous one did not.

The always-on assistant listens, verified on the P30 on 2026-09-02: the wake
word loads, the resident service holds the microphone in the foreground with
its notification posted, and the process survives. Two defects underneath had
to go first — a glibc `libonnxruntime.so` the RID graph substituted for the
Android one, and a `FOREGROUND_SERVICE` permission this head had never
declared, which had been refusing the service on every attempt since it
existed.

⚠️ **Two things are written and reachable but have never been seen to happen**,
and the table says so rather than implying otherwise: the boot receiver has not
been through a real reboot, and the earcons have not been heard. Both need
something a desk cannot supply — a power cycle, and a voice.

On the boot receiver, note the platform rule before treating it as a gap: from
Android 14 a microphone foreground service may NOT be started from
`BOOT_COMPLETED` at all. A boot receiver would bring the service back, not the
listening — after a reboot that needs one deliberate tap, by rule rather than by
omission.

The battery-exemption prompt used to be counted here and is not a gap — it is
implemented and reachable, which matters because it is the piece the other three
depend on: on Huawei, Xiaomi, Oppo and Vivo, a foreground service without that
exemption is killed whatever Android says.

The only other gap is the tool check, which is a diagnostic and not a product
control.

**On surface area the hybrid is ahead** — it has every native screen except the
Bench harness, plus five of its own. **On evidence it is behind**: the native
head is the one that has been deployed to the P30 and watched to work. Until
2026-09-02 the hybrid did not compile at all, for two unrelated reasons.
