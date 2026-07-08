// hosting/service.ts
//
// Port of CircleAI.Hosting.IAIService + AIService — the long-lived butler
// service that owns a single loaded chat generator for the process lifetime.
//
// Faithful port notes:
//   • The native QwenTextGenerator default is replaced by an injected
//     generatorFactory (AIOptions.generatorFactory) — the "inject native behind
//     interfaces" rule. When neither a factory nor a ModelPath is supplied, the
//     service throws exactly as the C# does when it has no way to resolve a model.
//   • CancellationToken plumbing is dropped (per the TS-port convention); the
//     disposed/stopped flags gate in-flight streams instead.
//   • Guid.NewGuid → randomUUID; Stopwatch → performance.now; DateTimeOffset.UtcNow
//     → ISO 8601 string; StringBuilder → string array joined.
//   • Feedback persona adaptation reproduces the exact verbosity/formality state
//     machine and topic-weight accumulation.

import { randomUUID } from "node:crypto";
import type { ChatMessage } from "../models/index.js";
import type { UpgradeInfo } from "../models/index.js";
import type {
  GenerationOptions,
  IChatGenerator,
  IModelSelector,
  ModelSelection,
} from "../inference/index.js";
import type { IModelLoader } from "../core/index.js";
import type { ModelRegistryService } from "../registry/index.js";
import { DeviceTier, DeviceTierDefaults } from "../device/index.js";
import type { DeviceProbe } from "../device/index.js";
import {
  DefaultDeviceContext,
  NullDeviceContext,
  snapshot as deviceSnapshot,
} from "../device/index.js";
import {
  FeedbackPolarity,
  PersonaState,
  type EpisodicMemoryEntry,
  type FeedbackSignal,
} from "../memory/index.js";
import { RagContextBuilder } from "../memory/rag.js";
import { FeedbackAnalyser } from "../memory/feedback_analyser.js";
import type { ToolInvocation, ToolResult } from "../tools/index.js";
import { AIOptions, DEFAULT_AI_OPTIONS } from "./options.js";
import { BrownoutReason, type IAIObserver } from "./observers.js";
import { SkillContextBuilder } from "./skills.js";
import {
  type IMemoryPressureSource,
  MemoryPressureLevel,
} from "./memory_pressure.js";

const TOOL_CALL_OPEN = "<tool_call>";
const TOOL_CALL_CLOSE = "</tool_call>";

/**
 * A model selector that can additionally produce a fallback chain for the
 * brownout hot-swap. The base {@link IModelSelector} carries bestFit; brownout
 * needs `chainFor`. Selectors that don't provide it simply never brown out.
 */
export interface IFallbackChainModelSelector extends IModelSelector {
  /** Ordered fallback chain starting with `modelId` (largest → smallest). */
  chainFor?(modelId: string): readonly string[];
}

/**
 * Long-lived butler service contract. Mirrors CircleAI.Hosting.IAIService.
 */
export interface IAIService {
  /** True once StartAsync has completed and the model is loaded. */
  readonly isReady: boolean;
  /** Resolves the model file, loads it, and optionally warms up. Idempotent. */
  startAsync(): Promise<void>;
  /** Releases the model handle and shuts the service down. */
  stopAsync(): Promise<void>;
  /** Single-user question convenience wrapper. */
  askAsync(question: string): Promise<string>;
  /** Generates a complete assistant reply for the conversation. */
  chatAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): Promise<string>;
  /** Streams the assistant reply piece-by-piece. */
  streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): AsyncGenerator<string>;
  /** Routes a tool invocation to the configured bridge. */
  invokeToolAsync(invocation: ToolInvocation): Promise<ToolResult>;
  /** Agentic run: loops on tool calls until plain text or max iterations. */
  agenticChatAsync(
    prompt: string,
    options?: GenerationOptions | null,
  ): Promise<string>;
  /** Records a feedback signal against a past response. */
  submitFeedbackAsync(signal: FeedbackSignal): Promise<void>;
  /** Compares installed models to the registry; returns detected upgrades. */
  checkForUpgradesAsync(): Promise<readonly UpgradeInfo[]>;
  /** (RT-07) Pre-warm the loaded generator without a user-facing call. */
  prewarmAsync(): Promise<void>;
  /** Async-dispose (C# IAsyncDisposable). */
  disposeAsync(): Promise<void>;
}

/**
 * Default {@link IAIService}. Loads a chat generator once and serves all
 * callers from that single handle. Mirrors CircleAI.Hosting.AIService.
 */
export class AIService implements IAIService {
  private readonly options: AIOptions;
  private readonly modelLoader: IModelLoader | null;
  private readonly generatorFactory: ((modelPath: string) => IChatGenerator) | null;
  private readonly modelSelector: IFallbackChainModelSelector | null;
  private readonly modelRegistry: ModelRegistryService | null;

  private resolvedModelId: string | null = null;
  private resolvedDeviceTier: DeviceTier = DeviceTier.Desktop;

  // Serialises StartAsync/StopAsync/Brownout (SemaphoreSlim(1,1) equivalent).
  private startGate: Promise<void> = Promise.resolve();

  private generator: IChatGenerator | null = null;
  private started = false;
  private disposed = false;
  // Bumped on stop/brownout/dispose so in-flight streams drain (shutdownCts).
  private shutdownEpoch = 0;

  private readonly pressureSource: IMemoryPressureSource | null;
  private pressureUnsub: (() => void) | null = null;

  private personaCache: PersonaState | null = null;
  private ragBuilder: RagContextBuilder | null = null;
  private skillContextBuilder: SkillContextBuilder | null = null;

  constructor(
    options: AIOptions,
    modelLoader: IModelLoader | null = null,
    generatorFactory: ((modelPath: string) => IChatGenerator) | null = null,
    modelSelector: IFallbackChainModelSelector | null = null,
    modelRegistry: ModelRegistryService | null = null,
    memoryPressureSource: IMemoryPressureSource | null = null,
  ) {
    if (!options) throw new Error("options required");
    this.options = options;
    this.modelLoader = modelLoader;
    // The factory may also ride on the options bag (TS injection seam).
    this.generatorFactory = generatorFactory ?? options.generatorFactory ?? null;
    this.modelSelector = modelSelector;
    this.modelRegistry = modelRegistry;
    this.pressureSource = memoryPressureSource;
  }

  get isReady(): boolean {
    return this.started && this.generator !== null && !this.disposed;
  }

  // ── option accessors with defaults ─────────────────────────────────────────

  private get systemPrompt(): string {
    return this.options.systemPrompt ?? DEFAULT_AI_OPTIONS.systemPrompt;
  }
  private get warmOnStart(): boolean {
    return this.options.warmOnStart ?? DEFAULT_AI_OPTIONS.warmOnStart;
  }
  private get ragTopK(): number {
    return this.options.ragTopK ?? DEFAULT_AI_OPTIONS.ragTopK;
  }
  private get personaUserId(): string {
    return this.options.personaUserId ?? DEFAULT_AI_OPTIONS.personaUserId;
  }
  private get skillTopK(): number {
    return this.options.skillTopK ?? DEFAULT_AI_OPTIONS.skillTopK;
  }
  private get defaultGenerationOptions(): GenerationOptions | undefined {
    return this.options.defaultGenerationOptions ?? undefined;
  }

  // ── Serialised critical sections ───────────────────────────────────────────

  private runExclusive<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.startGate.then(fn, fn);
    // Keep the chain alive but swallow rejections so the gate never wedges.
    this.startGate = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  // ── Upgrades ───────────────────────────────────────────────────────────────

  async checkForUpgradesAsync(): Promise<readonly UpgradeInfo[]> {
    this.throwIfDisposed();
    if (
      this.modelRegistry === null ||
      this.options.modelStorageDirectory == null ||
      this.options.modelStorageDirectory.trim().length === 0
    ) {
      return [];
    }
    try {
      return await this.modelRegistry.checkForUpgrades(
        this.options.modelStorageDirectory,
      );
    } catch {
      // Non-fatal: treat as no upgrades.
      return [];
    }
  }

  // ── Lifecycle ──────────────────────────────────────────────────────────────

  async startAsync(): Promise<void> {
    this.throwIfDisposed();
    if (this.started) return;

    await this.runExclusive(async () => {
      if (this.started) return;

      const modelPath = await this.resolveModelPath();

      const contextSize =
        this.options.contextSize ??
        DeviceTierDefaults.contextWindow(this.resolvedDeviceTier);
      void contextSize; // parity: passed to the native ctor; the factory owns it.

      const factory = this.generatorFactory;
      if (factory === null)
        throw new Error(
          "AIService needs an injected generatorFactory (or AIOptions.generatorFactory) to load a model.",
        );
      const generator = factory(modelPath);
      if (generator == null) throw new Error("Generator factory returned null.");
      this.generator = generator;

      if (this.warmOnStart) {
        try {
          await this.warmUp();
        } catch {
          // warm-up failure is non-fatal.
        }
      }

      this.started = true;

      // RT-04 — subscribe to platform pressure. Critical → brownout.
      if (this.pressureSource !== null && this.pressureUnsub === null) {
        this.pressureUnsub = this.pressureSource.subscribe(async (_old, next) => {
          if (next === MemoryPressureLevel.Critical)
            await this.brownoutAsync(BrownoutReason.MemoryPressure);
        });
      }

      await this.fireObserver((o) => o.onStartedAsync?.());

      if (this.options.checkForUpgradesOnStart) {
        const upgrades = await this.checkForUpgradesAsync();
        for (const u of upgrades)
          await this.fireObserver((o) => o.onUpgradeAvailableAsync?.(u));
      }
    });
  }

  async stopAsync(): Promise<void> {
    if (this.disposed) return;

    await this.trySavePersona();

    await this.runExclusive(async () => {
      this.shutdownEpoch++;
      this.disposeGenerator();
      this.generator = null;
      this.started = false;
      this.personaCache = null;

      await this.fireObserver((o) => o.onStoppedAsync?.());
    });
  }

  // ── RT-04 Brownout ─────────────────────────────────────────────────────────

  /**
   * Hot-swap the running generator to the next entry in its fallback chain.
   * No-op when not started, no fallback exists, or no selector is wired.
   * Mirrors AIService.BrownoutAsync.
   */
  async brownoutAsync(reason: BrownoutReason): Promise<boolean> {
    this.throwIfDisposed();
    if (!this.started || this.generator === null) return false;
    if (
      this.modelSelector === null ||
      typeof this.modelSelector.chainFor !== "function" ||
      this.resolvedModelId == null ||
      this.resolvedModelId.trim().length === 0
    ) {
      return false;
    }

    const from = this.resolvedModelId;
    const chain = this.modelSelector.chainFor(from);
    let idx = -1;
    for (let i = 0; i < chain.length; i++)
      if (chain[i].toLowerCase() === from.toLowerCase()) {
        idx = i;
        break;
      }
    if (idx < 0 || idx + 1 >= chain.length) return false;
    const to = chain[idx + 1];

    let swapped = false;
    await this.runExclusive(async () => {
      if (this.resolvedModelId != null && this.resolvedModelId.toLowerCase() === to.toLowerCase())
        return;

      // Cancel in-flight generations so they drain.
      this.shutdownEpoch++;
      this.disposeGenerator();
      this.generator = null;
      this.resolvedModelId = to;

      if (this.modelLoader === null)
        throw new Error(
          "Brownout requires an IModelLoader to fetch the fallback bundle.",
        );

      const existing = this.modelLoader.getModelPath(to);
      let modelPath: string;
      if (existing && (await this.modelLoader.modelExists(to))) {
        modelPath = existing;
      } else {
        modelPath = await this.modelLoader.downloadModelAsync(to);
      }
      if (!modelPath) throw new Error(`Brownout target '${to}' resolution failed.`);

      const factory = this.generatorFactory;
      if (factory === null)
        throw new Error("Brownout requires a generatorFactory to load the fallback.");
      this.generator = factory(modelPath);
      swapped = true;
    });

    if (swapped) await this.fireObserver((o) => o.onBrownoutAsync?.(from, to, reason));
    return swapped;
  }

  // ── Single-turn inference ──────────────────────────────────────────────────

  askAsync(question: string): Promise<string> {
    if (question == null || question.length === 0)
      throw new Error("question required");
    const messages: ChatMessage[] = [{ role: "user", content: question }];
    return this.chatAsync(messages, this.defaultGenerationOptions);
  }

  async chatAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): Promise<string> {
    if (!messages) throw new Error("messages required");
    await this.ensureStarted();

    const generator = this.generator;
    if (generator === null) throw new Error("Butler is not ready.");

    const userQuery = lastUserContent(messages);
    const prepared = await this.prepareMessages(messages, userQuery);
    const effectiveOptions = options ?? this.defaultGenerationOptions;

    const correlationId = randomUUID();
    const start = performance.now();
    const response = await generator.generateAsync(
      prepared,
      effectiveOptions ?? undefined,
    );
    const elapsedMs = performance.now() - start;

    void this.tryStoreEpisode(userQuery, response);

    await this.fireObserver((o) =>
      o.onChatCompletedAsync?.({
        correlationId,
        messages: prepared,
        response,
        elapsedMs,
        timestamp: new Date().toISOString(),
      }),
    );

    return response;
  }

  async *streamAsync(
    messages: readonly ChatMessage[],
    options?: GenerationOptions | null,
  ): AsyncGenerator<string> {
    if (!messages) throw new Error("messages required");
    await this.ensureStarted();

    const generator = this.generator;
    if (generator === null) throw new Error("Butler is not ready.");

    const userQuery = lastUserContent(messages);
    const prepared = await this.prepareMessages(messages, userQuery);
    const effectiveOptions = options ?? this.defaultGenerationOptions;

    const epoch = this.shutdownEpoch;
    const correlationId = randomUUID();
    const start = performance.now();
    let tokenCount = 0;
    let firstToken = true;
    const sb: string[] = [];

    for await (const piece of generator.streamAsync(
      prepared,
      effectiveOptions ?? undefined,
    )) {
      // Drain if the service stopped/brownout/disposed mid-stream.
      if (this.disposed || this.shutdownEpoch !== epoch) break;

      if (firstToken) {
        firstToken = false;
        await this.fireObserver((o) =>
          o.onStreamStartedAsync?.({
            correlationId,
            messages: prepared,
            elapsedMs: performance.now() - start,
            tokenCount: 0,
            timestamp: new Date().toISOString(),
          }),
        );
      }

      sb.push(piece);
      tokenCount++;
      yield piece;
    }

    const elapsedMs = performance.now() - start;
    void this.tryStoreEpisode(userQuery, sb.join(""));

    await this.fireObserver((o) =>
      o.onStreamCompletedAsync?.({
        correlationId,
        messages: prepared,
        elapsedMs,
        tokenCount,
        timestamp: new Date().toISOString(),
      }),
    );
  }

  async invokeToolAsync(invocation: ToolInvocation): Promise<ToolResult> {
    if (!invocation) throw new Error("invocation required");
    this.throwIfDisposed();

    if (this.options.toolBridge == null) {
      const failResult: ToolResult = {
        toolName: invocation.toolName,
        success: false,
        error: "No tool bridge configured.",
      };
      await this.fireObserver((o) =>
        o.onToolInvokedAsync?.({
          correlationId: randomUUID(),
          invocation,
          result: failResult,
          elapsedMs: 0,
          timestamp: new Date().toISOString(),
        }),
      );
      return failResult;
    }

    const correlationId = randomUUID();
    const start = performance.now();
    const result = await this.options.toolBridge.invoke(invocation);
    const elapsedMs = performance.now() - start;

    await this.fireObserver((o) =>
      o.onToolInvokedAsync?.({
        correlationId,
        invocation,
        result,
        elapsedMs,
        timestamp: new Date().toISOString(),
      }),
    );

    return result;
  }

  // ── v2.0 Agentic loop ──────────────────────────────────────────────────────

  async agenticChatAsync(
    prompt: string,
    options?: GenerationOptions | null,
  ): Promise<string> {
    if (prompt == null || prompt.length === 0) throw new Error("prompt required");
    await this.ensureStarted();

    const generator = this.generator;
    if (generator === null) throw new Error("Butler is not ready.");

    const maxIter = Math.max(
      1,
      this.options.agenticMaxIterations ??
        DeviceTierDefaults.agenticMaxIterations(this.resolvedDeviceTier),
    );
    const effectiveOptions = options ?? this.defaultGenerationOptions;

    const history: ChatMessage[] = [{ role: "user", content: prompt }];

    let lastResponse = "";
    for (let iteration = 0; iteration < maxIter; iteration++) {
      const prepared = await this.prepareMessages(history, prompt);

      const start = performance.now();
      const response = await generator.generateAsync(
        prepared,
        effectiveOptions ?? undefined,
      );
      const elapsedMs = performance.now() - start;

      lastResponse = response;
      history.push({ role: "assistant", content: response });

      await this.fireObserver((o) =>
        o.onChatCompletedAsync?.({
          correlationId: randomUUID(),
          messages: prepared,
          response,
          elapsedMs,
          timestamp: new Date().toISOString(),
        }),
      );

      const invocation = AIService.parseToolCall(response);
      if (invocation === null) break; // No tool call — done.

      if (this.options.toolBridge == null) {
        history.push({
          role: "tool",
          content: `{"tool": "${invocation.toolName}", "error": "No tool bridge configured."}`,
        });
        continue;
      }

      const toolResult = await this.invokeToolAsync(invocation);
      const toolContent = toolResult.success
        ? `{"tool": "${toolResult.toolName}", "result": ${JSON.stringify(toolResult.result ?? null)}}`
        : `{"tool": "${toolResult.toolName}", "error": ${JSON.stringify(toolResult.error ?? null)}}`;

      history.push({ role: "tool", content: toolContent });
    }

    void this.tryStoreEpisode(prompt, lastResponse);
    return lastResponse;
  }

  // ── v2.0 Feedback ──────────────────────────────────────────────────────────

  async submitFeedbackAsync(signal: FeedbackSignal): Promise<void> {
    if (!signal) throw new Error("signal required");
    this.throwIfDisposed();

    if (this.options.feedbackStore == null) return;

    try {
      await this.options.feedbackStore.addAsync(signal);

      const persona = await this.ensurePersona();
      if (signal.polarity === FeedbackPolarity.Positive) persona.positiveSignals++;
      else if (signal.polarity === FeedbackPolarity.Negative)
        persona.negativeSignals++;
      persona.totalInteractions++;

      const recentSignals = await this.options.feedbackStore.getRecentAsync(20);
      const adaptation = new FeedbackAnalyser().analyse(recentSignals);

      // Verbosity: float delta → string state machine.
      if (adaptation.verbosityDelta < 0)
        persona.verbosity = persona.verbosity === "detailed" ? "balanced" : "brief";
      else if (adaptation.verbosityDelta > 0)
        persona.verbosity = persona.verbosity === "brief" ? "balanced" : "detailed";

      // Formality: same pattern (analyser returns 0 currently; wired for future).
      if (adaptation.formalityDelta < 0)
        persona.formality = persona.formality === "formal" ? "neutral" : "casual";
      else if (adaptation.formalityDelta > 0)
        persona.formality = persona.formality === "casual" ? "neutral" : "formal";

      for (const topic of adaptation.preferredTopics) {
        const existing = persona.topicWeights[topic] ?? 0;
        persona.topicWeights[topic] = existing + 1;
      }

      await this.trySavePersona();
    } catch {
      // Non-fatal.
    }
  }

  // ── DisposeAsync ───────────────────────────────────────────────────────────

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;

    this.shutdownEpoch++;
    try {
      this.pressureUnsub?.();
    } catch {
      /* swallow */
    } finally {
      this.pressureUnsub = null;
    }

    await this.trySavePersona();

    try {
      await this.stopAsync();
    } catch {
      /* swallow */
    }

    this.disposeGenerator();
    this.generator = null;
  }

  // ── Prewarm ────────────────────────────────────────────────────────────────

  async prewarmAsync(): Promise<void> {
    this.throwIfDisposed();
    if (!this.started) {
      await this.startAsync();
      return;
    }
    await this.warmUp();
  }

  // ── Private — startup helpers ──────────────────────────────────────────────

  private async ensureStarted(): Promise<void> {
    this.throwIfDisposed();
    if (this.started) return;
    await this.startAsync();
  }

  private async resolveModelPath(): Promise<string> {
    // 1. Explicit path wins.
    if (this.options.modelPath != null && this.options.modelPath.trim().length > 0) {
      this.resolvedModelId = this.options.modelId ?? null;
      return this.options.modelPath;
    }

    if (this.modelLoader === null)
      throw new Error(
        "AIService needs either AIOptions.modelPath or an IModelLoader.",
      );

    // 2. Resolve modelId — pinned or auto-selected from the live device.
    let modelId = this.options.modelId ?? null;
    let autoSelected = false;

    if (modelId == null || modelId.trim().length === 0) {
      if (this.modelSelector === null)
        throw new Error(
          "AIOptions.modelId is null and no IModelSelector is registered.",
        );

      const deviceCtx = this.options.deviceContext ?? new DefaultDeviceContext();
      const probe: DeviceProbe =
        deviceCtx instanceof DefaultDeviceContext
          ? await deviceCtx.buildProbe()
          : await deviceSnapshot();
      const selection: ModelSelection = this.modelSelector.bestFit(
        probe,
        this.options.requiredCapabilities ?? DEFAULT_AI_OPTIONS.requiredCapabilities,
      );

      modelId = selection.modelId;
      this.resolvedDeviceTier = selection.tier;
      autoSelected = true;
    }

    this.resolvedModelId = modelId;
    await this.fireObserver((o) => o.onModelFetchingAsync?.(modelId!, autoSelected));

    // 3. Already on disk? Use it.
    const existing = this.modelLoader.getModelPath(modelId);
    if (existing && (await this.modelLoader.modelExists(modelId))) return existing;

    // 4. Fetch via the loader.
    const downloaded = await this.modelLoader.downloadModelAsync(modelId);
    if (!downloaded)
      throw new Error(`Model loader returned an invalid path for '${modelId}'.`);
    return downloaded;
  }

  private async warmUp(): Promise<void> {
    const generator = this.generator;
    if (generator === null) return;

    const warmMessages: ChatMessage[] = [
      { role: "system", content: this.systemPrompt },
      { role: "user", content: "." },
    ];
    const warmOptions: GenerationOptions = { maxTokens: 1, temperature: 0 };
    await generator.generateAsync(warmMessages, warmOptions);
  }

  // ── Private — v2.0 context enrichment ──────────────────────────────────────

  private async prepareMessages(
    messages: readonly ChatMessage[],
    userQuery: string,
  ): Promise<ChatMessage[]> {
    const systemContent = await this.buildEnrichedSystemPrompt(userQuery);

    const hasSystem = messages.some((m) => m.role.toLowerCase() === "system");

    const prepared: ChatMessage[] = [];
    if (hasSystem) {
      prepared.push(...messages);
    } else {
      if (systemContent != null && systemContent.trim().length > 0)
        prepared.push({ role: "system", content: systemContent });
      prepared.push(...messages);
    }
    return prepared;
  }

  private async buildEnrichedSystemPrompt(userQuery: string): Promise<string> {
    const sb: string[] = [this.systemPrompt];

    // 1. Persona hints.
    try {
      const persona = await this.ensurePersona();
      const hint = persona.toSystemPromptHint();
      if (hint != null && hint.trim().length > 0) {
        sb.push("\n");
        sb.push(hint);
      }
    } catch {
      /* persona load failure is non-fatal */
    }

    // 1b. Affect state.
    if (this.options.affectStore != null) {
      try {
        const affect = await this.options.affectStore.loadAsync(this.personaUserId);
        const hint = affect.toSystemPromptHint();
        if (hint != null && hint.trim().length > 0) {
          sb.push("\n");
          sb.push(hint);
        }
      } catch {
        /* affect load failure is non-fatal */
      }
    }

    // 2. Device context.
    const ctx = this.options.deviceContext;
    if (ctx != null && !(ctx instanceof NullDeviceContext)) {
      const ctxLines: string[] = [];
      if (ctx.localTime != null)
        ctxLines.push(
          `Local time: ${formatLocalTime(ctx.localTime)} (${ctx.timeZoneId ?? "UTC"})`,
        );
      if (ctx.locationHint != null && ctx.locationHint.trim().length > 0)
        ctxLines.push(`Location: ${ctx.locationHint}`);
      if (ctx.batteryLevel != null) {
        const pct = Math.trunc(ctx.batteryLevel * 100);
        const charging = ctx.isCharging === true ? " (charging)" : "";
        ctxLines.push(`Battery: ${pct}%${charging}`);
      }
      if (ctx.networkType != null && ctx.networkType.trim().length > 0)
        ctxLines.push(`Network: ${ctx.networkType}`);
      if (ctx.activeAppId != null && ctx.activeAppId.trim().length > 0)
        ctxLines.push(`Active app: ${ctx.activeAppId}`);

      if (ctxLines.length > 0) {
        sb.push("\n");
        sb.push("[Device context]\n");
        for (const line of ctxLines) sb.push(line + "\n");
      }
    }

    // 3. RAG context.
    if (
      this.options.episodicMemory != null &&
      this.ragTopK > 0 &&
      userQuery != null &&
      userQuery.trim().length > 0
    ) {
      try {
        const builder = this.ensureRagBuilder();
        const ragBlock = await builder.buildContextAsync(userQuery);
        if (ragBlock != null && ragBlock.trim().length > 0) {
          sb.push("\n");
          sb.push(ragBlock);
        }
      } catch {
        /* RAG failure is non-fatal */
      }
    }

    // 4. Skill context.
    if (
      this.options.skillStore != null &&
      userQuery != null &&
      userQuery.trim().length > 0
    ) {
      try {
        const skillBuilder = this.ensureSkillContextBuilder();
        const skillBlock = await skillBuilder.buildContextAsync(userQuery);
        if (skillBlock != null && skillBlock.trim().length > 0) {
          sb.push("\n");
          sb.push(skillBlock);
        }
      } catch {
        /* skill context failure is non-fatal */
      }
    }

    return sb.join("");
  }

  private ensureSkillContextBuilder(): SkillContextBuilder {
    if (this.skillContextBuilder !== null) return this.skillContextBuilder;
    this.skillContextBuilder = new SkillContextBuilder(
      this.options.skillStore!,
      this.skillTopK,
    );
    return this.skillContextBuilder;
  }

  private ensureRagBuilder(): RagContextBuilder {
    if (this.ragBuilder !== null) return this.ragBuilder;
    this.ragBuilder =
      this.options.ragBuilder ??
      new RagContextBuilder(this.options.episodicMemory!, null, this.ragTopK);
    return this.ragBuilder;
  }

  // ── Private — persona helpers ──────────────────────────────────────────────

  private async ensurePersona(): Promise<PersonaState> {
    if (this.personaCache !== null) return this.personaCache;
    if (this.options.personaStore == null) {
      const fresh = new PersonaState();
      fresh.userId = this.personaUserId;
      this.personaCache = fresh;
      return fresh;
    }
    this.personaCache = await this.options.personaStore.loadAsync(this.personaUserId);
    return this.personaCache;
  }

  private async trySavePersona(): Promise<void> {
    if (this.personaCache === null || this.options.personaStore == null) return;
    try {
      await this.options.personaStore.saveAsync(this.personaCache);
    } catch {
      /* non-fatal */
    }
  }

  // ── Private — episodic memory ──────────────────────────────────────────────

  private async tryStoreEpisode(
    userText: string,
    assistantText: string,
  ): Promise<void> {
    if (this.options.episodicMemory == null) return;
    if (userText == null || userText.trim().length === 0) return;

    try {
      const entry: EpisodicMemoryEntry = {
        id: randomUUID(),
        recordedAtUtc: new Date(),
        userText,
        assistantText,
        appContext: this.options.deviceContext?.activeAppId ?? undefined,
        embedding: undefined,
      };
      await this.options.episodicMemory.addAsync(entry);
    } catch {
      /* non-fatal */
    }
  }

  // ── Private — tool call parsing ────────────────────────────────────────────

  /**
   * Attempts to parse a tool call from Qwen3's native
   * `<tool_call>…</tool_call>` format. Returns null when absent. Mirrors
   * AIService.ParseToolCall.
   */
  static parseToolCall(response: string): ToolInvocation | null {
    if (response == null || response.trim().length === 0) return null;

    const start = response.indexOf(TOOL_CALL_OPEN);
    if (start < 0) return null;

    const contentStart = start + TOOL_CALL_OPEN.length;
    const end = response.indexOf(TOOL_CALL_CLOSE, contentStart);
    if (end < 0) return null;

    const json = response.substring(contentStart, end).trim();
    if (json.length === 0) return null;

    try {
      const root = JSON.parse(json) as Record<string, unknown>;

      // Support both {"name":...} and {"tool_name":...}.
      let toolName: string | null = null;
      if (typeof root["name"] === "string") toolName = root["name"] as string;
      else if (typeof root["tool_name"] === "string")
        toolName = root["tool_name"] as string;

      if (toolName == null || toolName.trim().length === 0) return null;

      const args: Record<string, unknown> = {};
      const argsProp = root["arguments"];
      if (isPlainObject(argsProp)) {
        for (const [k, v] of Object.entries(argsProp)) {
          // C#: string values kept as-is; everything else → raw JSON text.
          args[k] = typeof v === "string" ? v : JSON.stringify(v);
        }
      }

      return { toolName, arguments: args };
    } catch {
      return null;
    }
  }

  // ── Private — observer + disposal ──────────────────────────────────────────

  private async fireObserver(
    action: (o: IAIObserver) => Promise<void> | undefined,
  ): Promise<void> {
    const observer = this.options.observer;
    if (observer == null) return;
    try {
      await action(observer);
    } catch {
      // Observer errors are non-fatal.
    }
  }

  private disposeGenerator(): void {
    const g = this.generator;
    if (g == null) return;
    try {
      g.dispose();
    } catch {
      /* swallow */
    }
  }

  private throwIfDisposed(): void {
    if (this.disposed) throw new Error("AIService is disposed.");
  }
}

// ── module-local helpers ───────────────────────────────────────────────────────

function lastUserContent(messages: readonly ChatMessage[]): string {
  for (let i = messages.length - 1; i >= 0; i--) {
    if (messages[i].role.toLowerCase() === "user") return messages[i].content;
  }
  return "";
}

function formatLocalTime(d: Date): string {
  // C# "yyyy-MM-dd HH:mm" on the context's LocalTime (already local).
  const y = d.getFullYear();
  const mo = String(d.getMonth() + 1).padStart(2, "0");
  const da = String(d.getDate()).padStart(2, "0");
  const h = String(d.getHours()).padStart(2, "0");
  const mi = String(d.getMinutes()).padStart(2, "0");
  return `${y}-${mo}-${da} ${h}:${mi}`;
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}
