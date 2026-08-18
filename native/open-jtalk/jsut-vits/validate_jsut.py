"""Validate the exported JSUT VITS against human-referenced sentences.

Runs the same chain the phone will run — Open JTalk labels through the prosody
tokenisation into the ONNX graph — and scores the result with the whisper the
product listens with. The Python here mirrors OpenJTalkProsodyTokeniser.cs; if
the two disagree, this file is the reference and the C# is wrong.
"""
import sys, ctypes, pathlib, re, subprocess, wave
sys.stdout.reconfigure(encoding="utf-8")
import numpy as np, onnxruntime as ort

HERE = pathlib.Path(__file__).parent
NAT  = pathlib.Path(r"C:\Dev\Solutions\com.bhengubv\CircleAI\native\open-jtalk")
DLL  = NAT / "build" / "windows" / "Release" / "openjtalk_g2p.dll"
DIC  = NAT / "dic" / "open_jtalk_dic_utf_8-1.11"
ONNX = HERE / "jsut-onnx" / "jsut_vits_prosody.onnx"
OUT  = HERE / "jsut-out"; OUT.mkdir(exist_ok=True)
STT  = r"C:\Dev\Solutions\com.bhengubv\CircleAI\tools\stt-hear\bin\Release\net10.0\stt-hear.exe"
RATE = 22050

VOCAB = ["<blank>","<unk>","a","o","i","[","#","u","]","e","k","n","t","r","s",
         "N","m","_","sh","d","g","^","$","w","cl","h","y","b","j","ts","ch","z",
         "p","f","ky","ry","gy","hy","ny","by","my","py","v","dy","?","ty","<sos/eos>"]
ID = {s: i for i, s in enumerate(VOCAB)}
ABSENT = -50

lib = ctypes.CDLL(str(DLL))
lib.openjtalk_g2p_open.restype = ctypes.c_void_p
lib.openjtalk_g2p_open.argtypes = [ctypes.c_char_p]
lib.openjtalk_labels.restype = ctypes.c_int
lib.openjtalk_labels.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
lib.openjtalk_g2p.restype = ctypes.c_int
lib.openjtalk_g2p.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int]
# Without argtypes, ctypes passes the handle back as a Python int and a 64-bit
# pointer overflows the default c_int conversion.
lib.openjtalk_g2p_close.argtypes = [ctypes.c_void_p]
lib.openjtalk_g2p_close.restype = None

H = lib.openjtalk_g2p_open(str(DIC).encode("utf-8"))
if not H:
    print(f"FAIL: dictionary not opened at {DIC}"); sys.exit(1)
print(f"dictionary opened: {DIC.name}")


def labels_of(text):
    buf = ctypes.create_string_buffer(1 << 20)
    n = lib.openjtalk_labels(H, text.encode("utf-8"), buf, len(buf))
    if n <= 0: return []
    return buf.value.decode("utf-8").split("\n")


def num(pat, lab):
    m = re.search(pat, lab)
    return int(m.group(1)) if m else ABSENT


def prosody(labels):
    """ESPnet pyopenjtalk_g2p_prosody. Mirror of the C# tokeniser."""
    out = []
    N = len(labels)
    for n, cur in enumerate(labels):
        m = re.search(r"\-(.*?)\+", cur)
        if not m: continue
        p3 = m.group(1)
        if len(p3) == 1 and p3 in "AEIOU":
            p3 = p3.lower()
        if p3 == "sil":
            if n == 0: out.append("^")
            elif n == N - 1: out.append("?" if num(r"!(\d+)_", cur) == 1 else "$")
            continue
        if p3 == "pau":
            out.append("_"); continue
        out.append(p3)
        a1 = num(r"/A:([0-9\-]+)\+", cur)
        a2 = num(r"\+(\d+)\+", cur)
        a3 = num(r"\+(\d+)/", cur)
        f1 = num(r"/F:(\d+)_", cur)
        a2n = num(r"\+(\d+)\+", labels[n + 1]) if n + 1 < N else ABSENT
        carries = (len(p3) == 1 and p3 in "aeiouAEIOUN") or p3 == "cl"
        if a3 == 1 and a2n == 1 and carries: out.append("#")
        elif a1 == 0 and a2n == a2 + 1 and a2 != f1: out.append("]")
        elif a2 == 1 and a2n == 2: out.append("[")
    return out


def cer(r, h):
    r = [c for c in r if c.isalnum()]; h = [c for c in h if c.isalnum()]
    d = np.zeros((len(r)+1, len(h)+1), dtype=int)
    d[:, 0] = np.arange(len(r)+1); d[0, :] = np.arange(len(h)+1)
    for i in range(1, len(r)+1):
        for j in range(1, len(h)+1):
            d[i, j] = min(d[i-1, j]+1, d[i, j-1]+1, d[i-1, j-1] + (r[i-1] != h[j-1]))
    return d[len(r), len(h)] / max(1, len(r))


def hear(p):
    o = subprocess.run([STT, str(p), "ja"], capture_output=True, text=True,
                       encoding="utf-8", errors="replace").stdout
    for l in o.splitlines():
        if l.startswith("HEARD : "): return l[8:].strip().strip('"')
    return ""


so = ort.SessionOptions(); so.intra_op_num_threads = 4
S = ort.InferenceSession(str(ONNX), so, providers=["CPUExecutionProvider"])
print("onnx inputs :", [(i.name, i.shape) for i in S.get_inputs()])

CASES = [
    ("これはテスト文ですこの機械が日本語をちゃんと聞き取れているかどうかを計ります", "reazon-2"),
    ("日本語ちゃんと聞き取れてますか",                                             "reazon-1-head"),
    ("こんにちは。フランスの首都はパリです。",                                     "toy"),
]

print()
for ref, tag in CASES:
    labs = labels_of(ref)
    syms = prosody(labs)
    unk  = [s for s in syms if s not in ID]
    ids  = np.array([[ID.get(s, 1) for s in syms]], dtype=np.int64)

    y = S.run(None, {"text": ids,
                     "text_lengths": np.array([ids.shape[1]], dtype=np.int64),
                     "noise_scale": np.array(0.667, dtype=np.float32),
                     "noise_scale_dur": np.array(0.8, dtype=np.float32),
                     "alpha": np.array(1.0, dtype=np.float32)})[0].ravel().astype(np.float32)

    p = OUT / f"{tag}.wav"
    with wave.open(str(p), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
        w.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())

    back = hear(p)
    print(f"[{tag}]  ref  {ref}")
    print(f"          {len(labs)} labels -> {len(syms)} tokens, unk={len(unk)} {unk[:6]}")
    print(f"          sym  {' '.join(syms[:34])}{' ...' if len(syms) > 34 else ''}")
    print(f"          {len(y)/RATE:.1f}s   CER {cer(ref, back):.2f}   {back}")
    print()

lib.openjtalk_g2p_close(H)
print(f"wavs in {OUT}")
