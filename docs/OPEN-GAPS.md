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
| A5 | **Search across everything** | **Unbuilt, not unwired — this entry was wrong before.** `CircleAI.Search` is *primitives*: `SearchTokenisation`, `SearchScoring`, `SimdOps`, `VectorMath`. There is no index, no store of embeddings over memory/documents/transcripts, and no query API. `CircleAI.Embeddings.TextEmbedder` is real (MNN-backed) but needs a model, and **no embedding modality is catalogued** — the same gap as A3. Wiring a screen would wire it to nothing. |
| A6 | **Music generation** | **Procedural path CLOSED.** `MusicActivity` on the native head: nine moods, thirty seconds, plays it and saves a WAV through the system picker. It is the only capability that works on a phone with **nothing installed** — arithmetic, not a download — which needed a `NeedsNoModel` flag on the abilities row, because that list decides a row's state by whether a MODEL is present and would have rendered the one always-available capability as unavailable. **The NEURAL path is still an empty seam**: no `Music` model is catalogued, and a procedural chord bed is a bed, not music generation. The screen says bed. |

## A′. Two owners of one fact — found while closing the above

| # | Fact | The disagreement |
|---|---|---|
| A′1 | ~~Which languages exist~~ | **CLOSED.** The two tables now hold the same 78 languages with an empty symmetric difference and zero name disagreements. `KnownLanguages` had **20**; the 58 it lacked were **sourced, not typed** — script from the Unicode of each language's own native name, direction from `CultureInfo.TextInfo.IsRightToLeft` (ICU/CLDR), region from ICU likely-subtags, with the nine ICU had no answer for put back to ICU as region-qualified cultures and kept only where ICU confirmed them. **The derivation was checked against the 20 hand-written rows before it was trusted: script 20/20, right-to-left 20/20.** `WritingSystem` also gained 12 scripts — Bengali, Gujarati, Gurmukhi, Hangul, Japanese, Kannada, Malayalam, Myanmar, Sinhala, Tamil, Telugu, Thai — because 12 languages the app offers were collapsing to `Other`. They remain two TYPES (`CircleAI.Assistant` has zero ProjectReferences so a WASM head can load it), but they no longer disagree. |
| A′2 | **Where WAV is read** | Was three: `WavIo`, `tools/stt-hear`, and `CircleAIListener`. Now one (`WavIo`). `BrowserSubtitles` is a deliberate second owner of the subtitle formats — a WASM head cannot load `CircleAI.Voice` — and `SubtitleParityTests` asserts the two produce byte-identical output. |
| A′3 | **Where `Capabilities` lives** | The generated `Capabilities.cs` moved to `src/CircleAI.Assistant/` during the rename and kept `namespace CircleAI.Samples.It;`. It resolved only for code whose own namespace walked up into that one — `Services.razor` did, the bUnit project did not — so **`tests/CircleAI.Samples.It.Ui.Tests` stopped compiling and its 308 tests stopped running**, silently, because no other command builds it. Generator fixed, namespace is now `CircleAI.Assistant`, 308 passing. `CLAUDE.md` now names both test projects. |

| A′4 | **Where the model list comes from** | The live catalogue existed and nothing called it. `ModelScopeCatalogClient` fetches, caches, checks a signature and honours a cadence; `ModelRegistryService` takes one; `AIOptions.CatalogClient` and the DI wiring read it — and **nothing ever set it, nothing ever called `PrimeFromCatalogAsync`**, and ~20 sites bypass DI with `new ModelRegistryService()` so fixing the first two would still not have reached the app. **`ModelCatalogue` is the missing caller**; the fetched catalogue is now process-wide so one refresh reaches instances built afterwards. Wired into `CircleAISession` startup, fire-and-forget, daily cadence, never throws. |
| A′5 | **A refresh would have emptied the catalogue** | `AllModels` was `(_remoteRegistry ?? _embeddedRegistry)` — a **wholesale replacement** — and the two catalogues are disjoint in both name and host. Measured: **0 of the 88 shipped entries are sourced from ModelScope** (1 Asr, 14 Chat and 2 Vision on HuggingFace; 64 Tts on HuggingFaceBucket; 5 Tts and 1 Phonemizer on GitHubRelease; 1 WakeWord on HuggingFaceBucket). The live query asks ModelScope for MNN bundles, so **the first successful refresh would have replaced the entire catalogue with a disjoint set** — losing the voice, the ears, the wake word, the phonemiser dictionary *and* the curated chat ladder — and flattened model choice from "best that fits" to "smallest that fits", because the live client never sets `QualityRank`. `CatalogueMerge` makes curated the spine; a refresh can only ADD. Pinned by `CatalogueMergeTests` and `ModelCatalogueTests`. |
| A′6 | **The live fetch had never worked, twice over** | Proven against the real API on 2026-09-11, not inferred. `GET /api/v1/models?Name=MNN` answers **404** and always has; the working call is `PUT /api/v1/dolphin/models` with a JSON body. Independently, the parser read `Data.Models` where the API returns `Data.Model.Models` — one level deeper — and a missing property there is a `yield break`, so even with the URL corrected it would have fetched 519 models and catalogued **zero**, with no exception and nothing logged. Both fixed; `CatalogueListingTests` pins the shape against a captured real response. **Live run: 25 models fetched in 25 s, 18 new, and all 69 voices / 1 ASR / 1 wake word still present.** |

## A″. One blocker, three capabilities: the catalogue has no model for the modality

Diarisation, search and neural music are not three separate problems. Each has
working managed code and **nothing catalogued to run it with** — and until this
pass, two of them had no modality to be catalogued *under*.

| Modality | Consumer | Catalogued |
|---|---|---|
| `SpeakerEmbedding` | `CircleAI.Voice.Diarisation` | **nothing** (modality added 2026-09-11) |
| `Embedding` | `CircleAI.Embeddings.TextEmbedder` (MNN-backed, real) | **nothing** (modality added 2026-09-11) |
| `Music` | `CircleAI.Music` neural path | **nothing** (modality already existed) |

`ModelChoice.AnyCatalogued` already distinguishes "nothing exists yet" from
"nothing that runs on this phone", so an empty modality is a state the app can
describe rather than trip over.

**The next step for all three is the same**: source a permissively-licensed
model, pin its real SHA-256, and add it to the registry — `fully-free-opensource-always`
says licence FIRST, and `model-cataloguing-via-browser-pane` says a real hash,
not a guessed one.

## A‴. Voices: every offered language has one, and one of them could not speak

Audited 2026-09-11 against the shipped catalogue. **Both directions are clean.**

| Question | Answer |
|---|---|
| Languages the app offers | **78** |
| Voice entries catalogued | **69**, covering **78** tags |
| **Offered with no voice** | **ZERO** — `VoiceCoverageTests` keeps it that way |
| **Voices for languages never offered** | **ZERO.** Was 3 — Spanish, Dutch and Portuguese had six Piper voices catalogued (es_ES, es_MX, nl_BE, nl_NL, pt_BR, pt_PT) and were absent from the picker, so ~380 MB of voices were downloadable and unselectable. All three added; the change cost no new bytes and unlocked six voices. (Counts here read 69/72 at first — a case-sensitive regex dropped the six regional tags; the true figures were 75 then 78.) Portuguese is Angola and Mozambique. |

**And Japanese could not speak on the path that speaks.** `JSUT-VITS` is an ESPnet
VITS: it consumes Open JTalk phoneme ids, so it needs the 104 MB naist-jdic
dictionary, catalogued separately because every Japanese voice shares it.
`CircleAITtsProbe` fetched it; **`CircleAISpeaker` mentioned it nowhere**. So the
speaker downloaded 144 MB, asked `OpenJTalkPhonemizer.Open` for a phonemiser and
got `null` — the diagnostic screen could speak Japanese and the app could not,
and the error named a missing phonemiser rather than a missing download.

Fixed: the rule lives in `ModelPrerequisites`, both paths ask it, and it keys on
**architecture** rather than an entry-name prefix — which the probe's own comment
had asked for and could not have, because `ModelEntry` **dropped the registry's
`Architecture` field on deserialise**. It no longer does.

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
| E4 | **Two more tests timing the machine** | Found by the same full-run/isolation comparison as E1, on the net9 leg. `Circle33ReassuranceFillerTests.FastWork_SkipsFiller` does 20 ms of work and asserts no filler fired after a 5-second threshold — under load the 20 ms took **9 seconds**, so the filler fired and the test measured the scheduler rather than the branch (threshold now 60 s, so the gap is the test). `VoicePipelineTests.ActivationFailed_Event_FiredWhenTranscriberThrows` polled for an event for 3 seconds; the loop exits the moment the event lands, so 30 s costs a passing run nothing. Both pass in ~90 ms in isolation. |
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
