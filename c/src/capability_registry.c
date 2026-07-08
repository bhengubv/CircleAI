/*
 * capability_registry.c — CircleAI ExternalCapabilityRegistry (C11 port).
 *
 * Static registry of every external capability, transcribed 1:1 from
 * CapabilityRegistry.cs (id, repo, license, strategy, target package, and the
 * exact value bullets, in the same order). All data is static/borrowed.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/capability_registry.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdbool.h>

/* --- value-bullet blocks (one static array per capability) --- */

static const char *const BULLETS_claude_mem[] = {
    "Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence",
    "Three-tier semantic search", "Privacy-aware prompt stripping",
    "Multi-provider observation generation", "Five-hook session lifecycle",
    "MCP server for memory queries", "WAL-mode SQLite with FTS5",
    "Worker daemon with HTTP API", "Token economy tracking" };

static const char *const BULLETS_Amphion[] = {
    "FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS",
    "Metis multi-task unified speech", "Voice conversion family",
    "Singing voice synthesis (8 architectures)",
    "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
    "Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)",
    "Six-language G2P", "Speech enhancement + target speaker extraction",
    "Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)" };

static const char *const BULLETS_superpowers[] = {
    "14-skill library", "Cross-platform skill loader",
    "Per-task implementer + reviewer subagent orchestration",
    "Durable progress ledger", "File-based handoffs",
    "Verification-before-completion enforcement",
    "TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching" };

static const char *const BULLETS_GitNexus[] = {
    "16-language tree-sitter code parser",
    "44-node-type + 21-relationship-type code knowledge graph",
    "KuzuDB-based graph database with WAL",
    "Sigma.js WebGL force-directed graph rendering",
    "LangChain ReAct agent with Cypher graph queries",
    "Snowflake Arctic Embed XS 384-dim local embeddings",
    "Hybrid BM25 + semantic search via Reciprocal Rank Fusion",
    "Multi-LLM provider support", "Privacy-first design",
    "Multi-branch incremental code indexing" };

static const char *const BULLETS_PhoneHarness[] = {
    "Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
    "Low-level GUI gestures with normalised 0-1000 coords",
    "Vision + CLI dual-model architecture",
    "ADB host backend + SSH fallback for Termux", "Risk classification per action",
    "Skill injection via YAML", "HTTP streaming server for native-APK integration",
    "Action format auto-detection (Seed/AutoGLM/OpenAI)",
    "Trace JSONL append-only logging" };

static const char *const BULLETS_yapsnap[] = {
    "CPU-only streaming Zipformer transducer transcription",
    "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
    "10+ language transcription",
    "Sentence-level timestamps + diarization (ONNX, no PyTorch)" };

static const char *const BULLETS_json_render[] = {
    "Generative UI catalog", "10 platform adapters from one catalog",
    "36 pre-built shadcn/ui components" };

static const char *const BULLETS_last30days[] = {
    "Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
    "AI judge agent ranks by engagement",
    "Zero-config setup wizard auto-detects auth" };

static const char *const BULLETS_gstack[] = {
    "23 Claude Code skills covering full software factory",
    "Real browser QA via Playwright", "Team mode auto-update for shared repos",
    "OWASP + STRIDE security audit automation" };

static const char *const BULLETS_Sponsio[] = {
    "Deterministic agent contracts at runtime (Fuzzy LTL Monitor)",
    "Five-action enforcement",
    "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
    "Pattern library + natural-language compilation into contracts" };

static const char *const BULLETS_aimangastudio[] = {
    "AI manga/comic creation (script + character + panel + style + batch export)" };

static const char *const BULLETS_ai_resume_analyzer[] = {
    "Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js" };

static const char *const BULLETS_Agent_Reach[] = {
    "YouTube transcript extraction (no API)",
    "Twitter/X search + posts (no paid API)",
    "Reddit forum reading without 403",
    "Xiaohongshu/Bilibili/TikTok access",
    "GitHub repo info / issue reading without auth",
    "RSS subscription monitoring", "Clean webpage extraction",
    "Auto-select best access method per platform" };

static const char *const BULLETS_career_ops[] = {
    "Multi-agent job search system (Node + Go + Playwright)",
    "Resume tailoring per job + cover letter + tracking + interview prep" };

static const char *const BULLETS_presenton[] = {
    "Open-source AI presentation generator", "10+ LLM providers",
    "AI Presentation Generation API + PPTX export",
    "Custom design/template support" };

/* U+2192 RIGHTWARDS ARROW encoded as its UTF-8 bytes (\xe2\x86\x92) so the
 * strings are byte-identical to the C# source. */
static const char *const BULLETS_show_me_the_money[] = {
    "25 agent skills running an autonomous solo-founder business (idea \xe2\x86\x92 revenue)" };

static const char *const BULLETS_Understand_Anything[] = {
    "Codebase/knowledge base/docs \xe2\x86\x92 interactive knowledge graph",
    "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)" };

static const char *const BULLETS_dexter[] = {
    "Autonomous financial research agent", "Real-time market data tools",
    "WhatsApp integration" };

static const char *const BULLETS_quant_mind[] = {
    "Knowledge extraction from financial papers/news/blogs/reports \xe2\x86\x92 queryable base" };

static const char *const BULLETS_Anthropic_Cybersecurity_Skills[] = {
    "754 production-grade cybersecurity skills across 26 domains",
    "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
    "Compatible with 26+ AI platforms via agentskills.io" };

static const char *const BULLETS_HippoRAG[] = {
    "Memory framework inspired by human long-term memory",
    "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG" };

static const char *const BULLETS_Observer_AI[] = {
    "Micro-agent framework (sensors\xe2\x86\x92models\xe2\x86\x92tools, observe-log-react loop)",
    "Web app + downloadable desktop app + GitHub Pages deployment" };

static const char *const BULLETS_Bluehound[] = {
    "BLE wardriving / real-time device discovery with persistent JSON db",
    "Real-time BLE anomaly detection" };

static const char *const BULLETS_skylight[] = {
    "ADS-B aircraft tracking via cheap RTL-SDR",
    "Project planes + live sky onto a ceiling" };

static const char *const BULLETS_turbovec[] = {
    "TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
    "NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
    "Filter at search time" };

static const char *const BULLETS_flame[] = {
    "Flutter game engine (2D/2.5D/physics/audio/input/camera)" };

static const char *const BULLETS_kagent[] = {
    "Kubernetes-native AI agent framework (Helm + A2A protocol)" };

static const char *const BULLETS_airllm[] = {
    "70B model inference on a single 4GB GPU (no quantization)",
    "CPU inference + MacOS + sharded + non-sharded models" };

static const char *const BULLETS_shard[] = {
    "KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
    "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
    "Fused compressed-K attention with RoPE undo/reapply" };

static const char *const BULLETS_awesome_design_md[] = {
    "DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)" };

/* --- the registry table (order matches CapabilityRegistry.All) --- */

#define ENTRY(id, repo, lic, strat, pkg, bullets) \
    { id, repo, lic, strat, pkg, bullets, sizeof(bullets) / sizeof((bullets)[0]) }

static const ca_capability_entry_t REGISTRY[] = {
    ENTRY("claude-mem", "thedotmack/claude-mem", "MIT", "pattern-port", "CircleAI.Memory", BULLETS_claude_mem),
    ENTRY("Amphion", "open-mmlab/Amphion", "MIT", "wrap", "CircleAI.Speech", BULLETS_Amphion),
    ENTRY("superpowers", "obra/superpowers", "MIT", "pattern-port", "CircleAI.Skills", BULLETS_superpowers),
    ENTRY("GitNexus", "GitNexus/GitNexus", "MIT", "wrap", "CircleAI.CodeUnderstanding", BULLETS_GitNexus),
    ENTRY("PhoneHarness", "AmberSahdev/PhoneHarness", "MIT", "wrap", "CircleAI.WindowsAutomation", BULLETS_PhoneHarness),
    ENTRY("yapsnap", "yourfavorite/yapsnap", "MIT", "wrap", "CircleAI.Speech", BULLETS_yapsnap),
    ENTRY("json-render", "json-render/json-render", "MIT", "wrap", "CircleAI.Tools.Catalog", BULLETS_json_render),
    ENTRY("last30days", "obra/last30days-skill", "MIT", "wrap", "CircleAI.Inputs", BULLETS_last30days),
    ENTRY("gstack", "gstack-ai/gstack", "Apache-2.0", "pattern-port", "CircleAI.SDD", BULLETS_gstack),
    ENTRY("Sponsio", "sponsio/sponsio", "MIT", "pattern-port", "CircleAI.Safety", BULLETS_Sponsio),
    ENTRY("aimangastudio", "aimangastudio/aimangastudio", "MIT", "wrap", "CircleAI.Games", BULLETS_aimangastudio),
    ENTRY("ai-resume-analyzer", "ai-resume-analyzer/ai-resume-analyzer", "MIT", "wrap", "CircleAI.Domain.JobSearch", BULLETS_ai_resume_analyzer),
    ENTRY("Agent-Reach", "agent-reach/agent-reach", "MIT", "wrap", "CircleAI.Inputs", BULLETS_Agent_Reach),
    ENTRY("career-ops", "career-ops/career-ops", "MIT", "wrap", "CircleAI.Domain.JobSearch", BULLETS_career_ops),
    ENTRY("presenton", "presenton/presenton", "MIT", "wrap", "CircleAI.Domain.Presentations", BULLETS_presenton),
    ENTRY("show-me-the-money", "show-me-the-money/show-me-the-money", "MIT", "wrap", "CircleAI.AutonomousBiz", BULLETS_show_me_the_money),
    ENTRY("Understand-Anything", "understand-anything/understand-anything", "MIT", "wrap", "CircleAI.CodeUnderstanding", BULLETS_Understand_Anything),
    ENTRY("dexter", "dexter/dexter", "MIT", "wrap", "CircleAI.Domain.FinancialAgent", BULLETS_dexter),
    ENTRY("quant-mind", "quant-mind/quant-mind", "MIT", "wrap", "CircleAI.Domain.Finance", BULLETS_quant_mind),
    ENTRY("Anthropic-Cybersecurity-Skills", "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills", "Apache-2.0", "wrap", "CircleAI.Skills", BULLETS_Anthropic_Cybersecurity_Skills),
    ENTRY("HippoRAG", "OSU-NLP-Group/HippoRAG", "MIT", "wrap", "CircleAI.Memory.HippoRAG", BULLETS_HippoRAG),
    ENTRY("Observer AI", "ObserverAI/observer-ai", "MIT", "wrap", "CircleAI.Observer", BULLETS_Observer_AI),
    ENTRY("Bluehound", "bluehound/bluehound", "MIT", "wrap", "CircleAI.Vision", BULLETS_Bluehound),
    ENTRY("skylight", "skylight/skylight", "MIT", "wrap", "CircleAI.Spatial", BULLETS_skylight),
    ENTRY("turbovec", "turbovec/turbovec", "MIT", "wrap", "CircleAI.Embeddings", BULLETS_turbovec),
    ENTRY("flame", "flame-engine/flame", "MIT", "wrap", "CircleAI.Games", BULLETS_flame),
    ENTRY("kagent", "kagent-dev/kagent", "Apache-2.0", "wrap", "CircleAI.Operator", BULLETS_kagent),
    ENTRY("airllm", "lyogavin/airllm", "Apache-2.0", "wrap", "CircleAI.Inference", BULLETS_airllm),
    ENTRY("shard", "shard/shard", "Apache-2.0", "pattern-port", "CircleAI.Inference", BULLETS_shard),
    ENTRY("awesome-design-md", "design-md/awesome-design-md", "CC-BY-4.0", "wrap", "CircleAI.Skills.PackSources", BULLETS_awesome_design_md),
};

#define REGISTRY_COUNT (sizeof(REGISTRY) / sizeof(REGISTRY[0]))

/* case-insensitive equality (OrdinalIgnoreCase for ASCII). */
static bool cr_ieq(const char *a, const char *b) {
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        a++; b++;
    }
    return *a == *b;
}

const ca_capability_entry_t *ca_capability_registry_all(size_t *out_count) {
    if (out_count) *out_count = REGISTRY_COUNT;
    return REGISTRY;
}

size_t ca_capability_registry_count(void) { return REGISTRY_COUNT; }

const ca_capability_entry_t *ca_capability_registry_find(const char *id) {
    if (!id) return NULL;
    for (size_t i = 0; i < REGISTRY_COUNT; ++i)
        if (cr_ieq(REGISTRY[i].id, id)) return &REGISTRY[i];   /* FirstOrDefault */
    return NULL;
}

const ca_capability_entry_t **ca_capability_registry_by_package(
    const char *target_package, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!target_package) { if (out_count) *out_count = (size_t)-1; return NULL; }
    /* count matches first */
    size_t n = 0;
    for (size_t i = 0; i < REGISTRY_COUNT; ++i)
        if (cr_ieq(REGISTRY[i].target_package, target_package)) n++;
    if (n == 0) return NULL;
    const ca_capability_entry_t **out =
        (const ca_capability_entry_t **)malloc(n * sizeof(*out));
    if (!out) { if (out_count) *out_count = (size_t)-1; return NULL; }
    size_t j = 0;
    for (size_t i = 0; i < REGISTRY_COUNT; ++i)
        if (cr_ieq(REGISTRY[i].target_package, target_package)) out[j++] = &REGISTRY[i];
    if (out_count) *out_count = n;
    return out;
}
