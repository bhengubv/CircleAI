# voice-audit — the number has to be measured, not declared

The app says "74 languages, spoken out loud". On 2026-08-19 that was not true:
of 78 catalogued language codes, roughly a dozen actually spoke correctly on a
clean install. Not because voices were missing — they downloaded fine — but
because three separate defects all produced *plausible audio that was not the
sentence*:

- Japanese dropped bare kana (CER 0.42) — a zh-dominant lexicon
- 42 MMS voices had no `model.onnx.json`, then had one pointing `_` at an
  ordinary vocab entry instead of the blank
- `mms-npi` carries a vocabulary merged from Hebrew and Devanagari

None of them crashed. Every acoustic check — level, duration, noise floor —
passed. A listener who does not speak the language cannot tell, which is exactly
why the count looked honest.

## The rule

A language counts ONLY if it has been synthesised, transcribed by a recogniser
that genuinely covers it, and scored. Nothing is counted because it is in the
catalogue.

## Why the recogniser matters as much as the voice

whisper-tiny returned `""` for Igbo and mojibake for Lingala — a verdict about
the recogniser, not the voices. `facebook/mms-1b-all` carries per-language
adapters for ~1100 languages and judged both correctly (`mbate na yo` against
`Mbote na yo`). Where no adapter exists, the language is reported UNJUDGED
rather than counted or failed.

## Verdicts

| verdict   | meaning |
|-----------|---------|
| SPEAKS    | transcript matches the reference closely enough to be that sentence |
| GIBBERISH | audio produced, transcript unrelated — the dangerous case |
| SILENT    | little or no audio (usually 0 characters mapped) |
| UNMAPPED  | the vocabulary cannot represent the language's script (needs uroman) |
| NOVOICE   | no bundle, or the bundle is missing files |
| UNJUDGED  | no recogniser covers this language — never counted as passing |

Only SPEAKS counts toward the number the app is allowed to print.
