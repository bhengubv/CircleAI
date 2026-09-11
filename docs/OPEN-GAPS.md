# Open gaps

What is built and unreachable, what is genuinely blocked, and what has never run
on a phone. Every line here was **verified by grep or by a run**, not assumed.

Read this before claiming any capability is done. `csharp-is-core-signoff-language`
says "done" means C# runs **on the device** — code that compiles and is tested
is not done, it is ready.

Last verified: **2026-09-11**, against `main`. A4 closed; A1 and A2 reachable but with no screen.

---

## A. Reachable only from a test — the product can do it, no head calls it

This is the repo's most common defect by a wide margin. Every item in this
section was written, tested, committed, and then had no way in from the app.

| # | Capability | State |
|---|---|---|
| A1 | **Transcribe a file** | **Reachable.** `IConversation.TranscribeFileAsync` exists and `DeviceConversation` implements it over `Transcribing.FileAsync`. `CircleAIListener.HearAsync` no longer carries its own WAV reader — that was the **third** copy of one. **Still missing: no screen offers a file.** Neither head has a picker for audio. |
| A2 | **Subtitles** | **Reachable.** `IConversation.AsSubtitles` renders SRT/WebVTT. **Still missing: no screen offers the file to save.** |
| A3 | **Diarisation** | **Not reachable, and cannot run.** `Diarisation` is complete and tested; `ModelModality.SpeakerEmbedding` exists and **is empty**, so there is no model to embed with. Cataloguing one is the next real step. |
| A4 | **Translation** | **CLOSED.** `IConversation.TranslateAsync` → `LlmTranslationEngine`; `Translate.razor` now asks the product instead of hand-rolling a prompt. The engine took the screen's better wording (language *names*, and "give only the translation"). Native head still has no translation screen. |
| A5 | **Search across everything** | Unwired. `CircleAI.Search`, `CircleAI.Embeddings` and `CircleAI.Memory` exist; no screen searches across them. `Recalling` reaches memory for a single turn only. |
| A6 | **Music generation** | Unwired. `ProceduralMusicBedGenerator` works on any device; no screen. No `Music` model catalogued, so the neural path is an empty seam. |

## A′. Two owners of one fact — found while closing the above

| # | Fact | The disagreement |
|---|---|---|
| A′1 | **Which languages exist** | `CircleAI.Languages.KnownLanguages` lists **20**. `CircleAI.Assistant.SampleLanguages` lists **75**. Japanese, Korean, Vietnamese, Thai, Russian and ~50 others are in the second and not the first — on an app that ships a whole Open JTalk prosody stack **for Japanese**. Anything reaching for `KnownLanguages` silently degrades for most of the catalogue; `LlmTranslationEngine` did exactly that for one commit until a test asked for Japanese and got "from English to ja". The engine now takes the lookup as an argument and the device passes the 75. **The two tables still disagree.** |
| A′2 | **Where WAV is read** | Was three: `WavIo`, `tools/stt-hear`, and `CircleAIListener`. Now one (`WavIo`). `BrowserSubtitles` is a deliberate second owner of the subtitle formats — a WASM head cannot load `CircleAI.Voice` — and `SubtitleParityTests` asserts the two produce byte-identical output. |

| A′3 | **Where `Capabilities` lives** | The generated `Capabilities.cs` moved to `src/CircleAI.Assistant/` during the rename and kept `namespace CircleAI.Samples.It;`. It resolved only for code whose own namespace walked up into that one — `Services.razor` did, the bUnit project did not — so **`tests/CircleAI.Samples.It.Ui.Tests` stopped compiling and its 308 tests stopped running**, silently, because no other command builds it. Generator fixed, namespace is now `CircleAI.Assistant`, 308 passing. `CLAUDE.md` now names both test projects. |

## B. Blocked or unbuilt — not a wiring job

| # | Item | State |
|---|---|---|
| B1 | **Grammar-constrained tool decoding** | **Blocked at the native layer.** Constraining the sampler so an invalid token cannot be emitted needs logit access; `mnnbridge` exposes generate-and-stream with no hook into sampling. Not reachable from managed code without changing the native bridge. `ToolCallReader` does the achievable part — accept every spelling of a correct intention, and turn a wrong call into a sentence the model can act on. |
| B2 | **On-device image generation** | Only `CircleAI.Vision.Cloud` exists (OpenAI, Stability). Nothing on-device. Needs a diffusion runtime; MNN supports Stable Diffusion but the bridge does not expose it. |
| B3 | **Photo → 3D** | `CircleAI.Spatial` is `Contracts` + `InMemorySpatial` + `NullImplementations`. Nothing real behind it. |

## C. Decisions left open

| # | Decision | Why it is open |
|---|---|---|
| C1 | `QwenTextGenerator.MmapIsAllowed` defaults **OFF** | A deliberate temporary. `use_mmap` / `kvcache_mmap` were the proven cause of the MNN SIGSEGV (v7/v8 died, v9 survived twice). Turning them off costs load time and memory. Nobody has decided whether off is permanent or whether the fault is fixable. |
| C2 | Product libraries are **net10.0-only** | `CircleAI.Assistant*` do not multi-target, unlike the rest of the repo which is `net9.0;net10.0`. This blocks a net9.0 adopter from the assistant entirely — surfaced when `CircleAI.Tests` could not take a ProjectReference. |

## D. Never run on hardware

`feedback_p30_is_the_only_benchmark` — the P30 is the benchmark, desktop is a
compile gate. Everything here passes tests and has never been on a phone.

| # | Item | Status |
|---|---|---|
| D1 | `SeeingActivity` + `ImageBudget` | Builds clean for `net10.0-android`. Never installed, never run. The subsample path through `BitmapFactory` has never seen a real photo. |
| D2 | `Transcribing.FileAsync` on a phone | Proven on Windows via `tools/stt-hear` (JFK sample, verbatim, 0,81 confidence, SRT written). Never run on ARM64. |
| D3 | AlarmManager FGS notification reaper | Written after `SetTimeoutAfter` was **measured to fail** on EMUI. The replacement has never been verified. |
| D4 | `ToolCallReader` against a real model | Every malformed shape it handles is a real shape a 0.6B model produces, but they are reproduced in tests, not captured from a live Qwen run on the phone. |

---

## How this list is meant to be used

A capability moves out of section A only when a head calls it. It moves out of
section D only when it has run on the P30. Nothing here is "nearly done" — the
distance between a tested library and a working feature is exactly the distance
this file measures, and this repo has repeatedly mistaken the first for the
second.
