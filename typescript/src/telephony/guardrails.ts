// telephony/guardrails.ts
//
// Pre-TTS phrase blocking — faithful port of Guardrails.cs. The model's draft
// response runs through the guardrails before TTS: banned phrases are rewritten
// or the whole turn is replaced with a fallback message. Useful for keeping the
// AI on-script, banning PII leaks, or stopping competitor name mentions.
//
// C# `Regex(pattern, IgnoreCase | Compiled)` → JS `RegExp(pattern, "gi")`. The
// `g` flag is required so `String.replace` redacts every occurrence (matching
// `Regex.Replace`); `IsMatch` uses a non-stateful `RegExp.test` on a fresh
// instance to avoid `lastIndex` carry-over.

/** What a guardrail does on match. Mirrors `GuardrailAction`. */
export const GuardrailAction = {
  /** Block the turn entirely — the AI says {@link GuardrailRule.fallbackMessage} instead. */
  Replace: "Replace",
  /** Redact only the matched text (e.g. credit-card numbers → "[redacted]"). */
  Redact: "Redact",
  /** Pass through but flag in the audit log. */
  Warn: "Warn",
} as const;
export type GuardrailAction = (typeof GuardrailAction)[keyof typeof GuardrailAction];

/** One rule the guardrail checks. Mirrors `GuardrailRule`. */
export interface GuardrailRule {
  /** Display name for logging. */
  readonly name: string;
  /** Regex pattern (case-insensitive). */
  readonly pattern: string;
  /** What to do when the pattern matches. */
  readonly action: GuardrailAction;
  /** Replacement text for {@link GuardrailAction.Redact}. */
  readonly replaceWith?: string;
  /** Speak this instead when {@link GuardrailAction.Replace}. */
  readonly fallbackMessage?: string;
}

/** Constructs a {@link GuardrailRule}. */
export function guardrailRule(
  name: string,
  pattern: string,
  action: GuardrailAction,
  replaceWith?: string,
  fallbackMessage?: string,
): GuardrailRule {
  return { name, pattern, action, replaceWith, fallbackMessage };
}

/** Outcome of running guardrails on one text draft. Mirrors `GuardrailResult`. */
export interface GuardrailResult {
  readonly finalText: string;
  readonly wasModified: boolean;
  readonly wasBlocked: boolean;
  readonly triggeredRules: readonly string[];
}

/** Pre-TTS guardrail engine. Mirrors `Guardrails`. */
export class Guardrails {
  private readonly rules: Array<{ rule: GuardrailRule; regex: RegExp }>;
  private readonly defaultFallback: string;

  constructor(
    rules?: Iterable<GuardrailRule>,
    defaultFallback = "I'm sorry, I can't help with that right now.",
  ) {
    this.defaultFallback = defaultFallback;
    this.rules = [...(rules ?? [])].map((r) => ({ rule: r, regex: new RegExp(r.pattern, "gi") }));
  }

  /** Run the guardrails against a draft response. */
  apply(draft: string): GuardrailResult {
    if (!draft) {
      return { finalText: draft ?? "", wasModified: false, wasBlocked: false, triggeredRules: [] };
    }

    const triggered: string[] = [];
    let text = draft;
    const blocked = false;

    for (const { rule, regex } of this.rules) {
      // Non-stateful match test (fresh regex avoids `g`-flag lastIndex carry-over).
      if (!new RegExp(rule.pattern, "gi").test(text)) continue;
      triggered.push(rule.name);

      switch (rule.action) {
        case GuardrailAction.Replace:
          text = rule.fallbackMessage ?? this.defaultFallback;
          return { finalText: text, wasModified: true, wasBlocked: true, triggeredRules: triggered };

        case GuardrailAction.Redact:
          text = text.replace(new RegExp(rule.pattern, "gi"), rule.replaceWith ?? "[redacted]");
          break;

        case GuardrailAction.Warn:
          // No mutation; just flag.
          break;
      }
    }

    const modified = text !== draft;
    return { finalText: text, wasModified: modified, wasBlocked: blocked, triggeredRules: triggered };
  }
}

function escapeRegex(literal: string): string {
  return literal.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Common guardrails out of the box. Mirrors static `CommonGuardrails`. */
export const CommonGuardrails = {
  /** Redact 13-19 digit credit-card numbers. */
  get creditCardRedactor(): GuardrailRule {
    return guardrailRule(
      "credit-card",
      String.raw`\b(?:\d[ -]*?){13,19}\b`,
      GuardrailAction.Redact,
      "[redacted card number]",
    );
  },

  /** Block US SSN-shaped sequences (xxx-xx-xxxx). */
  get ssnBlocker(): GuardrailRule {
    return guardrailRule(
      "ssn",
      String.raw`\b\d{3}-\d{2}-\d{4}\b`,
      GuardrailAction.Replace,
      undefined,
      "For security I can't share that information.",
    );
  },

  /** Block competitor mentions — supply names per deployment. */
  competitorMention(...competitors: string[]): GuardrailRule {
    return guardrailRule(
      "competitor",
      String.raw`\b(?:` + competitors.map(escapeRegex).join("|") + String.raw`)\b`,
      GuardrailAction.Replace,
      undefined,
      "I can't comment on other providers, but I can help with your account.",
    );
  },
} as const;
