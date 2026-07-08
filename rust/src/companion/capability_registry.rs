//! capability_registry.rs
//!
//! `CapabilityEntry` + `ExternalCapabilityRegistry` — the single source of truth
//! for every external capability CircleAI intends to absorb. Ported 1:1 from
//! `CapabilityRegistry.cs`; the full entry list, value bullets, target packages,
//! and lookup helpers (`find`, `by_package`) are preserved verbatim.

/// One absorption-target capability.
#[derive(Debug, Clone, PartialEq)]
pub struct CapabilityEntry {
    /// Short slug.
    pub id: String,
    /// Upstream GitHub path (or `None` if mythology).
    pub repo: Option<String>,
    /// License classification.
    pub license: String,
    /// `"vendor"` / `"pattern-port"` / `"wrap"`.
    pub strategy: String,
    /// The `CircleAI.*` package the capability lands in.
    pub target_package: String,
    /// Concrete capability bullets.
    pub value_bullets: Vec<String>,
}

impl CapabilityEntry {
    pub fn new(
        id: &str,
        repo: Option<&str>,
        license: &str,
        strategy: &str,
        target_package: &str,
        value_bullets: &[&str],
    ) -> Self {
        Self {
            id: id.to_string(),
            repo: repo.map(|s| s.to_string()),
            license: license.to_string(),
            strategy: strategy.to_string(),
            target_package: target_package.to_string(),
            value_bullets: value_bullets.iter().map(|s| s.to_string()).collect(),
        }
    }
}

/// Static registry of every capability earmarked for absorption.
pub struct ExternalCapabilityRegistry;

impl ExternalCapabilityRegistry {
    /// The full absorption catalogue.
    pub fn all() -> Vec<CapabilityEntry> {
        vec![
            CapabilityEntry::new("claude-mem", Some("thedotmack/claude-mem"), "MIT", "pattern-port", "CircleAI.Memory",
                &["Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence", "Three-tier semantic search", "Privacy-aware prompt stripping",
                  "Multi-provider observation generation", "Five-hook session lifecycle", "MCP server for memory queries", "WAL-mode SQLite with FTS5",
                  "Worker daemon with HTTP API", "Token economy tracking"]),
            CapabilityEntry::new("Amphion", Some("open-mmlab/Amphion"), "MIT", "wrap", "CircleAI.Speech",
                &["FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS", "Metis multi-task unified speech",
                  "Voice conversion family", "Singing voice synthesis (8 architectures)", "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
                  "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)", "Six-language G2P", "Speech enhancement + target speaker extraction",
                  "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)"]),
            CapabilityEntry::new("superpowers", Some("obra/superpowers"), "MIT", "pattern-port", "CircleAI.Skills",
                &["14-skill library", "Cross-platform skill loader", "Per-task implementer + reviewer subagent orchestration",
                  "Durable progress ledger", "File-based handoffs", "Verification-before-completion enforcement",
                  "TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching"]),
            CapabilityEntry::new("GitNexus", Some("GitNexus/GitNexus"), "MIT", "wrap", "CircleAI.CodeUnderstanding",
                &["16-language tree-sitter code parser", "44-node-type + 21-relationship-type code knowledge graph",
                  "KuzuDB-based graph database with WAL", "Sigma.js WebGL force-directed graph rendering",
                  "LangChain ReAct agent with Cypher graph queries", "Snowflake Arctic Embed XS 384-dim local embeddings",
                  "Hybrid BM25 + semantic search via Reciprocal Rank Fusion", "Multi-LLM provider support",
                  "Privacy-first design", "Multi-branch incremental code indexing"]),
            CapabilityEntry::new("PhoneHarness", Some("AmberSahdev/PhoneHarness"), "MIT", "wrap", "CircleAI.WindowsAutomation",
                &["Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
                  "Low-level GUI gestures with normalised 0-1000 coords", "Vision + CLI dual-model architecture",
                  "ADB host backend + SSH fallback for Termux", "Risk classification per action",
                  "Skill injection via YAML", "HTTP streaming server for native-APK integration",
                  "Action format auto-detection (Seed/AutoGLM/OpenAI)", "Trace JSONL append-only logging"]),
            CapabilityEntry::new("yapsnap", Some("yourfavorite/yapsnap"), "MIT", "wrap", "CircleAI.Speech",
                &["CPU-only streaming Zipformer transducer transcription", "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
                  "10+ language transcription", "Sentence-level timestamps + diarization (ONNX, no PyTorch)"]),
            CapabilityEntry::new("json-render", Some("json-render/json-render"), "MIT", "wrap", "CircleAI.Tools.Catalog",
                &["Generative UI catalog", "10 platform adapters from one catalog", "36 pre-built shadcn/ui components"]),
            CapabilityEntry::new("last30days", Some("obra/last30days-skill"), "MIT", "wrap", "CircleAI.Inputs",
                &["Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
                  "AI judge agent ranks by engagement", "Zero-config setup wizard auto-detects auth"]),
            CapabilityEntry::new("gstack", Some("gstack-ai/gstack"), "Apache-2.0", "pattern-port", "CircleAI.SDD",
                &["23 Claude Code skills covering full software factory",
                  "Real browser QA via Playwright", "Team mode auto-update for shared repos",
                  "OWASP + STRIDE security audit automation"]),
            CapabilityEntry::new("Sponsio", Some("sponsio/sponsio"), "MIT", "pattern-port", "CircleAI.Safety",
                &["Deterministic agent contracts at runtime (Fuzzy LTL Monitor)", "Five-action enforcement",
                  "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
                  "Pattern library + natural-language compilation into contracts"]),
            CapabilityEntry::new("aimangastudio", Some("aimangastudio/aimangastudio"), "MIT", "wrap", "CircleAI.Games",
                &["AI manga/comic creation (script + character + panel + style + batch export)"]),
            CapabilityEntry::new("ai-resume-analyzer", Some("ai-resume-analyzer/ai-resume-analyzer"), "MIT", "wrap", "CircleAI.Domain.JobSearch",
                &["Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js"]),
            CapabilityEntry::new("Agent-Reach", Some("agent-reach/agent-reach"), "MIT", "wrap", "CircleAI.Inputs",
                &["YouTube transcript extraction (no API)", "Twitter/X search + posts (no paid API)",
                  "Reddit forum reading without 403", "Xiaohongshu/Bilibili/TikTok access",
                  "GitHub repo info / issue reading without auth", "RSS subscription monitoring",
                  "Clean webpage extraction", "Auto-select best access method per platform"]),
            CapabilityEntry::new("career-ops", Some("career-ops/career-ops"), "MIT", "wrap", "CircleAI.Domain.JobSearch",
                &["Multi-agent job search system (Node + Go + Playwright)",
                  "Resume tailoring per job + cover letter + tracking + interview prep"]),
            CapabilityEntry::new("presenton", Some("presenton/presenton"), "MIT", "wrap", "CircleAI.Domain.Presentations",
                &["Open-source AI presentation generator", "10+ LLM providers",
                  "AI Presentation Generation API + PPTX export", "Custom design/template support"]),
            CapabilityEntry::new("show-me-the-money", Some("show-me-the-money/show-me-the-money"), "MIT", "wrap", "CircleAI.AutonomousBiz",
                &["25 agent skills running an autonomous solo-founder business (idea \u{2192} revenue)"]),
            CapabilityEntry::new("Understand-Anything", Some("understand-anything/understand-anything"), "MIT", "wrap", "CircleAI.CodeUnderstanding",
                &["Codebase/knowledge base/docs \u{2192} interactive knowledge graph",
                  "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)"]),
            CapabilityEntry::new("dexter", Some("dexter/dexter"), "MIT", "wrap", "CircleAI.Domain.FinancialAgent",
                &["Autonomous financial research agent", "Real-time market data tools",
                  "WhatsApp integration"]),
            CapabilityEntry::new("quant-mind", Some("quant-mind/quant-mind"), "MIT", "wrap", "CircleAI.Domain.Finance",
                &["Knowledge extraction from financial papers/news/blogs/reports \u{2192} queryable base"]),
            CapabilityEntry::new("Anthropic-Cybersecurity-Skills", Some("Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills"), "Apache-2.0", "wrap", "CircleAI.Skills",
                &["754 production-grade cybersecurity skills across 26 domains",
                  "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
                  "Compatible with 26+ AI platforms via agentskills.io"]),
            CapabilityEntry::new("HippoRAG", Some("OSU-NLP-Group/HippoRAG"), "MIT", "wrap", "CircleAI.Memory.HippoRAG",
                &["Memory framework inspired by human long-term memory",
                  "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG"]),
            CapabilityEntry::new("Observer AI", Some("ObserverAI/observer-ai"), "MIT", "wrap", "CircleAI.Observer",
                &["Micro-agent framework (sensors\u{2192}models\u{2192}tools, observe-log-react loop)",
                  "Web app + downloadable desktop app + GitHub Pages deployment"]),
            CapabilityEntry::new("Bluehound", Some("bluehound/bluehound"), "MIT", "wrap", "CircleAI.Vision",
                &["BLE wardriving / real-time device discovery with persistent JSON db",
                  "Real-time BLE anomaly detection"]),
            CapabilityEntry::new("skylight", Some("skylight/skylight"), "MIT", "wrap", "CircleAI.Spatial",
                &["ADS-B aircraft tracking via cheap RTL-SDR",
                  "Project planes + live sky onto a ceiling"]),
            CapabilityEntry::new("turbovec", Some("turbovec/turbovec"), "MIT", "wrap", "CircleAI.Embeddings",
                &["TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
                  "NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
                  "Filter at search time"]),
            CapabilityEntry::new("flame", Some("flame-engine/flame"), "MIT", "wrap", "CircleAI.Games",
                &["Flutter game engine (2D/2.5D/physics/audio/input/camera)"]),
            CapabilityEntry::new("kagent", Some("kagent-dev/kagent"), "Apache-2.0", "wrap", "CircleAI.Operator",
                &["Kubernetes-native AI agent framework (Helm + A2A protocol)"]),
            CapabilityEntry::new("airllm", Some("lyogavin/airllm"), "Apache-2.0", "wrap", "CircleAI.Inference",
                &["70B model inference on a single 4GB GPU (no quantization)",
                  "CPU inference + MacOS + sharded + non-sharded models"]),
            CapabilityEntry::new("shard", Some("shard/shard"), "Apache-2.0", "pattern-port", "CircleAI.Inference",
                &["KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
                  "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
                  "Fused compressed-K attention with RoPE undo/reapply"]),
            CapabilityEntry::new("awesome-design-md", Some("design-md/awesome-design-md"), "CC-BY-4.0", "wrap", "CircleAI.Skills.PackSources",
                &["DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)"]),
        ]
    }

    /// Lookup by id (case-insensitive).
    pub fn find(id: &str) -> Option<CapabilityEntry> {
        Self::all()
            .into_iter()
            .find(|c| c.id.eq_ignore_ascii_case(id))
    }

    /// List entries by their target package (case-insensitive).
    pub fn by_package(target_package: &str) -> Vec<CapabilityEntry> {
        Self::all()
            .into_iter()
            .filter(|c| c.target_package.eq_ignore_ascii_case(target_package))
            .collect()
    }
}
