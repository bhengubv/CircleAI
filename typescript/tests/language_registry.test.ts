// language_registry.test.ts
//
// Verifies KnownLanguages against the 20 canonical entries in
// fixtures/language_tags.json.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
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

const fixturePath = path.join(__dirname, '..', '..', 'fixtures', 'language_tags.json');
const fixture: LanguageFixture = JSON.parse(fs.readFileSync(fixturePath, 'utf-8'));

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('KnownLanguages.ALL length', () => {
  it('contains exactly 20 languages', () => {
    assert.equal(KnownLanguages.ALL.length, 20);
  });

  it('length matches fixture assertion', () => {
    assert.equal(KnownLanguages.ALL.length, fixture.assertions.totalCount);
  });
});

describe('KnownLanguages fixture entries', () => {
  for (const entry of fixture.languages) {
    it(`bcpTag=${entry.bcpTag} (${entry.englishName})`, () => {
      const tag: LanguageTag | undefined = KnownLanguages.ALL.find(
        t => t.bcpTag === entry.bcpTag,
      );

      assert.ok(tag !== undefined, `expected language tag "${entry.bcpTag}" to be defined`);
      if (!tag) return;

      assert.equal(tag.bcpTag, entry.bcpTag);
      assert.equal(tag.englishName, entry.englishName);
      assert.equal(tag.nativeName, entry.nativeName);
      assert.equal(tag.writingSystem, entry.writingSystem as WritingSystem);
      assert.equal(tag.isRtl, entry.isRtl);
      assert.equal(tag.primaryRegion, entry.primaryRegion);
    });
  }
});

describe('KnownLanguages declaration order', () => {
  it('ALL is in the same order as the fixture', () => {
    for (let i = 0; i < fixture.languages.length; i++) {
      assert.equal(KnownLanguages.ALL[i].bcpTag, fixture.languages[i].bcpTag);
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
    assert.equal(count, fixture.assertions.africanLanguageCount);
  });
});

describe('KnownLanguages writing systems', () => {
  it('Latin is the most common writing system', () => {
    const latinCount = KnownLanguages.ALL.filter(t => t.writingSystem === WritingSystem.Latin).length;
    assert.ok(latinCount > 10, `expected latinCount > 10, got ${latinCount}`);
  });

  it('Devanagari used for Hindi', () => {
    assert.equal(KnownLanguages.Hindi.writingSystem, WritingSystem.Devanagari);
  });

  it('Ethiopic used for Amharic', () => {
    assert.equal(KnownLanguages.Amharic.writingSystem, WritingSystem.Ethiopic);
  });

  it('Han used for Mandarin', () => {
    assert.equal(KnownLanguages.Mandarin.writingSystem, WritingSystem.Han);
  });

  it('Arabic script used for Arabic', () => {
    assert.equal(KnownLanguages.Arabic.writingSystem, WritingSystem.Arabic);
  });
});

describe('KnownLanguages named constants', () => {
  it('IsiZulu has correct bcpTag', () => {
    assert.equal(KnownLanguages.IsiZulu.bcpTag, 'zu');
  });

  it('English has correct region', () => {
    assert.equal(KnownLanguages.English.primaryRegion, 'GB');
  });

  it('Sepedi has bcpTag nso', () => {
    assert.equal(KnownLanguages.Sepedi.bcpTag, 'nso');
  });

  it('Arabic is RTL', () => {
    assert.equal(KnownLanguages.Arabic.isRtl, true);
  });
});
