"""Export the JSUT VITS checkpoint to ONNX.

The checkpoint is a bare state dict, so the model graph has to be rebuilt by
espnet2 before torch can trace it. Two routes are tried, best first:

  1. espnet_onnx's TTSModelExport — knows VITS's dynamic axes and the inference
     signature, so the result takes a token sequence and returns audio.
  2. a manual torch.onnx.export over the generator's inference path, for the case
     espnet_onnx cannot load a checkpoint this vintage.

Route 2 exists because espnet_onnx pins espnet versions fairly tightly and a
version skew here would otherwise mean no export at all.
"""
import sys, pathlib, traceback
sys.stdout.reconfigure(encoding="utf-8")

SRC = pathlib.Path(r"C:\Dev\Solutions\com.bhengubv\CircleAI\native\open-jtalk\jsut-vits")
OUT = pathlib.Path(__file__).parent / "jsut-onnx"
OUT.mkdir(exist_ok=True)
CFG = SRC / "config.yaml"
PTH = SRC / "train.total_count.ave_10best.pth"

print(f"config {CFG.exists()}  weights {PTH.exists()} ({PTH.stat().st_size/1e6:.0f} MB)")


def route_espnet_onnx():
    from espnet2.bin.tts_inference import Text2Speech
    from espnet_onnx.export import TTSModelExport

    print("loading with espnet2 ...")
    t2s = Text2Speech(train_config=str(CFG), model_file=str(PTH), device="cpu")
    print("  loaded:", type(t2s.model).__name__)

    exporter = TTSModelExport(cache_dir=OUT)
    exporter.export(t2s, "jsut_vits_prosody", quantize=False)
    print("espnet_onnx export done ->", OUT)
    return True


def route_manual():
    import torch, yaml
    from espnet2.bin.tts_inference import Text2Speech

    print("loading with espnet2 (manual trace) ...")
    t2s = Text2Speech(train_config=str(CFG), model_file=str(PTH), device="cpu")
    tts = t2s.model.tts            # espnet2.gan_tts.vits.VITS
    gen = tts.generator
    gen.eval()

    class Wrap(torch.nn.Module):
        """Fixed inference signature: tokens in, waveform out.

        VITS's own inference() returns a dict and takes optional speaker/style
        arguments the JSUT single-speaker model does not use. Tracing through a
        dict return produces an unusable graph, so this pins the one path we
        call and the three scales we want controllable from C#.
        """
        def __init__(self, g):
            super().__init__()
            self.g = g

        def forward(self, text, text_lengths, noise_scale, noise_scale_dur, alpha):
            wav, _, _ = self.g.inference(
                text=text, text_lengths=text_lengths,
                noise_scale=noise_scale, noise_scale_dur=noise_scale_dur,
                alpha=alpha, max_len=None,
            )
            return wav

    w = Wrap(gen).eval()

    # ^ k o r e w a $  — a real token sequence, not zeros: VITS's duration
    # predictor branches on content, and a degenerate input can trace a graph
    # that only works for degenerate input.
    text = torch.tensor([[21, 10, 3, 2, 13, 9, 23, 2, 22]], dtype=torch.long)
    lens = torch.tensor([text.shape[1]], dtype=torch.long)
    ns   = torch.tensor(0.667, dtype=torch.float32)
    nsd  = torch.tensor(0.8, dtype=torch.float32)
    al   = torch.tensor(1.0, dtype=torch.float32)

    with torch.no_grad():
        probe = w(text, lens, ns, nsd, al)
    print(f"  eager forward ok: {tuple(probe.shape)} samples")

    dst = OUT / "jsut_vits_prosody.onnx"
    torch.onnx.export(
        w, (text, lens, ns, nsd, al), str(dst),
        input_names=["text", "text_lengths", "noise_scale", "noise_scale_dur", "alpha"],
        output_names=["wav"],
        # The eager probe returns (1, T) — samples on axis 1, not 0. Marking the
        # wrong axis dynamic bakes the probe's length into the graph, which
        # produces a model that only ever speaks one duration.
        dynamic_axes={"text": {1: "T"}, "wav": {1: "S"}},
        opset_version=17, do_constant_folding=True,
        dynamo=False,      # legacy tracer: the dynamo path needs onnxscript and
                           # buys nothing here, the graph is already static
    )
    print(f"manual export done -> {dst} ({dst.stat().st_size/1e6:.0f} MB)")
    return True


for name, fn in (("espnet_onnx", route_espnet_onnx), ("manual", route_manual)):
    print(f"\n=== route: {name} ===")
    try:
        if fn():
            print(f"\nSUCCEEDED via {name}")
            break
    except Exception:
        traceback.print_exc()
        print(f"route {name} failed, trying next")
else:
    print("\nBOTH ROUTES FAILED")
    sys.exit(1)
