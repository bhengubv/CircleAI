# Open gaps

What is built and unreachable, what is genuinely blocked, and what has never run
on a phone. Every line here was **verified by grep or by a run**, not assumed.

Read this before claiming any capability is done. `csharp-is-core-signoff-language`
says "done" means C# runs **on the device** — code that compiles and is tested
is not done, it is ready.

Last verified: **2026-09-11**, against `main`. Closed this pass: A1, A2, A4, A′3, C2. Found and fixed in passing: E1–E3.

---

## A. Reachable only from a test — the product can do it, no head calls it

This is the repo's most common defect by a wide margin. Every item in this
section was written, tested, committed, and then had no way in from the app.

| # | Capability | State |
|---|---|---|
| A1 | **Transcribe a file** | **CLOSED on the deployed head.** `IConversation.TranscribeFileAsync` over `Transcribing.FileAsync`, and `TranscribeActivity` on the native Android head — a picker for audio *and video* (the audio people want written down is often inside a video), progress, and `AndroidAudioDecoder` so a `.m4a` voice memo works. `CircleAIListener.HearAsync` no longer carries its own WAV reader — that was the **third** copy of one. `ModelModality.Asr` had also been a download to nowhere; **Listening now leads somewhere.** The Hybrid head's `Transcribe.razor` now opens a recording too, via `InputFile` spooled to a path. |
| A2 | **Subtitles** | **CLOSED on the deployed head.** `IConversation.AsSubtitles` renders SRT/WebVTT, and `TranscribeActivity` saves an `.srt` through `ActionCreateDocument` — the person chooses where, and the app needs no storage permission. |
| A3 | **Diarisation** | **Not reachable, and cannot run.** `Diarisation` is complete and tested; `ModelModality.SpeakerEmbedding` exists and **is empty**, so there is no model to embed with. Cataloguing one is the next real step. |
| A4 | **Translation** | **CLOSED.** `IConversation.TranslateAsync` → `LlmTranslationEngine`; `Translate.razor` now asks the product instead of hand-rolling a prompt. The engine took the screen's better wording (language *names*, and "give only the translation"). Native head still has no translation screen. |
| A5 | **Search across everything** | Unwired. `CircleAI.Search`, `CircleAI.Embeddings` and `CircleAI.Memory` exist; no screen searches across them. `Recalling` reaches memory for a single turn only. |
| A6 | **Music generation** | Unwired. `ProceduralMusicBedGenerator` works on any device; no screen. No `Music` model catalogued, so the neural path is an empty seam. |

## A′. Two owners of one fact — found while closing the above

| # | Fact | The disagreement |
|---|---|---|
| A′1 | **Which languages exist** | Two tables, and they cannot easily become one: `CircleAI.Assistant` has zero ProjectReferences so a WASM head can load it, and the richer `LanguageTag` (writing system, RTL, region) lives in `CircleAI.Languages`. Measured 2026-09-11 — `KnownLanguages` **20**, `SampleLanguages` **69**; only in Known: **es, pt** (Spanish and Portuguese, which the app therefore does not offer); only in Sample: **51**, including ja, ko, ru, ur, vi, th, bn; names that disagree on shared tags: **zero**. `LanguageTableTests` now pins that zero, so drift cannot grow silently. **Merging properly needs a writing system and an RTL flag for 51 languages — those must be SOURCED, not guessed: a wrong RTL flag renders somebody's language backwards.** |
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
| C2 | ~~14 libraries net10.0-only~~ | **CLOSED.** All fourteen — including the three `CircleAI.Assistant` projects, which are the product — now multi-target `net9.0;net10.0`. They needed nothing from net10; they were net10-only because that is what a new project defaults to, and both legs build clean. `TargetFrameworkTests` guards it, since nothing else can: a net10-only library builds fine and the net9 test leg stayed green precisely because the test project could not reference them. |

## E. Fixed in passing

| # | Item | What it was |
|---|---|---|
| E2 | **`SampleLanguages.Find` refused the tags it is actually handed** | A plain ordinal dictionary lookup, so it answered only for an exact lowercase primary subtag. Android hands back `en-US` and `pt-BR`; a model config writes `ZU`. Every one of those returned null — and null here means a screen prints the raw tag where the language's name belongs, so the commonest input was the one that failed. Now exact, then case-insensitive, then primary subtag. Found by `LanguageTableTests`, written minutes earlier for a different reason. |
| E3 | **Two native names disagreed between the tables** | `ha` was "Hausa" in `KnownLanguages` and "Harshen Hausa" in `SampleLanguages`; `ig` was "Igbo" vs "Asụsụ Igbo". Aligned the **unused** table to the **user-visible** one, which cannot make anybody's screen worse — but a Hausa or Igbo speaker should still be asked which they would rather read. |
| E1 | **Twelve tests were timing the machine** | `Circle34LoopbackRealtimeTests.SendText_CustomTtsDelegate_IsInvoked` failed once on the **net9 leg only** of a full run, at 4 s — and passed in 8 ms, 88 ms and 165 ms run on its own. The cause was a 2-second `CancellationTokenSource` in a test that does no real I/O: a performance assertion wearing a timeout's clothes, measuring whether the thread pool got round to the pump while three thousand other tests ran. Twelve of these existed across four files at 1–2 s. All now 30 s, which still fails a genuine hang and cannot be reached by a loaded machine doing the right thing. |

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
