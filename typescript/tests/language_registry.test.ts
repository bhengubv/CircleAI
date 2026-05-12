// language_registry.test.ts
//
// Verifies KnownLanguages against the 20 canonical entries in
// fixtures/language_tags.json.

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
    totalCount:          number;
    rtlLanguages:        string[];
    africanLanguageCount: number;
    regionCodes:         string[];
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
  test('contains exactly 20 languages', () => {
    expect(KnownLanguages.ALL.length).toBe(20);
  });

  test('length matches fixture assertion', () => {
    expect(KnownLanguages.ALL.length).toBe(fixture.assertions.totalCount);
  });
});

describe('KnownLanguages fixture entries', () => {
  test.each(fixture.languages)(
    'bcpTag=$bcpTag ($englishName)',
    (entry) => {
      // Find the matching tag in ALL
      const tag: LanguageTag | undefined = KnownLanguages.ALL.find(
        t => t.bcpTag === entry.bcpTag,
      );

      expect(tag).toBeDefined();
      if (!tag) return; // narrow type

      expect(tag.bcpTag).toBe(entry.bcpTag);
      expect(tag.englishName).toBe(entry.englishName);
      expect(tag.nativeName).toBe(entry.nativeName);
      expect(tag.writingSystem).toBe(entry.writingSystem as WritingSystem);
      expect(tag.isRtl).toBe(entry.isRtl);
      expect(tag.primaryRegion).toBe(entry.primaryRegion);
    },
  );
});

describe('KnownLanguages declaration order', () => {
  test('ALL is in the same order as the fixture', () => {
    for (let i = 0; i < fixture.languages.length; i++) {
      expect(KnownLanguages.ALL[i].bcpTag).toBe(fixture.languages[i].bcpTag);
    }
  });
});

describe('KnownLanguages RTL languages', () => {
  test('only Arabic is RTL', () => {
    const rtl = KnownLanguages.ALL.filter(t => t.isRtl).map(t => t.bcpTag);
    expect(rtl).toEqual(fixture.assertions.rtlLanguages);
  });
});

describe('KnownLanguages African languages', () => {
  test('13 African languages present', () => {
    const africanRegions = new Set(['ZA', 'KE', 'NG', 'ET', 'SO']);
    const count = KnownLanguages.ALL.filter(t => africanRegions.has(t.primaryRegion)).length;
    expect(count).toBe(fixture.assertions.africanLanguageCount);
  });
});

describe('KnownLanguages writing systems', () => {
  test('Latin is the most common writing system', () => {
    const latinCount = KnownLanguages.ALL.filter(t => t.writingSystem === WritingSystem.Latin).length;
    expect(latinCount).toBeGreaterThan(10);
  });

  test('Devanagari used for Hindi', () => {
    expect(KnownLanguages.Hindi.writingSystem).toBe(WritingSystem.Devanagari);
  });

  test('Ethiopic used for Amharic', () => {
    expect(KnownLanguages.Amharic.writingSystem).toBe(WritingSystem.Ethiopic);
  });

  test('Han used for Mandarin', () => {
    expect(KnownLanguages.Mandarin.writingSystem).toBe(WritingSystem.Han);
  });

  test('Arabic script used for Arabic', () => {
    expect(KnownLanguages.Arabic.writingSystem).toBe(WritingSystem.Arabic);
  });
});

describe('KnownLanguages named constants', () => {
  test('IsiZulu has correct bcpTag', () => {
    expect(KnownLanguages.IsiZulu.bcpTag).toBe('zu');
  });

  test('English has correct region', () => {
    expect(KnownLanguages.English.primaryRegion).toBe('GB');
  });

  test('Sepedi has bcpTag nso', () => {
    expect(KnownLanguages.Sepedi.bcpTag).toBe('nso');
  });

  test('Arabic is RTL', () => {
    expect(KnownLanguages.Arabic.isRtl).toBe(true);
  });
});
