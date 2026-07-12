// telephony/null_impls.ts
//
// No-op fallbacks for the telephony surface — a faithful port of
// NullImplementations.cs. Used when the host hasn't wired a real carrier: test
// runs, dry-runs, or "telephony not configured" composition lines.

import type {
  IInboundCallDispatcher,
  ICallSession,
  ISubscription,
  ITelephonyCarrier,
  OutboundDialOptions,
} from "./contracts.js";
import type { ProvisionedNumber } from "./primitives.js";

/** Null carrier — fail-soft on every operation. Mirrors `NullTelephonyCarrier`. */
export class NullTelephonyCarrier implements ITelephonyCarrier {
  static readonly instance = new NullTelephonyCarrier();

  get carrierId(): string {
    return "null";
  }

  get isConfigured(): boolean {
    return false;
  }

  provisionNumberAsync(
    _countryCode: string,
    _areaCode?: string,
    _signal?: AbortSignal,
  ): Promise<ProvisionedNumber> {
    throw new Error(
      "Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo).",
    );
  }

  configureInboundWebhookAsync(
    _phoneNumber: string,
    _inboundWebhook: string,
    _signal?: AbortSignal,
  ): Promise<void> {
    return Promise.resolve();
  }

  dialAsync(
    _fromNumber: string,
    _toNumber: string,
    _streamUrl: string,
    _options?: OutboundDialOptions,
    _signal?: AbortSignal,
  ): Promise<ICallSession> {
    throw new Error("Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.");
  }

  listNumbersAsync(_signal?: AbortSignal): Promise<readonly ProvisionedNumber[]> {
    return Promise.resolve([]);
  }
}

/** Null inbound dispatcher — never fires. Mirrors `NullInboundCallDispatcher`. */
export class NullInboundCallDispatcher implements IInboundCallDispatcher {
  static readonly instance = new NullInboundCallDispatcher();

  get carrierId(): string {
    return "null";
  }

  subscribe(_handler: (session: ICallSession) => Promise<void>): ISubscription {
    return NoopSubscription.instance;
  }
}

/** A subscription that does nothing on dispose. */
class NoopSubscription implements ISubscription {
  static readonly instance = new NoopSubscription();
  dispose(): void {
    /* no-op */
  }
}
