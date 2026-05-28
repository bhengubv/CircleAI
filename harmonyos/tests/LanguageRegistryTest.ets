// LanguageRegistryTest.ets — KnownLanguages registry verification (ArkTS)
// Verifies all 20 languages, canonical order, RTL flag, and writing systems.
// Must match fixtures/language_tags.json.
// Uses if/throw assertion pattern (no external test framework).

import {
  KnownLanguages,
  ALL_LANGUAGES,
  WritingSystem,
  LanguageTag,
  UNKNOWN_LANGUAGE,
} from '../src/main/ets/languages/index';

// ---------------------------------------------------------------------------
// Assertion helpers
// ---------------------------------------------------------------------------

function assertEqual<T>(label: string, actual: T, expected: T): void {
  if (actual !== expected) {
    throw new Error(`FAIL ${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertTrue(label: string, condition: boolean): void {
  if (!condition) throw new Error(`FAIL ${label}: condition was false`);
}

function assertFalse(label: string, condition: boolean): void {
  if (condition) throw new Error(`FAIL ${label}: expected false, got true`);
}

// ---------------------------------------------------------------------------
// Test: ALL_LANGUAGES count
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('count_20', ALL_LANGUAGES.length, 20);
  console.log('PASS: count_exactly_20');
})();

// ---------------------------------------------------------------------------
// Test: canonical declaration order (must match fixture order)
// Fixture order: zu, st, af, sw, ha, am, yo, ig, xh, nso, tn, so, om,
//                ar, en, pt, fr, es, zh, hi
// ---------------------------------------------------------------------------

((): void => {
  const expectedOrder: string[] = [
    'zu', 'st', 'af', 'sw', 'ha', 'am', 'yo', 'ig', 'xh',
    'nso', 'tn', 'so', 'om',
    'ar',
    'en', 'pt', 'fr', 'es',
    'zh', 'hi',
  ];
  for (let i: number = 0; i < expectedOrder.length; i++) {
    assertEqual(`order[${i}]_bcp47`, ALL_LANGUAGES[i].bcp47Tag, expectedOrder[i]);
  }
  console.log('PASS: canonical_declaration_order');
})();

// ---------------------------------------------------------------------------
// Test: Arabic is the only RTL language
// ---------------------------------------------------------------------------

((): void => {
  const rtl: LanguageTag[] = ALL_LANGUAGES.filter((t: LanguageTag): boolean => t.isRtl);
  assertEqual('rtl_count', rtl.length, 1);
  assertEqual('rtl_bcp47', rtl[0].bcp47Tag, 'ar');
  assertTrue('arabic_isRtl', KnownLanguages.Arabic.isRtl);
  console.log('PASS: only_arabic_is_rtl');
})();

// ---------------------------------------------------------------------------
// Test: 13 African languages
// African regions: ZA, KE, NG, ET, SO
// IsiZulu(ZA), Sesotho(ZA), Afrikaans(ZA), Swahili(KE), Hausa(NG),
// Amharic(ET), Yoruba(NG), Igbo(NG), Xhosa(ZA), Sepedi(ZA), Setswana(ZA),
// Somali(SO), Oromo(ET) = 13
// ---------------------------------------------------------------------------

((): void => {
  const africanRegions: Set<string> = new Set<string>(['ZA', 'KE', 'NG', 'ET', 'SO']);
  const count: number = ALL_LANGUAGES.filter(
    (t: LanguageTag): boolean => africanRegions.has(t.primaryRegion)
  ).length;
  assertEqual('african_count', count, 13);
  console.log('PASS: 13_african_languages');
})();

// ---------------------------------------------------------------------------
// Test: writing system spot checks
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('amharic_ethiopic',    KnownLanguages.Amharic.writingSystem,    WritingSystem.Ethiopic);
  assertEqual('arabic_arabic',       KnownLanguages.Arabic.writingSystem,     WritingSystem.Arabic);
  assertEqual('mandarin_han',        KnownLanguages.Mandarin.writingSystem,   WritingSystem.Han);
  assertEqual('hindi_devanagari',    KnownLanguages.Hindi.writingSystem,      WritingSystem.Devanagari);
  assertEqual('english_latin',       KnownLanguages.English.writingSystem,    WritingSystem.Latin);
  assertEqual('isizulu_latin',       KnownLanguages.IsiZulu.writingSystem,    WritingSystem.Latin);
  assertEqual('sesotho_latin',       KnownLanguages.Sesotho.writingSystem,    WritingSystem.Latin);
  console.log('PASS: writing_system_spot_checks');
})();

// ---------------------------------------------------------------------------
// Test: Latin is the most common writing system (must be >10)
// ---------------------------------------------------------------------------

((): void => {
  const latinCount: number = ALL_LANGUAGES.filter(
    (t: LanguageTag): boolean => t.writingSystem === WritingSystem.Latin
  ).length;
  assertTrue('latin_most_common', latinCount > 10);
  console.log(`PASS: latin_count=${latinCount}`);
})();

// ---------------------------------------------------------------------------
// Test: named constant BCP-47 spot checks
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('isizulu_bcp47',    KnownLanguages.IsiZulu.bcp47Tag,    'zu');
  assertEqual('sesotho_bcp47',    KnownLanguages.Sesotho.bcp47Tag,    'st');
  assertEqual('afrikaans_bcp47',  KnownLanguages.Afrikaans.bcp47Tag,  'af');
  assertEqual('swahili_bcp47',    KnownLanguages.Swahili.bcp47Tag,    'sw');
  assertEqual('hausa_bcp47',      KnownLanguages.Hausa.bcp47Tag,      'ha');
  assertEqual('amharic_bcp47',    KnownLanguages.Amharic.bcp47Tag,    'am');
  assertEqual('yoruba_bcp47',     KnownLanguages.Yoruba.bcp47Tag,     'yo');
  assertEqual('igbo_bcp47',       KnownLanguages.Igbo.bcp47Tag,       'ig');
  assertEqual('xhosa_bcp47',      KnownLanguages.Xhosa.bcp47Tag,      'xh');
  assertEqual('sepedi_bcp47',     KnownLanguages.Sepedi.bcp47Tag,     'nso');
  assertEqual('setswana_bcp47',   KnownLanguages.Setswana.bcp47Tag,   'tn');
  assertEqual('somali_bcp47',     KnownLanguages.Somali.bcp47Tag,     'so');
  assertEqual('oromo_bcp47',      KnownLanguages.Oromo.bcp47Tag,      'om');
  assertEqual('arabic_bcp47',     KnownLanguages.Arabic.bcp47Tag,     'ar');
  assertEqual('english_bcp47',    KnownLanguages.English.bcp47Tag,    'en');
  assertEqual('portuguese_bcp47', KnownLanguages.Portuguese.bcp47Tag, 'pt');
  assertEqual('french_bcp47',     KnownLanguages.French.bcp47Tag,     'fr');
  assertEqual('spanish_bcp47',    KnownLanguages.Spanish.bcp47Tag,    'es');
  assertEqual('mandarin_bcp47',   KnownLanguages.Mandarin.bcp47Tag,   'zh');
  assertEqual('hindi_bcp47',      KnownLanguages.Hindi.bcp47Tag,      'hi');
  console.log('PASS: all_bcp47_tags_correct');
})();

// ---------------------------------------------------------------------------
// Test: primary region spot checks
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('english_region',    KnownLanguages.English.primaryRegion,    'GB');
  assertEqual('arabic_region',     KnownLanguages.Arabic.primaryRegion,     'SA');
  assertEqual('mandarin_region',   KnownLanguages.Mandarin.primaryRegion,   'CN');
  assertEqual('hindi_region',      KnownLanguages.Hindi.primaryRegion,      'IN');
  assertEqual('french_region',     KnownLanguages.French.primaryRegion,     'FR');
  assertEqual('portuguese_region', KnownLanguages.Portuguese.primaryRegion, 'PT');
  assertEqual('spanish_region',    KnownLanguages.Spanish.primaryRegion,    'ES');
  assertEqual('swahili_region',    KnownLanguages.Swahili.primaryRegion,    'KE');
  assertEqual('amharic_region',    KnownLanguages.Amharic.primaryRegion,    'ET');
  assertEqual('somali_region',     KnownLanguages.Somali.primaryRegion,     'SO');
  console.log('PASS: primary_region_spot_checks');
})();

// ---------------------------------------------------------------------------
// Test: native name spot checks
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('isizulu_native',    KnownLanguages.IsiZulu.nativeName,    'isiZulu');
  assertEqual('swahili_native',    KnownLanguages.Swahili.nativeName,    'Kiswahili');
  assertEqual('arabic_native',     KnownLanguages.Arabic.nativeName,     'العربية');
  assertEqual('amharic_native',    KnownLanguages.Amharic.nativeName,    'አማርኛ');
  assertEqual('hindi_native',      KnownLanguages.Hindi.nativeName,      'हिन्दी');
  assertEqual('mandarin_native',   KnownLanguages.Mandarin.nativeName,   '中文');
  assertEqual('oromo_native',      KnownLanguages.Oromo.nativeName,      'Afaan Oromoo');
  assertEqual('yoruba_native',     KnownLanguages.Yoruba.nativeName,     'Yorùbá');
  assertEqual('portuguese_native', KnownLanguages.Portuguese.nativeName, 'Português');
  assertEqual('french_native',     KnownLanguages.French.nativeName,     'Français');
  assertEqual('spanish_native',    KnownLanguages.Spanish.nativeName,    'Español');
  console.log('PASS: native_name_spot_checks');
})();

// ---------------------------------------------------------------------------
// Test: non-RTL languages
// ---------------------------------------------------------------------------

((): void => {
  const nonRtl: LanguageTag[] = ALL_LANGUAGES.filter((t: LanguageTag): boolean => !t.isRtl);
  assertEqual('non_rtl_count', nonRtl.length, 19);
  assertFalse('english_not_rtl',    KnownLanguages.English.isRtl);
  assertFalse('hindi_not_rtl',      KnownLanguages.Hindi.isRtl);
  assertFalse('mandarin_not_rtl',   KnownLanguages.Mandarin.isRtl);
  assertFalse('amharic_not_rtl',    KnownLanguages.Amharic.isRtl);
  console.log('PASS: non_rtl_languages');
})();

// ---------------------------------------------------------------------------
// Test: UNKNOWN_LANGUAGE sentinel
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('unknown_bcp47',   UNKNOWN_LANGUAGE.bcp47Tag,   'und');
  assertEqual('unknown_english', UNKNOWN_LANGUAGE.englishName, 'Unknown');
  assertEqual('unknown_native',  UNKNOWN_LANGUAGE.nativeName,  'Unknown');
  assertFalse('unknown_rtl',     UNKNOWN_LANGUAGE.isRtl);
  assertEqual('unknown_region',  UNKNOWN_LANGUAGE.primaryRegion, '');
  console.log('PASS: unknown_language_sentinel');
})();

// ---------------------------------------------------------------------------
// Test: every language in ALL_LANGUAGES is also accessible as a named constant
// ---------------------------------------------------------------------------

((): void => {
  const namedTags: LanguageTag[] = [
    KnownLanguages.IsiZulu, KnownLanguages.Sesotho, KnownLanguages.Afrikaans,
    KnownLanguages.Swahili, KnownLanguages.Hausa, KnownLanguages.Amharic,
    KnownLanguages.Yoruba, KnownLanguages.Igbo, KnownLanguages.Xhosa,
    KnownLanguages.Sepedi, KnownLanguages.Setswana, KnownLanguages.Somali,
    KnownLanguages.Oromo, KnownLanguages.Arabic, KnownLanguages.English,
    KnownLanguages.Portuguese, KnownLanguages.French, KnownLanguages.Spanish,
    KnownLanguages.Mandarin, KnownLanguages.Hindi,
  ];
  for (let i: number = 0; i < namedTags.length; i++) {
    if (ALL_LANGUAGES[i] !== namedTags[i]) {
      throw new Error(
        `FAIL named_vs_all[${i}]: ` +
        `ALL_LANGUAGES[${i}].bcp47Tag='${ALL_LANGUAGES[i].bcp47Tag}' ` +
        `!= named='${namedTags[i].bcp47Tag}'`
      );
    }
  }
  console.log('PASS: named_constants_match_all_languages');
})();

console.log('LanguageRegistryTest: ALL PASS');
