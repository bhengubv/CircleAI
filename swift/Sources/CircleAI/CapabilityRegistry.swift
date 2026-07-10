// CapabilityRegistry.swift
//
// Port of CircleAI.Companion.CapabilityRegistry (CapabilityRegistry.cs).
// The single source of truth for every external capability we want CircleAI
// to absorb: each entry names the capability, its upstream repo, license,
// absorption strategy, the CircleAI.* package it lands in, and the concrete
// value bullets.
//
// In-memory static registry — a faithful, order-preserving port of the C#
// `ExternalCapabilityRegistry.All` array plus the `Find` and `ByPackage`
// lookups.

import Foundation

/// One absorption-target capability.
/// - `id`: short slug.
/// - `repo`: upstream GitHub path (or nil if mythology).
/// - `license`: license classification.
/// - `strategy`: "vendor" / "pattern-port" / "wrap".
/// - `targetPackage`: CircleAI.* package the capability lands in.
/// - `valueBullets`: concrete capability bullets (one per task in the original plan).
public struct CapabilityEntry: Sendable, Equatable {
    public let id: String
    public let repo: String?
    public let license: String
    public let strategy: String
    public let targetPackage: String
    public let valueBullets: [String]

    public init(id: String, repo: String?, license: String, strategy: String,
                targetPackage: String, valueBullets: [String]) {
        self.id = id
        self.repo = repo
        self.license = license
        self.strategy = strategy
        self.targetPackage = targetPackage
        self.valueBullets = valueBullets
    }
}

/// Static registry of every capability we've earmarked. Order matches the C#
/// reference exactly so index-based tests stay stable.
public enum ExternalCapabilityRegistry {
    public static let all: [CapabilityEntry] = [
        CapabilityEntry(id: "claude-mem", repo: "thedotmack/claude-mem", license: "MIT", strategy: "pattern-port", targetPackage: "CircleAI.Memory",
            valueBullets: ["Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence", "Three-tier semantic search", "Privacy-aware prompt stripping",
                           "Multi-provider observation generation", "Five-hook session lifecycle", "MCP server for memory queries", "WAL-mode SQLite with FTS5",
                           "Worker daemon with HTTP API", "Token economy tracking"]),
        CapabilityEntry(id: "Amphion", repo: "open-mmlab/Amphion", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Speech",
            valueBullets: ["FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS", "Metis multi-task unified speech",
                           "Voice conversion family", "Singing voice synthesis (8 architectures)", "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
                           "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)", "Six-language G2P", "Speech enhancement + target speaker extraction",
                           "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)"]),
        CapabilityEntry(id: "superpowers", repo: "obra/superpowers", license: "MIT", strategy: "pattern-port", targetPackage: "CircleAI.Skills",
            valueBullets: ["14-skill library", "Cross-platform skill loader", "Per-task implementer + reviewer subagent orchestration",
                           "Durable progress ledger", "File-based handoffs", "Verification-before-completion enforcement",
                           "TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching"]),
        CapabilityEntry(id: "GitNexus", repo: "GitNexus/GitNexus", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.CodeUnderstanding",
            valueBullets: ["16-language tree-sitter code parser", "44-node-type + 21-relationship-type code knowledge graph",
                           "KuzuDB-based graph database with WAL", "Sigma.js WebGL force-directed graph rendering",
                           "LangChain ReAct agent with Cypher graph queries", "Snowflake Arctic Embed XS 384-dim local embeddings",
                           "Hybrid BM25 + semantic search via Reciprocal Rank Fusion", "Multi-LLM provider support",
                           "Privacy-first design", "Multi-branch incremental code indexing"]),
        CapabilityEntry(id: "PhoneHarness", repo: "AmberSahdev/PhoneHarness", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.WindowsAutomation",
            valueBullets: ["Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
                           "Low-level GUI gestures with normalised 0-1000 coords", "Vision + CLI dual-model architecture",
                           "ADB host backend + SSH fallback for Termux", "Risk classification per action",
                           "Skill injection via YAML", "HTTP streaming server for native-APK integration",
                           "Action format auto-detection (Seed/AutoGLM/OpenAI)", "Trace JSONL append-only logging"]),
        CapabilityEntry(id: "yapsnap", repo: "yourfavorite/yapsnap", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Speech",
            valueBullets: ["CPU-only streaming Zipformer transducer transcription", "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
                           "10+ language transcription", "Sentence-level timestamps + diarization (ONNX, no PyTorch)"]),
        CapabilityEntry(id: "json-render", repo: "json-render/json-render", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Tools.Catalog",
            valueBullets: ["Generative UI catalog", "10 platform adapters from one catalog", "36 pre-built shadcn/ui components"]),
        CapabilityEntry(id: "last30days", repo: "obra/last30days-skill", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Inputs",
            valueBullets: ["Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
                           "AI judge agent ranks by engagement", "Zero-config setup wizard auto-detects auth"]),
        CapabilityEntry(id: "gstack", repo: "gstack-ai/gstack", license: "Apache-2.0", strategy: "pattern-port", targetPackage: "CircleAI.SDD",
            valueBullets: ["23 Claude Code skills covering full software factory",
                           "Real browser QA via Playwright", "Team mode auto-update for shared repos",
                           "OWASP + STRIDE security audit automation"]),
        CapabilityEntry(id: "Sponsio", repo: "sponsio/sponsio", license: "MIT", strategy: "pattern-port", targetPackage: "CircleAI.Safety",
            valueBullets: ["Deterministic agent contracts at runtime (Fuzzy LTL Monitor)", "Five-action enforcement",
                           "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
                           "Pattern library + natural-language compilation into contracts"]),
        CapabilityEntry(id: "aimangastudio", repo: "aimangastudio/aimangastudio", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Games",
            valueBullets: ["AI manga/comic creation (script + character + panel + style + batch export)"]),
        CapabilityEntry(id: "ai-resume-analyzer", repo: "ai-resume-analyzer/ai-resume-analyzer", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Domain.JobSearch",
            valueBullets: ["Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js"]),
        CapabilityEntry(id: "Agent-Reach", repo: "agent-reach/agent-reach", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Inputs",
            valueBullets: ["YouTube transcript extraction (no API)", "Twitter/X search + posts (no paid API)",
                           "Reddit forum reading without 403", "Xiaohongshu/Bilibili/TikTok access",
                           "GitHub repo info / issue reading without auth", "RSS subscription monitoring",
                           "Clean webpage extraction", "Auto-select best access method per platform"]),
        CapabilityEntry(id: "career-ops", repo: "career-ops/career-ops", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Domain.JobSearch",
            valueBullets: ["Multi-agent job search system (Node + Go + Playwright)",
                           "Resume tailoring per job + cover letter + tracking + interview prep"]),
        CapabilityEntry(id: "presenton", repo: "presenton/presenton", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Domain.Presentations",
            valueBullets: ["Open-source AI presentation generator", "10+ LLM providers",
                           "AI Presentation Generation API + PPTX export", "Custom design/template support"]),
        CapabilityEntry(id: "show-me-the-money", repo: "show-me-the-money/show-me-the-money", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.AutonomousBiz",
            valueBullets: ["25 agent skills running an autonomous solo-founder business (idea → revenue)"]),
        CapabilityEntry(id: "Understand-Anything", repo: "understand-anything/understand-anything", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.CodeUnderstanding",
            valueBullets: ["Codebase/knowledge base/docs → interactive knowledge graph",
                           "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)"]),
        CapabilityEntry(id: "dexter", repo: "dexter/dexter", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Domain.FinancialAgent",
            valueBullets: ["Autonomous financial research agent", "Real-time market data tools",
                           "WhatsApp integration"]),
        CapabilityEntry(id: "quant-mind", repo: "quant-mind/quant-mind", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Domain.Finance",
            valueBullets: ["Knowledge extraction from financial papers/news/blogs/reports → queryable base"]),
        CapabilityEntry(id: "Anthropic-Cybersecurity-Skills", repo: "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills", license: "Apache-2.0", strategy: "wrap", targetPackage: "CircleAI.Skills",
            valueBullets: ["754 production-grade cybersecurity skills across 26 domains",
                           "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
                           "Compatible with 26+ AI platforms via agentskills.io"]),
        CapabilityEntry(id: "HippoRAG", repo: "OSU-NLP-Group/HippoRAG", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Memory.HippoRAG",
            valueBullets: ["Memory framework inspired by human long-term memory",
                           "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG"]),
        CapabilityEntry(id: "Observer AI", repo: "ObserverAI/observer-ai", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Observer",
            valueBullets: ["Micro-agent framework (sensors→models→tools, observe-log-react loop)",
                           "Web app + downloadable desktop app + GitHub Pages deployment"]),
        CapabilityEntry(id: "Bluehound", repo: "bluehound/bluehound", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Vision",
            valueBullets: ["BLE wardriving / real-time device discovery with persistent JSON db",
                           "Real-time BLE anomaly detection"]),
        CapabilityEntry(id: "skylight", repo: "skylight/skylight", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Spatial",
            valueBullets: ["ADS-B aircraft tracking via cheap RTL-SDR",
                           "Project planes + live sky onto a ceiling"]),
        CapabilityEntry(id: "turbovec", repo: "turbovec/turbovec", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Embeddings",
            valueBullets: ["TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
                           "NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
                           "Filter at search time"]),
        CapabilityEntry(id: "flame", repo: "flame-engine/flame", license: "MIT", strategy: "wrap", targetPackage: "CircleAI.Games",
            valueBullets: ["Flutter game engine (2D/2.5D/physics/audio/input/camera)"]),
        CapabilityEntry(id: "kagent", repo: "kagent-dev/kagent", license: "Apache-2.0", strategy: "wrap", targetPackage: "CircleAI.Operator",
            valueBullets: ["Kubernetes-native AI agent framework (Helm + A2A protocol)"]),
        CapabilityEntry(id: "airllm", repo: "lyogavin/airllm", license: "Apache-2.0", strategy: "wrap", targetPackage: "CircleAI.Inference",
            valueBullets: ["70B model inference on a single 4GB GPU (no quantization)",
                           "CPU inference + MacOS + sharded + non-sharded models"]),
        CapabilityEntry(id: "shard", repo: "shard/shard", license: "Apache-2.0", strategy: "pattern-port", targetPackage: "CircleAI.Inference",
            valueBullets: ["KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
                           "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
                           "Fused compressed-K attention with RoPE undo/reapply"]),
        CapabilityEntry(id: "awesome-design-md", repo: "design-md/awesome-design-md", license: "CC-BY-4.0", strategy: "wrap", targetPackage: "CircleAI.Skills.PackSources",
            valueBullets: ["DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)"]),
    ]

    /// Lookup by id (case-insensitive).
    public static func find(_ id: String) -> CapabilityEntry? {
        all.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    /// List entries by their target package (case-insensitive).
    public static func byPackage(_ targetPackage: String) -> [CapabilityEntry] {
        all.filter { $0.targetPackage.caseInsensitiveCompare(targetPackage) == .orderedSame }
    }
}
