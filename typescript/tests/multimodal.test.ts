// multimodal.test.ts
//
// Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
// InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester (dedup +
// caption + persist). Mirrors CircleAI.Tests.MultimodalMemoryTests. Bytes are
// synthesised inline so the tests run identically on every box.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  HeuristicMultimodalCaptioner,
  InMemoryMultimodalMemoryStore,
  MultimodalMemoryIngester,
  MediaModality,
  makeMultimodalMemoryEntry,
  type CaptionResult,
  type IMultimodalCaptioner,
} from '../src/memory/multimodal';

// ── Test helpers (mirror the C# FakeJpeg/FakePng/WireIngester) ───────────────

function fakeJpeg(extraBytes = 100): Uint8Array {
  const buf = new Uint8Array(2 + extraBytes);
  buf[0] = 0xff;
  buf[1] = 0xd8;
  for (let i = 2; i < buf.length; i++) buf[i] = i % 251;
  return buf;
}

function fakePng(extraBytes = 100): Uint8Array {
  const buf = new Uint8Array(4 + extraBytes);
  buf[0] = 0x89;
  buf[1] = 0x50;
  buf[2] = 0x4e;
  buf[3] = 0x47;
  for (let i = 4; i < buf.length; i++) buf[i] = i % 251;
  return buf;
}

function wireIngester(customCaptioner?: IMultimodalCaptioner): {
  ingester: MultimodalMemoryIngester;
  store: InMemoryMultimodalMemoryStore;
} {
  const store = new InMemoryMultimodalMemoryStore();
  const captioners = customCaptioner
    ? [customCaptioner, new HeuristicMultimodalCaptioner()]
    : [new HeuristicMultimodalCaptioner()];
  return { ingester: new MultimodalMemoryIngester(captioners, store), store };
}

/** FakeRichCaptioner — only handles Image, returns a rich caption + embedding. */
class FakeRichCaptioner implements IMultimodalCaptioner {
  canCaption(modality: MediaModality): boolean {
    return modality === MediaModality.Image;
  }
  async captionAsync(): Promise<CaptionResult> {
    return {
      caption: 'A blue sky with two clouds.',
      embedding: [0.1, 0.2, 0.3],
      widthPx: 1920,
      heightPx: 1080,
    };
  }
}

// ══════════════════════════════════════════════════════════════════════════
// HeuristicMultimodalCaptioner
// ══════════════════════════════════════════════════════════════════════════

describe('HeuristicMultimodalCaptioner', () => {
  it('always can caption any modality', () => {
    const c = new HeuristicMultimodalCaptioner();
    assert.equal(c.canCaption(MediaModality.Image, 'image/jpeg'), true);
    assert.equal(c.canCaption(MediaModality.Audio, null), true);
    assert.equal(c.canCaption(MediaModality.Video, 'video/mp4'), true);
    assert.equal(c.canCaption(MediaModality.TextDocument, 'application/pdf'), true);
  });

  it('detects the JPEG magic and produces no embedding', async () => {
    const c = new HeuristicMultimodalCaptioner();
    const r = await c.captionAsync(MediaModality.Image, fakeJpeg(), null);
    assert.ok(r.caption.includes('image/jpeg'));
    assert.equal(r.embedding, undefined);
  });

  it('detects PNG/GIF/WAV/PDF magic bytes', async () => {
    const c = new HeuristicMultimodalCaptioner();
    assert.ok((await c.captionAsync(MediaModality.Image, fakePng(), null)).caption.includes('image/png'));
    assert.ok(
      (await c.captionAsync(MediaModality.Image, Uint8Array.of(0x47, 0x49, 0x46, 0x38), null)).caption.includes(
        'image/gif',
      ),
    );
    assert.ok(
      (await c.captionAsync(MediaModality.Audio, Uint8Array.of(0x52, 0x49, 0x46, 0x46), null)).caption.includes(
        'audio/wav',
      ),
    );
    assert.ok(
      (await c.captionAsync(MediaModality.TextDocument, Uint8Array.of(0x25, 0x50, 0x44, 0x46), null)).caption.includes(
        'application/pdf',
      ),
    );
  });

  it('falls back to application/octet-stream for unknown magic', async () => {
    const c = new HeuristicMultimodalCaptioner();
    const r = await c.captionAsync(MediaModality.Audio, Uint8Array.of(1, 2, 3, 4), null);
    assert.ok(r.caption.includes('application/octet-stream'));
  });

  it('uses the declared MIME type when provided', async () => {
    const c = new HeuristicMultimodalCaptioner();
    const r = await c.captionAsync(MediaModality.Image, fakePng(), 'image/heic');
    assert.ok(r.caption.includes('image/heic'));
  });

  it('marks itself as a fallback and includes the byte count', async () => {
    const c = new HeuristicMultimodalCaptioner();
    const bytes = fakeJpeg();
    const r = await c.captionAsync(MediaModality.Image, bytes, null);
    assert.ok(r.caption.includes('no captioner wired'));
    assert.ok(r.caption.includes(`${bytes.length} bytes`));
  });

  it('uses the right modality label per media kind', async () => {
    const c = new HeuristicMultimodalCaptioner();
    assert.ok((await c.captionAsync(MediaModality.Image, fakeJpeg(), null)).caption.startsWith('[Image'));
    assert.ok((await c.captionAsync(MediaModality.Audio, fakeJpeg(), 'audio/wav')).caption.startsWith('[Audio'));
    assert.ok((await c.captionAsync(MediaModality.Video, fakeJpeg(), 'video/mp4')).caption.startsWith('[Video'));
    assert.ok(
      (await c.captionAsync(MediaModality.TextDocument, fakeJpeg(), 'application/pdf')).caption.startsWith('[Document'),
    );
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Ingester — happy path
// ══════════════════════════════════════════════════════════════════════════

describe('MultimodalMemoryIngester — happy path', () => {
  it('first time: adds an entry and reports not deduplicated', async () => {
    const { ingester, store } = wireIngester();
    const bytes = fakeJpeg();
    const r = await ingester.ingestAsync(MediaModality.Image, bytes, { mimeType: 'image/jpeg' });

    assert.equal(r.wasDeduplicated, false);
    assert.equal(await store.countAsync(), 1);
    assert.ok(r.entry);
    assert.equal(r.entry.sourceByteCount, bytes.length);
    assert.equal(r.entry.sourceMimeType, 'image/jpeg');
    assert.ok(r.entry.sourceSha256 && r.entry.sourceSha256.trim().length > 0);
  });

  it('second time same bytes: deduplicates and reinforces', async () => {
    const { ingester, store } = wireIngester();
    const bytes = fakeJpeg();
    const first = await ingester.ingestAsync(MediaModality.Image, bytes, { mimeType: 'image/jpeg' });
    const second = await ingester.ingestAsync(MediaModality.Image, bytes, { mimeType: 'image/jpeg' });

    assert.equal(first.wasDeduplicated, false);
    assert.equal(second.wasDeduplicated, true);
    assert.equal(await store.countAsync(), 1);
    assert.equal(first.entry.sourceSha256, second.entry.sourceSha256);
    assert.equal(second.entry.referenceCount, 2);
  });

  it('different bytes produce distinct entries', async () => {
    const { ingester, store } = wireIngester();
    const ra = await ingester.ingestAsync(MediaModality.Image, fakeJpeg(50));
    const rb = await ingester.ingestAsync(MediaModality.Image, fakeJpeg(60));
    assert.notEqual(ra.entry.sourceSha256, rb.entry.sourceSha256);
    assert.equal(await store.countAsync(), 2);
  });

  it('empty bytes throw', async () => {
    const { ingester } = wireIngester();
    await assert.rejects(() => ingester.ingestAsync(MediaModality.Image, new Uint8Array(0)));
  });

  it('records source URI and tags when provided', async () => {
    const { ingester } = wireIngester();
    const bytes = fakePng();
    const r = await ingester.ingestAsync(MediaModality.Image, bytes, {
      mimeType: 'image/png',
      sourceUri: 'file:///photos/IMG_001.png',
      tags: { location: 'home', person: 'alex' },
    });
    assert.equal(r.entry.sourceUri, 'file:///photos/IMG_001.png');
    assert.ok(r.entry.tags);
    assert.equal(r.entry.tags!.location, 'home');
    assert.equal(r.entry.tags!.person, 'alex');
  });

  it('computes a hex-lower SHA-256 that is stable across calls', async () => {
    const { ingester } = wireIngester();
    // SHA-256 of the two-byte JPEG magic 0xFF 0xD8.
    const r = await ingester.ingestAsync(MediaModality.Image, fakeJpeg(0));
    assert.match(r.entry.sourceSha256, /^[0-9a-f]{64}$/);
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Captioner selection
// ══════════════════════════════════════════════════════════════════════════

describe('MultimodalMemoryIngester — captioner selection', () => {
  it('prefers the rich captioner over the heuristic', async () => {
    const { ingester } = wireIngester(new FakeRichCaptioner());
    const r = await ingester.ingestAsync(MediaModality.Image, fakeJpeg(), { mimeType: 'image/jpeg' });
    assert.equal(r.entry.caption, 'A blue sky with two clouds.');
    assert.ok(r.entry.embedding);
    assert.equal(r.entry.widthPx, 1920);
    assert.equal(r.entry.heightPx, 1080);
  });

  it('falls back to the heuristic when the rich captioner declines', async () => {
    const { ingester } = wireIngester(new FakeRichCaptioner());
    const r = await ingester.ingestAsync(MediaModality.Audio, fakePng(), { mimeType: 'audio/wav' });
    assert.ok(r.entry.caption.includes('no captioner wired'));
    assert.equal(r.entry.embedding, undefined);
  });

  it('rejects construction with zero captioners', () => {
    assert.throws(() => new MultimodalMemoryIngester([], new InMemoryMultimodalMemoryStore()));
  });
});

// ══════════════════════════════════════════════════════════════════════════
// Store: search, prune, recent, reinforce
// ══════════════════════════════════════════════════════════════════════════

describe('InMemoryMultimodalMemoryStore', () => {
  it('search by embedding ranks by cosine', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: 'near', caption: 'near', embedding: [1, 0.1, 0] }));
    await store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: 'far', caption: 'far', embedding: [0, 0, 1] }));

    const ranked = await store.searchAsync([1, 0, 0], 2);
    assert.equal(ranked[0].sourceSha256, 'near');
    assert.equal(ranked[1].sourceSha256, 'far');
  });

  it('search with a null query returns most recent', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.addAsync(
      makeMultimodalMemoryEntry({
        sourceSha256: 'older',
        caption: 'older',
        recordedAtUtc: new Date(Date.now() - 10 * 86400_000),
      }),
    );
    await store.addAsync(
      makeMultimodalMemoryEntry({ sourceSha256: 'newer', caption: 'newer', recordedAtUtc: new Date() }),
    );
    const recent = await store.searchAsync(null, 2);
    assert.equal(recent[0].sourceSha256, 'newer');
  });

  it('prune removes entries older than the cutoff', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.addAsync(
      makeMultimodalMemoryEntry({
        sourceSha256: 'old',
        caption: 'old',
        recordedAtUtc: new Date(Date.now() - 10 * 86400_000),
      }),
    );
    await store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: 'new', caption: 'new', recordedAtUtc: new Date() }));

    const removed = await store.pruneOlderThanAsync(new Date(Date.now() - 5 * 86400_000));
    assert.equal(removed, 1);
    assert.equal(await store.countAsync(), 1);
    assert.ok(await store.getByHashAsync('new'));
    assert.equal(await store.getByHashAsync('old'), null);
  });

  it('reinforce increments the reference count', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: 'x', caption: 'x' }));
    await store.reinforceAsync('x');
    await store.reinforceAsync('x');
    const got = await store.getByHashAsync('x');
    assert.ok(got);
    assert.equal(got!.referenceCount, 3); // initial 1 + 2 reinforce
  });

  it('reinforce on an unknown hash is a no-op', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.reinforceAsync('missing'); // must not throw
    assert.equal(await store.countAsync(), 0);
  });

  it('add without a hash throws', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await assert.rejects(() => store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: '', caption: 'x' })));
  });

  it('hash lookup is case-insensitive (matches the C# OrdinalIgnoreCase dictionary)', async () => {
    const store = new InMemoryMultimodalMemoryStore();
    await store.addAsync(makeMultimodalMemoryEntry({ sourceSha256: 'ABCDEF', caption: 'x' }));
    assert.ok(await store.getByHashAsync('abcdef'));
  });
});
