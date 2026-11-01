# Third-party notices

CircleAI's own code is MIT (see [LICENSE](LICENSE)). Some models it fetches onto the
device come under terms that ask for credit; this file gives it. Data that ships inside
the library carries its licence beside it instead (for example
`src/CircleAI/Voice/Data/nchlt/LICENSE.txt`).

## WeSpeaker-ResNet34-LM: "who said what" in a transcribed recording

- **What it does here:** turns a stretch of speech into a voice vector, so a transcript
  can say which voice spoke each line (`Diarisation`, `OnnxSpeakerIdentity`).
- **File fetched:** `onnx/model.onnx` from
  [onnx-community/wespeaker-voxceleb-resnet34-LM](https://huggingface.co/onnx-community/wespeaker-voxceleb-resnet34-LM),
  26,535,549 bytes, SHA-256
  `3955447b0499dc9e0a4541a895df08b03c69098eba4e56c02b5603e9f7f4fcbb`.
- **Where it comes from:** an ONNX conversion of
  [pyannote/wespeaker-voxceleb-resnet34-LM](https://huggingface.co/pyannote/wespeaker-voxceleb-resnet34-LM),
  which wraps the `wespeaker-voxceleb-resnet34-LM` pretrained model from
  [WeSpeaker](https://github.com/wenet-e2e/wespeaker).
- **Licence:** [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/).
  WeSpeaker's pretrained models follow the licence of the dataset they were trained on,
  and this one was trained on VoxCeleb, whose licence is CC BY 4.0.
- **Trained on:** the [VoxCeleb](https://mm.kaist.ac.kr/datasets/voxceleb/) speaker
  recognition datasets.
- **Changes:** none. The file is fetched as published, checked against the hash above,
  and run unmodified.
- **Credit:** Hongji Wang, Chengdong Liang, Shuai Wang, Zhengyang Chen, Binbin Zhang,
  Xu Xiang, Yanlei Deng and Yanmin Qian, "WeSpeaker: A research and production oriented
  speaker embedding learning toolkit", ICASSP 2023; and Hervé Bredin, pyannote.audio.

## Granite-Embedding-107M-Multilingual: search by meaning

- **What it does here:** turns a line of a kept recording, and a search, into vectors so a
  line can be found by what it means (`OnnxTextEmbedder`, `MeaningIndex`).
- **Files fetched:** `model.onnx` and `sentencepiece.bpe.model` from
  [ibm-granite/granite-embedding-107m-multilingual](https://huggingface.co/ibm-granite/granite-embedding-107m-multilingual),
  by IBM.
- **Licence:** [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0). Its model
  card states the training data is permissively licensed or IBM's own, and that MS-MARCO was
  left out for its non-commercial licence.
- **Changes:** none. The files are fetched as published, checked against their hashes, and
  run unmodified.

## Kokoro-82M: the Italian voice

- **What it does here:** speaks Italian, the one of its languages no other voice in the
  catalogue covers (`KokoroTtsEngine`).
- **Files fetched:** `model_quantized.onnx`, `tokenizer.json` and `if_sara.bin` from
  release [`kokoro-82m-v1.0`](https://github.com/bhengubv/circleai-voices/releases/tag/kokoro-82m-v1.0)
  of our own voice store, `bhengubv/circleai-voices` - unmodified copies of
  [onnx-community/Kokoro-82M-v1.0-ONNX](https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX),
  an ONNX conversion of [hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M).
  That repository's README carries the same attribution.
- **Licence:** [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).
- **Changes:** none. The files are mirrored as published, checked against their hashes,
  and run unmodified.
