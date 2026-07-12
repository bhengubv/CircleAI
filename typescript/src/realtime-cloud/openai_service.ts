// realtime-cloud/openai_service.ts
//
// (3.3.0) IRealtimeService backed by OpenAI's gpt-4o-realtime WSS API
// (OpenAiRealtimeService.cs). Authenticates with Bearer + OpenAI-Beta:
// realtime=v1 header. The session translates RealtimeAudioFrame ↔ OpenAI's
// input_audio_buffer / response.audio.delta JSON envelopes.

import type { IRealtimeService, IRealtimeSession, RealtimeSessionConfig } from "../realtime/index.js";
import type { OpenAiRealtimeOptions } from "./options.js";
import {
  NullRealtimeLogger,
  NullRealtimeTransportFactory,
  type IRealtimeLogger,
  type IRealtimeTransportFactory,
} from "./transport.js";
import { RealtimeWebSocketSession } from "./websocket_session.js";

/** (3.3.0) {@link IRealtimeService} backed by OpenAI Realtime. Mirrors C# `OpenAiRealtimeService`. */
export class OpenAiRealtimeService implements IRealtimeService {
  private readonly options: OpenAiRealtimeOptions;
  private readonly transports: IRealtimeTransportFactory;
  private readonly logger: IRealtimeLogger;

  constructor(
    options: OpenAiRealtimeOptions,
    transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.instance,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    if (options == null) throw new Error("options required");
    this.options = options;
    this.transports = transports ?? NullRealtimeTransportFactory.instance;
    this.logger = logger ?? NullRealtimeLogger.instance;
  }

  get providerId(): string {
    return "openai-realtime";
  }

  get isConfigured(): boolean {
    return !isBlank(this.options.apiKey);
  }

  async startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    this.ensureConfigured();

    const modelToUse = isBlank(config.model) ? this.options.defaultModel : config.model;
    const endpoint = `${this.options.webSocketEndpoint}?model=${encodeURIComponent(modelToUse)}`;

    const headers = new Map<string, string>([
      ["Authorization", `Bearer ${this.options.apiKey}`],
      ["OpenAI-Beta", this.options.betaHeader],
    ]);

    const transport = await this.transports.connectAsync(endpoint, headers, signal);
    return new RealtimeWebSocketSession(transport, config, this.providerId, this.logger);
  }

  private ensureConfigured(): void {
    if (!this.isConfigured) {
      throw new Error(
        "OpenAI Realtime is not configured. Set OpenAiRealtimeOptions.apiKey before calling startSessionAsync.",
      );
    }
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
