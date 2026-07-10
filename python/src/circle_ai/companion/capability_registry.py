# companion/capability_registry.py
#
# The single source of truth for every external capability CircleAI wants to
# absorb. Ported from CircleAI.Companion (CapabilityRegistry.cs) — the C#
# reference. Each entry names the capability, its upstream repo, the license, the
# absorption strategy, the target package, and the concrete value bullets.
#
# ``ExternalCapabilityRegistry`` exposes the full list plus case-insensitive
# lookup by id and by target package (matching the C# ``Find`` / ``ByPackage``).

from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple


@dataclass(frozen=True, slots=True)
class CapabilityEntry:
    """One absorption-target capability.

    Mirrors ``CircleAI.Companion.CapabilityEntry``.

    * ``id``            — short slug.
    * ``repo``          — upstream GitHub path (or ``None`` if mythology).
    * ``license``       — license classification.
    * ``strategy``      — "vendor" / "pattern-port" / "wrap".
    * ``target_package``— the ``CircleAI.*`` package the capability lands in.
    * ``value_bullets`` — concrete capability bullets (one per original task).
    """

    id: str
    repo: Optional[str]
    license: str
    strategy: str
    target_package: str
    value_bullets: Sequence[str]


# The registry, in the same order as the C# ``ExternalCapabilityRegistry.All``.
_ALL: Tuple[CapabilityEntry, ...] = (
    CapabilityEntry(
        "claude-mem", "thedotmack/claude-mem", "MIT", "pattern-port", "CircleAI.Memory",
        (
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
        ),
    ),
    CapabilityEntry(
        "Amphion", "open-mmlab/Amphion", "MIT", "wrap", "CircleAI.Speech",
        (
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
        ),
    ),
    CapabilityEntry(
        "superpowers", "obra/superpowers", "MIT", "pattern-port", "CircleAI.Skills",
        (
            "14-skill library",
            "Cross-platform skill loader",
            "Per-task implementer + reviewer subagent orchestration",
            "Durable progress ledger",
            "File-based handoffs",
            "Verification-before-completion enforcement",
            "TDD RED-GREEN-REFACTOR mandatory gate",
            "Parallel agent dispatching",
        ),
    ),
    CapabilityEntry(
        "GitNexus", "GitNexus/GitNexus", "MIT", "wrap", "CircleAI.CodeUnderstanding",
        (
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
        ),
    ),
    CapabilityEntry(
        "PhoneHarness", "AmberSahdev/PhoneHarness", "MIT", "wrap", "CircleAI.WindowsAutomation",
        (
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
        ),
    ),
    CapabilityEntry(
        "yapsnap", "yourfavorite/yapsnap", "MIT", "wrap", "CircleAI.Speech",
        (
            "CPU-only streaming Zipformer transducer transcription",
            "yt-dlp ingestion for YouTube/X/TikTok/Reels/URLs",
            "10+ language transcription",
            "Sentence-level timestamps + diarization (ONNX, no PyTorch)",
        ),
    ),
    CapabilityEntry(
        "json-render", "json-render/json-render", "MIT", "wrap", "CircleAI.Tools.Catalog",
        (
            "Generative UI catalog",
            "10 platform adapters from one catalog",
            "36 pre-built shadcn/ui components",
        ),
    ),
    CapabilityEntry(
        "last30days", "obra/last30days-skill", "MIT", "wrap", "CircleAI.Inputs",
        (
            "Multi-platform parallel search (Reddit/X/YouTube/TikTok/HN/Polymarket/GitHub)",
            "AI judge agent ranks by engagement",
            "Zero-config setup wizard auto-detects auth",
        ),
    ),
    CapabilityEntry(
        "gstack", "gstack-ai/gstack", "Apache-2.0", "pattern-port", "CircleAI.SDD",
        (
            "23 Claude Code skills covering full software factory",
            "Real browser QA via Playwright",
            "Team mode auto-update for shared repos",
            "OWASP + STRIDE security audit automation",
        ),
    ),
    CapabilityEntry(
        "Sponsio", "sponsio/sponsio", "MIT", "pattern-port", "CircleAI.Safety",
        (
            "Deterministic agent contracts at runtime (Fuzzy LTL Monitor)",
            "Five-action enforcement",
            "Adapter for LangChain/Claude Agent/OpenAI Agents/Google ADK/CrewAI/Vercel AI/MCP",
            "Pattern library + natural-language compilation into contracts",
        ),
    ),
    CapabilityEntry(
        "aimangastudio", "aimangastudio/aimangastudio", "MIT", "wrap", "CircleAI.Games",
        ("AI manga/comic creation (script + character + panel + style + batch export)",),
    ),
    CapabilityEntry(
        "ai-resume-analyzer", "ai-resume-analyzer/ai-resume-analyzer", "MIT", "wrap",
        "CircleAI.Domain.JobSearch",
        (
            "Resume upload + AI scoring against jobs",
            "React Router 7 + Vite + Puter.js",
        ),
    ),
    CapabilityEntry(
        "Agent-Reach", "agent-reach/agent-reach", "MIT", "wrap", "CircleAI.Inputs",
        (
            "YouTube transcript extraction (no API)",
            "Twitter/X search + posts (no paid API)",
            "Reddit forum reading without 403",
            "Xiaohongshu/Bilibili/TikTok access",
            "GitHub repo info / issue reading without auth",
            "RSS subscription monitoring",
            "Clean webpage extraction",
            "Auto-select best access method per platform",
        ),
    ),
    CapabilityEntry(
        "career-ops", "career-ops/career-ops", "MIT", "wrap", "CircleAI.Domain.JobSearch",
        (
            "Multi-agent job search system (Node + Go + Playwright)",
            "Resume tailoring per job + cover letter + tracking + interview prep",
        ),
    ),
    CapabilityEntry(
        "presenton", "presenton/presenton", "MIT", "wrap", "CircleAI.Domain.Presentations",
        (
            "Open-source AI presentation generator",
            "10+ LLM providers",
            "AI Presentation Generation API + PPTX export",
            "Custom design/template support",
        ),
    ),
    CapabilityEntry(
        "show-me-the-money", "show-me-the-money/show-me-the-money", "MIT", "wrap",
        "CircleAI.AutonomousBiz",
        ("25 agent skills running an autonomous solo-founder business (idea → revenue)",),
    ),
    CapabilityEntry(
        "Understand-Anything", "understand-anything/understand-anything", "MIT", "wrap",
        "CircleAI.CodeUnderstanding",
        (
            "Codebase/knowledge base/docs → interactive knowledge graph",
            "8 host support (Claude Code/Codex/Cursor/Copilot/Copilot CLI/Gemini CLI/OpenCode/Vibe CLI/Trae)",
        ),
    ),
    CapabilityEntry(
        "dexter", "dexter/dexter", "MIT", "wrap", "CircleAI.Domain.FinancialAgent",
        (
            "Autonomous financial research agent",
            "Real-time market data tools",
            "WhatsApp integration",
        ),
    ),
    CapabilityEntry(
        "quant-mind", "quant-mind/quant-mind", "MIT", "wrap", "CircleAI.Domain.Finance",
        ("Knowledge extraction from financial papers/news/blogs/reports → queryable base",),
    ),
    CapabilityEntry(
        "Anthropic-Cybersecurity-Skills",
        "Anthropic-Cybersecurity-Skills/Anthropic-Cybersecurity-Skills",
        "Apache-2.0", "wrap", "CircleAI.Skills",
        (
            "754 production-grade cybersecurity skills across 26 domains",
            "Mapped to 5 frameworks (MITRE ATT&CK / NIST CSF 2.0 / MITRE ATLAS / D3FEND / NIST AI RMF)",
            "Compatible with 26+ AI platforms via agentskills.io",
        ),
    ),
    CapabilityEntry(
        "HippoRAG", "OSU-NLP-Group/HippoRAG", "MIT", "wrap", "CircleAI.Memory.HippoRAG",
        (
            "Memory framework inspired by human long-term memory",
            "Cost + latency efficient online; less indexing than GraphRAG/RAPTOR/LightRAG",
        ),
    ),
    CapabilityEntry(
        "Observer AI", "ObserverAI/observer-ai", "MIT", "wrap", "CircleAI.Observer",
        (
            "Micro-agent framework (sensors→models→tools, observe-log-react loop)",
            "Web app + downloadable desktop app + GitHub Pages deployment",
        ),
    ),
    CapabilityEntry(
        "Bluehound", "bluehound/bluehound", "MIT", "wrap", "CircleAI.Vision",
        (
            "BLE wardriving / real-time device discovery with persistent JSON db",
            "Real-time BLE anomaly detection",
        ),
    ),
    CapabilityEntry(
        "skylight", "skylight/skylight", "MIT", "wrap", "CircleAI.Spatial",
        (
            "ADS-B aircraft tracking via cheap RTL-SDR",
            "Project planes + live sky onto a ceiling",
        ),
    ),
    CapabilityEntry(
        "turbovec", "turbovec/turbovec", "MIT", "wrap", "CircleAI.Embeddings",
        (
            "TurboQuant data-oblivious vector quantizer (10M vectors in 4 GB instead of 31 GB)",
            "NEON / AVX-512BW hand-written kernels",
            "Online ingest with no train step",
            "Filter at search time",
        ),
    ),
    CapabilityEntry(
        "flame", "flame-engine/flame", "MIT", "wrap", "CircleAI.Games",
        ("Flutter game engine (2D/2.5D/physics/audio/input/camera)",),
    ),
    CapabilityEntry(
        "kagent", "kagent-dev/kagent", "Apache-2.0", "wrap", "CircleAI.Operator",
        ("Kubernetes-native AI agent framework (Helm + A2A protocol)",),
    ),
    CapabilityEntry(
        "airllm", "lyogavin/airllm", "Apache-2.0", "wrap", "CircleAI.Inference",
        (
            "70B model inference on a single 4GB GPU (no quantization)",
            "CPU inference + MacOS + sharded + non-sharded models",
        ),
    ),
    CapabilityEntry(
        "shard", "shard/shard", "Apache-2.0", "pattern-port", "CircleAI.Inference",
        (
            "KV cache compression via per-layer online PCA (K) + Hadamard + VQ (V)",
            "TurboQuant streaming decode quantizer (Zandieh et al., ICLR 2026)",
            "Fused compressed-K attention with RoPE undo/reapply",
        ),
    ),
    CapabilityEntry(
        "awesome-design-md", "design-md/awesome-design-md", "CC-BY-4.0", "wrap",
        "CircleAI.Skills.PackSources",
        (
            "DESIGN.md collection from major brands (Airbnb / Apple / Cursor / Claude / Coinbase / Cohere / ElevenLabs / Composio + 50 more)",
        ),
    ),
)


class ExternalCapabilityRegistry:
    """Static registry of every capability earmarked for absorption.

    Mirrors ``CircleAI.Companion.ExternalCapabilityRegistry``. All members are
    class-level (the C# type is ``static``).
    """

    All: Sequence[CapabilityEntry] = _ALL

    @staticmethod
    def find(id: str) -> Optional[CapabilityEntry]:
        """Lookup by id (case-insensitive)."""
        lid = id.lower()
        for c in _ALL:
            if c.id.lower() == lid:
                return c
        return None

    @staticmethod
    def by_package(target_package: str) -> List[CapabilityEntry]:
        """List entries by their target package (case-insensitive)."""
        lp = target_package.lower()
        return [c for c in _ALL if c.target_package.lower() == lp]


__all__ = [
    "CapabilityEntry",
    "ExternalCapabilityRegistry",
]
