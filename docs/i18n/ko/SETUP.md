# BhenguAI 설정

이 라이브러리는 런타임에 네이티브 llama.cpp 바이너리가 필요합니다. 바이너리는 크기 및 라이선스 문제로 인해 번들에 포함되어 있지 않습니다.

## 네이티브 바이너리 획득

가장 간단한 방법: https://github.com/ggerganov/llama.cpp/releases 에서 미리 빌드된 llama.cpp 바이너리를 다운로드하십시오.

또는 소스에서 직접 빌드할 수 있습니다: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`.

## 플랫폼별 배치 위치

| 플랫폼 | 파일명 | 경로 |
|---|---|---|
| Windows x64 | `llama.dll` | `.exe` 파일과 동일한 디렉터리 |
| Linux x64 | `libllama.so` | 바이너리와 동일한 디렉터리 또는 `LD_LIBRARY_PATH` 내 |
| macOS arm64 | `libllama.dylib` | 바이너리와 동일한 디렉터리 |
| Android arm64 | `libllama.so` | APK 내 `lib/arm64-v8a/` 위치 |
| iOS arm64 | `libllama.dylib` | 앱 번들에 내장 |

## 모델 획득

다운로더는 ModelScope(기본)에서 Qwen 3 14B Q4_K_M 모델을 가져오며, 실패 시 HuggingFace(대체)에서 가져옵니다. 용량은 약 8GB입니다.

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## 검증

`samples/ConsoleTest`를 실행하십시오. 예상 출력: Qwen으로부터의 채팅 완성 결과.
