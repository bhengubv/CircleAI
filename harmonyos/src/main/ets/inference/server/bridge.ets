/**
 * inference/server/bridge
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// inference/server/bridge.ts
//
// Port of the CircleAI.Hosting.InferenceBridge contracts the inference server
// depends on: InferenceRequest / InferenceResponse / InferenceStatus /
// ModelDescriptor / ModelFormat / DeviceCapabilities / IInferenceBridge /
// InferenceFragment(+Kind), plus the in-process reference implementation
// (LocalProcessInferenceBridge) wired over an IChatGenerator. Also ports the
// CircleAI.Runtime.Backends enums (BackendKind / CapabilityTier) the server's
// admin + lifecycle layers key on.
//
// The server ports focus on the contracts + routing logic as in-memory
// handlers; this file supplies the bridge those handlers route to.

import { ChatFragmentKind } from "../../models/index";
import type { ChatMessage } from "../../models/index";
import type { GenerationOptions, IChatGenerator } from "../index";

// ── Runtime.Backends enums ────────────────────────────────────────────────────

/** MNN execution backend. Ported from CircleAI.Runtime.Backends.BackendKind. */
export enum BackendKind {
  Cpu = 0,
  Cuda = 1,
  Vulkan = 2,
  OpenCL = 3,
  Metal = 4,
  Ascend = 5,
  Cambricon = 6,
  CoreML = 7,
}

/** Capability tier / model size band. Ported from CapabilityTier. */
export enum CapabilityTier {
  Tier0_Tiny = 0,
  Tier1_Small = 1,
  Tier2_Medium = 2,
  Tier3_Large = 3,
  Tier4_Frontier = 4,
}

/** Case-insensitive parse of a BackendKind name. Mirrors Enum.TryParse(ignoreCase). */
export function parseBackendKind(s: string): BackendKind | null {
  const key = Object.keys(BackendKind).find(
    (k) => isNaN(Number(k)) && k.toLowerCase() === s.trim().toLowerCase(),
  );
  return key === undefined ? null : (BackendKind[key as keyof typeof BackendKind] as BackendKind);
}

/** Case-insensitive parse of a CapabilityTier name. */
export function parseCapabilityTier(s: string): CapabilityTier | null {
  const key = Object.keys(CapabilityTier).find(
    (k) => isNaN(Number(k)) && k.toLowerCase() === s.trim().toLowerCase(),
  );
  return key === undefined ? null : (CapabilityTier[key as keyof typeof CapabilityTier] as CapabilityTier);
}

// ── ModelDescriptor ───────────────────────────────────────────────────────────

/** On-disk encoding format of a model weight artefact. Ported from ModelFormat. */
export enum ModelFormat {
  Gguf = 0,
  Onnx = 1,
  CoreMl = 2,
  Tflite = 3,
  Unknown = 4,
}

/** Canonical descriptor for a single loaded model. Ported from ModelDescriptor. */
export interface ModelDescriptor {
  readonly modelId: string;
  readonly version: string;
  readonly format: ModelFormat;
  readonly contextWindowTokens: number;
  readonly vocabSize: number;
  readonly parameterCount: number;
  readonly quantisationLabel: string | null;
  readonly approximateMemoryBytes: number;
}

// ── DeviceCapabilities ──────────────────────────────────────────────────────

/** Static-ish capabilities report from the device. Ported from DeviceCapabilities. */
export interface DeviceCapabilities {
  readonly osName: string;
  readonly osVersion: string;
  readonly physicalMemoryBytes: number;
  readonly cpuCoreCount: number;
  readonly hasGpu: boolean;
  readonly gpuName: string | null;
  readonly gpuMemoryBytes: number | null;
  readonly hasNpu: boolean;
  readonly npuName: string | null;
  readonly hasTransportLayerEncryption: boolean;
}

// ── InferenceRequest / Response ───────────────────────────────────────────────

/** Terminal state of a single inference call. Ported from InferenceStatus. */
export enum InferenceStatus {
  Completed = 0,
  StoppedByToken = 1,
  StoppedByLength = 2,
  Failed = 3,
  Cancelled = 4,
}

/** One completion request. Ported from InferenceRequest (Guid -> string). */
export interface InferenceRequest {
  readonly id: string;
  readonly modelId: string;
  readonly prompt: string;
  readonly maxOutputTokens: number;
  readonly temperature: number;
  readonly topP: number;
  readonly stopSequences: readonly string[];
  readonly metadata: Readonly<Map<string, string>>;
  readonly requestedAt: string;
}

/** Result of a single completion call. Ported from InferenceResponse. */
export interface InferenceResponse {
  readonly requestId: string;
  readonly modelId: string;
  readonly outputText: string;
  readonly outputTokenCount: number;
  readonly promptTokenCount: number;
  readonly status: InferenceStatus;
  readonly inferenceMillis: number;
  readonly failureMessage: string | null;
  readonly completedAt: string;
  readonly reasoningText?: string | null;
}

/** Kind of fragment a streaming bridge emits. Ported from InferenceFragmentKind. */
export enum InferenceFragmentKind {
  Content = 0,
  Reasoning = 1,
}

/** A single fragment from streamFragments. Ported from InferenceFragment. */
export interface InferenceFragment {
  readonly kind: InferenceFragmentKind;
  readonly text: string;
}

// ── IInferenceBridge ──────────────────────────────────────────────────────────

/**
 * Cross-OS contract for an inference daemon. Ported from
 * CircleAI.Hosting.InferenceBridge.IInferenceBridge. `streamFragments` is a
 * required method here (TS interfaces have no default methods) — implementors
 * that don't surface reasoning can delegate to the free wrapper below.
 */
export interface IInferenceBridge {
  listLoadedModels(signal?: AbortSignal): Promise<readonly ModelDescriptor[]>;
  isModelLoaded(modelId: string, signal?: AbortSignal): Promise<boolean>;
  complete(request: InferenceRequest, signal?: AbortSignal): Promise<InferenceResponse>;
  streamCompletion(request: InferenceRequest, signal?: AbortSignal): AsyncGenerator<string>;
  streamFragments(request: InferenceRequest, signal?: AbortSignal): AsyncGenerator<InferenceFragment>;
  getDeviceCapabilities(signal?: AbortSignal): Promise<DeviceCapabilities>;
  /** Optional disposal hook — the lifecycle manager calls this on unload. */
  dispose?(): void;
}

// ── LocalProcessInferenceBridge ──────────────────────────────────────────────

const DEFAULT_DEVICE_CAPABILITIES: DeviceCapabilities = {
  osName: "Unknown",
  osVersion: "0.0",
  physicalMemoryBytes: 8 * 1024 * 1024 * 1024,
  cpuCoreCount: 8,
  hasGpu: false,
  gpuName: null,
  gpuMemoryBytes: null,
  hasNpu: false,
  npuName: null,
  hasTransportLayerEncryption: true,
};

/**
 * In-process IInferenceBridge — wraps any IChatGenerator and exposes it through
 * the bridge contract. Ported from
 * CircleAI.Hosting.InferenceBridge.LocalProcessInferenceBridge. The C#
 * ICapabilityProbe is injected here as a fixed DeviceCapabilities value (the
 * server ports keep hardware probing out of scope; inject real values if wanted).
 */
export class LocalProcessInferenceBridge implements IInferenceBridge {
  private readonly generator: IChatGenerator;
  private readonly descriptor: ModelDescriptor;
  private readonly capabilities: DeviceCapabilities;

  constructor(
    generator: IChatGenerator,
    descriptor: ModelDescriptor,
    capabilities: DeviceCapabilities = DEFAULT_DEVICE_CAPABILITIES,
  ) {
    if (!generator) throw new Error("generator required");
    if (!descriptor) throw new Error("descriptor required");
    this.generator = generator;
    this.descriptor = descriptor;
    this.capabilities = capabilities;
  }

  async listLoadedModels(_signal?: AbortSignal): Promise<readonly ModelDescriptor[]> {
    return [this.descriptor];
  }

  async isModelLoaded(modelId: string, _signal?: AbortSignal): Promise<boolean> {
    if (!modelId) throw new Error("modelId required");
    return this.descriptor.modelId === modelId;
  }

  async complete(request: InferenceRequest, _signal?: AbortSignal): Promise<InferenceResponse> {
    if (!request) throw new Error("request required");

    if (this.descriptor.modelId !== request.modelId) {
      return {
        requestId: request.id,
        modelId: request.modelId,
        outputText: "",
        outputTokenCount: 0,
        promptTokenCount: 0,
        status: InferenceStatus.Failed,
        inferenceMillis: 0,
        failureMessage: `Model '${request.modelId}' is not loaded by this bridge (have '${this.descriptor.modelId}').`,
        completedAt: new Date().toISOString(),
      };
    }

    const messages: ChatMessage[] = [{ role: "user", content: request.prompt }];
    const options = this.buildOptions(request);

    const started = performance.now();
    try {
      const response = await this.callGenerateResponse(messages, options);
      const output = response.text;
      const reasoning = response.reasoningContent ?? null;
      const status = determineStatus(output, request);
      return {
        requestId: request.id,
        modelId: request.modelId,
        outputText: output,
        outputTokenCount: estimateTokenCount(output),
        promptTokenCount: estimateTokenCount(request.prompt),
        status,
        inferenceMillis: performance.now() - started,
        failureMessage: null,
        completedAt: new Date().toISOString(),
        reasoningText: reasoning,
      };
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      return {
        requestId: request.id,
        modelId: request.modelId,
        outputText: "",
        outputTokenCount: 0,
        promptTokenCount: estimateTokenCount(request.prompt),
        status: InferenceStatus.Failed,
        inferenceMillis: performance.now() - started,
        failureMessage: message,
        completedAt: new Date().toISOString(),
      };
    }
  }

  async *streamCompletion(request: InferenceRequest, _signal?: AbortSignal): AsyncGenerator<string> {
    if (!request) throw new Error("request required");
    if (this.descriptor.modelId !== request.modelId) return;

    const messages: ChatMessage[] = [{ role: "user", content: request.prompt }];
    const options = this.buildOptions(request);

    let hasYielded = false;
    for await (const chunk of this.generator.streamAsync(messages, options)) {
      hasYielded = true;
      yield chunk;
    }
    if (!hasYielded) {
      // Fallback: generator streamed nothing — emit the full completion.
      const full = await this.generator.generateAsync(messages, options);
      yield full;
    }
  }

  async *streamFragments(
    request: InferenceRequest,
    _signal?: AbortSignal,
  ): AsyncGenerator<InferenceFragment> {
    if (!request) throw new Error("request required");
    if (this.descriptor.modelId !== request.modelId) return;

    const messages: ChatMessage[] = [{ role: "user", content: request.prompt }];
    const options = this.buildOptions(request);

    if (this.generator.streamFragmentsAsync) {
      for await (const f of this.generator.streamFragmentsAsync(messages, options)) {
        const kind =
          f.kind === ChatFragmentKind.Reasoning
            ? InferenceFragmentKind.Reasoning
            : InferenceFragmentKind.Content;
        yield { kind, text: f.text };
      }
    } else {
      // Default: tag every content chunk as Content (mirrors the C# default).
      for await (const chunk of this.generator.streamAsync(messages, options)) {
        yield { kind: InferenceFragmentKind.Content, text: chunk };
      }
    }
  }

  async getDeviceCapabilities(_signal?: AbortSignal): Promise<DeviceCapabilities> {
    return this.capabilities;
  }

  private buildOptions(request: InferenceRequest): GenerationOptions {
    return {
      maxTokens: request.maxOutputTokens,
      temperature: request.temperature,
      topP: request.topP,
      stopSequences: request.stopSequences.length === 0 ? undefined : [...request.stopSequences],
    };
  }

  private async callGenerateResponse(
    messages: readonly ChatMessage[],
    options: GenerationOptions,
  ) {
    // Prefer a native generateResponse when the generator provides one (our
    // DeterministicChatGenerator does), so reasoning is split from content.
    const gen = this.generator as IChatGenerator & {
      generateResponse?: (m: readonly ChatMessage[], o?: GenerationOptions) => Promise<{
        text: string;
        reasoningContent?: string | null;
      }>;
    };
    if (typeof gen.generateResponse === "function") {
      return gen.generateResponse(messages, options);
    }
    const text = await this.generator.generateAsync(messages, options);
    return { text, reasoningContent: null as string | null };
  }
}

// ── helpers ──────────────────────────────────────────────────────────────────

function determineStatus(output: string, request: InferenceRequest): InferenceStatus {
  if (request.stopSequences.length > 0) {
    for (const s of request.stopSequences) {
      if (s && output.includes(s)) return InferenceStatus.StoppedByToken;
    }
  }
  const produced = estimateTokenCount(output);
  return produced >= request.maxOutputTokens
    ? InferenceStatus.StoppedByLength
    : InferenceStatus.Completed;
}

/** Rough ~4-chars-per-token heuristic. Ported from EstimateTokenCount. */
export function estimateTokenCount(text: string): number {
  if (!text) return 0;
  return Math.max(1, Math.floor(text.length / 4));
}
