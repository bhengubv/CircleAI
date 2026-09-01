/**
 * realtime-cloud/nova_service
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/nova_service.ts
//
// (3.3.0) IRealtimeService backed by AWS Nova Sonic (NovaSonicService.cs). Real
// production use requires SigV4 signing on the WS handshake — surfaced via the
// IRealtimeTransportFactory's headers contract; the host's factory
// implementation performs the signing.

import type { IRealtimeService, IRealtimeSession, RealtimeSessionConfig } from "../realtime/index";
import type { NovaSonicOptions } from "./options";
import {
  NullRealtimeLogger,
  NullRealtimeTransportFactory,
  type IRealtimeLogger,
  type IRealtimeTransportFactory,
} from "./transport";
import { RealtimeWebSocketSession } from "./websocket_session";

/** (3.3.0) {@link IRealtimeService} backed by AWS Nova Sonic. Mirrors C# `NovaSonicService`. */
export class NovaSonicService implements IRealtimeService {
  private readonly options: NovaSonicOptions;
  private readonly transports: IRealtimeTransportFactory;
  private readonly logger: IRealtimeLogger;

  constructor(
    options: NovaSonicOptions,
    transports: IRealtimeTransportFactory = NullRealtimeTransportFactory.instance,
    logger: IRealtimeLogger = NullRealtimeLogger.instance,
  ) {
    if (options == null) throw new Error("options required");
    this.options = options;
    this.transports = transports ?? NullRealtimeTransportFactory.instance;
    this.logger = logger ?? NullRealtimeLogger.instance;
  }

  get providerId(): string {
    return "aws-nova-sonic";
  }

  get isConfigured(): boolean {
    return !isBlank(this.options.accessKeyId) && !isBlank(this.options.secretAccessKey);
  }

  async startSessionAsync(config: RealtimeSessionConfig, signal?: AbortSignal): Promise<IRealtimeSession> {
    if (config == null) throw new Error("config required");
    this.ensureConfigured();

    const endpoint = `wss://bedrock-runtime.${this.options.region}.amazonaws.com/model/${encodeURIComponent(
      config.model,
    )}/invoke-with-bidirectional-stream`;

    // Expose the credentials via headers; the host's transport factory is
    // responsible for SigV4-signing the request.
    const headers = new Map<string, string>([
      ["X-Amz-Access-Key", this.options.accessKeyId as string],
      ["X-Amz-Secret-Key", this.options.secretAccessKey as string],
      ["X-Amz-Region", this.options.region],
    ]);
    if (!isBlank(this.options.sessionToken)) {
      headers.set("X-Amz-Security-Token", this.options.sessionToken as string);
    }

    const transport = await this.transports.connectAsync(endpoint, headers, signal);
    return new RealtimeWebSocketSession(transport, config, this.providerId, this.logger);
  }

  private ensureConfigured(): void {
    if (!this.isConfigured) {
      throw new Error(
        "AWS Nova Sonic is not configured. Set NovaSonicOptions.accessKeyId and secretAccessKey before calling startSessionAsync.",
      );
    }
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
