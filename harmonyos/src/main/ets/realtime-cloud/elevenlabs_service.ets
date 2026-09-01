/**
 * realtime-cloud/elevenlabs_service
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/elevenlabs_service.ts
//
// (3.3.0) IRealtimeService backed by ElevenLabs Conversational AI
// (ElevenLabsConvService.cs). The endpoint takes ?agent_id={id}; xi-api-key
// header authenticates.

import type { IRealtimeService, IRealtimeSession, RealtimeSessionConfig } from "../realtime/index";
import type { ElevenLabsConvOptions } from "./options";
import {
  NullRealtimeLogger,
  NullRealtimeTransportFactory,
  type IRealtimeLogger,
  type IRealtimeTransportFactory,
} from "./transport";
import { RealtimeWebSocketSession } from "./websocket_session";

/** (3.3.0) {@link IRealtimeService} backed by ElevenLabs Conversational AI. Mirrors C# `ElevenLabsConvService`. */
export class ElevenLabsConvService implements IRealtimeService {
  private readonly options: ElevenLabsConvOptions;
  private readonly transports: IRealtimeTransportFactory;
  private readonly logger: IRealtimeLogger;

  constructor(
    options: ElevenLabsConvOptions,
    transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.instance,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    if (options == null) throw new Error("options required");
    this.options = options;
    this.transports = transports ?? NullRealtimeTransportFactory.instance;
    this.logger = logger ?? NullRealtimeLogger.instance;
  }

  get providerId(): string {
    return "elevenlabs-conv";
  }

  get isConfigured(): boolean {
    return !isBlank(this.options.apiKey) && !isBlank(this.options.agentId);
  }

  async startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    this.ensureConfigured();

    const endpoint = `${this.options.webSocketEndpoint}?agent_id=${encodeURIComponent(this.options.agentId as string)}`;
    const headers = new Map<string, string>([["xi-api-key", this.options.apiKey as string]]);

    const transport = await this.transports.connectAsync(endpoint, headers, signal);
    return new RealtimeWebSocketSession(transport, config, this.providerId, this.logger);
  }

  private ensureConfigured(): void {
    if (!this.isConfigured) {
      throw new Error(
        "ElevenLabs Conversational AI is not configured. Set ElevenLabsConvOptions.apiKey AND agentId before calling startSessionAsync.",
      );
    }
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
