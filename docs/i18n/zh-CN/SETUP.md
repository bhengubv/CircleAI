# BhenguAI 安装配置

本库在运行时需要原生 llama.cpp 二进制文件。这些文件未随包附带（原因：体积与许可证）。

## 获取原生二进制文件

最简单的方式：从 https://github.com/ggerganov/llama.cpp/releases 下载预构建的 llama.cpp 二进制文件。

或从源码构建：`git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`。

## 各平台放置位置

| 平台 | 文件名 | 路径 |
|---|---|---|
| Windows x64 | `llama.dll` | 与你的 `.exe` 同目录 |
| Linux x64 | `libllama.so` | 与你的可执行文件同目录，或放入 `LD_LIBRARY_PATH` |
| macOS arm64 | `libllama.dylib` | 与你的可执行文件同目录 |
| Android arm64 | `libllama.so` | 放入 APK 的 `lib/arm64-v8a/` 目录 |
| iOS arm64 | `libllama.dylib` | 嵌入应用包中 |

## 获取模型

下载器会从 ModelScope（主要源）或 HuggingFace（备用源）拉取 Qwen 3 14B Q4_K_M。约 8 GB。

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## 验证

运行 `samples/ConsoleTest`。预期输出：来自 Qwen 的聊天补全结果。
