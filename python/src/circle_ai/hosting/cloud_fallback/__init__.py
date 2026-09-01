"""circle_ai.hosting.cloud_fallback — port of CircleAI.Hosting.CloudFallback.

Composite fallback chain + between-turn backup-brain orchestrator over
injected chat generators, with a deterministic local fake for tests.
"""
from __future__ import annotations

from .chains import (
    BackupBrainOrchestrator,
    BackupBrainPolicy,
    BrainHealth,
    BrainStatus,
    CloudFallbackChain,
    FakeConfigurableChatGenerator,
    IConfigurableChatGenerator,
)
from .providers import (
    AnthropicChatGenerator, AnthropicChatOptions, CerebrasChatGenerator,
    CerebrasChatOptions, CloudChatOptionsBase, CloudChatResult,
    CloudFallbackServiceCollectionExtensions, CloudRealtimeServiceBase,
    DeepSeekChatGenerator, DeepSeekChatOptions, ElevenLabsConvOptions,
    ElevenLabsConvService, GeminiChatGenerator, GeminiChatOptions,
    GeminiLiveOptions, GeminiLiveService, GroqChatGenerator, GroqChatOptions,
    ICloudChatGenerator, NovaSonicOptions, NovaSonicService,
    OpenAiChatGenerator, OpenAiChatOptions,
    OpenAiCompatibleChatGeneratorBase, OpenAiRealtimeOptions,
    OpenAiRealtimeService, ProviderIds, RealtimeCloudOptionsBase,
    RealtimeCloudServiceCollectionExtensions, RealtimeWebSocketSession,
    TogetherChatGenerator, TogetherChatOptions, UltravoxOptions,
    UltravoxService, parse_sse,
)

__all__ = [
    "IConfigurableChatGenerator",
    "CloudFallbackChain",
    "BrainHealth",
    "BrainStatus",
    "BackupBrainPolicy",
    "BackupBrainOrchestrator",
    "FakeConfigurableChatGenerator",
    "ProviderIds", "CloudChatOptionsBase", "CloudChatResult", "ICloudChatGenerator",
    "OpenAiCompatibleChatGeneratorBase", "OpenAiChatGenerator", "OpenAiChatOptions",
    "GroqChatGenerator", "GroqChatOptions", "CerebrasChatGenerator",
    "CerebrasChatOptions", "DeepSeekChatGenerator", "DeepSeekChatOptions",
    "TogetherChatGenerator", "TogetherChatOptions", "GeminiChatGenerator",
    "GeminiChatOptions", "AnthropicChatGenerator", "AnthropicChatOptions",
    "CloudFallbackServiceCollectionExtensions", "parse_sse",
    "RealtimeCloudOptionsBase", "OpenAiRealtimeOptions", "GeminiLiveOptions",
    "NovaSonicOptions", "ElevenLabsConvOptions", "UltravoxOptions",
    "RealtimeWebSocketSession", "CloudRealtimeServiceBase",
    "OpenAiRealtimeService", "GeminiLiveService", "NovaSonicService",
    "ElevenLabsConvService", "UltravoxService",
    "RealtimeCloudServiceCollectionExtensions",
]
