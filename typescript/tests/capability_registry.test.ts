// capability_registry.test.ts
//
// Verifies the ported ExternalCapabilityRegistry (CapabilityRegistry.cs): the
// full 32-entry set, case-insensitive Find, and ByPackage filtering.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  EXTERNAL_CAPABILITY_REGISTRY,
  ExternalCapabilityRegistry,
  findCapability,
  capabilitiesByPackage,
} from '../src/companion/capability_registry';

describe('ExternalCapabilityRegistry', () => {
  it('contains all 30 earmarked capabilities', () => {
    assert.equal(EXTERNAL_CAPABILITY_REGISTRY.length, 30);
    assert.equal(ExternalCapabilityRegistry.all.length, 30);
  });

  it('every entry has an id, license, strategy, target package, and >=1 bullet', () => {
    for (const c of EXTERNAL_CAPABILITY_REGISTRY) {
      assert.ok(c.id.length > 0, `id for ${c.id}`);
      assert.ok(c.license.length > 0, `license for ${c.id}`);
      assert.ok(['vendor', 'pattern-port', 'wrap'].includes(c.strategy), `strategy for ${c.id}`);
      assert.ok(c.targetPackage.startsWith('CircleAI.'), `package for ${c.id}`);
      assert.ok(c.valueBullets.length >= 1, `bullets for ${c.id}`);
    }
  });

  it('finds by id case-insensitively', () => {
    const a = findCapability('claude-mem');
    assert.ok(a);
    assert.equal(a.repo, 'thedotmack/claude-mem');
    assert.equal(a.license, 'MIT');
    assert.equal(a.strategy, 'pattern-port');
    assert.equal(a.targetPackage, 'CircleAI.Memory');
    // Case-insensitive.
    assert.equal(findCapability('AMPHION')?.id, 'Amphion');
    assert.equal(ExternalCapabilityRegistry.find('hippoRAG')?.id, 'HippoRAG');
  });

  it('returns null for an unknown id', () => {
    assert.equal(findCapability('does-not-exist'), null);
  });

  it('preserves a known multi-bullet entry verbatim', () => {
    const superpowers = findCapability('superpowers');
    assert.ok(superpowers);
    assert.equal(superpowers.valueBullets.length, 8);
    assert.equal(superpowers.valueBullets[6], 'TDD RED-GREEN-REFACTOR mandatory gate');
  });

  it('lists entries by target package (case-insensitive)', () => {
    // Two Games entries: aimangastudio + flame.
    const games = capabilitiesByPackage('CircleAI.Games');
    assert.deepEqual(
      games.map((c) => c.id).sort(),
      ['aimangastudio', 'flame'],
    );
    // Two Speech entries: Amphion + yapsnap.
    assert.equal(ExternalCapabilityRegistry.byPackage('circleai.speech').length, 2);
    // Two Inference entries: airllm + shard.
    assert.equal(capabilitiesByPackage('CircleAI.Inference').length, 2);
  });

  it('unknown package → empty list', () => {
    assert.equal(capabilitiesByPackage('CircleAI.Nope').length, 0);
  });
});
