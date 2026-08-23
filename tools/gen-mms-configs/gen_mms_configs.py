"""gen_mms_configs.py — rebuild the MMS voices' model.onnx.json sidecars.

WHY THIS FILE EXISTS AT ALL. These configs were generated once by an ad-hoc
script that was never committed, their SHA-256 pins were written into
`src/CircleAI.Core/Models/embedded_registry.json`, and the bytes were then lost.
Measured 2026-08-23: 43 of the 49 pinned `model.onnx.json` files return 404 from
the bucket, so those voices download their model and their tokens.txt and then
fail on a 2 KB sidecar that does not exist. A generator that lives only in
someone's shell is the same as no generator, so this one is committed.

WHAT THE SIDECAR HAS TO SAY, and why each field is not a guess:

  audio.sample_rate = 16000
      Read from the models themselves, not assumed: 19 of the cached MMS models
      declare `sample_rate` in their ONNX metadata and every one says 16000. The
      five that declare nothing (amh, ibo, lin, npi, tir) are the same family.
      This matters because the engine takes its rate from THIS FILE
      (`OnnxTtsEngine` line ~291: `_config?.SampleRate ?? 22_050`), so a wrong
      value writes correct audio into a wrong header — which plays at the wrong
      speed and transcribes as gibberish, exactly the shape of failure that
      looks like a broken voice. The six sidecars that DO ship today claim
      22050 for a 16 kHz model.

  phoneme_id_map["_"] = [0]
      THE PAD RULE. `OnnxTtsEngine` interleaves whatever `"_"` resolves to. In
      MMS exports the interleaving blank is id 0 REGARDLESS of what label
      upstream's tokens.txt put there — upstream commonly has a literal "_"
      entry at the END of the range (1, 17, 38, or absent). Reproducing
      tokens.txt faithfully is what made 42 voices speak fluent nonsense:
      the engine dutifully interleaved a real phoneme between every character.
      Measured on mms-swh, same sentence:
          interleaving 38  ->  "Amari alunohriano."
          interleaving 0   ->  "Habaria asubuhi rafiki angu."
      So `_` is forced to 0 here, and any upstream `_` entry is dropped so it
      cannot win the lookup.

  phoneme_type = "text"
      MMS is a grapheme model — the vocabulary IS letters, not IPA. It takes no
      phonemiser.

  inference scales
      Copied from the sidecars already in the bucket; the models ignore the
      noise terms and respond only to length_scale.

Run:  python tools/gen-mms-configs/gen_mms_configs.py [--out DIR]

Reads each voice's own tokens.txt — from the local audit cache when present,
otherwise fetched from the bucket, which is PUBLIC (writing needs a token,
reading does not).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import pathlib
import sys
import urllib.request

REPO = pathlib.Path(__file__).resolve().parents[2]
REGISTRY = REPO / "src" / "CircleAI.Core" / "Models" / "embedded_registry.json"
CACHE = REPO / "tools" / "voice-audit" / ".cache"
BUCKET = "https://huggingface.co/buckets/thegeekco/circleai-voices/resolve"

# Verified across every cached MMS model that declares one. See the module note.
MMS_SAMPLE_RATE = 16000

# The interleaving blank. Never read from tokens.txt — see THE PAD RULE above.
BLANK_ID = 0


def read_tokens(voice: str) -> list[tuple[str, int]]:
    """Return (symbol, id) pairs from a voice's tokens.txt.

    Split on the LAST space, because the symbol may itself be a space — that is
    a real entry in these vocabularies and splitting on the first space silently
    drops it, taking every word boundary with it.
    """
    cached = CACHE / f"{voice}__tokens.txt"
    if cached.exists():
        text = cached.read_text(encoding="utf-8")
    else:
        url = f"{BUCKET}/{voice}/tokens.txt"
        with urllib.request.urlopen(url, timeout=60) as r:
            text = r.read().decode("utf-8")
        cached.parent.mkdir(parents=True, exist_ok=True)
        cached.write_text(text, encoding="utf-8")

    pairs: list[tuple[str, int]] = []
    for line in text.split("\n"):
        line = line.rstrip("\r")
        if not line:
            continue
        cut = line.rfind(" ")
        if cut <= 0:
            continue
        sym, raw = line[:cut], line[cut + 1:]
        try:
            pairs.append((sym, int(raw)))
        except ValueError:
            continue
    return pairs


def read_existing_config(voice: str) -> list[tuple[str, int]]:
    """Vocabulary from a sidecar that already ships, for the voices with no
    tokens.txt.

    FIVE BUNDLES ARE THE MIRROR IMAGE of the other 42: amh, ibo, lin, npi and
    tir publish a model.onnx.json (HTTP 302) and no tokens.txt (404), where the
    rest publish tokens.txt and no sidecar. Their published sidecars are the
    ones that need correcting most — they declare 22050 for a 16 kHz model and
    put the blank at 3 — so the vocabulary is taken from them and the two
    faulty fields are then overwritten.
    """
    cached = CACHE / f"{voice}__model.onnx.json"
    if cached.exists():
        text = cached.read_text(encoding="utf-8")
    else:
        url = f"{BUCKET}/{voice}/model.onnx.json"
        with urllib.request.urlopen(url, timeout=60) as r:
            text = r.read().decode("utf-8")

    cfg = json.loads(text)
    pairs: list[tuple[str, int]] = []
    for sym, ids in cfg.get("phoneme_id_map", {}).items():
        if isinstance(ids, list) and ids:
            pairs.append((sym, int(ids[0])))
    return pairs


def build_config(voice: str) -> dict:
    try:
        pairs = read_tokens(voice)
    except Exception:
        pairs = read_existing_config(voice)
    if not pairs:
        raise RuntimeError(f"{voice}: no vocabulary in tokens.txt or the sidecar")

    id_map: dict[str, list[int]] = {}
    for sym, tid in pairs:
        # Drop any upstream "_" so it cannot beat the blank we set below. The
        # published sidecars also carry <PAD>/<EOS>/<BOS>/<BLNK> labels; those
        # are real vocabulary entries and stay.
        if sym == "_":
            continue
        id_map[sym] = [tid]

    # THE PAD RULE, applied last so nothing can override it.
    id_map["_"] = [BLANK_ID]

    return {
        "audio": {"sample_rate": MMS_SAMPLE_RATE},
        "inference": {"noise_scale": 0.667, "length_scale": 1.0, "noise_w": 0.8},
        "phoneme_type": "text",
        "phoneme_id_map": id_map,
    }


def serialise(cfg: dict) -> bytes:
    """Stable bytes, so the SHA is reproducible from the same inputs."""
    return (json.dumps(cfg, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(REPO / "src" / "CircleAI.Voice" / "VoiceConfigs"))
    args = ap.parse_args()
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    reg = json.loads(REGISTRY.read_text(encoding="utf-8"))

    voices: list[str] = []
    for m in reg["Models"]:
        if not m["Name"].startswith("MMS-"):
            continue
        for bf in m.get("BundleFiles", []):
            if bf["Name"].endswith("model.onnx.json"):
                voices.append(bf["Name"].split("/")[0])

    print(f"{len(voices)} MMS voices pin a model.onnx.json\n")

    total = 0
    written = []
    for v in sorted(set(voices)):
        try:
            cfg = build_config(v)
        except Exception as e:  # noqa: BLE001 — report and continue; one bad voice is not fatal
            print(f"  {v:<12} FAILED: {e}")
            continue
        blob = serialise(cfg)
        (out / f"{v}.model.onnx.json").write_bytes(blob)
        sha = hashlib.sha256(blob).hexdigest()
        total += len(blob)
        written.append((v, sha, len(blob), len(cfg["phoneme_id_map"])))
        print(f"  {v:<12} {len(cfg['phoneme_id_map']):>3} symbols  "
              f"{len(blob):>5} bytes  {sha[:12]}")

    print(f"\n{len(written)} configs, {total} bytes ({total/1024:.0f} KB) -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
