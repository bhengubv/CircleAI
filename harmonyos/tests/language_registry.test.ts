// language_registry.test.ts
//
// Verifies KnownLanguages against the 20 canonical entries in
// fixtures/language_tags.json — HarmonyOS/ArkTS port.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { KnownLanguages, WritingSystem, type LanguageTag } from '../src/languages';

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

interface LanguageFixtureEntry {
  bcpTag:        string;
  englishName:   string;
  nativeName:    string;
  writingSystem: string;
  isRtl:         boolean;
  primaryRegion: string;
}

interface LanguageFixture {
  languages: LanguageFixtureEntry[];
  assertions: {
    totalCount:           number;
    rtlLanguages:         string[];
    africanLanguageCount: number;
    regionCodes:          string[];
  };
}

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = resolve(__dirname, '../../fixtures/language_tags.json');
const fixture: LanguageFixture = JSON.parse(readFileSync(fixturePath, 'utf-8'));

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('KnownLanguages.ALL length', () => {
  it('contains exactly 20 languages', () => {
    assert.strictEqual(KnownLanguages.ALL.length, 20);
  });

  it('length matches fixture assertion', () => {
    assert.strictEqual(KnownLanguages.ALL.length, fixture.assertions.totalCount);
  });
});

describe('KnownLanguages fixture entries', () => {
  for (const entry of fixture.languages) {
    it(`bcpTag=${entry.bcpTag} (${entry.englishName})`, () => {
      const tag: LanguageTag | undefined = KnownLanguages.ALL.find(
        t => t.bcpTag === entry.bcpTag,
      );

      assert.ok(tag !== undefined);
      if (!tag) return;

      assert.strictEqual(tag.bcpTag, entry.bcpTag);
      assert.strictEqual(tag.englishName, entry.englishName);
      assert.strictEqual(tag.nativeName, entry.nativeName);
      assert.strictEqual(tag.writingSystem, entry.writingSystem as WritingSystem);
      assert.strictEqual(tag.isRtl, entry.isRtl);
      assert.strictEqual(tag.primaryRegion, entry.primaryRegion);
    });
  }
});

describe('KnownLanguages declaration order', () => {
  it('ALL is in the same order as the fixture', () => {
    for (let i = 0; i < fixture.languages.length; i++) {
      assert.strictEqual(KnownLanguages.ALL[i].bcpTag, fixture.languages[i].bcpTag);
    }
  });
});

describe('KnownLanguages RTL languages', () => {
  it('only Arabic is RTL', () => {
    const rtl = KnownLanguages.ALL.filter(t => t.isRtl).map(t => t.bcpTag);
    assert.deepStrictEqual(rtl, fixture.assertions.rtlLanguages);
  });
});

describe('KnownLanguages African languages', () => {
  it('13 African languages present', () => {
    const africanRegions = new Set(['ZA', 'KE', 'NG', 'ET', 'SO']);
    const count = KnownLanguages.ALL.filter(t => africanRegions.has(t.primaryRegion)).length;
    assert.strictEqual(count, fixture.assertions.africanLanguageCount);
  });
});

describe('KnownLanguages writing systems', () => {
  it('Latin is the most common writing system', () => {
    const latinCount = KnownLanguages.ALL.filter(t => t.writingSystem === WritingSystem.Latin).length;
    assert.ok(latinCount > 10);
  });

  it('Devanagari used for Hindi', () => {
    assert.strictEqual(KnownLanguages.Hindi.writingSystem, WritingSystem.Devanagari);
  });

  it('Ethiopic used for Amharic', () => {
    assert.strictEqual(KnownLanguages.Amharic.writingSystem, WritingSystem.Ethiopic);
  });

  it('Han used for Mandarin', () => {
    assert.strictEqual(KnownLanguages.Mandarin.writingSystem, WritingSystem.Han);
  });

  it('Arabic script used for Arabic', () => {
    assert.strictEqual(KnownLanguages.Arabic.writingSystem, WritingSystem.Arabic);
  });
});

describe('KnownLanguages named constants', () => {
  it('IsiZulu has correct bcpTag', () => {
    assert.strictEqual(KnownLanguages.IsiZulu.bcpTag, 'zu');
  });

  it('English has correct region', () => {
    assert.strictEqual(KnownLanguages.English.primaryRegion, 'GB');
  });

  it('Sepedi has bcpTag nso', () => {
    assert.strictEqual(KnownLanguages.Sepedi.bcpTag, 'nso');
  });

  it('Arabic is RTL', () => {
    assert.strictEqual(KnownLanguages.Arabic.isRtl, true);
  });
});
