// accessibility/index.ts
// Full-parity port of CircleAI.Accessibility (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Accessibility vertical: user
// accessibility profiles and derived adaptation hints. Plus the static
// AccessibilityDomainContext.
//
// NOTE: The C# AccessibilityCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   enum AccessibilityNeed           → const enum-like (Visual=0..Speech=4)
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<AccessibilityNeed> → readonly AccessibilityNeed[]
//   double TextScale                 → number
//   bool HighContrast/…              → boolean
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   HintsFor — [] when no profile; else in this exact order:
//                contrast=high      (if HighContrast)
//                motion=reduced     (if ReducedMotion)
//                aria=verbose       (if ScreenReader)
//                text-scale=<F2>    (if TextScale > 1; value = TextScale.ToString("F2"))
//                need=<EnumName>    (one per Need, in profile order)

/** An accessibility need class. Mirrors C# `AccessibilityNeed` (Visual = 0). */
export type AccessibilityNeed = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link AccessibilityNeed} members. */
export const AccessibilityNeed = Object.freeze({
  Visual: 0,
  Hearing: 1,
  Motor: 2,
  Cognitive: 3,
  Speech: 4,
} as const) satisfies Record<string, AccessibilityNeed>;

/** Enum-name for each {@link AccessibilityNeed}, matching C# `Enum.ToString()`. */
const NEED_NAMES: readonly string[] = ["Visual", "Hearing", "Motor", "Cognitive", "Speech"];

/** A user's accessibility profile. Mirrors C# `UserAccessibilityProfile` record. */
export interface UserAccessibilityProfile {
  readonly userId: string;
  readonly needs: readonly AccessibilityNeed[];
  readonly textScale: number;
  readonly highContrast: boolean;
  readonly reducedMotion: boolean;
  readonly screenReader: boolean;
}

/** Constructs a {@link UserAccessibilityProfile}. */
export function userAccessibilityProfile(
  userId: string,
  needs: readonly AccessibilityNeed[],
  textScale: number,
  highContrast: boolean,
  reducedMotion: boolean,
  screenReader: boolean,
): UserAccessibilityProfile {
  return { userId, needs, textScale, highContrast, reducedMotion, screenReader };
}

/** A single UI adaptation hint. Mirrors C# `AdaptationHint` record. */
export interface AdaptationHint {
  readonly kind: string;
  readonly value: string;
}

/** Constructs an {@link AdaptationHint}. */
export function adaptationHint(kind: string, value: string): AdaptationHint {
  return { kind, value };
}

/** The accessibility board contract. Mirrors C# `IAccessibilityBoard`. */
export interface IAccessibilityBoard {
  setProfile(p: UserAccessibilityProfile): void;
  getProfile(userId: string): UserAccessibilityProfile | undefined;
  hintsFor(userId: string): readonly AdaptationHint[];
}

/** Deterministic in-memory {@link IAccessibilityBoard}. */
export class InMemoryAccessibilityBoard implements IAccessibilityBoard {
  private readonly profiles = new Map<string, UserAccessibilityProfile>();

  setProfile(p: UserAccessibilityProfile): void {
    if (p == null) throw new Error("p required");
    this.profiles.set(p.userId, p);
  }

  getProfile(userId: string): UserAccessibilityProfile | undefined {
    return this.profiles.get(userId);
  }

  hintsFor(userId: string): readonly AdaptationHint[] {
    const p = this.profiles.get(userId);
    if (p === undefined) return [];
    const hints: AdaptationHint[] = [];
    if (p.highContrast) hints.push({ kind: "contrast", value: "high" });
    if (p.reducedMotion) hints.push({ kind: "motion", value: "reduced" });
    if (p.screenReader) hints.push({ kind: "aria", value: "verbose" });
    if (p.textScale > 1) hints.push({ kind: "text-scale", value: p.textScale.toFixed(2) });
    for (const n of p.needs) hints.push({ kind: "need", value: NEED_NAMES[n] });
    return hints;
  }
}

/**
 * Static domain context for the Accessibility vertical. Mirrors C#
 * `AccessibilityDomainContext`.
 */
export const AccessibilityDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Accessibility] Expert accessibility and inclusive design assistant. Help with WCAG 2.2 compliance audits, screen reader compatibility, alternative text guidance, disability accommodation requests, and assistive technology selection. Always centre the lived experience of disabled users. Compliance: WCAG 2.2, UNCRPD, SA Promotion of Equality Act, POPIA.",
  complianceFlags: ["WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA"] as readonly string[],
  suggestedTools: ["screen_reader_test", "document_editor", "web_audit", "analytics"] as readonly string[],
} as const;
