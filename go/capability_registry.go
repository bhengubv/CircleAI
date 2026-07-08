// capability_registry.go
//
// Ported from CircleAI.Companion (CapabilityRegistry.cs) — the C# reference.
// Single source of truth for every external capability CircleAI absorbs, each
// naming the capability, upstream repo, value bullets, and target package.
//
//   - CapabilityEntry              (record)
//   - ExternalCapabilityRegistry   (static registry: All, Find, ByPackage)

package circleai

import "strings"

// CapabilityEntry is one absorption-target capability. Ported from the C# record
// CapabilityEntry(string Id, string? Repo, string License, string Strategy,
// string TargetPackage, IReadOnlyList<string> ValueBullets).
type CapabilityEntry struct {
	// ID is the short slug.
	ID string
	// Repo is the upstream GitHub path (nil if mythology).
	Repo *string
	// License is the license classification.
	License string
	// Strategy is "vendor" / "pattern-port" / "wrap".
	Strategy string
	// TargetPackage is the CircleAI.* package the capability lands in.
	TargetPackage string
	// ValueBullets are the concrete capability bullets.
	ValueBullets []string
}

// strptrCap returns a pointer to s (local helper to keep the registry literal
// terse without depending on other files' helpers).
func strptrCap(s string) *string { return &s }

// externalCapabilityRegistryAll is the backing slice for the registry. Order and
// contents match the C# ExternalCapabilityRegistry.All array exactly.
var externalCapabilityRegistryAll = []CapabilityEntry{
	{"claude-mem", strptrCap("thedotmack/claude-mem"), "MIT", "pattern-port", "CircleAI.Memory",
		[]string{"Multi-platform memory adapter", "SQLite-local + Postgres-server dual persistence", "Three-tier semantic search", "Privacy-aware prompt stripping",
			"Multi-provider observation generation", "Five-hook session lifecycle", "MCP server for memory queries", "WAL-mode SQLite with FTS5",
			"Worker daemon with HTTP API", "Token economy tracking"}},
	{"Amphion", strptrCap("open-mmlab/Amphion"), "MIT", "wrap", "CircleAI.Speech",
		[]string{"FastSpeech2/VITS/VALLE/NaturalSpeech2", "MaskGCT masked generative TTS", "Metis multi-task unified speech",
			"Voice conversion family", "Singing voice synthesis (8 architectures)", "Neural audio codecs (FACodec/NS3/SpeechTokenizer/DualCodec)",
			"Vocoder family (HiFiGAN/NSF-HiFiGAN/BigVGAN/MelGAN/APNet/Vocos/Wave-RNN)", "Six-language G2P", "Speech enhancement + target speaker extraction",
			"Audio quality metrics (F0/Energy/MCD/FAD/PESQ/SI-SDR/STOI/CER/WER)"}},
	{"superpowers", strptrCap("obra/superpowers"), "MIT", "pattern-port", "CircleAI.Skills",
		[]string{"14-skill library", "Cross-platform skill loader", "Per-task implementer + reviewer subagent orchestration",
			"Durable progress ledger", "File-based handoffs", "Verification-before-completion enforcement",
			"TDD RED-GREEN-REFACTOR mandatory gate", "Parallel agent dispatching"}},
	{"GitNexus", strptrCap("GitNexus/GitNexus"), "MIT", "wrap", "CircleAI.CodeUnderstanding",
		[]string{"16-language tree-sitter code parser", "44-node-type + 21-relationship-type code knowledge graph",
			"KuzuDB-based graph database with WAL", "Sigma.js WebGL force-directed graph rendering",
			"LangChain ReAct agent with Cypher graph queries", "Snowflake Arctic Embed XS 384-dim local embeddings",
			"Hybrid BM25 + semantic search via Reciprocal Rank Fusion", "Multi-LLM provider support",
			"Privacy-first design", "Multi-branch incremental code indexing"}},
	{"PhoneHarness", strptrCap("AmberSahdev/PhoneHarness"), "MIT", "wrap", "CircleAI.WindowsAutomation",
		[]string{"Five-stage agent pipeline (M0a/M1/M2/M3/M4)", "Semantic Android intents",
			"Low-level GUI gestures with normalised 0-1000 coords", "Vision + CLI dual-model architecture",
			"ADB host backend + SSH fallback for Termux", "Risk classification per action",
			"Skill injection via YAML", "HTTP streaming server for native-APK integration",
			"Action format auto-detection (Seed/AutoGLM/OpenAI)", "Trace JSONL append-only logging"}},
	{"yapsnap", strptrCap("yourfavorite/yapsnap"), "MIT", "wrap", "CircleAI.Speech",
		[]string{"CPU-only streaming Zipformer transducer transcription", "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
			"10+ language transcription", "Sentence-level timestamps + diarization (ONNX, no PyTorch)"}},
	{"json-render", strptrCap("json-render/json-render"), "MIT", "wrap", "CircleAI.Tools.Catalog",
		[]string{"Generative UI catalog", "10 platform adapters from one catalog", "36 pre-built shadcn/ui components"}},
	{"last30days", strptrCap("obra/last30days-skill"), "MIT", "wrap", "CircleAI.Inputs",
		[]string{"Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
			"AI judge agent ranks by engagement", "Zero-config setup wizard auto-detects auth"}},
	{"gstack", strptrCap("gstack-ai/gstack"), "Apache-2.0", "pattern-port", "CircleAI.SDD",
		[]string{"23 Claude Code skills covering full software factory",
			"Real browser QA via Playwright", "Team mode auto-update for shared repos",
			"OWASP + STRIDE security audit automation"}},
	{"Sponsio", strptrCap("sponsio/sponsio"), "MIT", "pattern-port", "CircleAI.Safety",
		[]string{"Deterministic agent contracts at runtime (Fuzzy LTL Monitor)", "Five-action enforcement",
			"Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
			"Pattern library + natural-language compilation into contracts"}},
	{"aimangastudio", strptrCap("aimangastudio/aimangastudio"), "MIT", "wrap", "CircleAI.Games",
		[]string{"AI manga/comic creation (script + character + panel + style + batch export)"}},
	{"ai-resume-analyzer", strptrCap("ai-resume-analyzer/ai-resume-analyzer"), "MIT", "wrap", "CircleAI.Domain.JobSearch",
		[]string{"Resume upload + AI scoring against jobs", "React Router 7 + Vite + Puter.js"}},
	{"Agent-Reach", strptrCap("agent-reach/agent-reach"), "MIT", "wrap", "CircleAI.Inputs",
		[]string{"YouTube transcript extraction (no API)", "Twitter/X search + posts (no paid API)",
			"Reddit forum reading without 403", "Xiaohongshu/Bilibili/TikTok access",
			"GitHub repo info / issue reading without auth", "RSS subscription monitoring",
			"Clean webpage extraction", "Auto-select best access method per platform"}},
	{"career-ops", strptrCap("career-ops/career-ops"), "MIT", "wrap", "CircleAI.Domain.JobSearch",
		[]string{"Multi-agent job search system (Node + Go + Playwright)",
			"Resume tailoring per job + cover letter + tracking + interview prep"}},
	{"presenton", strptrCap("presenton/presenton"), "MIT", "wrap", "CircleAI.Domain.Presentations",
		[]string{"Open-source AI presentation generator", "10+ LLM providers",
			"AI Presentation Generation API + PPTX export", "Custom design/template support"}},
	{"show-me-the-money", strptrCap("show-me-the-money/show-me-the-money"), "MIT", "wrap", "CircleAI.AutonomousBiz",
		[]string{"25 agent skills running an autonomous solo-founder business (idea → revenue)"}},
	{"Understand-Anything", strptrCap("understand-anything/understand-anything"), "MIT", "wrap", "CircleAI.CodeUnderstanding",
		[]string{"Codebase/knowledge base/docs → interactive knowledge graph",
			"8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)"}},
	{"dexter", strptrCap("dexter/dexter"), "MIT", "wrap", "CircleAI.Domain.FinancialAgent",
		[]string{"Autonomous financial research agent", "Real-time market data tools",
			"WhatsApp integration"}},
	{"quant-mind", strptrCap("quant-mind/quant-mind"), "MIT", "wrap", "CircleAI.Domain.Finance",
		[]string{"Knowledge extraction from financial papers/news/blogs/reports → queryable base"}},
	{"Anthropic-Cybersecurity-Skills", strptrCap("Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills"), "Apache-2.0", "wrap", "CircleAI.Skills",
		[]string{"754 production-grade cybersecurity skills across 26 domains",
			"Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
			"Compatible with 26+ AI platforms via agentskills.io"}},
	{"HippoRAG", strptrCap("OSU-NLP-Group/HippoRAG"), "MIT", "wrap", "CircleAI.Memory.HippoRAG",
		[]string{"Memory framework inspired by human long-term memory",
			"Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG"}},
	{"Observer AI", strptrCap("ObserverAI/observer-ai"), "MIT", "wrap", "CircleAI.Observer",
		[]string{"Micro-agent framework (sensors→models→tools, observe-log-react loop)",
			"Web app + downloadable desktop app + GitHub Pages deployment"}},
	{"Bluehound", strptrCap("bluehound/bluehound"), "MIT", "wrap", "CircleAI.Vision",
		[]string{"BLE wardriving / real-time device discovery with persistent JSON db",
			"Real-time BLE anomaly detection"}},
	{"skylight", strptrCap("skylight/skylight"), "MIT", "wrap", "CircleAI.Spatial",
		[]string{"ADS-B aircraft tracking via cheap RTL-SDR",
			"Project planes + live sky onto a ceiling"}},
	{"turbovec", strptrCap("turbovec/turbovec"), "MIT", "wrap", "CircleAI.Embeddings",
		[]string{"TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
			"NEON / AVX-512BW hand-written kernels", "Online ingest with no train step",
			"Filter at search time"}},
	{"flame", strptrCap("flame-engine/flame"), "MIT", "wrap", "CircleAI.Games",
		[]string{"Flutter game engine (2D/2.5D/physics/audio/input/camera)"}},
	{"kagent", strptrCap("kagent-dev/kagent"), "Apache-2.0", "wrap", "CircleAI.Operator",
		[]string{"Kubernetes-native AI agent framework (Helm + A2A protocol)"}},
	{"airllm", strptrCap("lyogavin/airllm"), "Apache-2.0", "wrap", "CircleAI.Inference",
		[]string{"70B model inference on a single 4GB GPU (no quantization)",
			"CPU inference + MacOS + sharded + non-sharded models"}},
	{"shard", strptrCap("shard/shard"), "Apache-2.0", "pattern-port", "CircleAI.Inference",
		[]string{"KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
			"TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
			"Fused compressed-K attention with RoPE undo/reapply"}},
	{"awesome-design-md", strptrCap("design-md/awesome-design-md"), "CC-BY-4.0", "wrap", "CircleAI.Skills.PackSources",
		[]string{"DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)"}},
}

// ExternalCapabilityRegistryAll returns the full registry of absorption-target
// capabilities (a defensive copy). Ported from the C#
// ExternalCapabilityRegistry.All.
func ExternalCapabilityRegistryAll() []CapabilityEntry {
	out := make([]CapabilityEntry, len(externalCapabilityRegistryAll))
	copy(out, externalCapabilityRegistryAll)
	return out
}

// ExternalCapabilityRegistryFind looks up a capability by id
// (case-insensitive), returning (nil, false) when absent. Ported from the C#
// ExternalCapabilityRegistry.Find.
func ExternalCapabilityRegistryFind(id string) (CapabilityEntry, bool) {
	for _, c := range externalCapabilityRegistryAll {
		if strings.EqualFold(c.ID, id) {
			return c, true
		}
	}
	return CapabilityEntry{}, false
}

// ExternalCapabilityRegistryByPackage returns all entries whose TargetPackage
// matches targetPackage (case-insensitive). Ported from the C#
// ExternalCapabilityRegistry.ByPackage.
func ExternalCapabilityRegistryByPackage(targetPackage string) []CapabilityEntry {
	var out []CapabilityEntry
	for _, c := range externalCapabilityRegistryAll {
		if strings.EqualFold(c.TargetPackage, targetPackage) {
			out = append(out, c)
		}
	}
	return out
}
