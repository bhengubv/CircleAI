/**
 * telephony/llm_judge
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/llm_judge.ts
//
// LLM-as-judge — faithful port of LlmJudge.cs. An LLM scores another LLM's reply
// against a rubric. Used in EvalSession to grade responses on dimensions like
// "policy compliance", "tone match", "factual accuracy".
//
// `System.Text.Json` parsing → JSON.parse over the tolerant-extracted object.
// The `JudgeCompletion` delegate is injected (the actual model call is a seam).

/** One scoring dimension. Mirrors `JudgeDimension`. */
export interface JudgeDimension {
  /** Display name. */
  readonly name: string;
  /** Plain-English rubric the judge sees. */
  readonly description: string;
}

/** Constructs a {@link JudgeDimension}. */
export function judgeDimension(name: string, description: string): JudgeDimension {
  return { name, description };
}

/** Result of one judging call. Mirrors `JudgeVerdict`. */
export interface JudgeVerdict {
  /** 0..10 per dimension. */
  readonly scores: ReadonlyMap<string, number>;
  /** pass / borderline / fail. */
  readonly overall: string;
  readonly reasoning: string;
}

/** Delegate that asks the actual LLM to grade. Mirrors the `JudgeCompletion` delegate. */
export type JudgeCompletion = (prompt: string, signal?: AbortSignal) => Promise<string>;

/** LLM-as-judge driver. Mirrors `LlmJudge`. */
export class LlmJudge {
  private readonly completion: JudgeCompletion;

  constructor(completion: JudgeCompletion) {
    if (completion === null || completion === undefined) throw new Error("completion is required");
    this.completion = completion;
  }

  /** Build the rubric prompt, ask the judge, parse JSON, return the verdict. */
  async judgeAsync(
    userUtterance: string,
    assistantResponse: string,
    dimensions: readonly JudgeDimension[],
    signal?: AbortSignal,
  ): Promise<JudgeVerdict> {
    if (userUtterance === null || userUtterance === undefined) {
      throw new Error("userUtterance is required");
    }
    if (assistantResponse === null || assistantResponse === undefined) {
      throw new Error("assistantResponse is required");
    }
    if (dimensions === null || dimensions === undefined) throw new Error("dimensions is required");

    const prompt = LlmJudge.buildPrompt(userUtterance, assistantResponse, dimensions);
    const raw = await this.completion(prompt, signal);
    return LlmJudge.parseVerdict(raw, dimensions);
  }

  private static buildPrompt(
    user: string,
    assistant: string,
    dims: readonly JudgeDimension[],
  ): string {
    const lines: string[] = [];
    lines.push("You are an evaluation judge. Score the assistant's reply across the rubric below.");
    lines.push("Reply ONLY in this JSON shape:");
    lines.push(
      '{ "scores": { "<dim_name>": <0-10>, ... }, "overall": "pass|borderline|fail", "reasoning": "<one paragraph>" }',
    );
    lines.push("");
    lines.push("Rubric:");
    for (const d of dims) {
      lines.push(`- ${d.name}: ${d.description}`);
    }
    lines.push("");
    lines.push("User utterance:");
    lines.push(user);
    lines.push("");
    lines.push("Assistant reply:");
    lines.push(assistant);
    // C# AppendLine terminates each line (including the last) with a newline.
    return lines.map((l) => l + "\n").join("");
  }

  private static parseVerdict(raw: string, dims: readonly JudgeDimension[]): JudgeVerdict {
    const scores = new Map<string, number>();
    try {
      const trimmed = LlmJudge.extractJson(raw);
      const root = JSON.parse(trimmed) as Map<string, unknown>;
      const s = root["scores"];
      if (s !== null && typeof s === "object" && !Array.isArray(s)) {
        const scoreObj = s as Map<string, unknown>;
        for (const dim of dims) {
          if (Object.prototype.hasOwnProperty.call(scoreObj, dim.name)) {
            const v = scoreObj[dim.name];
            if (typeof v === "number") {
              scores.set(dim.name, Math.trunc(v));
            } else if (typeof v === "string") {
              const n = Number.parseInt(v, 10);
              scores.set(dim.name, Number.isNaN(n) ? 0 : n);
            } else {
              scores.set(dim.name, 0);
            }
          } else {
            scores.set(dim.name, 0);
          }
        }
      }
      const overallRaw = root["overall"];
      const overall = typeof overallRaw === "string" ? overallRaw : "borderline";
      const reasonRaw = root["reasoning"];
      const reason = typeof reasonRaw === "string" ? reasonRaw : "";
      return { scores, overall, reasoning: reason };
    } catch {
      for (const d of dims) scores.set(d.name, 0);
      return { scores, overall: "borderline", reasoning: "Judge response could not be parsed." };
    }
  }

  /** Tolerate models that wrap JSON in prose or fenced code blocks. */
  private static extractJson(raw: string): string {
    const start = raw.indexOf("{");
    const end = raw.lastIndexOf("}");
    if (start < 0 || end < 0 || end <= start) return raw;
    return raw.substring(start, end + 1);
  }
}
