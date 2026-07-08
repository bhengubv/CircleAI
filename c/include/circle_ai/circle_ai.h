#ifndef CIRCLE_AI_H
#define CIRCLE_AI_H

/*
 * circle_ai.h — umbrella header for the CircleAI portable C11 SDK.
 * Include this single header to pull in all modules.
 */

#include "models.h"
#include "memory.h"
#include "identity.h"
#include "languages.h"
#include "companion.h"
#include "inference.h"
#include "tools.h"
#include "sync.h"
#include "security.h"

/* 1.5.0 portable surface */
#include "models_v15.h"
#include "device.h"
#include "selector.h"
#include "registry.h"
#include "prompt.h"
#include "agents.h"
#include "hosting.h"

/* memory-brain port */
#include "memory_brain.h"
#include "companion_brain.h"

/* LLM extractor + consolidation subsystem */
#include "llm_extractor.h"
#include "consolidation.h"

/* memory subsystems port — feedback / RAG / multimodal / compression */
#include "feedback_analyser.h"
#include "rag.h"
#include "multimodal.h"
#include "compression.h"

/* companion reasoning core — world model / predictive / inner monologue / theory of mind */
#include "companion_reason.h"

/* HER/Jarvis companion cognition — remaining contracts + real impls */
#include "herjarvis.h"

/* external capability absorption registry */
#include "capability_registry.h"

/* proactive scheduling substrate — cron + scheduler + sources/runners */
#include "proactive.h"

/* proactive briefing service + companion session factory */
#include "proactive_briefing.h"
#include "companion_session_factory.h"

/* companion-state sync layer — HLC, syncable store, channel, engine, bridges */
#include "companion_sync.h"

/* full Goal record + IGoalStore (rich, distinct from models.h fixture goal) */
#include "goal_store.h"

/* memory pipeline host runtime */
#include "companion_runtime.h"

/* CircleAI.Sync — MemorySyncService + SyncDelta seam + reconciliation */
#include "memory_sync_service.h"

/* Core model-management runtime — loader / manager / downloader / sources /
 * SafeModelHandle + facade + tenant/audit + ShardKvCodec + embeddings */
#include "model_runtime.h"
#include "circle_engine.h"
#include "tenant_audit.h"
#include "shard_kv.h"
#include "embedding_store.h"

/* Inference runtime + server (CircleAI.Inference / .Inference.Server /
 * .Inference.Server.Enterprise) */
#include "inference_rt.h"
#include "inference_server.h"
#include "inference_server_enterprise.h"

/* Hosting runtime + sub-hosts (CircleAI.Hosting / .CloudFallback /
 * .InferenceBridge / .Mcp / .Multiplayer) */
#include "host_ai.h"
#include "host_cron.h"
#include "host_tools_ui.h"
#include "host_cloud.h"
#include "host_bridge.h"
#include "host_mcp.h"
#include "host_multiplayer.h"

#endif /* CIRCLE_AI_H */
