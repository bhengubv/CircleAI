# Circle AI — コンパニオンセッション仕様

このドキュメントは、すべての言語ポートが実装しなければならない **`ICompanionSession` ライフサイクルコントラクト** を定義します。`ICompanionSession` は、ホストアプリケーション（MAUI、Android、iOS、Web、HarmonyOS）が対話する主要な API サーフェスです。

---

## 1. コンセプト

### 1.1 セッション

`ICompanionSession` は、1 人のユーザーと B! の間の **単一の継続的な会話** を表します。セッションは作成時（最初のメッセージ）から破棄時（ユーザーがアプリを閉じるか、セッションが明示的に終了する）まで続きます。

セッション自体は **永続化されません** — `CompanionTurn` 履歴と基礎となる `AffectState`/`PersonaState` のみが保存されます。翌日に作成された新しいセッションは、ストアから同じアフェクト状態とペルソナ状態を引き継ぎます。

### 1.2 コンテキスト

`CompanionContext` は、B! が状況を把握するために必要なすべての情報を保持します:

| フィールド | 用途 |
|-------|---------|
| `UserId` | このセッションが属するユーザー |
| `AppContext` | 呼び出し元アプリ（例: `"tgn.bidbaas"`、`"tgn.tagme"`） |
| `Interface` | レスポンスのレンダリング方法（音声、ウォッチ、テキストなど） |
| `Locale` | レスポンス言語のオーバーライド |
| `Affect` | このユーザーに対する B! の現在の感情状態 |
| `Persona` | このユーザーに対して B! が学習したスタイル |
| `ActiveGoals` | B! が積極的に支援すべきゴール |

### 1.3 インターフェース駆動のレスポンス整形

`InterfaceKind` 列挙型は出力の長さとスタイルを決定します:

| 値 | 暗黙の制約 |
|-------|--------------------|
| `Text` | デフォルト — 特別な制約なし |
| `Voice` | 短い文、マークダウンなし、リストなし |
| `Watch` | 最大約 40 ワード; 1 文が望ましい |
| `Car` | 非常に短く; リストなし; ハンズフリー安全性を確保 |
| `Tv` | 会話調; 簡潔; コードブロックなし |
| `Ar` | 超短いオーバーレイ（15 ワード以下） |
| `Iot` | 単一のアクションフレーズ |

実装時には、インターフェースに適した指示をシステムプロンプトに注入することが推奨されます。

---

## 2. セッションライフサイクル

```
┌──────────────────────────────────────────────┐
│                                              │
│  1. Create session with CompanionContext      │
│                                              │
│  2. User sends a message                     │
│     a. SendAsync(text)     → CompanionTurn   │
│     b. StreamAsync(text)   → token stream    │
│                                              │
│  3. Optionally: user sends feedback          │
│     SignalFeedbackAsync(Positive|Negative)   │
│                                              │
│  4. B! may raise ProactiveMessageReady event │
│     at any time (background thread is fine)  │
│                                              │
│  5. Dispose the session when done            │
│                                              │
└──────────────────────────────────────────────┘
```

---

## 3. インターフェースコントラクト

### 3.1 `SendAsync(userMessage)`

ブロッキング（awaitable な）送受信。ターンを `History` に追加します。

**事前条件:**
- `userMessage` は null でも空でもあってはなりません。

**事後条件:**
- `History.Count` が 1 増加します。
- `AffectState` がセッション内で更新されます（正確なタイミングは実装定義ですが、次の `GetContext()` 呼び出しが返る前に行われなければなりません）。
- 生成中に何らかの `IToolBridge` 呼び出しが発生した場合、返される `CompanionTurn.UsedTools` は `true` になります。

### 3.2 `StreamAsync(userMessage)`

トークン単位のストリーミング。**履歴は** レスポンス全体が組み立てられた後（つまりストリームが完了した後）に更新されます。ストリーミング中は更新されません。

返される非同期ストリームは **部分的なトークン** を発行します — 呼び出し元はそれらを結合する必要があります。

### 3.3 `AgentAsync(task, tools?)`

マルチステップのエージェントループを実行します: モデルはツールを呼び出して推論し、最終的な答えを出すまで繰り返します。最終的なテキストレスポンスを返します。

`tools` が null または空の場合、このメソッドは単一の `GenerateAsync` 呼び出しにフォールバックします（ツールループなし）。

### 3.4 `GetContext()`

最新の `AffectState` および `PersonaState` を含む **現在の** `CompanionContext` を返します。いつでも呼び出せ、進行中の非同期呼び出しの影響を受けません。

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

`History` 内の **最新のターン** に対するユーザーフィードバックを記録します。

- `FeedbackPolarity.Positive` → `AffectState.ApplyPositiveSignal()` を呼び出して永続化
- `FeedbackPolarity.Negative` → `AffectState.ApplyNegativeSignal()` を呼び出して永続化
- `FeedbackPolarity.Correction` → 修正を記録; アフェクトの変更なし

`History` が空の場合（まだターンがない場合）、このメソッドは何もしません。

### 3.6 `ProactiveMessageReady` イベント

B! が積極的に配信するメッセージ（リマインダー、ゴールの促しなど）がある場合に発火します。このイベントは `History` に自動的に **追加されません** — ホストは `SendAsync` を呼び出すか、メッセージを別の方法で表示する必要があります。

---

## 4. `CompanionTurn` フィールド

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. エラーハンドリング

| 条件 | 期待される動作 |
|-----------|--------------------|
| `IChatGenerator` が利用不可 | `GeneratorUnavailableException`（または言語同等のもの）をスロー |
| ツール呼び出しが失敗 | `ToolResult.Success = false`; エラーをコンテキストに含めてループを継続 |
| エンベディングが利用不可 | `EpisodicMemoryEntry.Embedding = null` を保存; 失敗させない |
| `AffectStore` の書き込みが失敗 | ログに記録して継続; 呼び出し元に伝播させない |

---

## 6. 最小限の実装（テスト用）

実際の LLM バックエンドをまだ持たないユニットテストおよび言語ポートのため:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

`tests/` 内のコンパニオンセッションテストは `MockChatGenerator` を使用して、実際のモデルを必要とせずに、セッションライフサイクル、履歴管理、フィードバックルーティング、およびアフェクト変更を検証します。

---

## 7. システムプロンプト組み立て順序

C# リファレンス実装は、以下の順序でシステムプロンプトを組み立てます:

1. ベースシステムプロンプト（ハードコードされたペルソナ: "You are B!, the on-device assistant…"）
2. `AffectState.ToSystemPromptHint()` — 空でない場合に追加
3. `PersonaState.ToSystemPromptHint()` — 空でない場合に追加
4. `InterfaceKind` の制約 — 適切な場合に追加
5. `AppContext` の指示 — オプション、ホストアプリが注入

実装はフィクスチャテストが通過する限り、これらの順序を自由に変えることができます。
