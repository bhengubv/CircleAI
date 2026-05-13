// companion_types.test.ts
//
// Tests for Companion types — HarmonyOS/ArkTS port.
// Verifies CompanionContext, CompanionTurn, InterfaceKind structural correctness.

import type { CompanionContext, CompanionTurn } from '../src/companion';
import { InterfaceKind } from '../src/companion';

describe('Companion types', () => {
  test('create companion context', () => {
    const ctx: CompanionContext = {
      identityId:           '550e8400-e29b-41d4-a716-446655440001',
      displayName:          'Test User',
      preferredLanguage:    'en-US',
      interface:            InterfaceKind.Mobile,
      personaHints:         '',
      affectSummary:        '',
      recentMemorySnippets: [],
      activeGoals:          [],
      contextBuiltAt:       new Date(),
    };
    expect(ctx.interface).toBe(InterfaceKind.Mobile);
    expect(ctx.preferredLanguage).toBe('en-US');
  });

  test('all interface kinds are defined', () => {
    const kinds = Object.values(InterfaceKind);
    expect(kinds).toContain('Mobile');
    expect(kinds).toContain('Wearable');
    expect(kinds).toContain('Desktop');
    expect(kinds).toContain('Web');
    expect(kinds).toContain('IoT');
    expect(kinds).toContain('Ambient');
    expect(kinds).toContain('Headless');
    expect(kinds.length).toBe(7);
  });

  test('create companion turn', () => {
    const turn: CompanionTurn = {
      role:      'user',
      content:   'Hello',
      timestamp: new Date(),
    };
    expect(turn.role).toBe('user');
    expect(turn.content).toBe('Hello');
  });

  test('companion context with null preferredLanguage', () => {
    const ctx: CompanionContext = {
      identityId:           '550e8400-e29b-41d4-a716-446655440002',
      displayName:          'Anonymous',
      preferredLanguage:    null,
      interface:            InterfaceKind.IoT,
      personaHints:         '',
      affectSummary:        '',
      recentMemorySnippets: [],
      activeGoals:          [],
      contextBuiltAt:       new Date(),
    };
    expect(ctx.preferredLanguage).toBeNull();
    expect(ctx.interface).toBe(InterfaceKind.IoT);
  });

  test('assistant turn', () => {
    const turn: CompanionTurn = {
      role:      'assistant',
      content:   'Hi there!',
      timestamp: new Date(),
    };
    expect(turn.role).toBe('assistant');
    expect(turn.content).toBe('Hi there!');
  });

  test('contextBuiltAt is a Date', () => {
    const now = new Date();
    const ctx: CompanionContext = {
      identityId:           '550e8400-e29b-41d4-a716-446655440003',
      displayName:          'Dev',
      preferredLanguage:    'zu',
      interface:            InterfaceKind.Headless,
      personaHints:         '',
      affectSummary:        '',
      recentMemorySnippets: ['memory 1', 'memory 2'],
      activeGoals:          ['goal 1'],
      contextBuiltAt:       now,
    };
    expect(ctx.contextBuiltAt).toBeInstanceOf(Date);
    expect(ctx.recentMemorySnippets.length).toBe(2);
    expect(ctx.activeGoals.length).toBe(1);
  });
});
