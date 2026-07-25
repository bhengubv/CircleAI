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
