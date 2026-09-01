/**
 * telephony/consult_escalation
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/consult_escalation.ts
//
// Consult escalation — faithful port of ConsultEscalation.cs. AI pauses the
// call, contacts a human expert out-of-band (chat / webhook / phone), conveys
// the question, receives an answer, and reads it back to the caller. Different
// from warm transfer — the caller stays with the AI, the human just answers
// behind the scenes.
//
// HTTP SEAM. The webhook channel uses the injected {@link IHttpClient}. Its
// per-ask timeout links to the caller's signal via {@link LinkedTimeout}; a
// timeout yields `undefined` (as the C# returns null), while a caller-cancel
// propagates.

import type { HttpRequest, IHttpClient } from "./contracts";
import { isSuccessStatusCode } from "./contracts";
import type { ILogger } from "./tool_calling";
import { LinkedTimeout, isCancellation } from "./internal";

/** Question the AI asks a human expert. Mirrors `ConsultRequest`. */
export interface ConsultRequest {
  /** Source call id for the audit trail. */
  readonly callId: string;
  /** Plain-English question text. */
  readonly question: string;
  /** Structured context (caller intent, last few utterances, customer record). */
  readonly contextJson: string;
  /** "normal" / "high". */
  readonly urgency: string;
}

/** Constructs a {@link ConsultRequest} (defaults `urgency` to "normal", matching C#). */
export function consultRequest(
  callId: string,
  question: string,
  contextJson: string,
  urgency = "normal",
): ConsultRequest {
  return { callId, question, contextJson, urgency };
}

/** Human reply. Mirrors `ConsultAnswer`. */
export interface ConsultAnswer {
  readonly answer: string;
  /** true = expert confirmed. */
  readonly confidence: boolean;
  readonly notes?: string;
}

/** Channel for asking a human expert. Mirrors `IConsultChannel`. */
export interface IConsultChannel {
  readonly name: string;
  askAsync(
    request: ConsultRequest,
    timeoutMs: number,
    signal?: AbortSignal,
  ): Promise<ConsultAnswer | undefined>;
}

/**
 * Default escalation driver: try channels in order until one returns within the
 * timeout. Mirrors `ConsultEscalator`.
 */
export class ConsultEscalator {
  private readonly channels: readonly IConsultChannel[];
  private readonly logger?: ILogger;

  constructor(channels: readonly IConsultChannel[], logger?: ILogger) {
    if (channels === null || channels === undefined) throw new Error("channels is required");
    this.channels = channels;
    this.logger = logger;
  }

  /** Walk channels in order; first one to return a non-null answer wins. */
  async escalateAsync(
    request: ConsultRequest,
    timeoutPerChannelMs: number,
    signal?: AbortSignal,
  ): Promise<ConsultAnswer | undefined> {
    if (request === null || request === undefined) throw new Error("request is required");
    for (const channel of this.channels) {
      try {
        const answer = await channel.askAsync(request, timeoutPerChannelMs, signal);
        if (answer !== undefined && answer !== null) {
          return answer;
        }
      } catch (ex) {
        this.logger?.warn(`Consult channel ${channel.name} threw`, ex);
      }
    }
    return undefined;
  }
}

/** HTTP webhook channel — POSTs the request, expects a JSON reply. Mirrors `HttpWebhookConsultChannel`. */
export class HttpWebhookConsultChannel implements IConsultChannel {
  private readonly http: IHttpClient;
  private readonly endpoint: string;
  private readonly channelName: string;

  constructor(http: IHttpClient, endpoint: string, name = "webhook") {
    if (http === null || http === undefined) throw new Error("http is required");
    if (endpoint === null || endpoint === undefined) throw new Error("endpoint is required");
    this.http = http;
    this.endpoint = endpoint;
    this.channelName = name;
  }

  get name(): string {
    return this.channelName;
  }

  async askAsync(
    request: ConsultRequest,
    timeoutMs: number,
    signal?: AbortSignal,
  ): Promise<ConsultAnswer | undefined> {
    const linked = new LinkedTimeout(signal, timeoutMs);
    try {
      const req: HttpRequest = {
        method: "POST",
        url: this.endpoint,
        headers: new Map([["Content-Type", "application/json"]]),
        body: JSON.stringify(request),
      };
      const resp = await this.http.send(req, linked.signal);
      if (!isSuccessStatusCode(resp.statusCode)) return undefined;

      const root = JSON.parse(resp.body) as Record<string, unknown>;
      const answerRaw = root["answer"];
      const answer = typeof answerRaw === "string" ? answerRaw : undefined;
      if (!answer || answer.trim().length === 0) return undefined;
      const confidence = root["confidence"] === true;
      const notesRaw = root["notes"];
      const notes = typeof notesRaw === "string" ? notesRaw : undefined;
      return { answer, confidence, notes };
    } catch (ex) {
      if (isCancellation(ex) && linked.timedOut) {
        return undefined;
      }
      throw ex;
    } finally {
      linked.dispose();
    }
  }
}
