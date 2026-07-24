# CircleAI — Vision & Roadmap

> **The thesis.** CircleAI is not a local ChatGPT. It is an on-device **organism**
> that *makes* artifacts and *takes* actions — offline, de-Googled, on the phone
> people already own. Frontier AI **answers**; CircleAI **produces and does**.
> That is a category a data-centre product cannot follow us into.

We do not compete feature-for-feature with GPT/Claude/Gemini — against a 0.6B
model that is a losing scoreboard. We compete on being the thing that still
works when the network doesn't, on a R2,500 phone, with no account and no data
trail, and that hands you a finished CV / advert / invoice at the end.

---

## The organism

The brain/body framing is the organising principle, not a metaphor we drop after
the intro. Each part is a real subsystem; each capability is a **specialist
behind a tool call**, orchestrated by the Neuron we already proved on the Huawei.

| Part | Role | In code | Status |
|---|---|---|---|
| **Brain** | reason + orchestrate | `NeuronNode` + concierge router + 2-slot residency | ✅ proven on Huawei |
| **Nervous system** | route each turn | `HeuristicNeuronRouter` → generalist / specialist | ✅ proven |
| **Memory** | remember across turns | episodic + RAG + knowledge graph | ✅ proven |
| **Metabolism** | use what the body supports | `DeviceAwareModelSelector` — floor-to-ceiling ladder | ✅ chat; extend to all modalities |
| **Hands (call tools)** | act in the world | tool calling | ✅ proven (battery, SKU price) |
| **Ears + mouth** | hear + speak | Whisper ASR + Piper TTS + `VoiceLoop` | 🟡 built, **never run on a phone** |
| **Eyes** | see images | `KimiVlGenerator` | 🟠 runtime built, **no model catalogued** |
| **Hands (make artifacts)** | CVs, invoices, media | — | ⬜ **the new work** |
| **Immune system** | defend, always-on | Watcher / ipblocklist / intercept | ⬜ not built |
| **Between bodies** | borrow a stronger peer's brain | AetherNet mesh offload | 🟠 contracts only (transport stubbed) |

Legend: ✅ built & proven · 🟡 built, unproven on device · 🟠 partial · ⬜ not built

---

## Principles (these shape every decision)

1. **Offline-first. No cloud, ever.** Tariffs closed that door. A weak phone
   scales via the mesh (peer offload), never via a server.
2. **De-Googled.** No GMS, no Play Services, no Google resolvers. Open stack only.
3. **Floor-to-ceiling ladder.** Build for the P30 Lite; the *same code* scales up
   on a Pixel/Samsung because the selector picks the best model the hardware can
   hold. "Dynamic — not all devices are the Huawei" lives in the selector.
4. **Existing ≠ best.** Audit before building to avoid duplicating *wiring* — but
   never settle for what happens to be there. Catalogue a **ladder of rungs**, not
   a single default.
5. **Programmatic media, not generative.** We render templates / HTML / TTS. We do
   not ship a 10 GB diffusion model. "Make a 30s advert from your content" — yes.
   "Dream up a novel image" — no.
6. **Prove on the Huawei before we claim it.** No claimed-vs-delivered gaps.
7. **One vertical fully, then breadth.** Land CVs end-to-end before scaffolding
   ten repos on an unproven base.
8. **Built-in protection is a shipping gate.** Security is defensive by purpose,
   end to end — it exists to shield the user, never to attack. A user must never
   be in the wild unprotected, so the defensive immune system (Phase 3 core) is a
   baseline every *shipped* build carries, not an optional later phase. We can
   prove verticals internally without it; we do not put the app in real users'
   hands without it.

---

## Phase 0 — Close the two open claims *(before anything new)*

We already ship two unproven claims. Close them first; they are the exact
claimed-vs-delivered trap.

- [ ] **Voice on the phone.** Build the Android voice head (`-p:ItVoiceOnAndroid=true`),
      run the wake → ASR → brain → TTS loop on the Huawei, prove it hears and speaks.
- [ ] **Vision has a model.** Fetch a real SHA-256 for an on-device VLM, catalogue it
      as `ModelModality.Vision`, exercise `KimiVlGenerator` on the phone with a photo.

---

## Phase 1 — Documents (the confirmed floor: CVs first)

Highest value to the actual user (no bundle, needs a job), lowest technical risk
(deterministic templating, no big model, trivially offline). The model writes the
content; the template does the layout. Everything after reuses this path.

- [ ] On-device document engine: content (model) → template → **PDF**, fully offline.
- [ ] **CV generator** — the minimum. Tailors bullets to a target role.
- [ ] Cover letters (same engine, different template).
- [ ] Invoices (ties into the ecosystem: BidBaas / SDPKT / LedgerAPI).
- [ ] Reports.
- [ ] Prove each on the Huawei — a PDF you can actually open and send.

**Sources / inspiration:** `career-ops`, `presenton`.
**Open decision:** is v1 a **document CV** (PDF) only, or does the **video CV**
(Phase 2) ship alongside it? Recommendation: document-first, video as fast-follow.

---

## Phase 2 — Media generation (the all-rounder)

Programmatic, not generative. `html-video` is the bet: MAUI already renders HTML,
so a 30s marketing clip or a **video CV** = template + the user's photos + a TTS
voiceover. Achievable offline, needs no giant model.

- [ ] **HTML → video / stills** renderer (30s social clips, video CVs).
- [ ] **TTS ladder** — evaluate `LUXTTS` / `voicebox` / `patter` / `speakr` as
      *rungs above Piper*, on real criteria: on-device quality, size, speed, and
      features Piper lacks (voice cloning, emotion, expressiveness, languages).
      Best rung that fits the device wins. **Audit against the 5 existing voice
      stacks first — do not build a 6th.**
- [ ] Data → graph / charts for reports & decks (`understand-anything`).
- [ ] Presentations (`presenton`).
- [ ] ASCII / stylised stills (`ASCILINE`).
- [ ] Music beds for clips (`suvmusic`) — hardest; on-device small model, tiered.

**Sources:** `html-video`, `ASCILINE`, `understand-anything`, `presenton`,
`suvmusic`, and the TTS cluster (`speakr`, `voicebox`, `patter`, `LUXTTS`).

---

## Phase 3 — Immune system (built-in security)

Autonomic and always-on — not a tool the user launches. **Defensive by purpose,
end to end** — every part exists to shield the user, never to attack. The
defensive core is a **pre-launch baseline** (Principle 8): no user ships into the
wild without it. The "antibodies" are defensive threat-*awareness* — warn and
protect — produced only under a defined threat, never bundled loose.

- [ ] **Reflexes (defensive, always-on):** on-device threat monitor + network
      defence. Pairs with Panik / Nope SOS. Sources: `Watcher`, `ipblocklist`,
      `intercept`, `shizuwall`.
- [ ] **Antibodies (offensive, gated by authorized-use boundary):** OSINT /
      threat-intel, invoked deliberately, never by default. Sources: `malwoverview`,
      `findme`, `deepdarkCTI`, `ghost-osint-crm`, `hacktricks`, `neko-master`.
      **Requires a written authorized-use boundary before it enters the product.**

---

## Phase 4 — Business operations (the operator)

CircleAI doesn't just make the invoice — it runs the small business: invoicing,
follow-ups, scheduling. Ties into the ecosystem economics.

- [ ] Automated operations engine. Sources: `gstack`, `automaton`, `dexter`,
      `career-ops`.

---

## Phase 5 — Code from mobile

Hardware-tiered, never cloud. A Pixel/Samsung (8–12 GB) holds a real 3–7B coding
model *locally* → the feature is genuinely present on capable phones and reports
*Unavailable* on a P30 Lite (exactly what the selector already expresses). Weak
phones get it later via mesh offload.

- [ ] On-device coding agent for capable phones. Sources: `PhonesHarness`,
      `adb-device-manager-2`, `dexter`.
- [ ] Mesh path for weak phones — **blocked on** AetherNet RT-12 (transport stubbed).

---

## Cross-cutting workstreams

- [ ] **Extend the selector ladder to every modality.** `PlanFor` already answers
      per-device for speech/vision; every new capability (documents, media, coding)
      reports the same way, so a Pixel gets the ceiling and a P30 the floor with no
      code change.
- [ ] **Mesh inference-offload** (AetherNet RT-12). The decentralised
      cloud-replacement. Contracts exist; the broadcast transport is stubbed.
- [ ] **Output surfaces beyond the phone** — e.g. cast to TV (`awesome-smart-tv`).

---

## How we work

- Prove it on the Huawei before it's "done." A screencap proves it *rendered*,
  not that it *works*.
- Audit the corpus (github.com/bhengubv forks) before building — a fork usually
  encodes the intended approach.
- Serialize heavy builds. Local test + manual deploy. `[skip ci]` on master.
