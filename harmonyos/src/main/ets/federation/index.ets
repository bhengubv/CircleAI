/**
 * federation/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// federation/index.ts
// Full-parity port of CircleAI.Federation (C#). C# is the exact spec.
//
// Federated-learning primitives: ModelDelta / FederationRound records, the
// RoundStatus / DeltaDispatchOutcome enums, the IFederationParticipant /
// IFederationAggregator / IFederationDeltaDispatcher contracts, the
// sample-size-weighted FederatedAveraging over little-endian IEEE-754 float
// payloads, and the in-process InMemoryFederationAggregator.
//
// Type mappings (C# → TS):
//   Guid Id / RoundId               → string (UUID v4)
//   byte[] DeltaPayload / Signature → Uint8Array
//   int SampleCount                 → number
//   DateTimeOffset                  → Date
//   BinaryPrimitives.Read/WriteSingleLittleEndian → DataView.get/setFloat32(le)
//   (float) cast on write           → Math.fround(...) then setFloat32
//   Task<byte[]?> TryCommitAsync    → Promise<Uint8Array | null>
//
// NOTE: The C# InMemoryFederationAggregator derives from CircleAIComponentBase
// and wraps each operation in RunOperationAsync (an audit/try wrapper that
// returns the inner result unchanged). That wrapper has no behavioural effect
// on the contract, so this port runs each operation directly and keeps the
// `componentName` for parity — mirroring how the other CircleAI.* boards port
// the CircleAIComponentBase pattern.

const FLOAT_BYTES = 4;

/**
 * One participant's signed contribution to a federation round. Mirrors C#
 * `ModelDelta` record. NO raw training data ever leaves the device — only the
 * delta payload.
 */
export interface ModelDelta {
  readonly id: string;
  readonly roundId: string;
  readonly contributorUhid: string;
  readonly modelId: string;
  readonly fromVersion: string;
  readonly deltaPayload: Uint8Array;
  readonly sampleCount: number;
  readonly signature: Uint8Array;
  readonly submittedAt: Date;
}

/** Constructs a {@link ModelDelta}. */
export function modelDelta(
  id: string,
  roundId: string,
  contributorUhid: string,
  modelId: string,
  fromVersion: string,
  deltaPayload: Uint8Array,
  sampleCount: number,
  signature: Uint8Array,
  submittedAt: Date,
): ModelDelta {
  return {
    id,
    roundId,
    contributorUhid,
    modelId,
    fromVersion,
    deltaPayload,
    sampleCount,
    signature,
    submittedAt,
  };
}

/** Lifecycle state of a {@link FederationRound}. Mirrors C# `RoundStatus`. */
export enum RoundStatus {
  Open = 0,
  Aggregating = 1,
  Committed = 2,
  Aborted = 3,
}

/**
 * One coordinated round of federated learning, bound to a specific model
 * version transition. Mirrors C# `FederationRound` record.
 */
export interface FederationRound {
  readonly id: string;
  readonly modelId: string;
  readonly fromVersion: string;
  readonly toVersion: string;
  readonly minParticipants: number;
  readonly maxParticipants: number;
  readonly currentParticipantCount: number;
  readonly status: RoundStatus;
  readonly openedAt: Date;
  readonly committedAt: Date | null;
}

/** Constructs a {@link FederationRound}. */
export function federationRound(
  id: string,
  modelId: string,
  fromVersion: string,
  toVersion: string,
  minParticipants: number,
  maxParticipants: number,
  currentParticipantCount: number,
  status: RoundStatus,
  openedAt: Date,
  committedAt: Date | null,
): FederationRound {
  return {
    id,
    modelId,
    fromVersion,
    toVersion,
    minParticipants,
    maxParticipants,
    currentParticipantCount,
    status,
    openedAt,
    committedAt,
  };
}

/** Outcome of a {@link IFederationDeltaDispatcher.verifyAndSubmitAsync} call. Mirrors C# `DeltaDispatchOutcome`. */
export enum DeltaDispatchOutcome {
  Accepted = 0,
  SignatureInvalid = 1,
  Duplicate = 2,
  RoundUnknown = 3,
  RoundClosed = 4,
}

/** A device that contributes to federation rounds. Mirrors C# `IFederationParticipant`. */
export interface IFederationParticipant {
  produceDeltaAsync(round: FederationRound, signal?: AbortSignal): Promise<ModelDelta>;
  applyAggregatedModelAsync(
    modelId: string,
    newVersion: string,
    aggregatedPayload: Uint8Array,
    signal?: AbortSignal,
  ): Promise<boolean>;
}

/** Coordinator for federation rounds. Mirrors C# `IFederationAggregator`. */
export interface IFederationAggregator {
  openRoundAsync(
    modelId: string,
    fromVersion: string,
    toVersion: string,
    minParticipants: number,
    maxParticipants: number,
    signal?: AbortSignal,
  ): Promise<FederationRound>;
  submitDeltaAsync(delta: ModelDelta, signal?: AbortSignal): Promise<void>;
  tryCommitAsync(roundId: string, signal?: AbortSignal): Promise<Uint8Array | null>;
  getRoundAsync(roundId: string, signal?: AbortSignal): Promise<FederationRound>;
}

/** Safe-by-default (verify + dedup + submit) delta dispatcher. Mirrors C# `IFederationDeltaDispatcher`. */
export interface IFederationDeltaDispatcher {
  verifyAndSubmitAsync(delta: ModelDelta, signal?: AbortSignal): Promise<DeltaDispatchOutcome>;
}

// ─────────────────────────────────────────────────────────────────────────────
// FederatedAveraging
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Sample-size-weighted averaging over {@link ModelDelta.deltaPayload} arrays
 * interpreted as little-endian IEEE-754 `float[]`. Mirrors C# `FederatedAveraging`.
 */
export const FederatedAveraging = {
  /**
   * Computes the sample-size-weighted average of the deltas and returns the
   * encoded result as little-endian IEEE-754 bytes.
   */
  average(deltas: readonly ModelDelta[]): Uint8Array {
    if (deltas == null) throw new Error("deltas required");
    if (deltas.length === 0) throw new Error("Cannot average an empty delta list.");

    const expectedBytes = deltas[0].deltaPayload.length;
    if (expectedBytes === 0) throw new Error("Delta payloads must be non-empty.");
    if (expectedBytes % FLOAT_BYTES !== 0) {
      throw new Error(
        `Delta payload length (${expectedBytes}) must be a multiple of ${FLOAT_BYTES} bytes.`,
      );
    }

    for (let i = 1; i < deltas.length; i++) {
      if (deltas[i].deltaPayload.length !== expectedBytes) {
        throw new Error(
          `Delta payload length mismatch: index 0 = ${expectedBytes} bytes, ` +
            `index ${i} = ${deltas[i].deltaPayload.length} bytes.`,
        );
      }
    }

    const floatCount = expectedBytes / FLOAT_BYTES;
    let totalSamples = 0;
    for (const d of deltas) {
      if (d.sampleCount < 0) {
        throw new Error(
          `SampleCount must be non-negative; delta ${d.id} reported ${d.sampleCount}.`,
        );
      }
      totalSamples += d.sampleCount;
    }
    if (totalSamples === 0) {
      throw new Error(
        "Total sample weight across deltas is zero — cannot perform weighted average.",
      );
    }

    // Accumulate in double precision (JS number is IEEE-754 double).
    const accumulator = new Float64Array(floatCount);
    for (const d of deltas) {
      const weight = d.sampleCount / totalSamples;
      const view = new DataView(d.deltaPayload.buffer, d.deltaPayload.byteOffset, d.deltaPayload.byteLength);
      for (let i = 0; i < floatCount; i++) {
        const value = view.getFloat32(i * FLOAT_BYTES, true);
        accumulator[i] += value * weight;
      }
    }

    const output = new Uint8Array(expectedBytes);
    const outView = new DataView(output.buffer);
    for (let i = 0; i < floatCount; i++) {
      // C# casts the double accumulator to float on write.
      outView.setFloat32(i * FLOAT_BYTES, Math.fround(accumulator[i]), true);
    }
    return output;
  },

  /** Encodes a float array as little-endian IEEE-754 bytes. Mirrors C# `EncodeFloats`. */
  encodeFloats(values: readonly number[]): Uint8Array {
    if (values == null) throw new Error("values required");
    const output = new Uint8Array(values.length * FLOAT_BYTES);
    const view = new DataView(output.buffer);
    for (let i = 0; i < values.length; i++) {
      view.setFloat32(i * FLOAT_BYTES, Math.fround(values[i]), true);
    }
    return output;
  },

  /** Decodes little-endian IEEE-754 bytes into a float array. Mirrors C# `DecodeFloats`. */
  decodeFloats(payload: Uint8Array): number[] {
    if (payload == null) throw new Error("payload required");
    if (payload.length % FLOAT_BYTES !== 0) {
      throw new Error(
        `Payload length (${payload.length}) must be a multiple of ${FLOAT_BYTES} bytes.`,
      );
    }
    const count = payload.length / FLOAT_BYTES;
    const output = new Array<number>(count);
    const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    for (let i = 0; i < count; i++) {
      output[i] = view.getFloat32(i * FLOAT_BYTES, true);
    }
    return output;
  },
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryFederationAggregator
// ─────────────────────────────────────────────────────────────────────────────

interface RoundState {
  snapshot: FederationRound;
  readonly deltas: ModelDelta[];
  committedPayload: Uint8Array | null;
}

/**
 * In-process reference {@link IFederationAggregator}. Stores all round + delta
 * state in memory and performs sample-size-weighted averaging on commit.
 * Signature verification is delegated to a caller-supplied validator. Mirrors
 * C# `InMemoryFederationAggregator`.
 */
export class InMemoryFederationAggregator implements IFederationAggregator {
  readonly componentName = "InMemoryFederationAggregator";
  private readonly rounds = new Map<string, RoundState>();
  private readonly signatureValidator: (delta: ModelDelta) => boolean;

  /**
   * @param signatureValidator Returns true when the delta's signature is valid.
   *   Pass `() => true` in tests where signatures are not under test. Deltas
   *   whose validator returns false are dropped at commit time.
   */
  constructor(signatureValidator: (delta: ModelDelta) => boolean) {
    if (signatureValidator == null) throw new Error("signatureValidator required");
    this.signatureValidator = signatureValidator;
  }

  async openRoundAsync(
    modelId: string,
    fromVersion: string,
    toVersion: string,
    minParticipants: number,
    maxParticipants: number,
    signal?: AbortSignal,
  ): Promise<FederationRound> {
    if (isEmpty(modelId)) throw new Error("modelId required");
    if (isEmpty(fromVersion)) throw new Error("fromVersion required");
    if (isEmpty(toVersion)) throw new Error("toVersion required");
    if (minParticipants <= 0) throw new Error("minParticipants must be positive.");
    if (maxParticipants < minParticipants) {
      throw new Error(
        `maxParticipants (${maxParticipants}) must be >= minParticipants (${minParticipants}).`,
      );
    }
    throwIfAborted(signal);

    const round = federationRound(
      crypto.randomUUID(),
      modelId,
      fromVersion,
      toVersion,
      minParticipants,
      maxParticipants,
      0,
      RoundStatus.Open,
      new Date(),
      null,
    );
    this.rounds.set(round.id, { snapshot: round, deltas: [], committedPayload: null });
    return round;
  }

  async submitDeltaAsync(delta: ModelDelta, signal?: AbortSignal): Promise<void> {
    if (delta == null) throw new Error("delta required");
    throwIfAborted(signal);

    const state = this.rounds.get(delta.roundId);
    if (state === undefined) {
      throw new KeyNotFoundError(`Round ${delta.roundId} is not open.`);
    }

    if (delta.deltaPayload.length === 0) {
      // Empty payloads are ignored (not stored, not counted) — the round stays viable.
      return;
    }

    if (state.snapshot.status !== RoundStatus.Open) {
      throw new Error(`Round ${delta.roundId} is ${RoundStatus[state.snapshot.status]}; not accepting deltas.`);
    }
    if (state.deltas.length >= state.snapshot.maxParticipants) {
      throw new Error(`Round ${delta.roundId} has reached MaxParticipants (${state.snapshot.maxParticipants}).`);
    }

    state.deltas.push(delta);
    state.snapshot = { ...state.snapshot, currentParticipantCount: state.deltas.length };
  }

  tryCommitAsync(roundId: string, signal?: AbortSignal): Promise<Uint8Array | null> {
    throwIfAborted(signal);
    const state = this.rounds.get(roundId);
    if (state === undefined) {
      throw new KeyNotFoundError(`Round ${roundId} is unknown.`);
    }

    if (state.snapshot.status === RoundStatus.Committed) {
      // Idempotent: re-return the previously committed payload.
      return Promise.resolve(state.committedPayload);
    }
    if (state.snapshot.status === RoundStatus.Aborted) {
      return Promise.resolve(null);
    }

    const validDeltas = state.deltas.filter((d) => this.signatureValidator(d));
    if (validDeltas.length < state.snapshot.minParticipants) {
      return Promise.resolve(null);
    }

    state.snapshot = { ...state.snapshot, status: RoundStatus.Aggregating };

    let aggregated: Uint8Array;
    try {
      aggregated = FederatedAveraging.average(validDeltas);
    } catch {
      // Payload encoding inconsistent — fall back to the median delta by SampleCount.
      aggregated = fallbackMedianPayload(validDeltas);
    }

    state.committedPayload = aggregated;
    state.snapshot = { ...state.snapshot, status: RoundStatus.Committed, committedAt: new Date() };
    return Promise.resolve(aggregated);
  }

  getRoundAsync(roundId: string, signal?: AbortSignal): Promise<FederationRound> {
    throwIfAborted(signal);
    const state = this.rounds.get(roundId);
    if (state === undefined) {
      throw new KeyNotFoundError(`Round ${roundId} is unknown.`);
    }
    return Promise.resolve(state.snapshot);
  }

  /** Total number of rounds currently tracked. Diagnostic only. Mirrors C# `RoundCount`. */
  get roundCount(): number {
    return this.rounds.size;
  }
}

/** Thrown when a round id is unknown. Mirrors C# `KeyNotFoundException`. */
export class KeyNotFoundError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "KeyNotFoundError";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// DefaultFederationDeltaDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Reference {@link IFederationDeltaDispatcher}. Composes signature verification,
 * replay de-duplication, and submission over an {@link IFederationAggregator} in
 * a single call so no step can be skipped. No exception is thrown on rejection —
 * the caller branches on the returned {@link DeltaDispatchOutcome}. Mirrors C#
 * `DefaultFederationDeltaDispatcher`.
 */
export class DefaultFederationDeltaDispatcher implements IFederationDeltaDispatcher {
  private readonly aggregator: IFederationAggregator;
  private readonly signatureValidator: (delta: ModelDelta) => boolean;
  private readonly seen = new Set<string>();

  /**
   * @param aggregator The round coordinator deltas are submitted to.
   * @param signatureValidator Returns true when the delta's signature verifies
   *   against the contributor's UHID key. Pass `() => true` only in tests where
   *   signatures are not the subject of test.
   */
  constructor(aggregator: IFederationAggregator, signatureValidator: (delta: ModelDelta) => boolean) {
    if (aggregator == null) throw new Error("aggregator required");
    if (signatureValidator == null) throw new Error("signatureValidator required");
    this.aggregator = aggregator;
    this.signatureValidator = signatureValidator;
  }

  /**
   * Verify the delta's signature, check it has not already been recorded, and
   * submit it. Mirrors C# `VerifyAndSubmitAsync`.
   */
  async verifyAndSubmitAsync(delta: ModelDelta, signal?: AbortSignal): Promise<DeltaDispatchOutcome> {
    if (delta == null) throw new Error("delta required");

    // 1. Verify the signature first — a forged or unsigned delta never touches the round.
    if (!this.signatureValidator(delta)) {
      return DeltaDispatchOutcome.SignatureInvalid;
    }

    // 2. De-duplicate: claim the delta id; a replay of the same id loses.
    if (this.seen.has(delta.id)) {
      return DeltaDispatchOutcome.Duplicate;
    }
    this.seen.add(delta.id);

    // 3. Submit, translating the aggregator's exceptions into outcomes so the
    //    caller can branch on the result without a try/catch of its own.
    try {
      await this.aggregator.submitDeltaAsync(delta, signal);
      return DeltaDispatchOutcome.Accepted;
    } catch (e) {
      this.seen.delete(delta.id);
      // Unknown round id (C# KeyNotFoundException) vs round-closed / max-participants
      // (C# InvalidOperationException, thrown here as a plain Error).
      return e instanceof KeyNotFoundError ? DeltaDispatchOutcome.RoundUnknown : DeltaDispatchOutcome.RoundClosed;
    }
  }
}

function fallbackMedianPayload(deltas: readonly ModelDelta[]): Uint8Array {
  const ordered = [...deltas].sort((a, b) => a.sampleCount - b.sampleCount);
  const median = ordered[Math.floor(ordered.length / 2)];
  return Uint8Array.from(median.deltaPayload);
}

function isEmpty(s: string | null | undefined): boolean {
  return s == null || s.length === 0;
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new Error("operation aborted");
}
