// inference/feedback_training.ts
//
// Port of CircleAI.Inference.FeedbackTrainingQueue (Phase D2) — an append-only
// queue of user-feedback signals the NightlyAdapterTrainer drains into LoRA
// training batches. The C# implementation is disk-backed line-delimited JSON;
// here the disk is an injected ILineStore seam with a deterministic in-memory
// default. Append/drain semantics (FIFO, take-N-then-rewrite-remainder,
// malformed-line skip) are byte-faithful.

/**
 * One feedback-tagged turn that will inform fine-tuning. Ported from
 * CircleAI.Inference.TrainingSample.
 *
 * polarity: +1 (positive) / -1 (negative) / 0 (correction).
 * atUtc: ISO 8601 UTC timestamp (C# used DateTimeOffset).
 */
export interface TrainingSample {
  readonly userText: string;
  readonly assistantText: string;
  readonly preferredText: string;
  readonly polarity: number;
  readonly atUtc: string;
}

/** Ported from CircleAI.Inference.IFeedbackTrainingQueue. */
export interface IFeedbackTrainingQueue {
  enqueue(sample: TrainingSample, signal?: AbortSignal): Promise<void>;
  drain(maxSamples: number, signal?: AbortSignal): Promise<readonly TrainingSample[]>;
  readonly pending: number;
}

/**
 * Line-oriented backing store — abstracts the append-only JSONL file the C#
 * queue uses (File.AppendAllText / File.ReadAllLines / File.WriteAllLines).
 */
export interface ILineStore {
  append(line: string): void;
  readAll(): string[];
  writeAll(lines: string[]): void;
  count(): number;
}

/** Deterministic in-memory JSONL store. Stand-in for the file. */
export class InMemoryLineStore implements ILineStore {
  private lines: string[] = [];

  append(line: string): void {
    this.lines.push(line);
  }

  readAll(): string[] {
    return [...this.lines];
  }

  writeAll(lines: string[]): void {
    this.lines = [...lines];
  }

  count(): number {
    return this.lines.length;
  }
}

/**
 * Append-only line-delimited JSON queue. Ported from
 * CircleAI.Inference.FileBackedFeedbackTrainingQueue over an ILineStore.
 */
export class FeedbackTrainingQueue implements IFeedbackTrainingQueue {
  private readonly store: ILineStore;

  constructor(store?: ILineStore) {
    this.store = store ?? new InMemoryLineStore();
  }

  get pending(): number {
    return this.store.count();
  }

  async enqueue(sample: TrainingSample, _signal?: AbortSignal): Promise<void> {
    if (!sample) throw new Error("sample required");
    const line = JSON.stringify(sample);
    this.store.append(line);
  }

  async drain(maxSamples: number, _signal?: AbortSignal): Promise<readonly TrainingSample[]> {
    if (maxSamples <= 0) throw new RangeError("maxSamples must be > 0");

    const allLines = this.store.readAll();
    const takeCount = Math.min(maxSamples, allLines.length);
    const taken: TrainingSample[] = [];
    for (let i = 0; i < takeCount; i++) {
      try {
        taken.push(JSON.parse(allLines[i]!) as TrainingSample);
      } catch {
        // malformed line skipped — matches C# Debug.WriteLine branch
      }
    }
    const remaining: string[] = [];
    for (let i = takeCount; i < allLines.length; i++) remaining.push(allLines[i]!);
    this.store.writeAll(remaining);
    return taken;
  }
}
