"""circle_ai.inference — port of CircleAI.Inference runtime.

Public surface:
  * contracts + records: IChatGenerator (Protocol), GenerationOptions,
    ChatCapability, IModelSelector/ModelSelection, PowerBudget, ChatFragment,
    ChatFragmentKind, generate_response_async, stream_fragments_async,
  * deterministic generator: DeterministicChatGenerator, build_qwen_chat_prompt,
  * <think> routing: route_text, find_stop_sequence,
  * KV compression + budget policy: KvCompressionMode, KvCompressionApplyResult,
    MnnKvCompression, IKvCompressionNative, InMemoryKvCompressionNative,
    PowerBudgetPolicy, PowerBudgetResolution,
  * context budget: ContextWindowBudgetManager,
  * prefix cache: PrefixCacheService,
  * vision: VisionInput,
  * model download service: IModelDownloadService, ModelDownloadService,
    BundleFileSpec, IFileFetcher, FileUrlFetcher, strip_sha_algorithm_prefix,
  * layer streaming: ILayerStreamingRunner, NullLayerStreamingRunner,
    LayerStreamingOrchestrator, LayerShardDiscovery, LayerStreamingPlan,
    LayerWeightShard, LayerActivations,
  * feedback training: TrainingSample, IFeedbackTrainingQueue,
    FileBackedFeedbackTrainingQueue,
  * nightly training: NightlyAdapterTrainer, NightlyAdapterTrainerOptions,
    LoRAAdapterManager, ILoRANative, InMemoryLoRANative,
    TrainingNotSupportedError, char_tokenizer.
"""
from .inference import (
    ChatCapability,
    ChatFragment,
    ChatFragmentKind,
    GenerationOptions,
    IChatGenerator,
    IModelSelector,
    ModelSelection,
    PowerBudget,
    generate_response_async,
    stream_fragments_async,
)
from .think_router import THINK_CLOSE, THINK_OPEN, find_stop_sequence, route_text
from .generator import DeterministicChatGenerator, build_qwen_chat_prompt
from .kv_compression import (
    IKvCompressionNative,
    InMemoryKvCompressionNative,
    KvCompressionApplyResult,
    KvCompressionMode,
    MnnKvCompression,
    PowerBudgetPolicy,
    PowerBudgetResolution,
)
from .context_budget import ContextWindowBudgetManager
from .prefix_cache import PrefixCacheService
from .vision import VisionInput
from .model_download_service import (
    BundleFileSpec,
    FileUrlFetcher,
    IFileFetcher,
    IModelDownloadService,
    ModelDownloadService,
    strip_sha_algorithm_prefix,
)
from .layer_streaming import (
    ILayerStreamingRunner,
    LayerActivations,
    LayerShardDiscovery,
    LayerStreamingOrchestrator,
    LayerStreamingPlan,
    LayerWeightShard,
    NullLayerStreamingRunner,
)
from .feedback_training import (
    FileBackedFeedbackTrainingQueue,
    IFeedbackTrainingQueue,
    TrainingSample,
)
from .nightly_trainer import (
    ILoRANative,
    InMemoryLoRANative,
    LoRAAdapterManager,
    NightlyAdapterTrainer,
    NightlyAdapterTrainerOptions,
    TrainingNotSupportedError,
    char_tokenizer,
)

__all__ = [
    # contracts + records
    "ChatCapability",
    "ChatFragment",
    "ChatFragmentKind",
    "GenerationOptions",
    "IChatGenerator",
    "IModelSelector",
    "ModelSelection",
    "PowerBudget",
    "generate_response_async",
    "stream_fragments_async",
    # deterministic generator
    "DeterministicChatGenerator",
    "build_qwen_chat_prompt",
    # <think> routing
    "THINK_OPEN",
    "THINK_CLOSE",
    "find_stop_sequence",
    "route_text",
    # KV compression + budget policy
    "IKvCompressionNative",
    "InMemoryKvCompressionNative",
    "KvCompressionApplyResult",
    "KvCompressionMode",
    "MnnKvCompression",
    "PowerBudgetPolicy",
    "PowerBudgetResolution",
    # context budget
    "ContextWindowBudgetManager",
    # prefix cache
    "PrefixCacheService",
    # vision
    "VisionInput",
    # model download service
    "BundleFileSpec",
    "FileUrlFetcher",
    "IFileFetcher",
    "IModelDownloadService",
    "ModelDownloadService",
    "strip_sha_algorithm_prefix",
    # layer streaming
    "ILayerStreamingRunner",
    "LayerActivations",
    "LayerShardDiscovery",
    "LayerStreamingOrchestrator",
    "LayerStreamingPlan",
    "LayerWeightShard",
    "NullLayerStreamingRunner",
    # feedback training
    "FileBackedFeedbackTrainingQueue",
    "IFeedbackTrainingQueue",
    "TrainingSample",
    # nightly training
    "ILoRANative",
    "InMemoryLoRANative",
    "LoRAAdapterManager",
    "NightlyAdapterTrainer",
    "NightlyAdapterTrainerOptions",
    "TrainingNotSupportedError",
    "char_tokenizer",
]
