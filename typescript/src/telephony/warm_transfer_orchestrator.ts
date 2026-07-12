// telephony/warm_transfer_orchestrator.ts
//
// Warm call transfer — faithful port of WarmTransferOrchestrator.cs. Park
// caller, dial target, speak the briefing to target via TTS, then bridge by
// issuing a cold transfer of the caller leg to the target. The AI's bridge-leg
// call ends once the caller is connected.
//
// The C# `ILogger` (defaulting to `NullLogger`) is injected as an optional
// {@link ILogger} — omit for the null-logger behaviour.

import type { BriefingSynthesiser, ICallSession, ITelephonyCarrier } from "./contracts.js";
import type { ILogger } from "./tool_calling.js";
import { audioFrame, CallMediaFormat, TransferMode } from "./primitives.js";

/** One warm-transfer request. Mirrors `WarmTransferRequest`. */
export interface WarmTransferRequest {
  /** The active call we want to transfer. */
  readonly sourceSession: ICallSession;
  /** E.164 number of the person we're transferring to. */
  readonly targetNumber: string;
  /** What the AI should say to the target before the bridge. */
  readonly briefingText: string;
  /** WSS endpoint the carrier will hand the target leg to. */
  readonly bridgeStreamUrl: string;
}

/** Outcome of a warm transfer. Mirrors `WarmTransferResult`. */
export interface WarmTransferResult {
  readonly succeeded: boolean;
  readonly failureReason?: string;
  readonly bridgeSession?: ICallSession;
}

/** Park caller, dial target, brief, bridge. Mirrors `IWarmTransferOrchestrator`. */
export interface IWarmTransferOrchestrator {
  executeAsync(request: WarmTransferRequest, signal?: AbortSignal): Promise<WarmTransferResult>;
}

/** Carrier-agnostic warm-transfer driver. Mirrors `DefaultWarmTransferOrchestrator`. */
export class DefaultWarmTransferOrchestrator implements IWarmTransferOrchestrator {
  private readonly carrier: ITelephonyCarrier;
  private readonly briefingTts: BriefingSynthesiser;
  private readonly logger?: ILogger;

  constructor(carrier: ITelephonyCarrier, briefingTts: BriefingSynthesiser, logger?: ILogger) {
    if (carrier === null || carrier === undefined) throw new Error("carrier is required");
    if (briefingTts === null || briefingTts === undefined) throw new Error("briefingTts is required");
    this.carrier = carrier;
    this.briefingTts = briefingTts;
    this.logger = logger;
  }

  async executeAsync(
    request: WarmTransferRequest,
    signal?: AbortSignal,
  ): Promise<WarmTransferResult> {
    if (request === null || request === undefined) throw new Error("request is required");
    if (request.sourceSession === null || request.sourceSession === undefined) {
      return { succeeded: false, failureReason: "SourceSession is required" };
    }
    if (!request.targetNumber || request.targetNumber.trim().length === 0) {
      return { succeeded: false, failureReason: "TargetNumber is required" };
    }

    // 1) Dial target on a fresh leg.
    let bridgeLeg: ICallSession;
    try {
      bridgeLeg = await this.carrier.dialAsync(
        request.sourceSession.info.to,
        request.targetNumber,
        request.bridgeStreamUrl,
        undefined,
        signal,
      );
    } catch (ex) {
      this.logger?.warn(`Warm-transfer dial to ${request.targetNumber} failed`, ex);
      return {
        succeeded: false,
        failureReason: `Failed to dial target: ${ex instanceof Error ? ex.message : String(ex)}`,
      };
    }

    // 2) Speak briefing to target.
    try {
      const briefingAudio = await this.briefingTts(request.briefingText, signal);
      if (briefingAudio.length > 0) {
        await bridgeLeg.sendAudioAsync(
          audioFrame(briefingAudio, CallMediaFormat.Pcm24000, 0),
          signal,
        );
      }
    } catch (ex) {
      this.logger?.warn("Warm-transfer briefing failed; hanging up bridge leg", ex);
      await bridgeLeg.hangUpAsync(signal);
      return {
        succeeded: false,
        failureReason: `Failed to brief target: ${ex instanceof Error ? ex.message : String(ex)}`,
      };
    }

    // 3) Hand caller off to target — this is the bridge moment.
    try {
      await request.sourceSession.transferAsync(
        request.targetNumber,
        TransferMode.Cold,
        undefined,
        signal,
      );
    } catch (ex) {
      this.logger?.warn("Warm-transfer bridge step failed", ex);
      await bridgeLeg.hangUpAsync(signal);
      return {
        succeeded: false,
        failureReason: `Failed to bridge caller: ${ex instanceof Error ? ex.message : String(ex)}`,
      };
    }

    // 4) AI leg ends; caller and target stay connected.
    await bridgeLeg.hangUpAsync(signal);
    return { succeeded: true, bridgeSession: bridgeLeg };
  }
}
