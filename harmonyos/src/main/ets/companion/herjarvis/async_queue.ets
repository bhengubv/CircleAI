/**
 * companion/herjarvis/async_queue
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/async_queue.ts
//
// A minimal unbounded async queue that reproduces the exact producer/consumer
// contract of System.Threading.Channels.Channel.CreateUnbounded<T>() as used by
// the HerJarvis stream implementations:
//
//   Writer.TryWrite(item)   -> enqueue(item)
//   Writer.TryComplete()    -> complete()
//   Reader.TryRead(out x)    -> tryDequeue()
//   await Reader.WaitToReadAsync(ct) then drain -> the `drain()` async iterator
//
// The `drain()` iterator yields every currently-buffered item, then awaits the
// next enqueue (or completion / abort), matching the C# pattern:
//
//   while (await reader.WaitToReadAsync(ct))
//       while (reader.TryRead(out var x)) yield return x;

export class AsyncQueue<T> {
  private readonly buffer: T[] = [];
  private completed = false;
  // A pending consumer waiting for the next item / completion.
  private waiter: (() => void) | null = null;

  /** Channel Writer.TryWrite — enqueue an item and wake any waiting consumer. */
  enqueue(item: T): boolean {
    if (this.completed) return false;
    this.buffer.push(item);
    this.wake();
    return true;
  }

  /** Channel Writer.TryComplete — mark the channel completed and wake consumers. */
  complete(): void {
    if (this.completed) return;
    this.completed = true;
    this.wake();
  }

  get isCompleted(): boolean {
    return this.completed;
  }

  /** Channel Reader.TryRead — pull one buffered item, or undefined if empty. */
  tryDequeue(): T | undefined {
    return this.buffer.shift();
  }

  private wake(): void {
    const w = this.waiter;
    if (w !== null) {
      this.waiter = null;
      w();
    }
  }

  /**
   * Drain the queue as an async iterator: yield all buffered items, then await
   * the next enqueue. Terminates when the queue is completed and drained, or
   * when the optional AbortSignal fires (mirroring the C# CancellationToken
   * ending the WaitToReadAsync loop).
   */
  async *drain(signal?: AbortSignal): AsyncGenerator<T> {
    for (;;) {
      // Flush everything currently buffered.
      let item: T | undefined;
      while ((item = this.buffer.shift()) !== undefined) {
        yield item;
      }
      if (this.completed) return;
      if (signal?.aborted) return;

      // Wait for the next enqueue / completion / abort.
      await new Promise<void>((resolve) => {
        if (this.buffer.length > 0 || this.completed) {
          resolve();
          return;
        }
        let settled = false;
        const finish = () => {
          if (settled) return;
          settled = true;
          signal?.removeEventListener("abort", onAbort);
          resolve();
        };
        const onAbort = () => finish();
        this.waiter = finish;
        signal?.addEventListener("abort", onAbort, { once: true });
      });
      if (signal?.aborted) return;
    }
  }
}
