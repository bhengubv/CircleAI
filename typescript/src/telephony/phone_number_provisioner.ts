// telephony/phone_number_provisioner.ts
//
// Orchestrates the "buy + configure + persist" loop across any carrier that
// implements ITelephonyCarrier — faithful port of PhoneNumberProvisioner.cs.
// Single call: pick a country, supply your inbound webhook, get back a
// ProvisionedNumber that's ready to take calls.
//
// The C# `ILogger` (default `NullLogger`) is an optional {@link ILogger}. `Uri`
// → an absolute URL string, validated with the URL constructor.

import type { ITelephonyCarrier } from "./contracts.js";
import type { ILogger } from "./tool_calling.js";
import type { ProvisionedNumber } from "./primitives.js";

function isAbsoluteUrl(url: string): boolean {
  try {
    // eslint-disable-next-line no-new
    new URL(url);
    return true;
  } catch {
    return false;
  }
}

/**
 * Persistence contract for assigned numbers. Default in-memory implementation is
 * fine for dev; production hosts should plug in a database-backed store. Mirrors
 * `IProvisionedNumberStore`.
 */
export interface IProvisionedNumberStore {
  saveAsync(num: ProvisionedNumber, signal?: AbortSignal): Promise<void>;
  listAsync(signal?: AbortSignal): Promise<readonly ProvisionedNumber[]>;
  findAsync(phoneNumber: string, signal?: AbortSignal): Promise<ProvisionedNumber | undefined>;
  removeAsync(phoneNumber: string, signal?: AbortSignal): Promise<void>;
}

/** Default in-memory store. Mirrors `InMemoryProvisionedNumberStore`. */
export class InMemoryProvisionedNumberStore implements IProvisionedNumberStore {
  private readonly byNumber = new Map<string, ProvisionedNumber>(); // key: lowercased phone number

  saveAsync(num: ProvisionedNumber, _signal?: AbortSignal): Promise<void> {
    if (num === null || num === undefined) throw new Error("number is required");
    this.byNumber.set(num.phoneNumber.toLowerCase(), num);
    return Promise.resolve();
  }

  listAsync(_signal?: AbortSignal): Promise<readonly ProvisionedNumber[]> {
    return Promise.resolve([...this.byNumber.values()]);
  }

  findAsync(phoneNumber: string, _signal?: AbortSignal): Promise<ProvisionedNumber | undefined> {
    return Promise.resolve(this.byNumber.get(phoneNumber.toLowerCase()));
  }

  removeAsync(phoneNumber: string, _signal?: AbortSignal): Promise<void> {
    this.byNumber.delete(phoneNumber.toLowerCase());
    return Promise.resolve();
  }
}

/**
 * Service that buys + configures + persists phone numbers from any carrier
 * behind {@link ITelephonyCarrier}. Mirrors `PhoneNumberProvisioner`.
 */
export class PhoneNumberProvisioner {
  private readonly carrier: ITelephonyCarrier;
  private readonly store: IProvisionedNumberStore;
  private readonly logger?: ILogger;

  constructor(carrier: ITelephonyCarrier, store?: IProvisionedNumberStore, logger?: ILogger) {
    if (carrier === null || carrier === undefined) throw new Error("carrier is required");
    this.carrier = carrier;
    this.store = store ?? new InMemoryProvisionedNumberStore();
    this.logger = logger;
  }

  /**
   * Buy a number, wire its inbound webhook, persist it, return the metadata.
   * @param countryCode ISO country code (e.g. "US", "ZA", "NG").
   * @param inboundWebhook HTTPS URL the carrier will hit when the number rings.
   * @param areaCode Optional area code / prefix preference.
   */
  async provisionAsync(
    countryCode: string,
    inboundWebhook: string,
    areaCode?: string,
    signal?: AbortSignal,
  ): Promise<ProvisionedNumber> {
    if (!countryCode || countryCode.trim().length === 0) {
      throw new Error("countryCode is required");
    }
    if (inboundWebhook === null || inboundWebhook === undefined) {
      throw new Error("inboundWebhook is required");
    }
    if (!isAbsoluteUrl(inboundWebhook)) {
      throw new Error("inboundWebhook must be an absolute URI");
    }

    const provisioned = await this.carrier.provisionNumberAsync(countryCode, areaCode, signal);

    try {
      await this.carrier.configureInboundWebhookAsync(provisioned.phoneNumber, inboundWebhook, signal);
    } catch (ex) {
      this.logger?.warn(
        `Webhook configuration failed for ${provisioned.phoneNumber} on ${this.carrier.carrierId}`,
        ex,
      );
      throw ex;
    }

    await this.store.saveAsync(provisioned, signal);
    return provisioned;
  }

  /** The provisioned numbers we know about, locally + via the carrier. */
  async listAsync(signal?: AbortSignal): Promise<readonly ProvisionedNumber[]> {
    const stored = await this.store.listAsync(signal);
    // Merge with carrier authoritative list — store may be stale.
    const carrierNumbers = await this.carrier.listNumbersAsync(signal);
    const merged = new Map<string, ProvisionedNumber>(); // key: lowercased phone number
    for (const n of stored) merged.set(n.phoneNumber.toLowerCase(), n);
    for (const n of carrierNumbers) merged.set(n.phoneNumber.toLowerCase(), n);
    return [...merged.values()];
  }
}
