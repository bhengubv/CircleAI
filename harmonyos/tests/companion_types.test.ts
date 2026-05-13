// companion_types.test.ts
//
// Tests for Companion types — HarmonyOS/ArkTS port.
// Verifies CompanionContext, CompanionTurn, InterfaceKind structural correctness.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type { CompanionContext, CompanionTurn } from '../src/companion';
import { InterfaceKind } from '../src/companion';

describe('Companion types', () => {
  it('create companion context', () => {
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
    assert.strictEqual(ctx.interface, InterfaceKind.Mobile);
    assert.strictEqual(ctx.preferredLanguage, 'en-US');
  });

  it('all interface kinds are defined', () => {
    const kinds = Object.values(InterfaceKind);
    assert.ok((kinds as string[]).includes('Mobile'));
    assert.ok((kinds as string[]).includes('Wearable'));
    assert.ok((kinds as string[]).includes('Desktop'));
    assert.ok((kinds as string[]).includes('Web'));
    assert.ok((kinds as string[]).includes('IoT'));
    assert.ok((kinds as string[]).includes('Ambient'));
    assert.ok((kinds as string[]).includes('Headless'));
    assert.strictEqual(kinds.length, 7);
  });

  it('create companion turn', () => {
    const turn: CompanionTurn = {
      role:      'user',
      content:   'Hello',
      timestamp: new Date(),
    };
    assert.strictEqual(turn.role, 'user');
    assert.strictEqual(turn.content, 'Hello');
  });

  it('companion context with null preferredLanguage', () => {
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
    assert.strictEqual(ctx.preferredLanguage, null);
    assert.strictEqual(ctx.interface, InterfaceKind.IoT);
  });

  it('assistant turn', () => {
    const turn: CompanionTurn = {
      role:      'assistant',
      content:   'Hi there!',
      timestamp: new Date(),
    };
    assert.strictEqual(turn.role, 'assistant');
    assert.strictEqual(turn.content, 'Hi there!');
  });

  it('contextBuiltAt is a Date', () => {
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
    assert.ok(ctx.contextBuiltAt instanceof Date);
    assert.strictEqual(ctx.recentMemorySnippets.length, 2);
    assert.strictEqual(ctx.activeGoals.length, 1);
  });
});
