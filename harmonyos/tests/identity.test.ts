// identity.test.ts
//
// Verifies IdentityTier enum, CircleIdentity schema, RegisteredDevice schema,
// and fixture examples from fixtures/identity.json — HarmonyOS/ArkTS port.

import * as fs from 'fs';
import * as path from 'path';
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

const fixturePath = path.resolve(__dirname, '../../fixtures/identity.json');
const fixture: IdentityFixture = JSON.parse(fs.readFileSync(fixturePath, 'utf-8'));

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
  test('has three values', () => {
    const tiers = Object.values(IdentityTier);
    expect(tiers.length).toBe(3);
  });

  test('contains Anonymous', () => {
    expect(IdentityTier.Anonymous).toBe('Anonymous');
  });

  test('contains Pseudonymous', () => {
    expect(IdentityTier.Pseudonymous).toBe('Pseudonymous');
  });

  test('contains Verified', () => {
    expect(IdentityTier.Verified).toBe('Verified');
  });

  test('tier values match fixture tierOrder', () => {
    for (const tier of fixture.assertions.tierOrder) {
      expect(Object.values(IdentityTier)).toContain(tier);
    }
  });

  test('all fixture identity tiers are valid IdentityTier values', () => {
    for (const tier of fixture.identityTiers) {
      expect(Object.values(IdentityTier)).toContain(tier);
    }
  });
});

describe('CircleIdentity schema', () => {
  test.each(fixture.examples)('$id — $description', (ex) => {
    const identity = buildIdentity(ex);

    expect(typeof identity.identityId).toBe('string');
    expect(identity.identityId.length).toBeGreaterThan(0);
    expect(typeof identity.displayName).toBe('string');
    expect(Object.values(IdentityTier)).toContain(identity.tier);
    expect(Array.isArray(identity.deviceIds)).toBe(true);
    expect(identity.createdAt instanceof Date).toBe(true);
    expect(identity.lastSeenAt instanceof Date).toBe(true);
    expect(identity.lastSeenAt >= identity.createdAt).toBe(true);
  });

  test('verified_multi_device has 3 device IDs', () => {
    const ex = fixture.examples.find(e => e.id === 'verified_multi_device')!;
    const identity = buildIdentity(ex);
    expect(identity.deviceIds.length).toBe(3);
    expect(identity.tier).toBe(IdentityTier.Verified);
  });

  test('pseudonymous_single_device has Pseudonymous tier', () => {
    const ex = fixture.examples.find(e => e.id === 'pseudonymous_single_device')!;
    const identity = buildIdentity(ex);
    expect(identity.tier).toBe(IdentityTier.Pseudonymous);
    expect(identity.preferredLanguage).toBe('en');
  });

  test('anonymous_iot has null preferredLanguage', () => {
    const ex = fixture.examples.find(e => e.id === 'anonymous_iot')!;
    const identity = buildIdentity(ex);
    expect(identity.tier).toBe(IdentityTier.Anonymous);
    expect(identity.preferredLanguage).toBeNull();
  });
});

describe('RegisteredDevice schema', () => {
  test('all fixture platforms are known platform strings', () => {
    const knownPlatforms = new Set(fixture.platforms);
    for (const ex of fixture.examples) {
      for (const d of buildDevices(ex)) {
        expect(knownPlatforms.has(d.platform)).toBe(true);
      }
    }
  });

  test('all devices have matching identityId', () => {
    for (const ex of fixture.examples) {
      const devices = buildDevices(ex);
      for (const d of devices) {
        expect(d.identityId).toBe(ex.identity.identityId);
      }
    }
  });

  test('anonymous_iot device has null deviceName', () => {
    const ex = fixture.examples.find(e => e.id === 'anonymous_iot')!;
    const devices = buildDevices(ex);
    expect(devices[0].deviceName).toBeNull();
    expect(devices[0].platform).toBe('iot');
  });

  test('verified_multi_device has android, watch, windows platforms', () => {
    const ex = fixture.examples.find(e => e.id === 'verified_multi_device')!;
    const platforms = buildDevices(ex).map(d => d.platform);
    expect(platforms).toContain('android');
    expect(platforms).toContain('watch');
    expect(platforms).toContain('windows');
  });
});
