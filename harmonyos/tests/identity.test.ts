// identity.test.ts
//
// Verifies IdentityTier enum, CircleIdentity schema, RegisteredDevice schema,
// and fixture examples from fixtures/identity.json — HarmonyOS/ArkTS port.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { IdentityTier, type CircleIdentity, type RegisteredDevice } from '../src/identity';

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

interface IdentityFixtureExample {
  id:          string;
  description: string;
  identity: {
    identityId:        string;
    displayName:       string;
    preferredLanguage: string | null;
    tier:              string;
    deviceIds:         string[];
    createdAt:         string;
    lastSeenAt:        string;
  };
  devices: Array<{
    deviceId:     string;
    identityId:   string;
    platform:     string;
    deviceName:   string | null;
    registeredAt: string;
    lastActiveAt: string;
  }>;
}

interface IdentityFixture {
  identityTiers: string[];
  platforms:     string[];
  examples:      IdentityFixtureExample[];
  assertions: {
    tierOrder:       string[];
    identityIdFormat: string;
    deviceIdFormat:   string;
    timestampFormat:  string;
  };
}

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = resolve(__dirname, '../../fixtures/identity.json');
const fixture: IdentityFixture = JSON.parse(readFileSync(fixturePath, 'utf-8'));

// ---------------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------------

function buildIdentity(ex: IdentityFixtureExample): CircleIdentity {
  return {
    identityId:        ex.identity.identityId,
    displayName:       ex.identity.displayName,
    preferredLanguage: ex.identity.preferredLanguage,
    tier:              ex.identity.tier as IdentityTier,
    deviceIds:         ex.identity.deviceIds,
    createdAt:         new Date(ex.identity.createdAt),
    lastSeenAt:        new Date(ex.identity.lastSeenAt),
  };
}

function buildDevices(ex: IdentityFixtureExample): RegisteredDevice[] {
  return ex.devices.map(d => ({
    deviceId:     d.deviceId,
    identityId:   d.identityId,
    platform:     d.platform,
    deviceName:   d.deviceName,
    registeredAt: new Date(d.registeredAt),
    lastActiveAt: new Date(d.lastActiveAt),
  }));
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('IdentityTier enum', () => {
  it('has three values', () => {
    const tiers = Object.values(IdentityTier);
    assert.strictEqual(tiers.length, 3);
  });

  it('contains Anonymous', () => {
    assert.strictEqual(IdentityTier.Anonymous, 'Anonymous');
  });

  it('contains Pseudonymous', () => {
    assert.strictEqual(IdentityTier.Pseudonymous, 'Pseudonymous');
  });

  it('contains Verified', () => {
    assert.strictEqual(IdentityTier.Verified, 'Verified');
  });

  it('tier values match fixture tierOrder', () => {
    for (const tier of fixture.assertions.tierOrder) {
      assert.ok(Object.values(IdentityTier).includes(tier as IdentityTier));
    }
  });

  it('all fixture identity tiers are valid IdentityTier values', () => {
    for (const tier of fixture.identityTiers) {
      assert.ok(Object.values(IdentityTier).includes(tier as IdentityTier));
    }
  });
});

describe('CircleIdentity schema', () => {
  for (const ex of fixture.examples) {
    it(`${ex.id} — ${ex.description}`, () => {
      const identity = buildIdentity(ex);

      assert.strictEqual(typeof identity.identityId, 'string');
      assert.ok(identity.identityId.length > 0);
      assert.strictEqual(typeof identity.displayName, 'string');
      assert.ok(Object.values(IdentityTier).includes(identity.tier));
      assert.strictEqual(Array.isArray(identity.deviceIds), true);
      assert.ok(identity.createdAt instanceof Date);
      assert.ok(identity.lastSeenAt instanceof Date);
      assert.ok(identity.lastSeenAt >= identity.createdAt);
    });
  }

  it('verified_multi_device has 3 device IDs', () => {
    const ex = fixture.examples.find(e => e.id === 'verified_multi_device')!;
    const identity = buildIdentity(ex);
    assert.strictEqual(identity.deviceIds.length, 3);
    assert.strictEqual(identity.tier, IdentityTier.Verified);
  });

  it('pseudonymous_single_device has Pseudonymous tier', () => {
    const ex = fixture.examples.find(e => e.id === 'pseudonymous_single_device')!;
    const identity = buildIdentity(ex);
    assert.strictEqual(identity.tier, IdentityTier.Pseudonymous);
    assert.strictEqual(identity.preferredLanguage, 'en');
  });

  it('anonymous_iot has null preferredLanguage', () => {
    const ex = fixture.examples.find(e => e.id === 'anonymous_iot')!;
    const identity = buildIdentity(ex);
    assert.strictEqual(identity.tier, IdentityTier.Anonymous);
    assert.strictEqual(identity.preferredLanguage, null);
  });
});

describe('RegisteredDevice schema', () => {
  it('all fixture platforms are known platform strings', () => {
    const knownPlatforms = new Set(fixture.platforms);
    for (const ex of fixture.examples) {
      for (const d of buildDevices(ex)) {
        assert.ok(knownPlatforms.has(d.platform));
      }
    }
  });

  it('all devices have matching identityId', () => {
    for (const ex of fixture.examples) {
      const devices = buildDevices(ex);
      for (const d of devices) {
        assert.strictEqual(d.identityId, ex.identity.identityId);
      }
    }
  });

  it('anonymous_iot device has null deviceName', () => {
    const ex = fixture.examples.find(e => e.id === 'anonymous_iot')!;
    const devices = buildDevices(ex);
    assert.strictEqual(devices[0].deviceName, null);
    assert.strictEqual(devices[0].platform, 'iot');
  });

  it('verified_multi_device has android, watch, windows platforms', () => {
    const ex = fixture.examples.find(e => e.id === 'verified_multi_device')!;
    const platforms = buildDevices(ex).map(d => d.platform);
    assert.ok((platforms as string[]).includes('android'));
    assert.ok((platforms as string[]).includes('watch'));
    assert.ok((platforms as string[]).includes('windows'));
  });
});
