// CapabilityRegistry.cs
//
// (3.3.0) Single source of truth for every external capability we
// want CircleAI to absorb: claude-mem, Amphion, superpowers, GitNexus,
// PhoneHarness, yapsnap, json-render, last30days, gstack, Sponsio,
// aimangastudio, ai-resume-analyzer, Agent-Reach, career-ops, presenton,
// show-me-the-money, Understand-Anything, dexter, quant-mind,
// Anthropic-Cybersecurity-Skills, HippoRAG, Observer AI, Bluehound,
// skylight, turbovec, flame, kagent, airllm, shard, awesome-design-md.
//
// Each registry entry names the capability, the upstream repo, the
// distinct value bullets, and the package it'll live in once vendored
// or pattern-ported.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Companion;

/// <summary>(3.3.0) One absorption-target capability.</summary>
/// <param name="Id">Short slug.</param>
/// <param name="Repo">Upstream GitHub path (or null if mythology).</param>
/// <param name="License">License classification.</param>
/// <param name="Strategy">"vendor" / "pattern-port" / "wrap".</param>
/// <param name="TargetPackage">CircleAI.* package the capability lands in.</param>
/// <param name="ValueBullets">Concrete capability bullets (one per task in the original plan).</param>
public sealed record CapabilityEntry(
    string                Id,
    string?               Repo,
    string                License,
    string                Strategy,
    string                TargetPackage,
    IReadOnlyList<string> ValueBullets);

/// <summary>(3.3.0) Static registry of every capability we've earmarked.</summary>
public static class ExternalCapabilityRegistry
{
    public static IReadOnlyList<CapabilityEntry> All { get; } = new[]
    {
        new CapabilityEntry("claude-mem",          "thedotmack/claude-mem",            "MIT",        "pattern-port", "CircleAI.Memory",
            new[] { "Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence", "Three-tier semantic search", "Privacy-aware prompt stripping",
                    "Multi-provider observation generation", "Five-hook session lifecycle", "MCP server for memory queries", "WAL-mode SQLite with FTS5",
                    "Worker daemon with HTTP API", "Token economy tracking" }),
        new CapabilityEntry("Amphion",             "open-mmlab/Amphion",               "MIT",        "wrap",         "CircleAI.Speech",
            new[] { "FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS", "Metis multi-task unified speech",
                    "Voice conversion family", "Singing voice synthesis (8 architectures)", "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
                    "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)", "Six-language G2P", "Speech enhancement + target speaker extraction",
                    "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)" }),
        new CapabilityEntry("superpowers",         "obra/superpowers",                 "MIT",        "pattern-port", "CircleAI.Skills",
            new[] { "14-skill library", "Cross-platform skill loader", "Per-task implementer + reviewer subagent orchestration",
                    "Durable progress ledger", "File-based handoffs", "Verification-before-completion enforcement",
                    "TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching" }),
        new CapabilityEntry("GitNexus",            "GitNexus/GitNexus",                "MIT",        "wrap",         "CircleAI.CodeUnderstanding",
            new[] { "16-language tree-sitter code parser", "44-node-type + 21-relationship-type code knowledge graph",
                    "KuzuDB-based graph database with WAL", "Sigma.js WebGL force-directed graph rendering",
                    "LangChain ReAct agent with Cypher graph queries", "Snowflake Arctic Embed XS 384-dim local embeddings",
                    "Hybrid BM25 + semantic search via Reciprocal Rank Fusion", "Multi-LLM provider support",
                    "Privacy-first design", "Multi-branch incremental code indexing" }),
        new CapabilityEntry("PhoneHarness",        "AmberSahdev/PhoneHarness",         "MIT",        "wrap",         "CircleAI.WindowsAutomation",
            new[] { "Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
                    "Low-level GUI gestures with normalised 0-1000 coords", "Vision + CLI dual-model architecture",
                    "ADB host backend + SSH fallback for Termux", "Risk classification per action",
                    "Skill injection via YAML", "HTTP streaming server for native-APK integration",
                    "Action format auto-detection (Seed/AutoGLM/OpenAI)", "Trace JSONL append-only logging" }),
        new CapabilityEntry("yapsnap",             "yourfavorite/yapsnap",             "MIT",        "wrap",         "CircleAI.Speech",
            new[] { "CPU-only streaming Zipformer transducer transcription", "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
                    "10+ language transcription", "Sentence-level timestamps + diarization (ONNX, no PyTorch)" }),
        new CapabilityEntry("json-render",         "json-render/json-render",          "MIT",        "wrap",         "CircleAI.Tools.Catalog",
            new[] { "Generative UI catalog", "10 platform adapters from one catalog", "36 pre-built shadcn/ui components" }),
        new CapabilityEntry("last30days",          "obra/last30days-skill",            "MIT",        "wrap",         "CircleAI.Inputs",
            new[] { "Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
                    "AI judge agent ranks by engagement", "Zero-config setup wizard auto-detects auth" }),
        new CapabilityEntry("gstack",              "gstack-ai/gstack",                 "Apache-2.0", "pattern-port", "CircleAI.SDD",
            new[] { "23 Claude Code skills covering full software factory",
                    "Real browser QA via Playwright", "Team mode auto-update for shared repos",
                    "OWASP + STRIDE security audit automation" }),
        new CapabilityEntry("Sponsio",             "sponsio/sponsio",                  "MIT",        "pattern-port", "CircleAI.Safety",
            new[] { "Deterministic agent contracts at runtime (Fuzzy LTL Monitor)", "Five-action enforcement",
                    "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
                    "Pattern library + natural-language compilation into contracts" }),
        new CapabilityEntry("aimangastudio",       "aimangastudio/aimangastudio",      "MIT",        "wrap",         "CircleAI.Games",
            new[] { "AI manga/comic creation (script + character + panel + style + batch export)" }),
        new CapabilityEntry("ai-resume-analyzer",  "ai-resume-analyzer/ai-resume-analyzer", "MIT",   "wrap",         "CircleAI.Domain.JobSearch",
            new[] { "Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js" }),
        new CapabilityEntry("Agent-Reach",         "agent-reach/agent-reach",          "MIT",        "wrap",         "CircleAI.Inputs",
            new[] { "YouTube transcript extraction (no API)", "Twitter/X search + posts (no paid API)",
                    "Reddit forum reading without 403", "Xiaohongshu/Bilibili/TikTok access",
                    "GitHub repo info / issue reading without auth", "RSS subscription monitoring",
                    "Clean webpage extraction", "Auto-select best access method per platform" }),
        new CapabilityEntry("career-ops",          "career-ops/career-ops",            "MIT",        "wrap",         "CircleAI.Domain.JobSearch",
            new[] { "Multi-agent job search system (Node + Go + Playwright)",
                    "Resume tailoring per job + cover letter + tracking + interview prep" }),
        new CapabilityEntry("presenton",           "presenton/presenton",              "MIT",        "wrap",         "CircleAI.Domain.Presentations",
            new[] { "Open-source AI presentation generator", "10+ LLM providers",
                    "AI Presentation Generation API + PPTX export", "Custom design/template support" }),
        new CapabilityEntry("show-me-the-money",   "show-me-the-money/show-me-the-money", "MIT",     "wrap",         "CircleAI.AutonomousBiz",
            new[] { "25 agent skills running an autonomous solo-founder business (idea → revenue)" }),
        new CapabilityEntry("Understand-Anything", "understand-anything/understand-anything", "MIT", "wrap",         "CircleAI.CodeUnderstanding",
            new[] { "Codebase/knowledge base/docs → interactive knowledge graph",
                    "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)" }),
        new CapabilityEntry("dexter",              "dexter/dexter",                    "MIT",        "wrap",         "CircleAI.Domain.FinancialAgent",
            new[] { "Autonomous financial research agent", "Real-time market data tools",
                    "WhatsApp integration" }),
        new CapabilityEntry("quant-mind",          "quant-mind/quant-mind",            "MIT",        "wrap",         "CircleAI.Domain.Finance",
            new[] { "Knowledge extraction from financial papers/news/blogs/reports → queryable base" }),
        new CapabilityEntry("Anthropic-Cybersecurity-Skills", "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills", "Apache-2.0", "wrap", "CircleAI.Skills",
            new[] { "754 production-grade cybersecurity skills across 26 domains",
                    "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
                    "Compatible with 26+ AI platforms via agentskills.io" }),
        new CapabilityEntry("HippoRAG",            "OSU-NLP-Group/HippoRAG",           "MIT",        "wrap",         "CircleAI.Memory.HippoRAG",
            new[] { "Memory framework inspired by human long-term memory",
                    "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG" }),
        new CapabilityEntry("Observer AI",         "ObserverAI/observer-ai",           "MIT",        "wrap",         "CircleAI.Observer",
            new[] { "Micro-agent framework (sensors→models→tools, observe-log-react loop)",
                    "Web app + downloadable desktop app + GitHub Pages deployment" }),
        new CapabilityEntry("Bluehound",           "bluehound/bluehound",              "MIT",        "wrap",         "CircleAI.Vision",
            new[] { "BLE wardriving / real-time device discovery with persistent JSON db",
                    "Real-time BLE anomaly detection" }),
        new CapabilityEntry("skylight",            "skylight/skylight",                "MIT",        "wrap",         "CircleAI.Spatial",
            new[] { "ADS-B aircraft tracking via cheap RTL-SDR",
                    "Project planes + live sky onto a ceiling" }),
        new CapabilityEntry("turbovec",            "turbovec/turbovec",                "MIT",        "wrap",         "CircleAI.Embeddings",
            new[] { "TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
                    "NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
                    "Filter at search time" }),
        new CapabilityEntry("flame",               "flame-engine/flame",               "MIT",        "wrap",         "CircleAI.Games",
            new[] { "Flutter game engine (2D/2.5D/physics/audio/input/camera)" }),
        new CapabilityEntry("kagent",              "kagent-dev/kagent",                "Apache-2.0", "wrap",         "CircleAI.Operator",
            new[] { "Kubernetes-native AI agent framework (Helm + A2A protocol)" }),
        new CapabilityEntry("airllm",              "lyogavin/airllm",                  "Apache-2.0", "wrap",         "CircleAI.Inference",
            new[] { "70B model inference on a single 4GB GPU (no quantization)",
                    "CPU inference + MacOS + sharded + non-sharded models" }),
        new CapabilityEntry("shard",               "shard/shard",                      "Apache-2.0", "pattern-port", "CircleAI.Inference",
            new[] { "KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
                    "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
                    "Fused compressed-K attention with RoPE undo/reapply" }),
        new CapabilityEntry("awesome-design-md",   "design-md/awesome-design-md",      "CC-BY-4.0",  "wrap",         "CircleAI.Skills.PackSources",
            new[] { "DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)" }),
    };

    /// <summary>(3.3.0) Lookup by id.</summary>
    public static CapabilityEntry? Find(string id)
        => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>(3.3.0) List entries by their target package.</summary>
    public static IReadOnlyList<CapabilityEntry> ByPackage(string targetPackage)
        => All.Where(c => c.TargetPackage.Equals(targetPackage, StringComparison.OrdinalIgnoreCase)).ToList();
}
