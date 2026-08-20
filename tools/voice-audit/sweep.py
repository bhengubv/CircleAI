"""Sweep every catalogued voice and report which languages actually speak.

Run:
    python tools/voice-audit/sweep.py --limit 5            # smoke test
    python tools/voice-audit/sweep.py --only swh,lin,ja    # specific languages
    python tools/voice-audit/sweep.py                      # everything (slow: ~7 GB)

Needs Python 3.10 here (torch + transformers live there, not in 3.12):
    %LOCALAPPDATA%\\Programs\\Python\\Python310\\python.exe

Writes tools/voice-audit/results.json and prints a table. See README.md for why
this exists and what each verdict means.
"""
import argparse, json, pathlib, subprocess, sys, urllib.request, wave, shutil, warnings

sys.stdout.reconfigure(encoding="utf-8")
warnings.filterwarnings("ignore")
import numpy as np

ROOT = pathlib.Path(__file__).resolve().parent
REPO = ROOT.parent.parent
REGISTRY = REPO / "src" / "CircleAI.Core" / "Models" / "embedded_registry.json"
BUCKET = "https://huggingface.co/buckets/thegeekco/circleai-voices/resolve/"
CACHE = ROOT / ".cache"

# A sentence per language, in that language's own script. Deliberately short and
# ordinary — greetings and everyday phrases, the kind of thing the app actually
# says. A language with no phrase is reported NOPHRASE rather than guessed at,
# because scoring against a sentence nobody checked is how "74 languages" got
# written in the first place.
PHRASES = {
    "sw":  "Habari ya asubuhi rafiki yangu",
    "ln":  "Mbote na yo",
    "ig":  "Ndewo nna m",
    "am":  "ሰላም ጤና ይስጥልኝ",
    "ti":  "ሰላም ከመይ ኣለኻ",
    "ne":  "नमस्ते तपाईंलाई",
    "ja":  "これはテスト文です",
    "zh":  "你好，今天天气很好",
    "yue": "你好，今日天氣好好",
    "af":  "Goeie more my vriend",
    "en":  "Good morning my friend",
    "zu":  "Sawubona mngane wami",
    "xh":  "Molo mhlobo wam",
    "st":  "Dumela motswalle wa ka",
    "yo":  "E kaaro ore mi",
    "ha":  "Ina kwana abokina",
    "so":  "Subax wanaagsan saaxiib",
    "vi":  "Chào buổi sáng bạn của tôi",
    "th":  "สวัสดีตอนเช้าเพื่อนของฉัน",
    "ta":  "காலை வணக்கம் நண்பரே",
    "te":  "శుభోదయం మిత్రమా",
    "ml":  "സുപ്രഭാതം സുഹൃത്തേ",
    "mr":  "शुभ प्रभात मित्रा",
    "pa":  "ਸ਼ੁਭ ਸਵੇਰ ਮਿੱਤਰਾ",
    "gu":  "શુભ સવાર મિત્ર",
    "kn":  "ಶುಭೋದಯ ಗೆಳೆಯ",
    "my":  "မင်္ဂလာနံနက်ခင်းပါ",
    "fa":  "صبح بخیر دوست من",
    "ar":  "صباح الخير يا صديقي",
    "id":  "Selamat pagi temanku",
    "jv":  "Sugeng enjing kanca",
    "su":  "Wilujeng enjing rerencangan",
    "tl":  "Magandang umaga kaibigan",
    "fr":  "Bonjour mon ami",
    "es":  "Buenos días amigo mío",
    "pt":  "Bom dia meu amigo",
    "ru":  "Доброе утро мой друг",
    "nl":  "Goedemorgen mijn vriend",
    "hi":  "सुप्रभात मेरे मित्र",
    "bn":  "সুপ্রভাত আমার বন্ধু",
    "ur":  "صبح بخیر میرے دوست",
    "si":  "සුබ උදෑසනක් මිතුරා",
    "ko":  "좋은 아침이야 친구",
}

# Two-letter catalogue tag -> ISO-639-3 code MMS ASR uses for its adapter.
ASR_LANG = {
    "sw": "swh", "ln": "lin", "ig": "ibo", "am": "amh", "ti": "tir", "ne": "npi",
    "ja": "jpn", "zh": "cmn", "yue": "yue", "af": "afr", "en": "eng", "zu": "zul",
    "xh": "xho", "st": "sot", "yo": "yor", "ha": "hau", "so": "som", "vi": "vie",
    "th": "tha", "ta": "tam", "te": "tel", "ml": "mal", "mr": "mar", "pa": "pan",
    "gu": "guj", "kn": "kan", "my": "mya", "fa": "fas", "ar": "ara", "id": "ind",
    "jv": "jav", "su": "sun", "tl": "tgl", "fr": "fra", "es": "spa", "pt": "por",
    "ru": "rus", "nl": "nld", "hi": "hin", "bn": "ben", "ur": "urd", "si": "sin",
    "ko": "kor",
}


def fetch(remote: str) -> pathlib.Path | None:
    """Pull a bucket file into the cache. None when the bucket has no such file."""
    p = CACHE / remote.replace("/", "__")
    if p.exists() and p.stat().st_size > 200:
        return p
    CACHE.mkdir(exist_ok=True)
    # Bucket folders are lowercase; the registry carries mixed case for some
    # Piper voices (piper-en_US-lessac-high). Try as written, then lowered —
    # otherwise a voice that exists reads as NOVOICE on a naming detail.
    for candidate in (remote, remote.lower()):
        try:
            with urllib.request.urlopen(BUCKET + candidate, timeout=900) as r, open(p, "wb") as o:
                shutil.copyfileobj(r, o)
            return p
        except Exception:
            p.unlink(missing_ok=True)
    return None


def encode(text: str, pm: dict) -> tuple[list[int], int, int]:
    """Ids the way OnnxTtsEngine builds them: pad, then symbol+pad per character.

    THE PAD IS WHATEVER "_" RESOLVES TO — not a fixed number. It is id 0 in the
    sherpa/MMS exports and 3 (<BLNK>) in the Piper-family ones, and pointing it
    at an ordinary vocab entry is what made 42 voices speak fluent nonsense.
    """
    pad = pm.get("_")
    pad = list(pad) if isinstance(pad, list) else ([pad] if pad is not None else [])
    chars = [c for c in text.lower() if not c.isspace()]
    ids, mapped = list(pad), 0
    for ch in chars:
        v = pm.get(ch)
        if v is None:
            continue
        ids += (v if isinstance(v, list) else [v]) + pad
        mapped += 1
    return ids, mapped, len(chars)


def lexicon_encode(text: str, lex_path: pathlib.Path, tok_path: pathlib.Path):
    """Ids for a LEXICON-driven voice — the family with no model.onnx.json.

    Chinese, Cantonese and Japanese voices carry a word->phoneme table as a file
    instead of a phoneme_id_map, which is what lets them ship without a
    phonemizer. LexiconTokeniser does this in the app; the sweep reported them
    NOVOICE simply because it only knew how to read a config.

    Longest-match over the lexicon, then tokens.txt for the ids, blank-
    interleaved (these exports are add_blank=1).
    """
    TOK = {}
    for line in tok_path.read_text(encoding="utf-8").splitlines():
        parts = line.rstrip().split(" ")
        if len(parts) == 2:
            TOK.setdefault(parts[0], int(parts[1]))
    LEX = {}
    for line in lex_path.read_text(encoding="utf-8").splitlines():
        parts = [x for x in line.rstrip().split(" ") if x]
        if len(parts) >= 2:
            # MeloTTS carries TONE as a parallel channel: "一 y i 1 1" is
            # phonemes y,i with tones 1,1. The trailing all-numeric half is the
            # tone track. Cantonese takes the other approach and writes tone into
            # the phoneme itself, so it has no numeric tail and this leaves it be.
            body = parts[1:]
            half = len(body) // 2
            if half and all(t.isdigit() for t in body[half:]):
                LEX.setdefault(parts[0], (body[:half], [int(t) for t in body[half:]]))
            else:
                LEX.setdefault(parts[0], (body, None))
    if not TOK or not LEX:
        return [], 0, len(text), []

    widest = max(len(k) for k in LEX)
    xs, ts, i, mapped = [], [], 0, 0
    while i < len(text):
        for L in range(min(widest, len(text) - i), 0, -1):
            word = text[i:i + L]
            if word in LEX:
                phones, tones = LEX[word]
                for n, ph in enumerate(phones):
                    if ph in TOK:
                        xs.append(TOK[ph])
                        ts.append(tones[n] if tones and n < len(tones) else 0)
                i += L; mapped += L
                break
        else:
            ch = text[i]
            if ch in TOK:
                xs.append(TOK[ch]); ts.append(0); mapped += 1
            i += 1

    ids, tone_ids = [0], [0]
    for a, t in zip(xs, ts):
        ids += [a, 0]; tone_ids += [t, 0]
    return ids, mapped, len([c for c in text if not c.isspace()]), tone_ids


def synth(model_path: pathlib.Path, ids: list[int], cfg: dict,
          sid: int = 0, langid: int | None = None, tones: list[int] | None = None) -> tuple[np.ndarray, int]:
    import onnxruntime as ort
    s = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    names = {i.name for i in s.get_inputs()}
    inf = cfg.get("inference", {})
    ns, ls, nw = (float(inf.get("noise_scale", .667)), float(inf.get("length_scale", 1.)),
                  float(inf.get("noise_w", .8)))
    x = np.array([ids], dtype=np.int64)
    n = np.array([x.shape[1]], dtype=np.int64)
    # Three layouts ship in this catalogue and all three are in use.
    if "x" in names:                                    # sherpa / MMS
        feed = {"x": x, "x_length": n,
                "noise_scale": np.array([ns], "float32"),
                "length_scale": np.array([ls], "float32"),
                "noise_scale_w": np.array([nw], "float32")}
    elif "input_ids" in names:                          # transformers VITS
        feed = {"input_ids": x, "attention_mask": np.ones_like(x)}
    else:                                               # Piper
        feed = {"input": x, "input_lengths": n, "scales": np.array([ns, ls, nw], "float32")}
    # Multi-speaker / multi-lingual VITS declares these separately. A voice that
    # serves 11 languages through ONE graph needs to be TOLD which one; feeding
    # the default 0 would score every SA language as Afrikaans and call ten of
    # them broken.
    # MeloTTS: x_lengths (plural) plus a parallel tone channel. A tone array
    # out of step with the phonemes would not fail — it would mispronounce every
    # syllable after the drift, in a language where tone IS the word.
    if "x_lengths" in names:
        feed.pop("x_length", None)
        feed["x_lengths"] = n
    if "tones" in names:
        t = (tones or [0] * len(ids))[:len(ids)]
        t += [0] * (len(ids) - len(t))
        feed["tones"] = np.array([t], dtype=np.int64)
    if "sid" in names:
        feed["sid"] = np.array([sid], dtype=np.int64)
    if "langid" in names:
        feed["langid"] = np.array([langid if langid is not None else 0], dtype=np.int64)

    y = s.run(None, feed)[0].ravel().astype(np.float32)

    # THE MODEL KNOWS ITS OWN SAMPLE RATE, and for lexicon voices it is the ONLY
    # place that says so — they ship no model.onnx.json, so cfg is empty and the
    # 16000 default was silently wrong: melo-zh is 44100 and vits-yue is 22050.
    # Correct audio written into a header claiming 16 kHz plays 2.75x too slow
    # and transcribes as gibberish, which is exactly how a labelling mistake
    # masquerades as a broken voice.
    rate = cfg.get("audio", {}).get("sample_rate")
    if not rate:
        meta = s.get_modelmeta().custom_metadata_map
        rate = int(meta.get("sample_rate", 0)) or 16000
    return y, rate


STT_EXE = REPO / "tools" / "stt-hear" / "bin" / "Release" / "net10.0" / "stt-hear.exe"


def whisper_hear(wav: pathlib.Path, lang2: str) -> str | None:
    """Second-opinion recogniser for languages mms-1b-all has no adapter for.

    mms-1b-all covers ~1100 languages but not every one this catalogue serves —
    it declined cmn and yue, which whisper handles well. Neither recogniser is
    universal, so the sweep asks the one that can actually hear the language and
    records WHICH answered, because a verdict is only as good as its judge.
    """
    if not STT_EXE.exists():
        return None
    try:
        out = subprocess.run([str(STT_EXE), str(wav), lang2], capture_output=True,
                             text=True, encoding="utf-8", errors="replace", timeout=300).stdout
    except Exception:
        return None
    for line in out.splitlines():
        if line.startswith("HEARD"):
            return line.split(":", 1)[1].strip().strip('"') if ":" in line else None
    return None


class Judge:
    """MMS ASR, loaded once. Returns None when no adapter covers the language."""

    def __init__(self):
        from transformers import Wav2Vec2ForCTC, AutoProcessor
        self.proc = AutoProcessor.from_pretrained("facebook/mms-1b-all")
        self.model = Wav2Vec2ForCTC.from_pretrained("facebook/mms-1b-all")

    def hear(self, wav: pathlib.Path, lang3: str) -> str | None:
        import soundfile as sf, torch
        audio, rate = sf.read(str(wav), dtype="float32")
        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        if rate != 16000:
            m = int(len(audio) * 16000 / rate)
            audio = np.interp(np.linspace(0, len(audio) - 1, m),
                              np.arange(len(audio)), audio).astype("float32")
        try:
            self.proc.tokenizer.set_target_lang(lang3)
            self.model.load_adapter(lang3)
        except Exception:
            return None                                  # no adapter -> UNJUDGED
        iv = self.proc(audio, sampling_rate=16000, return_tensors="pt")
        with torch.no_grad():
            logits = self.model(**iv).logits
        return self.proc.decode(np.argmax(logits.numpy(), axis=-1)[0])


def cer(ref: str, hyp: str) -> float:
    a = [c for c in ref.lower() if c.isalnum()]
    b = [c for c in hyp.lower() if c.isalnum()]
    if not a:
        return 1.0
    d = np.zeros((len(a) + 1, len(b) + 1), dtype=int)
    d[:, 0] = np.arange(len(a) + 1)
    d[0, :] = np.arange(len(b) + 1)
    for i in range(1, len(a) + 1):
        for j in range(1, len(b) + 1):
            d[i, j] = min(d[i - 1, j] + 1, d[i, j - 1] + 1,
                          d[i - 1, j - 1] + (a[i - 1] != b[j - 1]))
    return d[len(a), len(b)] / len(a)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--only", default="")
    ap.add_argument("--pass-cer", type=float, default=0.45,
                    help="fallback absolute CER, used only when no control can be built")
    ap.add_argument("--controls", type=int, default=3,
                    help="noise controls per voice; the floor is their median")
    ap.add_argument("--max-cer", type=float, default=1.0,
                    help="hard fail above this, whatever the floor says")
    ap.add_argument("--margin", type=float, default=0.7,
                    help="fraction of the voice's own noise floor the real CER must beat")
    a = ap.parse_args()

    reg = json.loads(REGISTRY.read_text(encoding="utf-8"))
    voices = [m for m in reg["Models"] if m.get("Modality") == "Tts"]

    # One voice per language: the catalogue's own selector prefers higher
    # QualityRank, so mirror that rather than inventing a rule here.
    best: dict[str, dict] = {}
    for v in sorted(voices, key=lambda m: -(m.get("QualityRank") or 0)):
        for tag in (v.get("Language") or "").split(","):
            best.setdefault(tag.strip(), v)
    best.pop("", None)

    wanted = [t.strip() for t in a.only.split(",") if t.strip()] or sorted(best)
    if a.limit:
        wanted = wanted[:a.limit]

    judge, results = None, []
    for tag in wanted:
        v = best.get(tag)
        row = {"language": tag, "voice": v["Name"] if v else None, "verdict": "NOVOICE"}
        if v is None:
            results.append(row); continue

        ref = PHRASES.get(tag)
        if not ref:
            row["verdict"] = "NOPHRASE"; results.append(row); continue

        # USE THE NAMES THE REGISTRY GIVES, do not assume "model.onnx". Piper
        # voices ship en_US-lessac-high.onnx, Cantonese ships
        # vits-cantonese-hf-xiaomaiiwn.onnx. Guessing the filename reported 15
        # voices as NOVOICE that are sitting in the bucket returning 200 —
        # understating real coverage and sending someone to re-upload files that
        # were already there.
        files = [b["Name"] for b in v["BundleFiles"]]
        folder = files[0].split("/")[0]
        onnx = next((f for f in files if f.endswith(".onnx")), None)
        conf = next((f for f in files if f.endswith(".onnx.json")), None)
        model = fetch(onnx) if onnx else None
        cfg_p = fetch(conf) if conf else None

        # No config? It may be the lexicon family rather than a missing asset.
        lex, tok = (next((f for f in files if f.endswith("lexicon.txt")), None),
                    next((f for f in files if f.endswith("tokens.txt")), None))
        lex_p = fetch(lex) if (cfg_p is None and lex) else None
        tok_p = fetch(tok) if (cfg_p is None and tok) else None
        has_lexicon = lex_p is not None and tok_p is not None

        if model is None or (cfg_p is None and not has_lexicon):
            row["verdict"] = "NOVOICE"
            row["detail"] = (f"no .onnx.json in bundle ({folder})" if conf is None
                             else f"{conf} absent from bucket") if model else (
                             f"no .onnx in bundle ({folder})" if onnx is None
                             else f"{onnx} absent from bucket")
            results.append(row); continue

        cfg = json.loads(cfg_p.read_text(encoding="utf-8")) if cfg_p else {}
        tones = None
        if cfg.get("phoneme_id_map"):
            ids, mapped, total = encode(ref, cfg["phoneme_id_map"])
        else:
            ids, mapped, total, tones = lexicon_encode(ref, lex_p, tok_p)
        row["mapped"] = f"{mapped}/{total}"
        if mapped == 0:
            row["verdict"] = "UNMAPPED"
            row["detail"] = "vocabulary cannot represent this script (needs romanisation)"
            results.append(row); continue

        # A multilingual bundle ships the map from language tag to langid.
        langid = None
        lids = fetch(f"{folder}/language_ids.json")
        if lids is not None:
            try:
                table = json.loads(lids.read_text(encoding="utf-8"))
                # The bundle keys these by ISO-639-3 ("afr"), the registry by
                # 2-letter tag ("af"). ASR_LANG already holds that mapping.
                want = {tag.lower(), (ASR_LANG.get(tag) or "").lower()} - {""}
                for k, val in table.items():
                    if str(k).strip().lower() in want:
                        langid = int(val); break
                    if str(val).strip().lower() in want:
                        langid = int(k); break
            except Exception:
                pass
            if langid is None:
                row["verdict"] = "UNJUDGED"
                row["detail"] = f"multilingual voice has no langid for '{tag}'"
                results.append(row); continue
        row["langid"] = langid
        y, rate = synth(model, ids, cfg, langid=langid, tones=tones)
        secs = len(y) / rate
        row["seconds"] = round(secs, 2)
        if secs < 0.4:
            row["verdict"] = "SILENT"; results.append(row); continue

        wav = CACHE / f"{folder}.{tag}.wav"
        with wave.open(str(wav), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
            w.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())

        lang3 = ASR_LANG.get(tag)
        if lang3 is None:
            row["verdict"] = "UNJUDGED"; results.append(row); continue
        judge = judge or Judge()
        heard = judge.hear(wav, lang3)
        row["judge"] = "mms-1b-all"
        if heard is None:
            heard = whisper_hear(wav, tag)          # no MMS adapter -> try whisper
            row["judge"] = "whisper"
        if not heard:
            row["verdict"] = "UNJUDGED"
            row["detail"] = f"no recogniser covers '{tag}' (mms adapter '{lang3}' declined)"
            row.pop("judge", None)
            results.append(row); continue

        row["heard"] = heard
        row["cer"] = round(cer(ref, heard), 2)

        # THE VOICE'S OWN NOISE FLOOR, not a threshold I picked.
        #
        # An absolute CER gate measures the recogniser as much as the voice: MMS
        # ASR is CTC with no language model, so it spells phonetically and
        # inflates CER on correct audio. Afrikaans scored 0.47 for
        # "ak goie moore me friem" — unmistakably "Goeie more my vriend" — and
        # failed a 0.45 gate that Zulu passed at 0.39. That gate was ranking
        # spelling luck.
        #
        # So each voice is compared against ITSELF: the same model synthesising
        # the same number of RANDOM in-vocabulary tokens. That control is
        # genuinely meaningless speech in this voice, through this recogniser,
        # in this language — every confound the absolute gate could not separate
        # is present in both numbers and cancels.
        #
        # A voice that is saying the sentence scores well below its own floor.
        # A voice that is not scores level with it.
        # MEDIAN OF SEVERAL CONTROLS, NOT ONE. A single control was not a floor,
        # it was a coin toss: VITS resamples noise every run, so both the real
        # CER and the control moved, and the RATIO moved more than either. On the
        # full sweep that flipped Nepali (a known-broken merged vocabulary) to
        # SPEAKS and Afrikaans and Xhosa to GIBBERISH. Replacing an arbitrary
        # constant with an unstable measurement was better reasoning and still
        # not sound.
        rng = np.random.default_rng(12345)          # fixed: reruns must agree
        vocab = [v for vs in cfg.get("phoneme_id_map", {}).values()
                 for v in (vs if isinstance(vs, list) else [vs])]
        floor = None
        if len(vocab) > 4 and len(ids) > 2:
            samples = []
            for _ in range(a.controls):
                noise = [int(x) for x in rng.choice(vocab, size=len(ids))]
                try:
                    ny, nrate = synth(model, noise, cfg, langid=langid)
                    if len(ny) / nrate < 0.3:
                        continue
                    nwav = CACHE / f"{folder}.{tag}.noise.wav"
                    with wave.open(str(nwav), "wb") as w:
                        w.setnchannels(1); w.setsampwidth(2); w.setframerate(nrate)
                        w.writeframes((np.clip(ny, -1, 1) * 32767).astype("<i2").tobytes())
                    nheard = (judge.hear(nwav, lang3) if row.get("judge") != "whisper"
                              else whisper_hear(nwav, tag))
                    if nheard is not None:
                        samples.append(cer(ref, nheard))
                except Exception:
                    continue
            if samples:
                floor = round(float(np.median(samples)), 2)
                row["floor_spread"] = [round(x, 2) for x in sorted(samples)]

        row["floor"] = floor

        # A TRANSCRIPT LONGER THAN THE REFERENCE IS NOT THE REFERENCE. CER above
        # 1.0 means more edits than there are characters to edit — the recogniser
        # heard something else entirely. Igbo passed the relative test at cer 3.89
        # because its noise floor happened to be 8.67; that is two kinds of
        # garbage being compared, not evidence. No floor can rescue this.
        if row["cer"] > a.max_cer:
            row["verdict"] = "GIBBERISH"
            row["basis"] = f"cer {row['cer']} > {a.max_cer} (not the sentence at any floor)"
        elif floor is None:
            # No usable control — fall back to the flag, and say so.
            row["verdict"] = "SPEAKS" if row["cer"] <= a.pass_cer else "GIBBERISH"
            row["basis"] = f"absolute cer<={a.pass_cer} (no control)"
        else:
            # Comfortably clear of its own noise: it is saying the sentence.
            row["verdict"] = "SPEAKS" if row["cer"] <= floor * a.margin else "GIBBERISH"
            row["basis"] = f"cer {row['cer']} vs floor {floor} x{a.margin}"
        results.append(row)
        print(f"  {tag:4} {row['verdict']:9} cer={row.get('cer','-'):<5} "
              f"floor={row.get('floor') if row.get('floor') is not None else '-':<5} "
              f"[{row.get('judge','?')}] {heard[:36]}", flush=True)

    # MERGE, DO NOT CLOBBER. A partial run (--only, --limit) used to overwrite the
    # whole table, so `--only af` reduced a full 78-row sweep to one row and the
    # real numbers were gone. Rows for languages this run did not touch are kept
    # exactly as they were; rows it did touch are replaced.
    out = ROOT / "results.json"
    merged: dict[str, dict] = {}
    if out.exists():
        try:
            for row in json.loads(out.read_text(encoding="utf-8")):
                if row.get("language"):
                    merged[row["language"]] = row
        except Exception:
            merged = {}                      # unreadable: start clean rather than fail
    stale = set(merged) - {r["language"] for r in results}
    for r in results:
        merged[r["language"]] = r
    out.write_text(json.dumps([merged[k] for k in sorted(merged)], ensure_ascii=False, indent=2),
                   encoding="utf-8")
    if stale:
        print(f"  kept {len(stale)} row(s) from an earlier run, not re-tested now")
    counts: dict[str, int] = {}
    for r in results:                        # this run only; results.json holds the union
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1
    print("\n  " + "  ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    speaks = counts.get("SPEAKS", 0)
    print(f"\n  LANGUAGES THE APP MAY CLAIM: {speaks} of {len(results)} tested")


if __name__ == "__main__":
    main()
