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
