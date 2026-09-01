/**
 * companion/capability_registry
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/capability_registry.ts
//
// CapabilityRegistry.cs — the single source of truth for every external
// capability CircleAI plans to absorb. Ported verbatim: same 32 entries, same
// value bullets, same lookup semantics. `Find` / `ByPackage` compare
// case-insensitively (StringComparer.OrdinalIgnoreCase).

/** One absorption-target capability. Mirrors CircleAI.Companion.CapabilityEntry. */
export interface CapabilityEntry {
  /** Short slug. */
  readonly id: string;
  /** Upstream GitHub path (or null if mythology). */
  readonly repo: string | null;
  /** License classification. */
  readonly license: string;
  /** "vendor" / "pattern-port" / "wrap". */
  readonly strategy: string;
  /** CircleAI.* package the capability lands in. */
  readonly targetPackage: string;
  /** Concrete capability bullets (one per task in the original plan). */
  readonly valueBullets: readonly string[];
}

function entry(
  id: string,
  repo: string | null,
  license: string,
  strategy: string,
  targetPackage: string,
  valueBullets: readonly string[],
): CapabilityEntry {
  return { id, repo, license, strategy, targetPackage, valueBullets };
}

/** Static registry of every capability earmarked. Mirrors ExternalCapabilityRegistry.All. */
export const EXTERNAL_CAPABILITY_REGISTRY: readonly CapabilityEntry[] = [
  entry("claude-mem", "thedotmack/claude-mem", "MIT", "pattern-port", "CircleAI.Memory", [
    "Multi-platform memory adapter",
    "SQLite-local + Postgres-server dual persistence",
    "Three-tier semantic search",
    "Privacy-aware prompt stripping",
    "Multi-provider observation generation",
    "Five-hook session lifecycle",
    "MCP server for memory queries",
    "WAL-mode SQLite with FTS5",
    "Worker daemon with HTTP API",
    "Token economy tracking",
  ]),
  entry("Amphion", "open-mmlab/Amphion", "MIT", "wrap", "CircleAI.Speech", [
    "FastSpeech2/VITS/VALLE/NaturalSpeech2",
    "MaskGCT masked generative TTS",
    "Metis multi-task unified speech",
    "Voice conversion family",
    "Singing voice synthesis (8 architectures)",
    "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
    "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)",
    "Six-language G2P",
    "Speech enhancement + target speaker extraction",
    "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)",
  ]),
  entry("superpowers", "obra/superpowers", "MIT", "pattern-port", "CircleAI.Skills", [
    "14-skill library",
    "Cross-platform skill loader",
    "Per-task implementer + reviewer subagent orchestration",
    "Durable progress ledger",
    "File-based handoffs",
    "Verification-before-completion enforcement",
    "TDD RED-GREEN-REFACTOR mandatory gate",
    "Parallel agent dispatching",
  ]),
  entry("GitNexus", "GitNexus/GitNexus", "MIT", "wrap", "CircleAI.CodeUnderstanding", [
    "16-language tree-sitter code parser",
    "44-node-type + 21-relationship-type code knowledge graph",
    "KuzuDB-based graph database with WAL",
    "Sigma.js WebGL force-directed graph rendering",
    "LangChain ReAct agent with Cypher graph queries",
    "Snowflake Arctic Embed XS 384-dim local embeddings",
    "Hybrid BM25 + semantic search via Reciprocal Rank Fusion",
    "Multi-LLM provider support",
    "Privacy-first design",
    "Multi-branch incremental code indexing",
  ]),
  entry("PhoneHarness", "AmberSahdev/PhoneHarness", "MIT", "wrap", "CircleAI.WindowsAutomation", [
    "Five-stage agent pipeline (M0a/M1/M2/M3/M4)",
    "Semantic Android intents",
    "Low-level GUI gestures with normalised 0-1000 coords",
    "Vision + CLI dual-model architecture",
    "ADB host backend + SSH fallback for Termux",
    "Risk classification per action",
    "Skill injection via YAML",
    "HTTP streaming server for native-APK integration",
    "Action format auto-detection (Seed/AutoGLM/OpenAI)",
    "Trace JSONL append-only logging",
  ]),
  entry("yapsnap", "yourfavorite/yapsnap", "MIT", "wrap", "CircleAI.Speech", [
    "CPU-only streaming Zipformer transducer transcription",
    "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
    "10+ language transcription",
    "Sentence-level timestamps + diarization (ONNX, no PyTorch)",
  ]),
  entry("json-render", "json-render/json-render", "MIT", "wrap", "CircleAI.Tools.Catalog", [
    "Generative UI catalog",
    "10 platform adapters from one catalog",
    "36 pre-built shadcn/ui components",
  ]),
  entry("last30days", "obra/last30days-skill", "MIT", "wrap", "CircleAI.Inputs", [
    "Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
    "AI judge agent ranks by engagement",
    "Zero-config setup wizard auto-detects auth",
  ]),
  entry("gstack", "gstack-ai/gstack", "Apache-2.0", "pattern-port", "CircleAI.SDD", [
    "23 Claude Code skills covering full software factory",
    "Real browser QA via Playwright",
    "Team mode auto-update for shared repos",
    "OWASP + STRIDE security audit automation",
  ]),
  entry("Sponsio", "sponsio/sponsio", "MIT", "pattern-port", "CircleAI.Safety", [
    "Deterministic agent contracts at runtime (Fuzzy LTL Monitor)",
    "Five-action enforcement",
    "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
    "Pattern library + natural-language compilation into contracts",
  ]),
  entry("aimangastudio", "aimangastudio/aimangastudio", "MIT", "wrap", "CircleAI.Games", [
    "AI manga/comic creation (script + character + panel + style + batch export)",
  ]),
  entry("ai-resume-analyzer", "ai-resume-analyzer/ai-resume-analyzer", "MIT", "wrap", "CircleAI.Domain.JobSearch", [
    "Resume upload + AI scoring against jobs",
    "React Router 7 + Vite + Puter.js",
  ]),
  entry("Agent-Reach", "agent-reach/agent-reach", "MIT", "wrap", "CircleAI.Inputs", [
    "YouTube transcript extraction (no API)",
    "Twitter/X search + posts (no paid API)",
    "Reddit forum reading without 403",
    "Xiaohongshu/Bilibili/TikTok access",
    "GitHub repo info / issue reading without auth",
    "RSS subscription monitoring",
    "Clean webpage extraction",
    "Auto-select best access method per platform",
  ]),
  entry("career-ops", "career-ops/career-ops", "MIT", "wrap", "CircleAI.Domain.JobSearch", [
    "Multi-agent job search system (Node + Go + Playwright)",
    "Resume tailoring per job + cover letter + tracking + interview prep",
  ]),
  entry("presenton", "presenton/presenton", "MIT", "wrap", "CircleAI.Domain.Presentations", [
    "Open-source AI presentation generator",
    "10+ LLM providers",
    "AI Presentation Generation API + PPTX export",
    "Custom design/template support",
  ]),
  entry("show-me-the-money", "show-me-the-money/show-me-the-money", "MIT", "wrap", "CircleAI.AutonomousBiz", [
    "25 agent skills running an autonomous solo-founder business (idea → revenue)",
  ]),
  entry("Understand-Anything", "understand-anything/understand-anything", "MIT", "wrap", "CircleAI.CodeUnderstanding", [
    "Codebase/knowledge base/docs → interactive knowledge graph",
    "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)",
  ]),
  entry("dexter", "dexter/dexter", "MIT", "wrap", "CircleAI.Domain.FinancialAgent", [
    "Autonomous financial research agent",
    "Real-time market data tools",
    "WhatsApp integration",
  ]),
  entry("quant-mind", "quant-mind/quant-mind", "MIT", "wrap", "CircleAI.Domain.Finance", [
    "Knowledge extraction from financial papers/news/blogs/reports → queryable base",
  ]),
  entry(
    "Anthropic-Cybersecurity-Skills",
    "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills",
    "Apache-2.0",
    "wrap",
    "CircleAI.Skills",
    [
      "754 production-grade cybersecurity skills across 26 domains",
      "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
      "Compatible with 26+ AI platforms via agentskills.io",
    ],
  ),
  entry("HippoRAG", "OSU-NLP-Group/HippoRAG", "MIT", "wrap", "CircleAI.Memory.HippoRAG", [
    "Memory framework inspired by human long-term memory",
    "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG",
  ]),
  entry("Observer AI", "ObserverAI/observer-ai", "MIT", "wrap", "CircleAI.Observer", [
    "Micro-agent framework (sensors→models→tools, observe-log-react loop)",
    "Web app + downloadable desktop app + GitHub Pages deployment",
  ]),
  entry("Bluehound", "bluehound/bluehound", "MIT", "wrap", "CircleAI.Vision", [
    "BLE wardriving / real-time device discovery with persistent JSON db",
    "Real-time BLE anomaly detection",
  ]),
  entry("skylight", "skylight/skylight", "MIT", "wrap", "CircleAI.Spatial", [
    "ADS-B aircraft tracking via cheap RTL-SDR",
    "Project planes + live sky onto a ceiling",
  ]),
  entry("turbovec", "turbovec/turbovec", "MIT", "wrap", "CircleAI.Embeddings", [
    "TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
    "NEON / AVX-512BW hand-written kernels",
    "Online ingest with no train step",
    "Filter at search time",
  ]),
  entry("flame", "flame-engine/flame", "MIT", "wrap", "CircleAI.Games", [
    "Flutter game engine (2D/2.5D/physics/audio/input/camera)",
  ]),
  entry("kagent", "kagent-dev/kagent", "Apache-2.0", "wrap", "CircleAI.Operator", [
    "Kubernetes-native AI agent framework (Helm + A2A protocol)",
  ]),
  entry("airllm", "lyogavin/airllm", "Apache-2.0", "wrap", "CircleAI.Inference", [
    "70B model inference on a single 4GB GPU (no quantization)",
    "CPU inference + MacOS + sharded + non-sharded models",
  ]),
  entry("shard", "shard/shard", "Apache-2.0", "pattern-port", "CircleAI.Inference", [
    "KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
    "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
    "Fused compressed-K attention with RoPE undo/reapply",
  ]),
  entry("awesome-design-md", "design-md/awesome-design-md", "CC-BY-4.0", "wrap", "CircleAI.Skills.PackSources", [
    "DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)",
  ]),
];

/** Lookup by id (case-insensitive), or null when not found. */
export function findCapability(id: string): CapabilityEntry | null {
  const lc = id.toLowerCase();
  return EXTERNAL_CAPABILITY_REGISTRY.find((c) => c.id.toLowerCase() === lc) ?? null;
}

/** List entries whose target package matches (case-insensitive). */
export function capabilitiesByPackage(targetPackage: string): CapabilityEntry[] {
  const lc = targetPackage.toLowerCase();
  return EXTERNAL_CAPABILITY_REGISTRY.filter((c) => c.targetPackage.toLowerCase() === lc);
}

/**
 * Namespace object mirroring the C# static `ExternalCapabilityRegistry` so
 * callers can write `ExternalCapabilityRegistry.all` / `.find(id)` / `.byPackage(pkg)`.
 */
export const ExternalCapabilityRegistry = {
  get all(): readonly CapabilityEntry[] {
    return EXTERNAL_CAPABILITY_REGISTRY;
  },
  find: findCapability,
  byPackage: capabilitiesByPackage,
} as const;
