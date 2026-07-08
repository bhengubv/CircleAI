// companion_session_factory.test.ts
//
// Verifies CompanionSessionFactory (CompanionSessionFactory.cs): the identity
// provider resolves a richer display name / language; without one the identity
// id is used as the display name; a blank identity id is rejected; each created
// session is a working ICompanionSession.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  CompanionSessionFactory,
  InterfaceKind,
  type IIdentityProvider,
  type ResolvedIdentity,
} from '../src/companion/index';
import type { IChatGenerator } from '../src/inference/index';
import type { IEpisodicMemoryStore, EpisodicMemoryEntry } from '../src/memory/index';
import type { IRecall } from '../src/memory/recall';
import type { MemoryHit } from '../src/memory/graph';

// ── Minimal fakes for the three required collaborators ───────────────────────

const generator: IChatGenerator = {
  async generateAsync() {
    return 'hello from the companion';
  },
  async *streamAsync() {
    yield 'hi';
  },
  // Optional members of IChatGenerator are not exercised by these tests; the
  // interface only requires generate/stream for the session's send path.
} as unknown as IChatGenerator;

class FakeEpisodicStore implements IEpisodicMemoryStore {
  entries: EpisodicMemoryEntry[] = [];
  async addAsync(entry: EpisodicMemoryEntry): Promise<void> {
    this.entries.push(entry);
  }
  async searchAsync(): Promise<readonly EpisodicMemoryEntry[]> {
    return [];
  }
  async getRecentAsync(): Promise<readonly EpisodicMemoryEntry[]> {
    return [];
  }
  async countAsync(): Promise<number> {
    return this.entries.length;
  }
  async pruneOlderThanAsync(): Promise<number> {
    return 0;
  }
}

const recall: IRecall = {
  async recallAsync(): Promise<readonly MemoryHit[]> {
    return [];
  },
};

function makeFactory(identity?: IIdentityProvider | null) {
  return new CompanionSessionFactory({
    generator,
    episodic: new FakeEpisodicStore(),
    recall,
    identity,
  });
}

describe('CompanionSessionFactory', () => {
  it('uses the identity id as display name when no identity provider is given', async () => {
    const factory = makeFactory();
    const session = await factory.createAsync('user-42', InterfaceKind.Mobile);
    assert.equal(session.identityId, 'user-42');
    assert.equal(session.interface, InterfaceKind.Mobile);
    assert.equal(session.getContext().displayName, 'user-42');
    assert.ok(session.sessionId.length > 0);
  });

  it('resolves display name + language from the identity provider', async () => {
    const resolved: ResolvedIdentity = { displayName: 'Thabo', preferredLanguage: 'zu' };
    const provider: IIdentityProvider = { async getCurrentIdentityAsync() {
      return resolved;
    } };
    const factory = makeFactory(provider);
    const session = await factory.createAsync('user-42', InterfaceKind.Web);
    const ctx = session.getContext();
    assert.equal(ctx.displayName, 'Thabo');
    assert.equal(ctx.preferredLanguage, 'zu');
  });

  it('falls back to identity id when the provider returns null', async () => {
    const provider: IIdentityProvider = { async getCurrentIdentityAsync() {
      return null;
    } };
    const factory = makeFactory(provider);
    const session = await factory.createAsync('anon', InterfaceKind.Headless);
    assert.equal(session.getContext().displayName, 'anon');
  });

  it('produces a session that actually replies', async () => {
    const factory = makeFactory();
    const session = await factory.createAsync('user-1', InterfaceKind.Desktop);
    assert.equal(await session.sendAsync('hi'), 'hello from the companion');
  });

  it('rejects a blank identity id', async () => {
    const factory = makeFactory();
    await assert.rejects(() => factory.createAsync('  ', InterfaceKind.Mobile), /identityId required/);
  });

  it('rejects missing required collaborators', () => {
    assert.throws(
      // @ts-expect-error deliberate missing generator
      () => new CompanionSessionFactory({ episodic: new FakeEpisodicStore(), recall }),
      /generator required/,
    );
  });
});
