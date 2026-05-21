# Circle AI — Especificação de Sessão do Companion

Este documento define o **contrato de ciclo de vida de `ICompanionSession`** que todas as
implementações de linguagem devem seguir. `ICompanionSession` é a superfície de API primária
com a qual os aplicativos host (MAUI, Android, iOS, Web, HarmonyOS) interagem.

---

## 1. Conceitos

### 1.1 Sessão

Um `ICompanionSession` representa uma **conversa contínua única** entre um
usuário e o B!. Ela vai desde a criação (primeira mensagem) até o descarte (o usuário fecha o
aplicativo ou a sessão é encerrada explicitamente).

As sessões **não são persistidas em si mesmas** — apenas o histórico de `CompanionTurn` e os
estados subjacentes `AffectState`/`PersonaState` são armazenados. Uma nova sessão criada no
dia seguinte retoma os mesmos estados de afeto e persona dos repositórios.

### 1.2 Contexto

`CompanionContext` carrega tudo o que o B! precisa para se manter contextualizado:

| Campo | Finalidade |
|-------|---------|
| `UserId` | A qual usuário esta sessão pertence |
| `AppContext` | O aplicativo que está fazendo a chamada (ex.: `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | Como a resposta será renderizada (voz, relógio, texto…) |
| `Locale` | Substituição de idioma para as respostas |
| `Affect` | Estado emocional atual do B! para este usuário |
| `Persona` | Estilo aprendido do B! para este usuário |
| `ActiveGoals` | Objetivos com os quais o B! deve auxiliar proativamente |

### 1.3 Formatação de resposta orientada por interface

O enum `InterfaceKind` determina o comprimento e o estilo da saída:

| Valor | Restrição implícita |
|-------|--------------------|
| `Text` | Padrão — sem restrições especiais |
| `Voice` | Frases curtas, sem markdown, sem listas |
| `Watch` | Máximo de ~40 palavras; frase única preferida |
| `Car` | Muito curto; sem listas; seguro para uso sem olhar para a tela |
| `Tv` | Conversacional; breve; sem blocos de código |
| `Ar` | Sobreposições ultra-curtas (≤ 15 palavras) |
| `Iot` | Frase de ação única |

Recomenda-se que as implementações injetem instruções adequadas à interface no
prompt de sistema.

---

## 2. Ciclo de vida da sessão

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

## 3. Contrato de interface

### 3.1 `SendAsync(userMessage)`

Envio-recebimento bloqueante (aguardável). Acrescenta o turno ao `History`.

**Pré-condições:**
- `userMessage` não deve ser nulo nem vazio.

**Pós-condições:**
- `History.Count` aumenta em 1.
- `AffectState` é atualizado dentro da sessão (o momento exato é definido pela implementação,
  mas deve ocorrer antes que a próxima chamada a `GetContext()` retorne).
- O `CompanionTurn.UsedTools` retornado é `true` se alguma invocação de `IToolBridge`
  ocorreu durante a geração.

### 3.2 `StreamAsync(userMessage)`

Streaming token por token. **O `History` é atualizado** após a resposta completa ser montada
(ou seja, após o término do stream), não durante.

O stream assíncrono retornado emite **tokens parciais** — os chamadores devem concatená-los.

### 3.3 `AgentAsync(task, tools?)`

Executa um loop agêntico de múltiplas etapas: o modelo chama ferramentas e raciocina até
produzir uma resposta final. Retorna o texto final da resposta.

Se `tools` for nulo ou vazio, o método recorre a uma única chamada `GenerateAsync`
(sem loop de ferramentas).

### 3.4 `GetContext()`

Retorna o `CompanionContext` **atual**, incluindo os últimos `AffectState` e
`PersonaState`. Pode ser chamado a qualquer momento; não é afetado por chamadas assíncronas
em andamento.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

Registra o feedback do usuário para o **turno mais recente** no `History`.

- `FeedbackPolarity.Positive` → chama `AffectState.ApplyPositiveSignal()` e persiste
- `FeedbackPolarity.Negative` → chama `AffectState.ApplyNegativeSignal()` e persiste
- `FeedbackPolarity.Correction` → registra a correção; sem mutação de afeto

Se `History` estiver vazio (nenhum turno ainda), este método não faz nada.

### 3.6 Evento `ProactiveMessageReady`

Dispara quando o B! tem uma mensagem para entregar proativamente (lembrete, incentivo de
objetivo etc.). O evento **não** adiciona ao `History` automaticamente — o host deve chamar
`SendAsync` ou apresentar a mensagem de outra forma.

---

## 4. Campos de `CompanionTurn`

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. Tratamento de erros

| Condição | Comportamento esperado |
|-----------|--------------------|
| `IChatGenerator` indisponível | Lançar `GeneratorUnavailableException` (ou equivalente na linguagem) |
| Falha na invocação de ferramenta | `ToolResult.Success = false`; incluir erro no contexto; continuar o loop |
| Embedding indisponível | Armazenar `EpisodicMemoryEntry.Embedding = null`; não falhar |
| Falha na escrita do `AffectStore` | Registrar em log e continuar; não expor ao chamador |

---

## 6. Implementação mínima viável (testes)

Para testes unitários e implementações de linguagem que ainda não possuem um backend real de LLM:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

Os testes de sessão do companion em `tests/` usam um `MockChatGenerator` para verificar o
ciclo de vida da sessão, o gerenciamento do histórico, o roteamento de feedback e as mutações
de afeto sem exigir um modelo real.

---

## 7. Ordem de montagem do prompt de sistema

A implementação de referência em C# monta o prompt de sistema nesta ordem:

1. Prompt de sistema base (persona fixada: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — acrescentado se não estiver vazio
3. `PersonaState.ToSystemPromptHint()` — acrescentado se não estiver vazio
4. Restrições de `InterfaceKind` — acrescentadas conforme apropriado
5. Instruções de `AppContext` — opcionais, injetadas pelo aplicativo host

As implementações têm liberdade para ordenar esses itens de forma diferente, desde que os
testes de fixture passem.
