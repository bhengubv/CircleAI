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
    try:
        with urllib.request.urlopen(BUCKET + remote, timeout=900) as r, open(p, "wb") as o:
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


def synth(model_path: pathlib.Path, ids: list[int], cfg: dict) -> tuple[np.ndarray, int]:
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
    y = s.run(None, feed)[0].ravel().astype(np.float32)
    return y, cfg.get("audio", {}).get("sample_rate", 16000)


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
                    help="CER at or below which a language counts as SPEAKS")
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

        folder = v["BundleFiles"][0]["Name"].split("/")[0]
        model = fetch(f"{folder}/model.onnx")
        cfg_p = fetch(f"{folder}/model.onnx.json")
        if model is None or cfg_p is None:
            row["verdict"] = "NOVOICE"
            row["detail"] = "model.onnx.json absent from bucket" if model else "model absent"
            results.append(row); continue

        cfg = json.loads(cfg_p.read_text(encoding="utf-8"))
        ids, mapped, total = encode(ref, cfg.get("phoneme_id_map", {}))
        row["mapped"] = f"{mapped}/{total}"
        if mapped == 0:
            row["verdict"] = "UNMAPPED"
            row["detail"] = "vocabulary cannot represent this script (needs romanisation)"
            results.append(row); continue

        y, rate = synth(model, ids, cfg)
        secs = len(y) / rate
        row["seconds"] = round(secs, 2)
        if secs < 0.4:
            row["verdict"] = "SILENT"; results.append(row); continue

        wav = CACHE / f"{folder}.wav"
        with wave.open(str(wav), "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
            w.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())

        lang3 = ASR_LANG.get(tag)
        if lang3 is None:
            row["verdict"] = "UNJUDGED"; results.append(row); continue
        judge = judge or Judge()
        heard = judge.hear(wav, lang3)
        if heard is None:
            row["verdict"] = "UNJUDGED"; results.append(row); continue

        row["heard"] = heard
        row["cer"] = round(cer(ref, heard), 2)
        row["verdict"] = "SPEAKS" if row["cer"] <= a.pass_cer else "GIBBERISH"
        results.append(row)
        print(f"  {tag:4} {row['verdict']:9} cer={row.get('cer','-'):<5} {heard[:48]}", flush=True)

    (ROOT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2),
                                       encoding="utf-8")
    counts: dict[str, int] = {}
    for r in results:
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1
    print("\n  " + "  ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    speaks = counts.get("SPEAKS", 0)
    print(f"\n  LANGUAGES THE APP MAY CLAIM: {speaks} of {len(results)} tested")


if __name__ == "__main__":
    main()
