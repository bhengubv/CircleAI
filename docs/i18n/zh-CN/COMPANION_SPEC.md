# Circle AI — 伴侣会话规范

本文档定义了所有语言移植版本必须实现的 **ICompanionSession 生命周期契约**。`ICompanionSession` 是宿主应用程序（MAUI、Android、iOS、Web、HarmonyOS）所交互的主要 API 接口。

---

## 1. 概念

### 1.1 会话

`ICompanionSession` 代表用户与 B! 之间的**一次连续对话**。它从创建（第一条消息）开始，到销毁（用户关闭应用或会话被显式结束）为止。

会话本身**不会被持久化** — 只有 `CompanionTurn` 历史记录以及底层的 `AffectState`/`PersonaState` 会被存储。第二天创建的新会话会从存储中恢复相同的情感状态和角色状态。

### 1.2 上下文

`CompanionContext` 携带了 B! 保持基础所需的一切信息：

| 字段 | 用途 |
|-------|---------|
| `UserId` | 此会话所属的用户 |
| `AppContext` | 调用方应用（例如 `"tgn.bidbaas"`、`"tgn.tagme"`） |
| `Interface` | 响应的渲染方式（语音、手表、文本……） |
| `Locale` | 覆盖响应所用语言 |
| `Affect` | B! 针对该用户的当前情感状态 |
| `Persona` | B! 为该用户学习到的风格 |
| `ActiveGoals` | B! 应主动协助的目标 |

### 1.3 由界面驱动的响应塑形

`InterfaceKind` 枚举决定输出长度和风格：

| 值 | 隐含约束 |
|-------|--------------------|
| `Text` | 默认 — 无特殊约束 |
| `Voice` | 短句，无 markdown，无列表 |
| `Watch` | 最多约 40 个单词；优先使用单句 |
| `Car` | 极短；无列表；适合免视野操作 |
| `Tv` | 对话风格；简短；无代码块 |
| `Ar` | 超短叠加层（≤ 15 个单词） |
| `Iot` | 单个动作短语 |

建议各实现将适合当前界面的指令注入到系统提示中。

---

## 2. 会话生命周期

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

## 3. 接口契约

### 3.1 `SendAsync(userMessage)`

阻塞式（可等待）的发送-接收。将本轮对话追加到 `History`。

**前置条件：**
- `userMessage` 不得为 null 或空字符串。

**后置条件：**
- `History.Count` 增加 1。
- `AffectState` 在会话内部被更新（确切时机由实现定义，但必须在下一次 `GetContext()` 调用返回之前完成）。
- 如果在生成过程中发生了任何 `IToolBridge` 调用，则返回的 `CompanionTurn.UsedTools` 为 `true`。

### 3.2 `StreamAsync(userMessage)`

逐 token 流式传输。**History 在完整响应组装完毕后更新**（即流结束后），而非流式传输过程中。

返回的异步流发出**部分 token** — 调用方必须自行拼接。

### 3.3 `AgentAsync(task, tools?)`

运行多步骤智能体循环：模型调用工具并进行推理，直至产生最终答案。返回最终文本响应。

如果 `tools` 为 null 或为空，该方法将回退为单次 `GenerateAsync` 调用（无工具循环）。

### 3.4 `GetContext()`

返回**当前的** `CompanionContext`，包括最新的 `AffectState` 和 `PersonaState`。可在任何时刻调用；不受正在进行的异步调用影响。

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

为 `History` 中**最近一轮**对话记录用户反馈。

- `FeedbackPolarity.Positive` → 调用 `AffectState.ApplyPositiveSignal()` 并持久化
- `FeedbackPolarity.Negative` → 调用 `AffectState.ApplyNegativeSignal()` 并持久化
- `FeedbackPolarity.Correction` → 记录纠正内容；不改变情感状态

如果 `History` 为空（尚无任何轮次），此方法为空操作。

### 3.6 `ProactiveMessageReady` 事件

当 B! 有消息需要主动推送时触发（提醒、目标提示等）。该事件**不会**自动追加到 `History` — 宿主必须调用 `SendAsync` 或以其他方式呈现该消息。

---

## 4. `CompanionTurn` 字段

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. 错误处理

| 条件 | 预期行为 |
|-----------|--------------------|
| `IChatGenerator` 不可用 | 抛出 `GeneratorUnavailableException`（或对应语言的等效异常） |
| 工具调用失败 | `ToolResult.Success = false`；将错误包含在上下文中；继续循环 |
| 嵌入向量不可用 | 存储 `EpisodicMemoryEntry.Embedding = null`；不抛出错误 |
| `AffectStore` 写入失败 | 记录日志并继续；不向调用方暴露 |

---

## 6. 最小可行实现（用于测试）

对于尚未接入真实 LLM 后端的单元测试及语言移植版本：

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

`tests/` 目录中的伴侣会话测试使用 `MockChatGenerator` 来验证会话生命周期、历史记录管理、反馈路由以及情感状态变更，无需接入真实模型。

---

## 7. 系统提示组装顺序

参考 C# 实现按以下顺序组装系统提示：

1. 基础系统提示（硬编码角色："You are B!, the on-device assistant…"）
2. `AffectState.ToSystemPromptHint()` — 若非空则追加
3. `PersonaState.ToSystemPromptHint()` — 若非空则追加
4. `InterfaceKind` 约束 — 按需追加
5. `AppContext` 指令 — 可选，由宿主应用注入

只要固件测试通过，各实现可自由调整顺序。
