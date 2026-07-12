// telephony/speculative_generator.ts
//
// Speculative generation — faithful port of SpeculativeGenerator.cs. While the
// user is still speaking, start generating a draft response from the partial
// transcript. If the user keeps talking we discard and restart with the new
// partial; when they finish we use whichever speculative branch is closest.
// Cuts time-to-first-token by ~300-600 ms.
//
// The in-flight generation Task → a `Promise<string>`. The per-branch
// CancellationTokenSource → an AbortController we abort when a branch is
// superseded. Superseded-branch rejections are swallowed exactly as the C#
// swallows `OperationCanceledException`.

import { isCancellation, utcNow } from "./internal.js";

/** One in-flight speculative branch. Mirrors `SpeculativeBranch`. */
export interface SpeculativeBranch {
  readonly partialTranscript: string;
  readonly responseTask: Promise<string>;
  readonly startedAt: Date;
}

/** Function that drives a response generation given a partial transcript. Mirrors the `ResponseGenerator` delegate. */
export type ResponseGenerator = (transcript: string, signal?: AbortSignal) => Promise<string>;

/** Manages speculative-generation branches. Mirrors `ISpeculativeGenerator`. */
export interface ISpeculativeGenerator {
  /** The branch currently considered most likely to commit. */
  readonly activeBranch: SpeculativeBranch | null;

  /** Start (or restart) the speculative branch using `partialTranscript`. */
  speculate(partialTranscript: string, generator: ResponseGenerator): void;

  /** Commit to a final transcript and return the matching response. */
  commitAsync(
    finalTranscript: string,
    generator: ResponseGenerator,
    signal?: AbortSignal,
  ): Promise<string>;

  /** Abort any active speculation. */
  abort(): void;
}

function startsWithCi(value: string, prefix: string): boolean {
  return value.toLowerCase().startsWith(prefix.toLowerCase());
}
function equalsCi(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

/** Default driver. Cancels older branches when the partial diverges. Mirrors `DefaultSpeculativeGenerator`. */
export class DefaultSpeculativeGenerator implements ISpeculativeGenerator {
  private active: SpeculativeBranch | null = null;
  private activeController: AbortController | null = null;
  private readonly clock: () => Date;
  private readonly minPartialLength: number;

  constructor(clock?: () => Date, minPartialLength = 8) {
    this.clock = clock ?? utcNow;
    this.minPartialLength = minPartialLength;
  }

  get activeBranch(): SpeculativeBranch | null {
    return this.active;
  }

  speculate(partialTranscript: string, generator: ResponseGenerator): void {
    if (generator === null || generator === undefined) throw new Error("generator is required");
    if (!partialTranscript || partialTranscript.trim().length === 0) return;
    if (partialTranscript.length < this.minPartialLength) return;

    // If the new partial is just an extension of the active one, keep it.
    if (this.active !== null && startsWithCi(partialTranscript, this.active.partialTranscript)) {
      return;
    }
    const toCancel = this.activeController;
    const controller = new AbortController();
    this.activeController = controller;
    const task = generator(partialTranscript, controller.signal);
    // Prevent unhandled-rejection noise for branches that get superseded/aborted.
    task.catch(() => undefined);
    this.active = { partialTranscript, responseTask: task, startedAt: this.clock() };

    toCancel?.abort();
  }

  async commitAsync(
    finalTranscript: string,
    generator: ResponseGenerator,
    signal?: AbortSignal,
  ): Promise<string> {
    if (generator === null || generator === undefined) throw new Error("generator is required");
    if (!finalTranscript || finalTranscript.trim().length === 0) return "";

    const active = this.active;

    if (active !== null && startsWithCi(finalTranscript, active.partialTranscript)) {
      try {
        const draft = await active.responseTask;
        if (equalsCi(finalTranscript, active.partialTranscript)) {
          return draft;
        }
        // Final extended the partial — finalize via a fresh generation.
        // (For our contract: re-run with full transcript.)
      } catch (ex) {
        if (!isCancellation(ex)) {
          /* swallow draft errors — fall through to fresh generation */
        }
      }
    }

    // No usable speculative draft — generate fresh.
    const toCancel = this.activeController;
    this.activeController = null;
    this.active = null;
    toCancel?.abort();

    return generator(finalTranscript, signal);
  }

  abort(): void {
    const toCancel = this.activeController;
    this.activeController = null;
    this.active = null;
    toCancel?.abort();
  }
}
