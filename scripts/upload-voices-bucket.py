"""Publish voice assets to the circleai-voices BUCKET.

A BUCKET IS NOT A MODEL REPO. That distinction cost hours: buckets are S3-like
Xet object storage under huggingface.co/buckets/<org>/<name>, a different
namespace from models/datasets/spaces. Every /api/models and /resolve/main/
probe against this name returns 401 — which reads as "private" but actually
means "no model repo by that name" — and an earlier version of this script used
HfApi.upload_file(), the MODEL api, so its uploads never landed here.

Requires huggingface_hub >= 1.0 for batch_bucket_files(). The pinned 0.21.3 in
this environment has no bucket support at all.

    pip install -U "huggingface_hub>=1.0"
    export HF_TOKEN=hf_...            # or: hf auth login
    python scripts/upload-voices-bucket.py             # dry run
    python scripts/upload-voices-bucket.py --publish

Three payloads, independently selectable, because they have different licences
and different urgency:

  --dict     Open JTalk dictionary (modified BSD, clean) -> open-jtalk-dic/
  --voice    JSUT VITS (cc-by-4.0 over a research corpus) -> jsut-vits/
  --configs  the 42 rebuilt MMS model.onnx.json voice configs

WHY --configs MATTERS: mms-* folders in the bucket hold model.onnx and
tokens.txt but NO model.onnx.json. Verified on a phone 2026-08-19: the Swahili
voice downloaded fine, then OnnxTtsEngine hit its "NO VOICE CONFIG" branch, fell
back to raw code points, and ONNX Runtime threw
  Gather ... indices element out of data bounds, idx=73 must be within [-39,38]
So every MMS voice downloads and then cannot speak. The configs are the fix.
"""
import argparse, hashlib, os, pathlib, sys

BUCKET = "thegeekco/circleai-voices"
ROOT = pathlib.Path(__file__).resolve().parent.parent / "native" / "open-jtalk"
CONFIGS = pathlib.Path(os.environ.get("CIRCLEAI_MMS_CONFIGS", ""))

DICT_DIR = ROOT / "dic" / "open_jtalk_dic_utf_8-1.11"
DICT_FILES = ("sys.dic", "matrix.bin", "char.bin", "unk.dic",
              "left-id.def", "right-id.def", "pos-id.def", "rewrite.def", "COPYING")


def plan(args):
    """(local Path, remote path in bucket) pairs."""
    items = []
    if args.dict:
        for n in DICT_FILES:
            items.append((DICT_DIR / n, f"open-jtalk-dic/{n}"))
    if args.voice:
        items.append((ROOT / "jsut-vits" / "jsut_vits_prosody.onnx", "jsut-vits/model.onnx"))
    if args.configs:
        if not CONFIGS.is_dir():
            print("  --configs needs CIRCLEAI_MMS_CONFIGS=<dir of mms-xxx__model.onnx.json>")
        else:
            for p in sorted(CONFIGS.glob("mms-*__model.onnx.json")):
                voice = p.name.split("__")[0]          # mms-swh
                items.append((p, f"{voice}/model.onnx.json"))
    return items


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", action="store_true")
    ap.add_argument("--dict", action="store_true")
    ap.add_argument("--voice", action="store_true")
    ap.add_argument("--configs", action="store_true")
    a = ap.parse_args()
    if not (a.dict or a.voice or a.configs):
        a.dict = a.configs = True          # licence-clean payloads by default
        print("no payload flags: defaulting to --dict --configs (NOT the cc-by-4.0 voice)\n")

    items = plan(a)
    if not items:
        print("nothing to do"); return 1

    total, missing = 0, 0
    print(f"bucket: {BUCKET}\n")
    for local, remote in items:
        if not local.exists():
            print(f"  MISSING  {local}"); missing += 1; continue
        total += local.stat().st_size
        print(f"  {local.stat().st_size/1e6:9.1f} MB  {remote}")
    print(f"\n  {len(items)-missing} files, {total/1e6:.1f} MB")
    if missing:
        print(f"\nrefusing: {missing} file(s) missing."); return 1
    if not a.publish:
        print("\ndry run. add --publish to upload."); return 0

    try:
        from huggingface_hub import batch_bucket_files
    except ImportError:
        print("\nhuggingface_hub is too old for buckets. "
              'pip install -U "huggingface_hub>=1.0"'); return 2
    if not (os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN")):
        print("\nno HF_TOKEN in the environment (or run: hf auth login)"); return 2

    # Batched, but not transactional — the docs are explicit that a mid-batch
    # failure leaves earlier files uploaded. Chunked so a failure is small and
    # re-running is cheap; every add is idempotent on the same remote path.
    CHUNK = 8
    for i in range(0, len(items), CHUNK):
        part = items[i:i + CHUNK]
        print(f"uploading {i+1}-{i+len(part)} of {len(items)} ...", flush=True)
        batch_bucket_files(BUCKET, add=[(str(l), r) for l, r in part])
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
