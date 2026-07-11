// accessibility_board.test.ts
// Verifies the CircleAI.Accessibility port: profile storage and the exact,
// ordered adaptation-hint derivation (including F2 text-scale and need names).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  InMemoryAccessibilityBoard,
  AccessibilityDomainContext,
  AccessibilityNeed,
  userAccessibilityProfile,
} from "../src/accessibility/index";

describe("InMemoryAccessibilityBoard", () => {
  it("returns [] when no profile is set", () => {
    const b = new InMemoryAccessibilityBoard();
    assert.deepEqual(b.hintsFor("nobody"), []);
  });

  it("derives hints in the exact C# order with F2 scale and enum-name needs", () => {
    const b = new InMemoryAccessibilityBoard();
    b.setProfile(
      userAccessibilityProfile("u1", [AccessibilityNeed.Visual, AccessibilityNeed.Motor], 1.5, true, true, true),
    );
    assert.equal(b.getProfile("u1")?.textScale, 1.5);
    assert.deepEqual(
      b.hintsFor("u1").map((h) => [h.kind, h.value]),
      [
        ["contrast", "high"],
        ["motion", "reduced"],
        ["aria", "verbose"],
        ["text-scale", "1.50"],
        ["need", "Visual"],
        ["need", "Motor"],
      ],
    );
  });

  it("omits flags that are off and omits text-scale when <= 1", () => {
    const b = new InMemoryAccessibilityBoard();
    b.setProfile(userAccessibilityProfile("u2", [AccessibilityNeed.Hearing], 1.0, false, false, false));
    assert.deepEqual(
      b.hintsFor("u2").map((h) => [h.kind, h.value]),
      [["need", "Hearing"]],
    );
    assert.equal(AccessibilityNeed.Visual, 0);
    assert.equal(AccessibilityNeed.Speech, 4);
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(AccessibilityDomainContext.systemPromptSnippet.includes("[DOMAIN: Accessibility]"));
    assert.deepEqual(AccessibilityDomainContext.complianceFlags, ["WCAG_2_2", "UNCRPD", "Equality_Act", "POPIA"]);
    assert.deepEqual(AccessibilityDomainContext.suggestedTools, [
      "screen_reader_test",
      "document_editor",
      "web_audit",
      "analytics",
    ]);
  });
});
