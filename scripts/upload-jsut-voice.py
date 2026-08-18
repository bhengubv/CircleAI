"""Publish the Japanese voice + Open JTalk dictionary to the voices bucket.

Run it yourself — it needs an HF token, which this agent does not handle:

    export HF_TOKEN=hf_...        # or: huggingface-cli login
    python scripts/upload-jsut-voice.py            # dry run, lists what would go
    python scripts/upload-jsut-voice.py --publish  # actually uploads

LICENCE, BEFORE YOU PASS --publish. The JSUT VITS weights are cc-by-4.0 on the
ESPnet card and derive from the JSUT corpus, whose own terms are research /
non-commercial. That is outside the MIT/Apache/BSD/OFL/PD rule this project
otherwise holds to. The Open JTalk dictionary is modified BSD and is clean.
Hence --dict-only, for publishing the unambiguous half alone.

Paths and hashes match native/open-jtalk/README.md and the JSUT-VITS entry in
embedded_registry.json. Uploading under different names will break the registry's
hash check, which is the thing that makes a sideloaded voice trustworthy.
"""
import argparse, hashlib, os, pathlib, sys

REPO = "thegeekco/circleai-voices"
ROOT = pathlib.Path(__file__).resolve().parent.parent / "native" / "open-jtalk"

# (local path, path in the bucket, expected sha256 or None)
VOICE = [
    (ROOT / "jsut-vits" / "jsut_vits_prosody.onnx", "jsut-vits/model.onnx",
     "78672bceed1f62389a8b0604cba978d7f4af934ddf43f71190b4870b14ef2f57"),
]
DICT_DIR = ROOT / "dic" / "open_jtalk_dic_utf_8-1.11"
DICT = [(DICT_DIR / n, f"open-jtalk-dic/{n}", None) for n in
        ("sys.dic", "matrix.bin", "char.bin", "unk.dic",
         "left-id.def", "right-id.def", "pos-id.def", "rewrite.def", "COPYING")]


def sha256(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for blk in iter(lambda: f.read(1 << 20), b""):
            h.update(blk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--publish", action="store_true", help="actually upload")
    ap.add_argument("--dict-only", action="store_true",
                    help="upload only the BSD dictionary, not the cc-by-4.0 voice")
    a = ap.parse_args()

    items = DICT if a.dict_only else VOICE + DICT
    total, bad = 0, False
    print(f"target: {REPO}\n")
    for local, remote, want in items:
        if not local.exists():
            print(f"  MISSING  {local}"); bad = True; continue
        size = local.stat().st_size
        total += size
        note = ""
        if want:
            got = sha256(local)
            note = " sha OK" if got == want else f" SHA MISMATCH got={got[:16]}"
            if got != want: bad = True
        print(f"  {size/1e6:8.1f} MB  {remote}{note}")
    print(f"\n  total {total/1e6:.1f} MB")

    if bad:
        print("\nrefusing: a file is missing or its hash does not match the registry.")
        return 1
    if not a.publish:
        print("\ndry run. re-run with --publish to upload.")
        return 0

    token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN")
    if not token:
        print("\nno HF_TOKEN in the environment. export it, or run huggingface-cli login.")
        return 2

    from huggingface_hub import HfApi
    api = HfApi(token=token)
    for local, remote, _ in items:
        print(f"uploading {remote} ...", flush=True)
        api.upload_file(path_or_fileobj=str(local), path_in_repo=remote,
                        repo_id=REPO, repo_type="model")
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
