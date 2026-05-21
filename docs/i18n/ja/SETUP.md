# BhenguAI セットアップ

このライブラリは実行時にネイティブの llama.cpp バイナリを必要とします。サイズおよびライセンス上の理由からバンドルには含まれていません。

## ネイティブバイナリの取得

最も簡単な方法: https://github.com/ggerganov/llama.cpp/releases からビルド済みの llama.cpp バイナリをダウンロードします。

またはソースからビルドする場合: `git clone https://github.com/ggerganov/llama.cpp && cd llama.cpp && cmake -B build && cmake --build build --config Release`

## プラットフォームごとの配置場所

| プラットフォーム | ファイル名 | パス |
|---|---|---|
| Windows x64 | `llama.dll` | `.exe` と同じディレクトリ |
| Linux x64 | `libllama.so` | バイナリと同じディレクトリ、または `LD_LIBRARY_PATH` 内 |
| macOS arm64 | `libllama.dylib` | バイナリと同じディレクトリ |
| Android arm64 | `libllama.so` | APK内の `lib/arm64-v8a/` |
| iOS arm64 | `libllama.dylib` | アプリバンドル内に埋め込み |

## モデルの取得

ダウンローダーは Qwen 3 14B Q4_K_M をModelScope（優先）またはHuggingFace（フォールバック）から取得します。約8GBです。

```csharp
var loader = new LocalModelLoader();
var modelPath = await loader.DownloadModelAsync("Qwen3-14B-Q4");
```

## 動作確認

`samples/ConsoleTest` を実行してください。Qwen からのチャット補完が出力されれば成功です。
