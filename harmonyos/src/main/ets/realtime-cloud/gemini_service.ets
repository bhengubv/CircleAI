/**
 * realtime-cloud/gemini_service
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/gemini_service.ts
//
// (3.3.0) IRealtimeService backed by Google Gemini Live (BidiGenerateContent)
// (GeminiLiveService.cs). Authenticates with the API key on the query string;
// uses Google's setup / clientContent / serverContent JSON envelope.

import type { IRealtimeService, IRealtimeSession, RealtimeSessionConfig } from "../realtime/index";
import type { GeminiLiveOptions } from "./options";
import {
  NullRealtimeLogger,
  NullRealtimeTransportFactory,
  type IRealtimeLogger,
  type IRealtimeTransportFactory,
} from "./transport";
import { RealtimeWebSocketSession } from "./websocket_session";

/** (3.3.0) {@link IRealtimeService} backed by Gemini Live. Mirrors C# `GeminiLiveService`. */
export class GeminiLiveService implements IRealtimeService {
  private readonly options: GeminiLiveOptions;
  private readonly transports: IRealtimeTransportFactory;
  private readonly logger: IRealtimeLogger;

  constructor(
    options: GeminiLiveOptions,
    transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.instance,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    if (options == null) throw new Error("options required");
    this.options = options;
    this.transports = transports ?? NullRealtimeTransportFactory.instance;
    this.logger = logger ?? NullRealtimeLogger.instance;
  }

  get providerId(): string {
    return "gemini-live";
  }

  get isConfigured(): boolean {
    return !isBlank(this.options.apiKey);
  }

  async startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    this.ensureConfigured();

    const endpoint = `${this.options.webSocketEndpoint}?key=${encodeURIComponent(this.options.apiKey as string)}`;
    const transport = await this.transports.connectAsync(endpoint, null, signal);
    return new RealtimeWebSocketSession(transport, config, this.providerId, this.logger);
  }

  private ensureConfigured(): void {
    if (!this.isConfigured) {
      throw new Error("Gemini Live is not configured. Set GeminiLiveOptions.apiKey before calling startSessionAsync.");
    }
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
