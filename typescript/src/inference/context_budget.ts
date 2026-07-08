// inference/context_budget.ts
//
// Port of CircleAI.Inference.ContextWindowBudgetManager. Tracks token usage
// against a fixed context window and signals when the KV cache should be
// partially evicted to keep inference latency manageable.

/**
 * Tracks token usage against a fixed context window and signals when the KV
 * cache should be partially evicted. Ported verbatim from
 * CircleAI.Inference.ContextWindowBudgetManager.
 */
export class ContextWindowBudgetManager {
  /** Maximum number of tokens the model's context window can hold. */
  readonly contextSize: number;

  /** Fill ratio at or above which shouldEvict becomes true. */
  readonly evictionThreshold: number;

  private usedTokens = 0;

  /**
   * @param contextSize Total context window size in tokens. Must be > 0.
   * @param evictionThreshold Fill ratio (0-1) that triggers eviction. Defaults
   *   to 0.85 (85%).
   */
  constructor(contextSize: number, evictionThreshold = 0.85) {
    if (contextSize <= 0) {
      throw new RangeError("Context size must be greater than zero.");
    }
    if (evictionThreshold < 0.0 || evictionThreshold > 1.0) {
      throw new RangeError("Eviction threshold must be in the range [0, 1].");
    }
    this.contextSize = contextSize;
    this.evictionThreshold = evictionThreshold;
  }

  /** Cumulative tokens consumed so far (prompt + completion). */
  get used(): number {
    return this.usedTokens;
  }

  /** Tokens still available before the context window is full. */
  get remainingTokens(): number {
    return this.contextSize - this.usedTokens;
  }

  /** Proportion of the context window currently occupied (0-1). */
  get fillRatio(): number {
    return this.usedTokens / this.contextSize;
  }

  /** true when the fill ratio has reached or exceeded the eviction threshold. */
  get shouldEvict(): boolean {
    return this.fillRatio >= this.evictionThreshold;
  }

  /**
   * Records the token cost of one exchange (a prompt + its completion).
   */
  recordExchange(promptTokens: number, completionTokens: number): void {
    if (promptTokens < 0) throw new RangeError("Token counts must not be negative.");
    if (completionTokens < 0) throw new RangeError("Token counts must not be negative.");
    this.usedTokens += promptTokens + completionTokens;
  }

  /**
   * Calculates how many of the oldest tokens should be dropped so that
   * fillRatio returns to `targetFillRatio`. Returns 0 when already at or below
   * the target. Ported verbatim (int truncation preserved).
   *
   * @param targetFillRatio Desired fill ratio after eviction. Defaults to 0.50.
   */
  calculateEvictionCount(targetFillRatio = 0.5): number {
    if (targetFillRatio < 0.0 || targetFillRatio > 1.0) {
      throw new RangeError("Target fill ratio must be in the range [0, 1].");
    }
    const targetUsed = Math.trunc(this.contextSize * targetFillRatio);
    const evict = this.usedTokens - targetUsed;
    return evict > 0 ? evict : 0;
  }

  /** Resets the used-token counter to zero. Call after clearing the KV cache. */
  reset(): void {
    this.usedTokens = 0;
  }
}
