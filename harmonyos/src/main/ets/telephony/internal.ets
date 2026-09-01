/**
 * telephony/internal
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/internal.ts
//
// Small shared helpers used across the telephony port. Mirrors idioms the C#
// relies on that have no direct TS built-in:
//   • CancellationToken.ThrowIfCancellationRequested()  → throwIfAborted
//   • Task.Delay(TimeSpan, ct)                           → delay
//   • DateTimeOffset.UtcNow                              → the injected clock's default
//   • OperationCanceledException                         → OperationCancelledError
//
// C# `lock (_gate)` blocks are elided: JavaScript executes on a single thread,
// so the guarded regions in the source are already atomic between awaits. The
// `Interlocked.*` counters likewise become plain numeric mutation. This is a
// deliberate, faithful mapping — not a stub.

/** Raised when a telephony operation is cancelled via its {@link AbortSignal}. Mirrors `OperationCanceledException`. */
export class OperationCancelledError extends Error {
  constructor(message = "The telephony operation was cancelled.") {
    super(message);
    this.name = "OperationCancelledError";
  }
}

/** Throws {@link OperationCancelledError} if `signal` is already aborted. Mirrors `ThrowIfCancellationRequested`. */
export function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new OperationCancelledError();
}

/**
 * `await`-able delay of `ms` milliseconds that rejects with
 * {@link OperationCancelledError} if `signal` aborts first. Mirrors
 * `Task.Delay(TimeSpan, CancellationToken)`.
 */
export function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new OperationCancelledError());
      return;
    }
    let onAbort: (() => void) | undefined;
    const timer = setTimeout(() => {
      if (onAbort && signal) signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    if (signal) {
      onAbort = () => {
        clearTimeout(timer);
        signal.removeEventListener("abort", onAbort!);
        reject(new OperationCancelledError());
      };
      signal.addEventListener("abort", onAbort, { once: true });
    }
  });
}

/** The default UTC clock (mirrors `() => DateTimeOffset.UtcNow`). */
export function utcNow(): Date {
  return new Date();
}

/**
 * A linked {@link AbortController} that aborts when its own timeout fires OR the
 * parent signal aborts — mirrors
 * `CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token)`.
 * `timedOut` distinguishes a timeout from a parent-cancellation, exactly as the
 * C# checks `timeout.IsCancellationRequested && !ct.IsCancellationRequested`.
 */
export class LinkedTimeout {
  private readonly controller = new AbortController();
  private readonly timer?: ReturnType<typeof setTimeout>;
  private readonly parentAbort?: () => void;
  private _timedOut = false;

  constructor(
    private readonly parent: AbortSignal | undefined,
    timeoutMs?: number,
  ) {
    if (parent?.aborted) {
      this.controller.abort();
    } else if (parent) {
      this.parentAbort = () => this.controller.abort();
      parent.addEventListener("abort", this.parentAbort, { once: true });
    }
    if (timeoutMs !== undefined && timeoutMs >= 0 && !this.controller.signal.aborted) {
      this.timer = setTimeout(() => {
        this._timedOut = true;
        this.controller.abort();
      }, timeoutMs);
    }
  }

  /** The linked signal to pass downstream. */
  get signal(): AbortSignal {
    return this.controller.signal;
  }

  /** True when the timeout fired and the parent did not itself abort. */
  get timedOut(): boolean {
    return this._timedOut && !(this.parent?.aborted ?? false);
  }

  /** Manually trip the linked signal (mirrors `cts.Cancel()`). */
  cancel(): void {
    this.controller.abort();
  }

  /** Release the timer + parent subscription (mirrors `using`/`Dispose`). */
  dispose(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    if (this.parent && this.parentAbort) this.parent.removeEventListener("abort", this.parentAbort);
  }
}

/** True when an error represents a cancellation (native `AbortError` or {@link OperationCancelledError}). */
export function isCancellation(err: unknown): boolean {
  return (
    err instanceof OperationCancelledError ||
    (typeof err === "object" && err !== null && (err as { name?: string }).name === "AbortError")
  );
}

/**
 * An unbounded async queue: one producer writes frames, one consumer drains them
 * via an async iterator. Mirrors `System.Threading.Channels.Channel` used by
 * TestCallSession for inbound audio/DTMF.
 */
export class AsyncChannel<T> {
  private readonly buffer: T[] = [];
  private readonly waiters: Array<(value: IteratorResult<T>) => void> = [];
  private completed = false;

  /** Enqueue one item; returns false once the channel is completed. Mirrors `Writer.TryWrite`. */
  tryWrite(item: T): boolean {
    if (this.completed) return false;
    const waiter = this.waiters.shift();
    if (waiter) {
      waiter({ value: item, done: false });
    } else {
      this.buffer.push(item);
    }
    return true;
  }

  /** Mark the channel complete; pending + future reads drain then end. Mirrors `Writer.TryComplete`. */
  tryComplete(): boolean {
    if (this.completed) return false;
    this.completed = true;
    let waiter = this.waiters.shift();
    while (waiter) {
      waiter({ value: undefined as unknown as T, done: true });
      waiter = this.waiters.shift();
    }
    return true;
  }

  /** Drain the channel as an async iterable (mirrors `Reader.ReadAllAsync`). */
  async *readAllAsync(signal?: AbortSignal): AsyncIterable<T> {
    for (;;) {
      throwIfAborted(signal);
      if (this.buffer.length > 0) {
        yield this.buffer.shift() as T;
        continue;
      }
      if (this.completed) return;
      const next = await new Promise<IteratorResult<T>>((resolve, reject) => {
        let onAbort: (() => void) | undefined;
        const push = (r: IteratorResult<T>): void => {
          if (onAbort && signal) signal.removeEventListener("abort", onAbort);
          resolve(r);
        };
        this.waiters.push(push);
        if (signal) {
          onAbort = () => {
            const idx = this.waiters.indexOf(push);
            if (idx >= 0) this.waiters.splice(idx, 1);
            reject(new OperationCancelledError());
          };
          signal.addEventListener("abort", onAbort, { once: true });
        }
      });
      if (next.done) return;
      yield next.value;
    }
  }
}
