# On-device hardware findings — Huawei P30 Lite

What CircleAI actually does on a real low-RAM, de-Googled Android phone — measured, not assumed. Written for developers building on CircleAI who need to know the constraints before they hit them.

**TL;DR:** on a 3.6 GB phone only ~1.5 GB is actually free. Model *fit* is against **free** RAM, not total. A ~1.5B chat model loads and writes correct code; a 4B model gets OOM-killed on load. Everything pure-managed (documents, charts, decks, music, image stills, business/security/mesh logic) runs with no RAM concern. Vision still stops at inference (MNN's vision bridge doesn't support SmolVLM's arch). TTS, which was blocked the same way at grapheme→phoneme, now **works fully** — espeak-ng was moved out-of-process into its own GPL app so CircleAI can call it without linking GPL, and a real Piper WAV comes out. See "Native walls," below.

---

## The test devices

| Device | SoC / OS | Total RAM | Free RAM (typical) | Storage free | Tier |
|---|---|---|---|---|---|
| **Huawei P30 Lite** (MAR-LX1M) | Kirin 710, EMUI, **no GMS/Play Services** | **3.6 GB** (3,776,516 kB) | **~1.3–1.5 GB** | 38 GB | Phone |
| Redmi (M2003J15SC, "merlin") | Helio G80, MIUI | **2.6 GB** (2,758,136 kB) | ~1 GB | — | Phone |

The P30 Lite is the reference device: mid-range, **de-Googled** (no Play Services — the whole point of CircleAI's degoogled-by-default stance), arm64, EMUI. Device internet works (reaches `modelscope.cn` at 147 ms), so on-device model download over Wi-Fi is fine.

## The number that matters most: total RAM ≠ usable RAM

```
MemTotal:      3,776,516 kB   (3.6 GB)   ← device class
MemAvailable:  1,485,140 kB   (1.45 GB)  ← what a model must fit into
MemFree:         100,776 kB   (0.1 GB)   ← genuinely unused
```

The OS + EMUI + cached apps hold ~2 GB. Most of that is **reclaimable cache** (Android evicts it on demand when you allocate), but **your model has to load into what's actually free right now** — measured at **1.3–1.5 GB** at app start on this phone, dropping to **~0.8 GB** once a 1.5B model is resident.

**Consequence:** the model selector must gate on **free** RAM, not total. Getting this wrong crashes the app (see below).

## What actually loads and runs

| Workload | Model / engine | Result on the P30 Lite |
|---|---|---|
| **Chat / coding** | Qwen2.5-1.5B-Instruct-MNN (~880 MB) | ✅ Loads in ~1.3 GB free; generated a **correct `is_prime()`** offline |
| Chat (too big) | Qwen3-4B-MNN (2.7 GB) | ❌ **OOM-killed on load** — needs 3.6 GB, only ~1.5 GB free |
| **Documents** | PDFsharp (pure-managed) | ✅ CV / cover letter / invoice / report → real PDFs, no RAM concern |
| **Charts / decks** | PDFsharp | ✅ chart PDF (42 KB), presentation PDF (63 KB) |
| **Music bed** | procedural WAV synth | ✅ 705,644-byte 8 s PCM WAV (exact = 8 × 44100 × 2 + 44) |
| **Image still** | managed PNG encoder | ✅ 540×960 PNG (valid `89504E47` header) |
| **Business / mesh / security** | pure-managed | ✅ invoice totals, mesh advertise+list, blocklist match, deny-by-default gate |
| **ASR** | Whisper-tiny (77 MB) | Selector: `Good` (fits) — inference not yet run on-device |
| **TTS** | Piper-en_US-lessac-high (108 MB) | ✅ **Full TTS.** select → download → **ONNX Runtime loads the voice** → grapheme→phoneme via the **out-of-process espeak G2P app** → a real **2.6 s WAV** (22 050 Hz mono, 71% peak amplitude = genuine speech). espeak-ng (GPL-3.0) runs in a separate app; CircleAI calls it over a ContentProvider and never links it. See "Native walls," below |
| **Vision** | SmolVLM-256M (311 MB) — fits + loads | ⚠ MNN image path fails (`code -6`): SmolVLM's SigLIP arch ≠ the bridge's Qwen-VL/Kimi-VL vision support. Bridge-supported Qwen-VL 2B+ needs ~2.4 GB → doesn't fit ~1 GB free. Pipeline (select→download→load) proven; **inference needs a 4 GB+ phone** |
| **Coding (dedicated 3–7B agent)** | — | Tier-floored to Tablet (6 GB+); a general 1.5B still codes, see note |

**Practical model ceiling on a 3.6 GB phone: ~1.5–2B parameters.** Downloads of 400–900 MB are practical over Wi-Fi; multi-GB is slow and won't fit free RAM anyway.

## CircleAI's own C# engine: Bonsai 2 2B (measured 2026-10-30 to 31)

The 2B (`Ternary-Bonsai-2-2B-PTQ1_0.gguf`, 429 MB, qwen35 packed as ternary PTQ1_0) run by `Inference/Managed` inside the Release service — no native code on its path.

| What | On the P30 Lite |
|---|---|
| The runtime | Mono AOT without LLVM (.NET 10.0.14). **No ARM vector intrinsics** (`AdvSimd.IsSupported` is false); `Vector128` reports itself accelerated, yet a byte shuffle or an add costs 7.4 ns |
| Mono's LLVM AOT | **Crash-loops the service at start** (`aot-runtime.c:5341`, `plt_entry`), so it is not used |
| One 6144×2048 ternary product, one thread | scalar 384 ms · `Vector128` 226 ms · byte table 43 ms · **lookup table 8.2–9.8 ms** (the bitnet.cpp idea) |
| The lookup table on 1–8 threads | 10.8 · 4.4 · **3.65** · 4.5 · 3.9 · 4.3 · 5.1 · 4.7 ms — three threads are the best, and more make it slower |
| Loading the 2B | 3.8–4.6 s (the file is memory-mapped) |
| Reading the prompt | 81 tokens in 78–96 s (about 1 token a second), one token at a time |
| Writing the answer | 0.94 tokens a second — 0.50 until the output head's 248,320 rows were cut into chunks small enough to stay in the cache |
| The answer | Word for word the desktop's: "I am an AI assistant designed to provide helpful information and support." |

**Measured since (2026-11-02, OPEN-GAPS D5):** through the inference process's own build site (the soak `engine` check) the 2B was ready in 44 s, read the question's 18 new tokens past its primed 63-token system turn in 9.4 s, and wrote 13 tokens at 1.47 a second (1.66 in S4's run). The native fork on the same path wrote **3.27 a second** — the C# engine is about half the fork's speed on this phone. The app's own questions went to Qwen3.5-0.8B on MNN, which the phone chose at about 14 tokens a second and said why - until 2026-11-04, when the owner ruled the speed gate out: the Bonsai 2 fleet is the brain, and the C# engine's speed here is the engine's to raise.

### The S9 sign-off run (2026-11-02, the Release service with S4–S8 and the fixes S9 found)

Driven by broadcast (`SoakReceiver`, DUMP-protected) with no screen touched and no device setting changed; the heat and battery were pretended inside the app (`PretendConditions`). adb's USB link stopped answering at 14:55Z and the run went on over Wi-Fi adb. **Times here are UTC. The phone's clock runs at UTC+9**, so its logcat reads nine hours ahead — 03:39 on 11-03 there is 18:39Z on 11-02 — while the service's own log file and Wolverine write UTC.

| What | On the P30 Lite |
|---|---|
| The install | In place over versionCode 62, four times in the run: `firstInstallTime` kept (2026-10-23), the models kept; the service up in about 10 s with no fatal line in its first 45 s |
| The C# kernels, measured when a C# brain is first built | four threads fastest (~1.68 tokens a second of a 2B's products, one thread 0.73); weights read at 0.756 GB/s in PTQ1_0 and 1.058 in PQ2_0 — the factor the planner scales a packing by (OPEN-GAPS C8) |
| The bench, by broadcast | lookup table 7.3–7.5 ms per 6144×2048 PTQ1_0 product on one thread, PQ2_0 8.2 ms at best; three tokens through it together 97.8 ms against 41.0 ms one by one, so MTP drafting would slow this phone (C7). It ran while a 24-hour soak from October was still asking questions — a soak resumes on every start, and that one was stopped at 15:56Z (178 asked, 178 answered, 169 correct, no restart) — which is why its threads did not scale and the measurement above did |
| The 2B through C# (the inference process's own build site) | loaded in 16 s, its warm-up started from the primed start kept on disk; the 87-token question ran 24 tokens past it in 12.8 s and wrote 7 at 1.68 a second: "The capital of Kenya is Nairobi." — no thinking in it |
| A cold start after the inference process was ended | the new process read the 2B's primed start from disk (13 of 76 tokens) and the question ran 19 of 82: "Red." Through the queue, MNN reloaded and answered in 24.1 s |
| A question through the queue | Qwen3.5-0.8B on MNN: 299 prompt tokens in 7.9 s, 10 tokens at 10.2 a second, 18.6 s in all with its load |
| A tool turn through the queue | "What is my battery level?" with tools allowed: `get_battery_level` ran in 13 ms — "Your battery is at 100%." — 275 ms in all. Offered a calculator, the 0.8B answered "6789 multiplied by 3 equals 20367." without calling it |
| The log, in logcat and in the file | the files read back by broadcast hold what logcat showed: the kernel measurement, the engine's load, the tool that ran. The wake word writes a line a second, so the service's file needs a filter to reach back further than three minutes |
| A flat, hot phone (8%, unplugged, heat 2, pretended) | the balance stepped the brain down 12 s later — "Full -> LeanMemory - the phone is very hot, so its working memory is packed tighter" — and grew it back after ten calm minutes |
| Fetching as setup fetches | the 2B (429 MB) in 42 s and its tower (364 MB) in 36 s, each checked against its SHA-256; Qwen3.5-2B on MNN (1386 MB) in 2 min 11 s |
| A photo, to the C# 2B and its tower | S8's 100×60 fixture: 12 picture tokens from the tower in 25.2 s, 96 prompt tokens (33 run, primed) in 42.4 s, 19 tokens at 1.65 a second: "This image shows a red circle and a blue square placed side-by-side on a white background." |
| A photo, through the app's own path | before S9's fix the text brain answered it without ever getting the picture (OPEN-GAPS E31); after it, the picture goes to SmolVLM, the phone's eyes, admitted beside the brain — and MNN's vision call returns -6 for SmolVLM, PNG or JPEG, so the ask fails in about 3 s rather than guess (E32). Sent to the C# 2B and its tower instead, it was admitted and then ended by the 90 s watchdog during its prefill, twice, and Wolverine shortened the context (E33); the C# pair was kept off a picture where the kernels ran below four tokens a second, as here (1.68), until the owner ruled that gate out on 2026-11-04 |
| A brain too big for the phone, forced | Qwen3.5-2B on MNN (1386 MB), fetched and pinned: the supervisor held it back until validated, the phone validated it while idle and charging, and its load was killed for memory — "HEALTH CircleAI:OutOfMemory:InferenceWorker.Load", then Wolverine "smaller-brain: stepped down from Qwen3.5-2B-MNN until 18:56Z (trip 1)", and the next brain was the 0.8B built without it. Two reports wait in Wolverine's outbox (no endpoint yet). Read back from the service's log file, which outlived logcat |
| The 30-minute soak | 6 asked, 6 answered, 6 correct, no restart, no fatal line |
| A spoken question, the brain warm | 83 prompt tokens in 2.35 s, 9 tokens at 12.8 a second; 3.4 s in all |
| Memory, answering on MNN | the inference process 755 MB resident, the main process 375 MB; 77 MB free with 1.5 GB of the 2.4 GB swap in use, while the October soak was still running |

**Again on the build committed with S9 (installed 18:43Z, re-run 19:17–19:22Z):** the 2B through C# answered "The capital of Kenya is Nairobi." at 1.67 tokens a second with no thinking; after the inference process was ended, "Red." 16 s later from the primed start on disk (13 of 76 tokens); the 2B and its tower described the photo — "This picture displays a red circle and a blue square on a white background." (the tower 25.5 s, 1.65 tokens a second); a tool turn answered in 84 ms (`get_battery_level`, "Your battery is at 100%."); both log files read back; and questions through the queue answered on MNN, before and after the process was ended (25 s and 26 s, each with MNN's load). No fatal line. The first C# warm-up read the system turn afresh (38.7 s): a primed start is kept per context length, and Wolverine's cap from E33 had built the engine at 1024.

**No primed start for the MNN brain, by design:** its turns are content-only, and a restored prefix on Qwen3.5's linear attention would be full-attention KV without the linear state, so every MNN turn reads `prefix-miss cache=off`. A primed start is the C# engine's (`kv=primed-disk`).

### Where the C# engine runs on this phone (2026-11-04, read only)

Read with the shell while the C# 2B - the phone's brain since the owner ruled the speed gate out that
day - read one question through the queue. Nothing was written to the phone and no setting changed.

| What | What the phone did |
|---|---|
| Where Android lets it run | both processes in the `foreground` cpuset (cores 0-7) and schedtune group, every core allowed; the `background` set (cores 2-3 only) does not apply |
| Where its threads ran | the engine's three workers moved across all eight cores: in 69 samples a worker was on an A53 little core 17 times, a quarter, and in six samples two or three workers shared one core; the thread that hands them work stayed on the A73s |
| The big cores' clock | 2.19 GHz for the first 30 s of the turn, then 1.82 GHz to its end, a sixth less; the cause cannot be read from the shell (the cap, the governor and the thermal zones read empty) |
| The turn | nothing written for over 90 s - the prompt read at about a token a second, not the answer written; the watchdog saw the work (3.1 cores) and waited |

So nothing confines the engine to the slow cores: the scheduler puts its workers there, and stacks them,
and a product split across the workers waits at its barrier for the slowest. A worker spins 200 us after
each product and then sleeps (`WorkerPool`), so between products it looks half-busy - the kind of thread
an energy-aware scheduler moves to a little core.

## Bugs this testing found (and fixed)

1. **GC-heap-limit misread.** `DeviceProbe.Snapshot()` read RAM from `GC.GetGCMemoryInfo()` — the per-app **GC heap limit (~100 MB)** in an Android sandbox, not physical RAM. The phone classified as a **Wearable** and *every* model came back `NothingFits`. Chat hid it (silent fallback to the smallest model); the per-modality capability sweep surfaced it. **Fix:** a `PlatformMemoryProbe` hook the Android head sets from `ActivityManager` + `StatFs`.

2. **OOM crash from total-vs-free RAM.** The first fix reported **total** RAM (3.6 GB) as "available." The selector picked Qwen3-4B (needs 3.6 GB) and the app was **OOM-killed loading it**. **Fix:** `DeviceProbe` now carries two numbers — `RamTotalBytes` (→ tier) and `RamAvailableBytes` (→ fit). The Android head reports `AvailMem` for fit and `TotalMem` for tier.

Both fixes are in `src/CircleAI.Core/DeviceProbe.cs`; the second is verified on-device (the phone now picks Qwen2.5-1.5B and runs).

## Native walls this testing hit — one solved, one open

Two gaps surfaced by actually attempting inference on the phone, not by trusting the selector's "fits" verdict:

- **Vision — `code -6` (open, hardware).** SmolVLM-256M downloads (311 MB) and *loads*, but MNN's image path rejects its SigLIP vision encoder: the bundled bridge generates from images only for the Qwen-VL/Kimi-VL family, which start ~2 GB and don't fit the ~1 GB free. A **hardware** wall — a 4 GB+ phone runs a bridge-supported 2B VLM.
- **TTS — `DllNotFoundException: espeak-ng` (solved, licensing).** The Piper voice downloads and ONNX Runtime loads it — the synthesis half was always real. The *first* step, grapheme→phoneme, needs espeak-ng, which is **GPL-3.0**; CircleAI is permissive (MIT/Apache/BSD only; its one other GPL dependency, DOOM, is kept out-of-process). Linking espeak in-process would relicense the whole app. **Fix:** espeak-ng now lives in its own tiny GPL app (`com.bhengubv.espeakng`) that exposes text→IPA over a `ContentProvider`; CircleAI calls it out-of-process and never links it. Result: a real 2.6 s Piper WAV (71% peak amplitude) on the P30, licence intact. `libespeak-ng.so` is cross-compiled for arm64 with the NDK; the data is version-matched espeak-ng 1.52.0. The client falls back to text-only if the G2P app is absent.

The shared lesson: **a model *loading* is not a model *running*.** Both walls surfaced only because the probe runs the actual inference on the device and reports the exact native result.

## Guidance for developers targeting low-RAM Android

- **Set the memory hook at startup**, before anything selects a model:
  ```csharp
  DeviceProbe.PlatformMemoryProbe = () => {
      var am = (ActivityManager)GetSystemService(Context.ActivityService)!;
      var mi = new ActivityManager.MemoryInfo(); am.GetMemoryInfo(mi);
      var storage = new StatFs(FilesDir!.AbsolutePath).AvailableBytes;
      return new DeviceProbe.PlatformMemory(mi.AvailMem, storage, mi.TotalMem); // free, storage, total
  };
  ```
- **Expect ~1.5B**, not the flagship-class models, on a 3.6 GB phone. Design prompts/features around that capability level.
- **Leave headroom.** A 1.5B needing ~1.2 GB against ~1.3 GB free is tight — the KV cache grows during generation. A safety margin (fit against ≤ ~80% of free) and wiring `onTrimMemory` into the eviction path are the recommended next hardening.
- **Download on-device.** The phone reaches the model host over Wi-Fi; models land in the app's files dir and are verified against the catalogued SHA-256.
- **The coding tier-floor is blunt.** CircleAI gates the *dedicated 3–7B coding agent* to Tablet tier, but a general 1.5B on a phone writes correct code — the floor should scale with model size, not be a flat tier gate.

## How to reproduce

Install the IT! sample, tap **Caps**, and pull the report:

```
adb exec-out run-as com.bhengubv.itsample cat files/capability-report.txt
```

It prints the live device line (`3.6 GB RAM (1.5 GB free now), tier Phone`), a per-modality selector verdict, and renders every document + media artifact to `files/` for you to pull and inspect. APK size: 53 MB chat-only, 81 MB with the full capability sweep.

---

*Measured on the Huawei P30 Lite (MAR-LX1M), 2026-07. Numbers are real readings from that device; re-run the Caps sweep on your target hardware for its own envelope.*
