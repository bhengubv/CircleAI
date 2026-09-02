# Parity audit — native head vs hybrid

Every user-facing capability in `samples/CircleAI.Samples.It.Android`, checked
against the hybrid. Taken from the code, not from memory: the control list is
every `Ui.Action`, `Quiet`, `Compact` and `Hint` string in the native activities,
plus the behaviours that have no button.

Status is one of **in**, **missing**, or **deliberate** (absent on purpose, with
the reason stated).

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
| Voice turn | `VoiceTurn` | in — verified genuinely linked, not reimplemented: `<Compile Include="..\..\CircleAI.Samples.It.Android\VoiceTurn.cs" />`, so its P30-tuned thresholds carry over |
| Greeting cycle | `HomeActivity.SpeakNext` | in — `Home.razor` cycles zu → af → st → sw → en through `Voice.SpeakAsync` while the brain is still arriving |
| Language read-out line | `HomeActivity._lang` | in — `Home.razor` renders "Answering in …", deliberately empty until a turn has been heard |
| **Battery exemption prompt** | `HomeActivity.AskToKeepRunning` | ✅ **corrected** — **in**, not missing. `DeviceSetup.AllowBackgroundAsync` is ported "vendor list and all" (Huawei, Xiaomi, Oppo, Vivo: standard intent first, vendor screen after, every call wrapped), and it is WIRED — `Setup.razor:202` calls it. This row previously read *missing*, on the step it itself calls the one that decides whether the rest survives. |
| Always-on assistant | `ResidentAssistant` | **in, and verified end to end on the P30 2026-09-02** — `DeviceResidentAssistant`, turned on at Settings › Phone › "Answer to its name". `ResidentWakeWord` is LINKED from the native head; the orchestration is re-expressed against `ISpokenLanguage` rather than copied, because the native file reads its own SharedPreferences store and this app would then have two. Measured: `resident listening: on`, service `isForeground=true` with its notification posted, `capture: VoiceRecognition + 2 effect(s)`, process alive with no ANR. Two defects had to be fixed first — the glibc ONNX substitution above, and a missing `FOREGROUND_SERVICE` permission in this head's manifest that had been refusing the service since it existed. |
| **Start on boot** | `BootReceiver` | **missing** — verified: no mention anywhere in the hybrid |
| **Earcons** | `Earcon` | **missing** — verified: no mention anywhere in the hybrid. The sounds that say it heard you. |
| Sideloaded bundle import | `ItTtsProbe`, `WakeWordActivity` | partial — `DeviceVoiceHost` carries the sideload-before-download path; the wake screen's does not |

## The shape of what is left

Two behaviours, both parts of one capability: **staying alive to listen.**
`ResidentAssistant` — the always-on loop itself — is now written and wired.
What remains is `BootReceiver` (surviving a restart) and `Earcon` (the sound
that says it heard you).

✅ **It listens.** Verified on the P30 on 2026-09-02: the wake word loads, the
resident service holds the microphone in the foreground with its notification
posted, and the process survives. Two defects underneath had to go first — a
glibc `libonnxruntime.so` the RID graph substituted for the Android one, and a
`FOREGROUND_SERVICE` permission this head had never declared, which had been
refusing the service on every attempt since it existed.

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
