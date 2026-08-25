# Parity audit — native head vs hybrid

Every user-facing capability in `samples/CircleAI.Samples.It.Android`, checked
against the hybrid. Taken from the code, not from memory: the control list is
every `Ui.Action`, `Quiet`, `Compact` and `Hint` string in the native activities,
plus the behaviours that have no button.

Status is one of **in**, **missing**, or **deliberate** (absent on purpose, with
the reason stated).

## Screens

| Screen | Native | Hybrid | Status |
|---|---|---|---|
| Home / the circle | `HomeActivity` | `Home.razor` | in |
| Languages | `LanguagePickerActivity` | `Languages.razor` | in |
| What it can do | `AbilitiesActivity` | `Abilities.razor` | in |
| Typing / chat | `MainActivity` | `Chat.razor` | in, partial — see controls |
| Your CV | `CareerActivity` | `Career.razor` | in |
| Aim at a job | `JobSpecActivity` | `JobSpec.razor` | in |
| Hey B | `WakeWordActivity` | `WakeWord.razor` | in |
| Setting it up | `FirstRun` + `SetupTour` | `Setup.razor` | in |
| Settings | — | `Settings.razor` | new in hybrid |
| Bench | `BenchActivity` | — | **deliberate** — a dev harness, `Exported = true`, launched by adb, reachable from no screen. Porting it would put a test rig in a shared UI it never appears in. |

## Controls

| Control | Where | Status |
|---|---|---|
| Turn on | Abilities | in |
| Next / Say it / Say it or type it | Your CV | in — "Say it" reports that the microphone is not wired |
| 10 plus languages | Home | in |
| What it can do | Home, chat | in |
| Aim my CV at this job | Aim at a job | in |
| Paste the job advert here | Aim at a job | in |
| Search 10 plus languages | Languages | in |
| Type a message / Send | Chat | in |
| Read an image | Chat | in — `IBrain.SeeAsync` over `ItSession.RunImageTurnAsync` |
| Speak in 10 plus languages | Chat | in — hero button, its own row |
| Use the mic | Chat | in — one spoken turn via `IConversation.TurnAsync` |
| Read it out | Chat | in — renamed from "TTS", which nobody outside the project could guess at |
| Run the tool check | Chat | **missing** — a diagnostic probe, not a product control |

## Behaviours with no button

| Behaviour | Native | Status |
|---|---|---|
| Readiness drives the headline | `Readiness` | in |
| Setup tour during the wait | `SetupTour` | in — folded into `Setup.razor` |
| Choose a language, persistently | `SpokenLanguage.Choose` | in |
| Hand control back to detection | `SpokenLanguage.ClearChoice` | in — **the native never called it**; Settings does |
| Wake phrase, its own language | `ResidentWakeWord.KeywordsFor` | in — and **decoupled** from the answering language, which the native welds together |
| Speaking replies aloud | `SpokenReply` | in — `IVoiceHost.SayAsync` speaks arbitrary text, not just the checked greeting |
| Voice turn | `VoiceTurn` | in — the real `VoiceTurn` is linked, not reimplemented, so its P30-tuned thresholds carry over |
| Greeting cycle | `HomeActivity.SpeakNext` | in — the circle greets in a catalogued language while the brain is still arriving |
| Language read-out line | `HomeActivity._lang` | in |
| **Always-on assistant** | `ResidentAssistant` | **missing** |
| **Start on boot** | `BootReceiver` | **missing** |
| **Battery exemption prompt** | `HomeActivity.AskToKeepRunning` | **missing** — the step that decides whether the rest survives on Huawei/Xiaomi/Oppo/Vivo |
| **Earcons** | `Earcon` | **missing** — the sounds that say it heard you |
| Sideloaded bundle import | `ItTtsProbe`, `WakeWordActivity` | partial — the probe path carries it; the wake screen's does not |

## The shape of what is left

The gaps are not scattered. Almost all of them are **one capability**: the app
listening and speaking as a conversation rather than as a demo. `SpokenReply`,
`VoiceTurn`, `HandsFree`, `Earcon`, `SpokenLanguage` detection, the greeting
cycle and the read-out line are all parts of that single feature.

The rest are three smaller things — vision, the tool check, and staying alive
(`ResidentAssistant`, `BootReceiver`, battery exemption).
