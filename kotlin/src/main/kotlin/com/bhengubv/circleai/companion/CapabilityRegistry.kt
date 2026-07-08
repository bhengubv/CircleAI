// CapabilityRegistry.kt
//
// Kotlin port of CircleAI.Companion.CapabilityRegistry — the C# reference
// (CapabilityRegistry.cs) is the EXACT spec. A single source of truth for every
// external capability CircleAI intends to absorb. Each entry names the
// capability, upstream repo, licence, absorption strategy, the CircleAI.*
// package it lands in, and its distinct value bullets.

package com.bhengubv.circleai.companion

/**
 * One absorption-target capability. Mirrors the C# `CapabilityEntry` record.
 *
 * @param id Short slug.
 * @param repo Upstream GitHub path (or null if mythology).
 * @param license License classification.
 * @param strategy "vendor" / "pattern-port" / "wrap".
 * @param targetPackage CircleAI.* package the capability lands in.
 * @param valueBullets Concrete capability bullets (one per task in the original plan).
 */
data class CapabilityEntry(
    val id: String,
    val repo: String?,
    val license: String,
    val strategy: String,
    val targetPackage: String,
    val valueBullets: List<String>,
)

/** Static registry of every capability earmarked for absorption. */
object ExternalCapabilityRegistry {

    val all: List<CapabilityEntry> = listOf(
        CapabilityEntry(
            "claude-mem", "thedotmack/claude-mem", "MIT", "pattern-port", "CircleAI.Memory",
            listOf(
                "Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence",
                "Three-tier semantic search", "Privacy-aware prompt stripping",
                "Multi-provider observation generation", "Five-hook session lifecycle",
                "MCP server for memory queries", "WAL-mode SQLite with FTS5",
                "Worker daemon with HTTP API", "Token economy tracking",
            ),
        ),
        CapabilityEntry(
            "Amphion", "open-mmlab/Amphion", "MIT", "wrap", "CircleAI.Speech",
            listOf(
                "FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS",
                "Metis multi-task unified speech", "Voice conversion family",
                "Singing voice synthesis (8 architectures)",
                "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
                "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)",
                "Six-language G2P", "Speech enhancement + target speaker extraction",
                "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)",
            ),
        ),
        CapabilityEntry(
            "superpowers", "obra/superpowers", "MIT", "pattern-port", "CircleAI.Skills",
            listOf(
                "14-skill library", "Cross-platform skill loader",
                "Per-task implementer + reviewer subagent orchestration",
                "Durable progress ledger", "File-based handoffs",
                "Verification-before-completion enforcement",
                "TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching",
            ),
        ),
        CapabilityEntry(
            "GitNexus", "GitNexus/GitNexus", "MIT", "wrap", "CircleAI.CodeUnderstanding",
            listOf(
                "16-language tree-sitter code parser",
                "44-node-type + 21-relationship-type code knowledge graph",
                "KuzuDB-based graph database with WAL",
                "Sigma.js WebGL force-directed graph rendering",
                "LangChain ReAct agent with Cypher graph queries",
                "Snowflake Arctic Embed XS 384-dim local embeddings",
                "Hybrid BM25 + semantic search via Reciprocal Rank Fusion",
                "Multi-LLM provider support", "Privacy-first design",
                "Multi-branch incremental code indexing",
            ),
        ),
        CapabilityEntry(
            "PhoneHarness", "AmberSahdev/PhoneHarness", "MIT", "wrap", "CircleAI.WindowsAutomation",
            listOf(
                "Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
                "Low-level GUI gestures with normalised 0-1000 coords",
                "Vision + CLI dual-model architecture",
                "ADB host backend + SSH fallback for Termux", "Risk classification per action",
                "Skill injection via YAML", "HTTP streaming server for native-APK integration",
                "Action format auto-detection (Seed/AutoGLM/OpenAI)", "Trace JSONL append-only logging",
            ),
        ),
        CapabilityEntry(
            "yapsnap", "yourfavorite/yapsnap", "MIT", "wrap", "CircleAI.Speech",
            listOf(
                "CPU-only streaming Zipformer transducer transcription",
                "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
                "10+ language transcription",
                "Sentence-level timestamps + diarization (ONNX, no PyTorch)",
            ),
        ),
        CapabilityEntry(
            "json-render", "json-render/json-render", "MIT", "wrap", "CircleAI.Tools.Catalog",
            listOf(
                "Generative UI catalog", "10 platform adapters from one catalog",
                "36 pre-built shadcn/ui components",
            ),
        ),
        CapabilityEntry(
            "last30days", "obra/last30days-skill", "MIT", "wrap", "CircleAI.Inputs",
            listOf(
                "Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
                "AI judge agent ranks by engagement", "Zero-config setup wizard auto-detects auth",
            ),
        ),
        CapabilityEntry(
            "gstack", "gstack-ai/gstack", "Apache-2.0", "pattern-port", "CircleAI.SDD",
            listOf(
                "23 Claude Code skills covering full software factory",
                "Real browser QA via Playwright", "Team mode auto-update for shared repos",
                "OWASP + STRIDE security audit automation",
            ),
        ),
        CapabilityEntry(
            "Sponsio", "sponsio/sponsio", "MIT", "pattern-port", "CircleAI.Safety",
            listOf(
                "Deterministic agent contracts at runtime (Fuzzy LTL Monitor)",
                "Five-action enforcement",
                "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
                "Pattern library + natural-language compilation into contracts",
            ),
        ),
        CapabilityEntry(
            "aimangastudio", "aimangastudio/aimangastudio", "MIT", "wrap", "CircleAI.Games",
            listOf("AI manga/comic creation (script + character + panel + style + batch export)"),
        ),
        CapabilityEntry(
            "ai-resume-analyzer", "ai-resume-analyzer/ai-resume-analyzer", "MIT", "wrap", "CircleAI.Domain.JobSearch",
            listOf("Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js"),
        ),
        CapabilityEntry(
            "Agent-Reach", "agent-reach/agent-reach", "MIT", "wrap", "CircleAI.Inputs",
            listOf(
                "YouTube transcript extraction (no API)", "Twitter/X search + posts (no paid API)",
                "Reddit forum reading without 403", "Xiaohongshu/Bilibili/TikTok access",
                "GitHub repo info / issue reading without auth", "RSS subscription monitoring",
                "Clean webpage extraction", "Auto-select best access method per platform",
            ),
        ),
        CapabilityEntry(
            "career-ops", "career-ops/career-ops", "MIT", "wrap", "CircleAI.Domain.JobSearch",
            listOf(
                "Multi-agent job search system (Node + Go + Playwright)",
                "Resume tailoring per job + cover letter + tracking + interview prep",
            ),
        ),
        CapabilityEntry(
            "presenton", "presenton/presenton", "MIT", "wrap", "CircleAI.Domain.Presentations",
            listOf(
                "Open-source AI presentation generator", "10+ LLM providers",
                "AI Presentation Generation API + PPTX export", "Custom design/template support",
            ),
        ),
        CapabilityEntry(
            "show-me-the-money", "show-me-the-money/show-me-the-money", "MIT", "wrap", "CircleAI.AutonomousBiz",
            listOf("25 agent skills running an autonomous solo-founder business (idea → revenue)"),
        ),
        CapabilityEntry(
            "Understand-Anything", "understand-anything/understand-anything", "MIT", "wrap", "CircleAI.CodeUnderstanding",
            listOf(
                "Codebase/knowledge base/docs → interactive knowledge graph",
                "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)",
            ),
        ),
        CapabilityEntry(
            "dexter", "dexter/dexter", "MIT", "wrap", "CircleAI.Domain.FinancialAgent",
            listOf(
                "Autonomous financial research agent", "Real-time market data tools",
                "WhatsApp integration",
            ),
        ),
        CapabilityEntry(
            "quant-mind", "quant-mind/quant-mind", "MIT", "wrap", "CircleAI.Domain.Finance",
            listOf("Knowledge extraction from financial papers/news/blogs/reports → queryable base"),
        ),
        CapabilityEntry(
            "Anthropic-Cybersecurity-Skills",
            "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills", "Apache-2.0", "wrap", "CircleAI.Skills",
            listOf(
                "754 production-grade cybersecurity skills across 26 domains",
                "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
                "Compatible with 26+ AI platforms via agentskills.io",
            ),
        ),
        CapabilityEntry(
            "HippoRAG", "OSU-NLP-Group/HippoRAG", "MIT", "wrap", "CircleAI.Memory.HippoRAG",
            listOf(
                "Memory framework inspired by human long-term memory",
                "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG",
            ),
        ),
        CapabilityEntry(
            "Observer AI", "ObserverAI/observer-ai", "MIT", "wrap", "CircleAI.Observer",
            listOf(
                "Micro-agent framework (sensors→models→tools, observe-log-react loop)",
                "Web app + downloadable desktop app + GitHub Pages deployment",
            ),
        ),
        CapabilityEntry(
            "Bluehound", "bluehound/bluehound", "MIT", "wrap", "CircleAI.Vision",
            listOf(
                "BLE wardriving / real-time device discovery with persistent JSON db",
                "Real-time BLE anomaly detection",
            ),
        ),
        CapabilityEntry(
            "skylight", "skylight/skylight", "MIT", "wrap", "CircleAI.Spatial",
            listOf(
                "ADS-B aircraft tracking via cheap RTL-SDR",
                "Project planes + live sky onto a ceiling",
            ),
        ),
        CapabilityEntry(
            "turbovec", "turbovec/turbovec", "MIT", "wrap", "CircleAI.Embeddings",
            listOf(
                "TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
                "NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
                "Filter at search time",
            ),
        ),
        CapabilityEntry(
            "flame", "flame-engine/flame", "MIT", "wrap", "CircleAI.Games",
            listOf("Flutter game engine (2D/2.5D/physics/audio/input/camera)"),
        ),
        CapabilityEntry(
            "kagent", "kagent-dev/kagent", "Apache-2.0", "wrap", "CircleAI.Operator",
            listOf("Kubernetes-native AI agent framework (Helm + A2A protocol)"),
        ),
        CapabilityEntry(
            "airllm", "lyogavin/airllm", "Apache-2.0", "wrap", "CircleAI.Inference",
            listOf(
                "70B model inference on a single 4GB GPU (no quantization)",
                "CPU inference + MacOS + sharded + non-sharded models",
            ),
        ),
        CapabilityEntry(
            "shard", "shard/shard", "Apache-2.0", "pattern-port", "CircleAI.Inference",
            listOf(
                "KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
                "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
                "Fused compressed-K attention with RoPE undo/reapply",
            ),
        ),
        CapabilityEntry(
            "awesome-design-md", "design-md/awesome-design-md", "CC-BY-4.0", "wrap", "CircleAI.Skills.PackSources",
            listOf(
                "DESIGN.md collection from major brands " +
                    "(Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)",
            ),
        ),
    )

    /** Lookup by id (case-insensitive). */
    fun find(id: String): CapabilityEntry? = all.firstOrNull { it.id.equals(id, ignoreCase = true) }

    /** List entries by their target package (case-insensitive). */
    fun byPackage(targetPackage: String): List<CapabilityEntry> =
        all.filter { it.targetPackage.equals(targetPackage, ignoreCase = true) }
}
