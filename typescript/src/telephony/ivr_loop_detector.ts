// telephony/ivr_loop_detector.ts
//
// Detect when an outbound call has landed in an IVR loop — repeating prompts,
// looping menus, the AI pressing the same digit over and over. Faithful port of
// IvrLoopDetector.cs. Surfaces a verdict the orchestrator can act on (escalate
// to a human, abandon, or try a different path).

/** One observation in the IVR conversation. Mirrors `IvrRound`. */
export interface IvrRound {
  /** Text heard from the IVR. */
  readonly speech: string;
  /** Digits the AI sent in response, if any. */
  readonly dtmfPressed?: string;
  /** When this round happened. */
  readonly at: Date;
}

/** Constructs an {@link IvrRound}. */
export function ivrRound(speech: string, dtmfPressed: string | undefined, at: Date): IvrRound {
  return { speech, dtmfPressed, at };
}

/** Verdict on IVR navigation health. Mirrors `IvrLoopVerdict`. */
export interface IvrLoopVerdict {
  /** True if the navigator looks stuck. */
  readonly isLooping: boolean;
  /** Estimated length of the repeating cycle (number of rounds). */
  readonly loopLength: number;
  /** Human-readable reason. */
  readonly reason: string;
}

/** Records IVR rounds and surfaces a loop verdict. Mirrors `IvrLoopDetector`. */
export class IvrLoopDetector {
  private readonly rounds: IvrRound[] = [];
  private readonly maxRoundsToTrack: number;
  private readonly minRoundsForLoop: number;
  private readonly similarityThreshold: number;

  constructor(maxRoundsToTrack = 32, minRoundsForLoop = 2, similarityThreshold = 0.85) {
    this.maxRoundsToTrack = maxRoundsToTrack;
    this.minRoundsForLoop = minRoundsForLoop;
    this.similarityThreshold = similarityThreshold;
  }

  /** Append one round and return the current verdict. */
  observe(round: IvrRound): IvrLoopVerdict {
    if (round === null || round === undefined) throw new Error("round is required");
    this.rounds.push(round);
    while (this.rounds.length > this.maxRoundsToTrack) {
      this.rounds.shift();
    }
    return this.evaluate();
  }

  /** Current verdict without adding a new round. */
  currentVerdict(): IvrLoopVerdict {
    return this.evaluate();
  }

  /** Drop all history. */
  reset(): void {
    this.rounds.length = 0;
  }

  private evaluate(): IvrLoopVerdict {
    const n = this.rounds.length;

    // Strong signal first — same DTMF + similar prompt three times in a row.
    if (n >= 3) {
      const tail = this.rounds.slice(n - 3);
      const allSameDtmf = tail.every((r) => r.dtmfPressed === tail[0]!.dtmfPressed);
      const allSimilar = tail.every((r) => this.similarTo(r.speech, tail[0]!.speech));
      if (allSameDtmf && allSimilar) {
        return { isLooping: true, loopLength: 1, reason: "Same prompt-and-press triple in a row." };
      }
    }

    if (n < this.minRoundsForLoop * 2) {
      return { isLooping: false, loopLength: 0, reason: "Not enough rounds to evaluate." };
    }

    // Look for a repeating cycle of length L in the last N rounds.
    for (let L = this.minRoundsForLoop; L <= Math.trunc(n / 2); L++) {
      const tail = this.rounds.slice(n - 2 * L);
      let looped = true;
      for (let i = 0; i < L; i++) {
        if (
          !this.similarTo(tail[i]!.speech, tail[L + i]!.speech) ||
          tail[i]!.dtmfPressed !== tail[L + i]!.dtmfPressed
        ) {
          looped = false;
          break;
        }
      }
      if (looped) {
        return { isLooping: true, loopLength: L, reason: `Detected repeating cycle of length ${L}.` };
      }
    }
    return { isLooping: false, loopLength: 0, reason: "No loop detected." };
  }

  private similarTo(a: string, b: string): boolean {
    if (a === b) return true;
    if (a.toLowerCase() === b.toLowerCase()) return true;
    // C# guards `a is null || b is null`; strings are non-null here by type.
    // Cheap Jaccard over word sets (case-insensitive).
    const setA = new Set(
      a
        .split(" ")
        .filter((w) => w.length > 0)
        .map((w) => w.toLowerCase()),
    );
    const setB = new Set(
      b
        .split(" ")
        .filter((w) => w.length > 0)
        .map((w) => w.toLowerCase()),
    );
    if (setA.size === 0 || setB.size === 0) return false;
    let inter = 0;
    for (const w of setA) {
      if (setB.has(w)) inter++;
    }
    const union = new Set([...setA, ...setB]).size;
    return inter / union >= this.similarityThreshold;
  }
}
