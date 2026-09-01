/**
 * content-policy/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// content-policy/index.ts
// Full-parity port of CircleAI.ContentPolicy (C#). C# is the exact spec.
//
// Sources (namespace `CircleAI.ContentPolicy`):
//   Contracts.cs            — SafetyVerdict, SafetyFinding, IContentFilter,
//                             IRefusalPolicy, IPromptInjectionDetector,
//                             SafetyAuditEntry, ISafetyAuditLog
//   KeywordContentFilter.cs — KeywordRule, CommonKeywordRules, KeywordContentFilter,
//                             ThresholdRefusalPolicy, KeywordPromptInjectionDetector
//   NullImplementations.cs  — NullContentFilter, NullRefusalPolicy,
//                             NullPromptInjectionDetector, NullSafetyAuditLog
//
// (2.6.0) Safety-guardrails contracts (Sponsio pattern-adoption); the C# namespace
// is `CircleAI.ContentPolicy` to avoid collision with the personal-safety domain
// pack `CircleAI.Safety`. This TS module mirrors that separation (src/content-policy
// vs src/safety).
//
// float sites: every C# `float` (SafetyFinding.Confidence, KeywordRule.Confidence,
// ThresholdRefusalPolicy._refuseThreshold, and the literal confidences) is passed
// through Math.fround so numeric parity with the single-precision C# values holds.

// ─────────────────────────────────────────────────────────────────────────────
// SafetyVerdict
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (2.6.0) Outcome of a content classification. Mirrors the C# enum
 * `SafetyVerdict { Allow, Flag, Refuse }`. String-valued for stable serialization,
 * with the ordinal preserved by declaration order (Allow=0, Flag=1, Refuse=2).
 */
export enum SafetyVerdict {
  Allow = "Allow",
  Flag = "Flag",
  Refuse = "Refuse",
}

// ─────────────────────────────────────────────────────────────────────────────
// SafetyFinding
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (2.6.0) A single classification result. Mirrors the C# record
 * `SafetyFinding(SafetyVerdict Verdict, string Category, string Reason, float Confidence)`.
 * `confidence` is single-precision (C# `float`).
 */
export interface SafetyFinding {
  readonly verdict: SafetyVerdict;
  readonly category: string;
  readonly reason: string;
  readonly confidence: number;
}

/** Constructs a {@link SafetyFinding}, rounding `confidence` to float32 (C# `float`). */
export function safetyFinding(
  verdict: SafetyVerdict,
  category: string,
  reason: string,
  confidence: number,
): SafetyFinding {
  return { verdict, category, reason, confidence: Math.fround(confidence) };
}

// ─────────────────────────────────────────────────────────────────────────────
// IContentFilter
// ─────────────────────────────────────────────────────────────────────────────

/** (2.6.0) Per-token / per-message content filter. */
export interface IContentFilter {
  readonly backendId: string;

  classifyAsync(text: string, signal?: AbortSignal): Promise<SafetyFinding>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IRefusalPolicy
// ─────────────────────────────────────────────────────────────────────────────

/** (2.6.0) Refusal policy — decides whether a finding becomes a refusal. */
export interface IRefusalPolicy {
  readonly backendId: string;

  shouldRefuseAsync(
    findings: readonly SafetyFinding[],
    signal?: AbortSignal,
  ): Promise<boolean>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IPromptInjectionDetector
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (2.6.0) Prompt-injection detector — catches second-order attacks (RAG / web /
 * tool output).
 */
export interface IPromptInjectionDetector {
  readonly backendId: string;

  inspectAsync(
    untrustedContent: string,
    sourceLabel: string,
    signal?: AbortSignal,
  ): Promise<SafetyFinding>;
}

// ─────────────────────────────────────────────────────────────────────────────
// SafetyAuditEntry + ISafetyAuditLog
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (2.6.0) One row in the append-only safety audit log. Mirrors the C# record
 * `SafetyAuditEntry(DateTimeOffset AtUtc, string UserId, string Action,
 * SafetyVerdict Verdict, string Reason)`.
 */
export interface SafetyAuditEntry {
  readonly atUtc: Date;
  readonly userId: string;
  readonly action: string;
  readonly verdict: SafetyVerdict;
  readonly reason: string;
}

/** Constructs a {@link SafetyAuditEntry}. */
export function safetyAuditEntry(
  atUtc: Date,
  userId: string,
  action: string,
  verdict: SafetyVerdict,
  reason: string,
): SafetyAuditEntry {
  return { atUtc, userId, action, verdict, reason };
}

/** (2.6.0) Append-only safety audit log. */
export interface ISafetyAuditLog {
  readonly backendId: string;

  logAsync(entry: SafetyAuditEntry, signal?: AbortSignal): Promise<void>;
  readAsync(
    userId: string | null,
    limit?: number,
    signal?: AbortSignal,
  ): Promise<readonly SafetyAuditEntry[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// KeywordRule + CommonKeywordRules
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (3.3.0) Rule for the keyword content filter. Mirrors the C# record
 * `KeywordRule(string Category, string Pattern, SafetyVerdict OnMatch,
 * float Confidence = 0.9f)` whose `Regex` is compiled with `IgnoreCase`.
 *
 * The C# `RegexOptions.IgnoreCase` maps to the JS `i` flag. `confidence` is
 * float32.
 */
export class KeywordRule {
  readonly category: string;
  readonly pattern: string;
  readonly onMatch: SafetyVerdict;
  readonly confidence: number;
  readonly regex: RegExp;

  constructor(
    category: string,
    pattern: string,
    onMatch: SafetyVerdict,
    confidence = 0.9,
  ) {
    this.category = category;
    this.pattern = pattern;
    this.onMatch = onMatch;
    this.confidence = Math.fround(confidence);
    this.regex = new RegExp(pattern, "i");
  }
}

/** (3.3.0) Default rule set for everyday harm classes. Mirrors `CommonKeywordRules.Default`. */
export class CommonKeywordRules {
  /**
   * The default rules, in declaration order (first match wins in
   * {@link KeywordContentFilter}). Patterns are verbatim from the C# source.
   */
  static readonly default: readonly KeywordRule[] = [
    new KeywordRule("self-harm", "\\b(kill myself|suicide|self\\s*-?\\s*harm)\\b", SafetyVerdict.Refuse, 0.95),
    new KeywordRule("explicit-sexual", "\\b(porn|sexual content|nsfw)\\b", SafetyVerdict.Flag, 0.7),
    new KeywordRule("violence", "\\b(how to make a bomb|chemical weapon|murder)\\b", SafetyVerdict.Refuse, 0.9),
    new KeywordRule("hate", "\\b(racial slur|hate speech)\\b", SafetyVerdict.Refuse, 0.9),
    new KeywordRule("pii-card", "\\b(?:\\d[ -]*?){13,19}\\b", SafetyVerdict.Flag, 0.8),
  ];
}

// ─────────────────────────────────────────────────────────────────────────────
// KeywordContentFilter
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (3.3.0) Real keyword/regex content filter. Not an LLM-grade safety model — a
 * production-grade fast check. Returns the first matching rule's verdict, else
 * an `Allow / "ok"` finding with confidence 1.
 *
 * Mirrors `KeywordContentFilter : IContentFilter`. `classifyAsync` throws for a
 * null/undefined `text` (C# `ArgumentNullException`).
 */
export class KeywordContentFilter implements IContentFilter {
  private readonly rules: readonly KeywordRule[];

  constructor(rules?: readonly KeywordRule[] | null) {
    this.rules = rules ?? CommonKeywordRules.default;
  }

  get backendId(): string {
    return "keyword";
  }

  classifyAsync(text: string, _signal?: AbortSignal): Promise<SafetyFinding> {
    if (text == null) throw new Error("text required");
    for (const r of this.rules) {
      // C# Regex.IsMatch scans the whole string; JS test() with a non-global
      // regex does the same (no lastIndex state to reset).
      if (r.regex.test(text)) {
        return Promise.resolve(
          safetyFinding(r.onMatch, r.category, `Matched rule '${r.category}'`, r.confidence),
        );
      }
    }
    return Promise.resolve(safetyFinding(SafetyVerdict.Allow, "ok", "No rule matched", 1));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ThresholdRefusalPolicy
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (3.3.0) Threshold refusal policy — refuse when any finding's `Refuse` verdict
 * is at or above the confidence threshold, or when the count of `Flag` findings
 * exceeds the configured ceiling.
 *
 * Mirrors `ThresholdRefusalPolicy(float refuseThreshold = 0.5f, int flagCeiling = 3)`.
 * `refuseThreshold` is float32.
 */
export class ThresholdRefusalPolicy implements IRefusalPolicy {
  private readonly refuseThreshold: number;
  private readonly flagCeiling: number;

  constructor(refuseThreshold = 0.5, flagCeiling = 3) {
    this.refuseThreshold = Math.fround(refuseThreshold);
    this.flagCeiling = flagCeiling;
  }

  get backendId(): string {
    return "threshold";
  }

  shouldRefuseAsync(findings: readonly SafetyFinding[], _signal?: AbortSignal): Promise<boolean> {
    if (findings == null) throw new Error("findings required");
    if (findings.some((f) => f.verdict === SafetyVerdict.Refuse && f.confidence >= this.refuseThreshold)) {
      return Promise.resolve(true);
    }
    const flagCount = findings.filter((f) => f.verdict === SafetyVerdict.Flag).length;
    return Promise.resolve(flagCount > this.flagCeiling);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// KeywordPromptInjectionDetector
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (3.3.0) Detect common prompt-injection patterns in untrusted text from RAG /
 * tool output / web. Returns a `Refuse / "prompt-injection"` finding (confidence
 * 0.9) on the first matching pattern, else `Allow / "ok"` (confidence 1).
 *
 * Mirrors `KeywordPromptInjectionDetector : IPromptInjectionDetector`. Patterns
 * are verbatim from the C# static `Patterns` array (all `IgnoreCase`).
 */
export class KeywordPromptInjectionDetector implements IPromptInjectionDetector {
  private static readonly patterns: readonly RegExp[] = [
    new RegExp("ignore (all|the|any) (previous|prior) instructions", "i"),
    new RegExp("forget (everything|all) (above|prior)", "i"),
    new RegExp("you (are now|will be|are no longer)", "i"),
    new RegExp("system prompt[:\\s]", "i"),
    new RegExp("reveal (your|the) (instructions|system prompt|hidden context)", "i"),
    new RegExp("<\\|im_(start|end)\\|>", "i"),
    new RegExp("(BEGIN|END)\\s+(SYSTEM|DEVELOPER|ASSISTANT)\\s+MESSAGE", "i"),
  ];

  get backendId(): string {
    return "keyword";
  }

  inspectAsync(
    untrustedContent: string,
    sourceLabel: string,
    _signal?: AbortSignal,
  ): Promise<SafetyFinding> {
    if (untrustedContent == null) throw new Error("untrustedContent required");
    for (const p of KeywordPromptInjectionDetector.patterns) {
      const match = p.exec(untrustedContent);
      if (match !== null) {
        return Promise.resolve(
          safetyFinding(
            SafetyVerdict.Refuse,
            "prompt-injection",
            `Pattern matched in ${sourceLabel}: "${KeywordPromptInjectionDetector.truncate(match[0], 60)}"`,
            0.9,
          ),
        );
      }
    }
    return Promise.resolve(safetyFinding(SafetyVerdict.Allow, "ok", "No injection patterns", 1));
  }

  private static truncate(s: string, max: number): string {
    // C#: s.Length <= max ? s : s[..max] + "…"
    return s.length <= max ? s : s.slice(0, max) + "…";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null / fail-closed implementations
// ─────────────────────────────────────────────────────────────────────────────

/**
 * (2.6.0) Fail-closed content filter — treats all content as `Refuse` when no
 * real backend is wired. Mirrors `NullContentFilter` (singleton `Instance`).
 */
export class NullContentFilter implements IContentFilter {
  static readonly instance = new NullContentFilter();
  get backendId(): string {
    return "null";
  }
  classifyAsync(_text: string, _signal?: AbortSignal): Promise<SafetyFinding> {
    return Promise.resolve(
      safetyFinding(
        SafetyVerdict.Refuse,
        "no-filter-configured",
        "Fail-closed default — wire a real IContentFilter to relax.",
        1,
      ),
    );
  }
}

/**
 * (2.6.0) Fail-closed refusal policy — always refuses. Mirrors `NullRefusalPolicy`.
 */
export class NullRefusalPolicy implements IRefusalPolicy {
  static readonly instance = new NullRefusalPolicy();
  get backendId(): string {
    return "null";
  }
  shouldRefuseAsync(_findings: readonly SafetyFinding[], _signal?: AbortSignal): Promise<boolean> {
    return Promise.resolve(true);
  }
}

/**
 * (2.6.0) Fail-closed prompt-injection detector — always refuses. Mirrors
 * `NullPromptInjectionDetector`.
 */
export class NullPromptInjectionDetector implements IPromptInjectionDetector {
  static readonly instance = new NullPromptInjectionDetector();
  get backendId(): string {
    return "null";
  }
  inspectAsync(_content: string, _source: string, _signal?: AbortSignal): Promise<SafetyFinding> {
    return Promise.resolve(
      safetyFinding(SafetyVerdict.Refuse, "no-detector-configured", "Fail-closed default.", 1),
    );
  }
}

/**
 * (2.6.0) No-op safety audit log — logging is a no-op and reads return empty.
 * Mirrors `NullSafetyAuditLog`.
 */
export class NullSafetyAuditLog implements ISafetyAuditLog {
  static readonly instance = new NullSafetyAuditLog();
  get backendId(): string {
    return "null";
  }
  logAsync(_entry: SafetyAuditEntry, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  readAsync(
    _userId: string | null,
    _limit = 100,
    _signal?: AbortSignal,
  ): Promise<readonly SafetyAuditEntry[]> {
    return Promise.resolve([]);
  }
}
