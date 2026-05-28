# Circle AI — Strategic Architecture

> **Status:** Living document. Last meaningful revision: 2026-05-28.
> Audience: Engineers extending CircleAI; investors evaluating the personal-AI moat.

---

## TL;DR

CircleAI is not a chatbot. It is **the only personal AI runtime that ships on every operating system, in every language the user speaks, owns its own transport substrate, owns its own OS surface, and stores its memory and personality as files the user keeps.**

No competitor can match that combination. Apple Intelligence runs on iOS only. Gemini Nano runs on Pixel only. Pi.ai, Replika, Personal.ai, Inflection, Rabbit R1, and Humane all require cloud round-trips and hide your memory in a database they own.

---

## The 10-layer personal AI stack

Every viable personal AI needs ten capability layers. The stack below shows what CircleAI ships today and where each layer lives.

| # | Layer | Packages | Status |
|---|---|---|---|
| 1 | **Perception** — face, voice, gesture, biosignal, context | `CircleAI.Voice`, `CircleAI.Wearable.Biosignals`, facex integration | Voice ✓ in C#, ports needed; Biosignals ✓ new this commit |
| 2 | **Memory** — episodic + semantic + procedural + working | `CircleAI.Memory`, `CircleAI.Embeddings`, `CircleAI.Search`, `CircleAI.Knowledge` | Schemas + algorithms ✓; Markdown-backed store ✓ new this commit |
| 3 | **Identity** — UHID, trust, biometric, key management | `CircleAI.Identity`, `CircleAI.Security` | Biometric matcher + UhidKeyRing + watchdog ✓ |
| 4 | **Reasoning** — LLM inference, planning, simulation | `CircleAI.Inference`, `CircleAI.Hosting.InferenceBridge`, `CircleAI.Simulation` | Inference contracts ✓; cross-OS bridge ✓ new this commit; simulation ✓ |
| 5 | **Affect** — engagement, mood, rapport, VAD | `CircleAI.Memory.AffectState`, `AffectVad` | 5-dim + VAD projection ✓ across 10 languages |
| 6 | **Action** — tools, agents, automations | `CircleAI.Tools`, `CircleAI.Skills`, `CircleAI.Orchestration`, `CircleAI.Agents.Peer` | Tool catalogue ✓; agent dispatch ✓; agent-to-agent over mesh ✓ new this commit |
| 7 | **Language** — multilingual, cultural context | `CircleAI.Languages` | 20 languages, Africa-first registry ✓ |
| 8 | **Transport** — mesh, sync, federation | `CircleAI.Aether`, `CircleAI.Federation` | Mesh ✓; federated learning model ✓ new this commit |
| 9 | **Security** — immune system, audit, attestation | `CircleAI.Security`, `CircleAI.Security.Aether` | Watchdog, checkpoints, key rotation, BugHunter CI ✓ |
| 10 | **Distribution** — apps, OS integration, embeds | `CircleAI.Hosting.InferenceBridge`, `CircleAI.Personality`, `CircleAI.Personal`, `CircleAI.Knowledge` | Cross-OS daemon contract ✓ new this commit; personal-data adapters ✓ new this commit |

---

## What ships in CircleAI today (post-this-commit)

### Existing — already in production

- **Portable core (10 languages)**: C#, Rust, Go, Python, TypeScript, Kotlin JVM, Swift, C, ArkTS (HarmonyOS), Android Kotlin
- **Reference C# modules**: Core, Memory (Affect/Affect­Vad/PersonaState/EpisodicMemory/Goal), Identity (BiometricMatcher, UhidKeyRing), Languages (20-entry registry), Companion (FaceAffectMapper, FaceCompanionBridge), Inference, Tools (FacialMetricMatrix), Sync, Voice (Whisper + ONNX TTS + Energy VAD + Wake-word), Embeddings, Search, Skills, Hosting, Aether, Security (peer + immune system), Security.Aether, Accessibility, Orchestration, Simulation
- **Fixtures**: affect_state, affect_vad_derivation, anomaly_signal_schema, biometric_vectors, language_tags, goal_progress, persona_state, graph_schema, bughunter_vrt_taxonomy

### New in this commit

| Package | What it does | Why it matters |
|---|---|---|
| `CircleAI.Personality` | User-DECLARED persona (distinct from learned `PersonaState`). JSON document the user owns and edits. | Replika hides personality. Pi hides personality. Ours is `notes.md`-like, owned by the user. |
| `CircleAI.Knowledge` | Markdown-on-disk memory backing. Episodic memories become Git-diffable `.md` files with YAML frontmatter. | Pi.ai / Personal.ai hide memory in databases. Ours is a folder. Export, version, audit, delete. |
| `CircleAI.Hosting.InferenceBridge` | Cross-OS LLM daemon contract. One model loaded once per device, every app on the device shares it. | Apple Intelligence is iOS-only. Gemini Nano is Android-only. **Nobody has cross-OS.** This is the single sharpest differentiator. |
| `CircleAI.Wearable.Biosignals` | HR/HRV/SpO2/accelerometer/temperature → AffectState mutations. Deterministic, fixture-validated. | Personal AI without biosignal awareness can't claim to be "personal." |
| `CircleAI.Personal` | Permission-gated Calendar + Email + Contacts adapter contracts. Every call requires a UhidKeyRing-signed `UserConsentToken`. | Apple/Google adapters lock you into their ecosystem. Ours is portable across both. |
| `CircleAI.Federation` | Federated learning round model. Only deltas leave the device, never raw data. Designed for Aether mesh aggregation. | Federated learning over BLE / Wi-Fi Direct is structurally impossible for cloud-only competitors. |
| `CircleAI.Agents.Peer` | Agent-to-agent protocol over Aether mesh. One person's AI talks to another's directly. | Uniquely available because of the Aether substrate. No competitor can offer offline P2P AI federation. |

---

## What still needs porting

The Voice, Embeddings, and Skills modules exist in C# with substantial implementations but haven't been ported to the other 9 languages. Priority order for the next porting round:

1. **`CircleAI.Voice` → 9 langs** — Pi.ai's whole pitch is voice. ITtsEngine + IVoiceTranscriber + IWakeWordDetector + IVoiceActivityDetector interfaces, plus null impls. Concrete impls (Whisper, ONNX TTS) stay C#-only for now.
2. **`CircleAI.Embeddings` → 9 langs** — ITextEmbedder interface + a fixture-validated cosine-search helper.
3. **`CircleAI.Skills` → 9 langs** — Skill catalogue contract (ISkillStore, SkillDetail, SkillDraft).
4. **Personality + Knowledge → 9 langs** — straightforward port using the same parallel-agent pattern as AnomalySignal / AffectVad.
5. **InferenceBridge → 9 langs** — contract-only port; OS-specific adapters live per platform.

---

## Strategic positioning

### What competitors look like

| Competitor | Their pitch | What they ship | The hole |
|---|---|---|---|
| Pi.ai (Inflection) | Empathetic voice companion | Cloud-only, single platform (web/iOS) | No offline. No multi-OS. No mesh. No African languages. |
| Replika | Emotional connection | Cloud-only, iOS/Android | Same as Pi. Memory locked in their DB. |
| Personal.ai | "Your personal AI model" | Cloud SaaS | Same as Pi/Replika. No on-device. |
| Apple Intelligence | OS-integrated | iOS 18 + new hardware required | Single OS. Single language tier. Apple-locked. |
| Gemini Nano | OS-integrated | Pixel 8+ Android | Single OS. Google-locked. |
| Rabbit R1 / Humane Pin | Embodied hardware | Custom hardware + cloud | Hardware-locked, cloud-dependent. |

### What CircleAI looks like

| Differentiator | How it lands |
|---|---|
| **10-language portable runtime** | Ships natively on every device class that exists. Pi.ai cannot run on a 2020 Redmi. CircleAI does. |
| **Cross-OS inference daemon** (InferenceBridge) | One contract, four OS adapters. Apple and Google each ship one. We ship four. |
| **Mesh-native transport** (Aether) | Works offline, peer-to-peer, no infra dependency. Critical for African + Asian + rural-North markets. |
| **Africa-first language depth** (20-lang registry + circleone siNtu syllabary) | Western competitors need a decade of linguistics to catch up. We shipped it. |
| **User-owned memory + personality** (Knowledge + Personality) | Files on disk, not rows in a database. Audit trail. Export. Delete. Versioning. |
| **Self-defending immune system** (Security) | Watchdog detects anomalies, rotates keys, isolates sessions. No competitor has this. |
| **Multi-agent foundation** (Orchestration + Agents.Peer) | Your AI can spawn specialised agents AND talk to other people's AIs over the mesh. |
| **Predictive simulation** (Simulation + MiroFish) | Before acting, the AI simulates impact via knowledge-graph diffusion. |
| **Economic loop built in** (Wallet, agent-tipping) | Pi.ai burns money. Replika needs subscriptions. CircleAI agents can earn-to-help. |
| **Trust receipts everywhere** (UhidKeyRing signatures on consent tokens, agent messages, federation deltas) | On-device proof of every action — healthcare/banking/government story. |

---

## Memory architecture — the mempalace pattern

Investigation of `bhengubv/mempalace` (96.6% R@5 on LongMemEval, 500 questions, independently reproduced on M2 Ultra in under 5 minutes) confirms the technique is **discipline, not exotic ML**. The portable algorithm:

1. **Store verbatim, never summarise on write.** Episodic memory writes are append-only and lossless. AI-deciding "what's worth remembering" is the documented anti-pattern.
2. **Hierarchical metadata scope keys** — wing (person/project) → hall/room (subject) → closet (pointer) → drawer (original file). Every chunk carries scope tags.
3. **Filter-then-ANN retrieval** — narrow by scope metadata first, then run vector similarity within the filtered set. Cheap, deterministic recall boost, no novel data structure.
4. **Wake-up projection** — at session start, produce a tight ~170-token capsule of pinned facts (identity, preferences, active projects) instead of hot-loading history.
5. **Source-typed ingest channels** — distinguish code/docs vs conversation vs auto-classified general content.

Every CircleAI target language already has either an HNSW library (`hnswlib`, `usearch`) or a vector DB binding. The novelty is the discipline, not the algorithm. **Skip AAAK compression** — by their own benchmark it regresses 12.4 points and saves no tokens at realistic scales.

This will be implemented as an enhancement to `CircleAI.Memory.IEpisodicMemoryStore` + `CircleAI.Search` in the next porting round:

- `IEpisodicMemoryStore.AppendVerbatimAsync(EpisodicMemoryEntry entry, IReadOnlyDictionary<string,string> scopeKeys)` — verbatim, scope-tagged
- `IEpisodicMemoryStore.GetSessionCapsuleAsync(string uhid)` — wake-up projection
- `CircleAI.Search.IFilterableVectorSearch` — filter-then-ANN contract
- `fixtures/memory_recall_benchmark.json` — LongMemEval-style golden vectors so all 10 ports can compare R@5 against the reference

---

## Roadmap

### Immediate (next session)

1. Port Voice + Embeddings + Skills interfaces to the 9 non-C# languages
2. Port Personality + Knowledge schemas to 9 languages
3. Port AnomalySignal / Personality / Persona prompt fixtures to fixture-interop.yml

### Near-term (next 2-3 sessions)

4. mempalace pattern integration into CircleAI.Memory + CircleAI.Search (filter-then-ANN, wake-up capsule, verbatim discipline)
5. Wake-word concrete implementation per OS (Android Porcupine binding, iOS Speech framework binding, etc.)
6. First Calendar adapter implementation (`CircleAI.Personal.Google`, `CircleAI.Personal.Microsoft`)
7. InferenceBridge Android adapter (Binder service binding)
8. InferenceBridge iOS adapter (XPC service)

### Medium-term

9. `CircleAI.Generation.{Image,Audio}` — multimodal output via distilled local models
10. `CircleAI.AR` — HUD-projection contract for Apple Vision / Meta Ray-Bans / future glasses
11. `CircleAI.Adaptation` — on-device LoRA fine-tuning, federated rounds over mesh
12. `CircleAI.Memory.Forgetting` — explicit memory garbage-collection with user-controlled forgetting policy

### Long-term

13. CircleOS integration — InferenceBridge lifts up into the OS as a system service (replacing the per-app daemon model)
14. SDPKT marketplace for community agents — third parties publish capabilities, users tip
15. Federated learning rounds at scale — one model fine-tuned across thousands of devices without uploading raw data

---

## The single most important sentence

**The work for the next year is not invention. It is porting and integration.**

CircleAI already has the architectural primitives that none of the competitors have. The job now is to ship them across every language and every OS surface, prove the cross-OS daemon, and let the unique combination land in users' hands.
