// companion_types.test.ts
//
// Verifies CompanionContext, CompanionTurn, CompanionProactiveEvent,
// and InterfaceKind enum (must have exactly 7 values).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  InterfaceKind,
  type CompanionContext,
  type CompanionTurn,
  type CompanionProactiveEvent,
} from '../src/companion';

// ---------------------------------------------------------------------------
// InterfaceKind enum
// ---------------------------------------------------------------------------

describe('InterfaceKind enum', () => {
  it('has exactly 7 values', () => {
    const values = Object.values(InterfaceKind);
    assert.equal(values.length, 7);
  });

  it('contains Mobile', () => {
    assert.equal(InterfaceKind.Mobile, 'Mobile');
  });

  it('contains Wearable', () => {
    assert.equal(InterfaceKind.Wearable, 'Wearable');
  });

  it('contains Desktop', () => {
    assert.equal(InterfaceKind.Desktop, 'Desktop');
  });

  it('contains Web', () => {
    assert.equal(InterfaceKind.Web, 'Web');
  });

  it('contains IoT', () => {
    assert.equal(InterfaceKind.IoT, 'IoT');
  });

  it('contains Ambient', () => {
    assert.equal(InterfaceKind.Ambient, 'Ambient');
  });

  it('contains Headless', () => {
    assert.equal(InterfaceKind.Headless, 'Headless');
  });

  it('all 7 values are unique', () => {
    const values = Object.values(InterfaceKind);
    const unique = new Set(values);
    assert.equal(unique.size, 7);
  });
});

// ---------------------------------------------------------------------------
// CompanionContext construction
// ---------------------------------------------------------------------------

describe('CompanionContext', () => {
  function makeContext(overrides?: Partial<CompanionContext>): CompanionContext {
    return {
      identityId:           'test-identity-001',
      displayName:          'Sipho Dlamini',
      preferredLanguage:    'zu',
      interface:            InterfaceKind.Mobile,
      personaHints:         '[User preferences]\nKeep responses brief.\n',
      affectSummary:        '[Affect state]\nYou are fully engaged — be enthusiastic and thorough.\n',
      recentMemorySnippets: ['Yesterday you asked about the weather in Durban.'],
      activeGoals:          ['Learn TypeScript'],
      contextBuiltAt:       new Date('2026-05-13T09:00:00Z'),
      ...overrides,
    };
  }

  it('constructs with all required fields', () => {
    const ctx = makeContext();
    assert.equal(ctx.identityId, 'test-identity-001');
    assert.equal(ctx.displayName, 'Sipho Dlamini');
    assert.equal(ctx.preferredLanguage, 'zu');
    assert.equal(ctx.interface, InterfaceKind.Mobile);
  });

  it('preferredLanguage can be null', () => {
    const ctx = makeContext({ preferredLanguage: null });
    assert.equal(ctx.preferredLanguage, null);
  });

  it('recentMemorySnippets is an array', () => {
    const ctx = makeContext();
    assert.ok(Array.isArray(ctx.recentMemorySnippets), 'recentMemorySnippets should be an array');
  });

  it('activeGoals is an array', () => {
    const ctx = makeContext();
    assert.ok(Array.isArray(ctx.activeGoals), 'activeGoals should be an array');
  });

  it('contextBuiltAt is a Date', () => {
    const ctx = makeContext();
    assert.ok(ctx.contextBuiltAt instanceof Date, 'contextBuiltAt should be a Date');
  });

  it('interface accepts any InterfaceKind value', () => {
    for (const kind of Object.values(InterfaceKind)) {
      const ctx = makeContext({ interface: kind });
      assert.equal(ctx.interface, kind);
    }
  });
});

// ---------------------------------------------------------------------------
// CompanionTurn construction
// ---------------------------------------------------------------------------

describe('CompanionTurn', () => {
  it('constructs user turn', () => {
    const turn: CompanionTurn = {
      role:      'user',
      content:   'Hello B!',
      timestamp: new Date(),
    };
    assert.equal(turn.role, 'user');
    assert.equal(turn.content, 'Hello B!');
    assert.ok(turn.timestamp instanceof Date, 'timestamp should be a Date');
  });

  it('constructs assistant turn', () => {
    const turn: CompanionTurn = {
      role:      'assistant',
      content:   'Hi there! How can I help?',
      timestamp: new Date(),
    };
    assert.equal(turn.role, 'assistant');
  });

  it('timestamps are ordered in a sequence', () => {
    const t1 = new Date('2026-05-13T09:00:00Z');
    const t2 = new Date('2026-05-13T09:00:05Z');
    const userTurn: CompanionTurn      = { role: 'user',      content: 'Hey', timestamp: t1 };
    const assistantTurn: CompanionTurn = { role: 'assistant', content: 'Hi',  timestamp: t2 };
    assert.ok(assistantTurn.timestamp > userTurn.timestamp, 'assistantTurn should come after userTurn');
  });
});

// ---------------------------------------------------------------------------
// CompanionProactiveEvent construction
// ---------------------------------------------------------------------------

describe('CompanionProactiveEvent', () => {
  function makeEvent(overrides?: Partial<CompanionProactiveEvent>): CompanionProactiveEvent {
    return {
      sessionId:   'session-abc-001',
      identityId:  'identity-xyz-002',
      interface:   InterfaceKind.Mobile,
      message:     'Just checking in — how are you doing today?',
      triggerName: 'daily-checkin',
      generatedAt: new Date('2026-05-13T08:00:00Z'),
      ...overrides,
    };
  }

  it('constructs with all required fields', () => {
    const event = makeEvent();
    assert.equal(event.sessionId, 'session-abc-001');
    assert.equal(event.identityId, 'identity-xyz-002');
    assert.equal(event.interface, InterfaceKind.Mobile);
    assert.ok(event.message.length > 0, 'message should be non-empty');
    assert.equal(event.triggerName, 'daily-checkin');
    assert.ok(event.generatedAt instanceof Date, 'generatedAt should be a Date');
  });

  it('interface can be Headless for background events', () => {
    const event = makeEvent({ interface: InterfaceKind.Headless });
    assert.equal(event.interface, InterfaceKind.Headless);
  });

  it('message is non-empty', () => {
    const event = makeEvent();
    assert.ok(event.message.trim().length > 0, 'message should have non-whitespace content');
  });
});
