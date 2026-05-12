// companion_types.test.ts
//
// Verifies CompanionContext, CompanionTurn, CompanionProactiveEvent,
// and InterfaceKind enum (must have exactly 7 values).

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
  test('has exactly 7 values', () => {
    const values = Object.values(InterfaceKind);
    expect(values.length).toBe(7);
  });

  test('contains Mobile', () => {
    expect(InterfaceKind.Mobile).toBe('Mobile');
  });

  test('contains Wearable', () => {
    expect(InterfaceKind.Wearable).toBe('Wearable');
  });

  test('contains Desktop', () => {
    expect(InterfaceKind.Desktop).toBe('Desktop');
  });

  test('contains Web', () => {
    expect(InterfaceKind.Web).toBe('Web');
  });

  test('contains IoT', () => {
    expect(InterfaceKind.IoT).toBe('IoT');
  });

  test('contains Ambient', () => {
    expect(InterfaceKind.Ambient).toBe('Ambient');
  });

  test('contains Headless', () => {
    expect(InterfaceKind.Headless).toBe('Headless');
  });

  test('all 7 values are unique', () => {
    const values = Object.values(InterfaceKind);
    const unique = new Set(values);
    expect(unique.size).toBe(7);
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

  test('constructs with all required fields', () => {
    const ctx = makeContext();
    expect(ctx.identityId).toBe('test-identity-001');
    expect(ctx.displayName).toBe('Sipho Dlamini');
    expect(ctx.preferredLanguage).toBe('zu');
    expect(ctx.interface).toBe(InterfaceKind.Mobile);
  });

  test('preferredLanguage can be null', () => {
    const ctx = makeContext({ preferredLanguage: null });
    expect(ctx.preferredLanguage).toBeNull();
  });

  test('recentMemorySnippets is an array', () => {
    const ctx = makeContext();
    expect(Array.isArray(ctx.recentMemorySnippets)).toBe(true);
  });

  test('activeGoals is an array', () => {
    const ctx = makeContext();
    expect(Array.isArray(ctx.activeGoals)).toBe(true);
  });

  test('contextBuiltAt is a Date', () => {
    const ctx = makeContext();
    expect(ctx.contextBuiltAt instanceof Date).toBe(true);
  });

  test('interface accepts any InterfaceKind value', () => {
    for (const kind of Object.values(InterfaceKind)) {
      const ctx = makeContext({ interface: kind });
      expect(ctx.interface).toBe(kind);
    }
  });
});

// ---------------------------------------------------------------------------
// CompanionTurn construction
// ---------------------------------------------------------------------------

describe('CompanionTurn', () => {
  test('constructs user turn', () => {
    const turn: CompanionTurn = {
      role:      'user',
      content:   'Hello B!',
      timestamp: new Date(),
    };
    expect(turn.role).toBe('user');
    expect(turn.content).toBe('Hello B!');
    expect(turn.timestamp instanceof Date).toBe(true);
  });

  test('constructs assistant turn', () => {
    const turn: CompanionTurn = {
      role:      'assistant',
      content:   'Hi there! How can I help?',
      timestamp: new Date(),
    };
    expect(turn.role).toBe('assistant');
  });

  test('timestamps are ordered in a sequence', () => {
    const t1 = new Date('2026-05-13T09:00:00Z');
    const t2 = new Date('2026-05-13T09:00:05Z');
    const userTurn: CompanionTurn    = { role: 'user',      content: 'Hey', timestamp: t1 };
    const assistantTurn: CompanionTurn = { role: 'assistant', content: 'Hi', timestamp: t2 };
    expect(assistantTurn.timestamp > userTurn.timestamp).toBe(true);
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

  test('constructs with all required fields', () => {
    const event = makeEvent();
    expect(event.sessionId).toBe('session-abc-001');
    expect(event.identityId).toBe('identity-xyz-002');
    expect(event.interface).toBe(InterfaceKind.Mobile);
    expect(event.message.length).toBeGreaterThan(0);
    expect(event.triggerName).toBe('daily-checkin');
    expect(event.generatedAt instanceof Date).toBe(true);
  });

  test('interface can be Headless for background events', () => {
    const event = makeEvent({ interface: InterfaceKind.Headless });
    expect(event.interface).toBe(InterfaceKind.Headless);
  });

  test('message is non-empty', () => {
    const event = makeEvent();
    expect(event.message.trim().length).toBeGreaterThan(0);
  });
});
