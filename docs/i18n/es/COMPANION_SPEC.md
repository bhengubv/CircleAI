# Circle AI — Especificación de Sesión de Compañero

Este documento define el **contrato de ciclo de vida de ICompanionSession** que todos los
puertos de idioma deben implementar. `ICompanionSession` es la superficie de API principal
con la que interactúan las aplicaciones anfitrionas (MAUI, Android, iOS, Web, HarmonyOS).

---

## 1. Conceptos

### 1.1 Sesión

Una `ICompanionSession` representa una **conversación continua única** entre un
usuario y B!. Abarca desde la creación (primer mensaje) hasta la eliminación (el usuario
cierra la aplicación o la sesión se finaliza explícitamente).

Las sesiones **no se persisten en sí mismas** — solo se almacenan el historial de
`CompanionTurn` y los estados subyacentes `AffectState`/`PersonaState`. Una nueva sesión
creada al día siguiente retoma el mismo estado de afecto y persona desde los almacenes.

### 1.2 Contexto

`CompanionContext` lleva todo lo que B! necesita para mantenerse orientado:

| Campo | Propósito |
|-------|---------|
| `UserId` | A qué usuario pertenece esta sesión |
| `AppContext` | La aplicación que realiza la llamada (p. ej. `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | Cómo se renderizará la respuesta (voz, reloj, texto…) |
| `Locale` | Idioma de respuesta alternativo |
| `Affect` | El estado emocional actual de B! para este usuario |
| `Persona` | El estilo aprendido de B! para este usuario |
| `ActiveGoals` | Objetivos con los que B! debe asistir de forma proactiva |

### 1.3 Conformación de respuesta según interfaz

El enum `InterfaceKind` determina la longitud y el estilo de la salida:

| Valor | Restricción implícita |
|-------|--------------------|
| `Text` | Por defecto — sin restricciones especiales |
| `Voice` | Frases cortas, sin markdown, sin listas |
| `Watch` | Máximo ~40 palabras; se prefiere una sola oración |
| `Car` | Muy corto; sin listas; seguro para uso sin mirar la pantalla |
| `Tv` | Conversacional; breve; sin bloques de código |
| `Ar` | Superposiciones ultrabreves (≤ 15 palabras) |
| `Iot` | Frase de acción única |

Se recomienda a las implementaciones inyectar instrucciones apropiadas para la interfaz
en el prompt del sistema.

---

## 2. Ciclo de vida de la sesión

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

## 3. Contrato de interfaz

### 3.1 `SendAsync(userMessage)`

Envío-recepción bloqueante (awaitable). Añade el turno al historial `History`.

**Precondiciones:**
- `userMessage` debe ser no nulo y no vacío.

**Postcondiciones:**
- `History.Count` aumenta en 1.
- `AffectState` se actualiza dentro de la sesión (el momento exacto está definido por la
  implementación, pero debe ocurrir antes de que la siguiente llamada a `GetContext()`
  retorne).
- El `CompanionTurn.UsedTools` retornado es `true` si ocurrieron invocaciones de
  `IToolBridge` durante la generación.

### 3.2 `StreamAsync(userMessage)`

Transmisión token por token. El **historial se actualiza** después de que se ensambla la
respuesta completa (es decir, después de que el stream se completa), no durante.

El stream asíncrono retornado emite **tokens parciales** — quienes llamen deben
concatenarlos.

### 3.3 `AgentAsync(task, tools?)`

Ejecuta un bucle agéntico de múltiples pasos: el modelo llama herramientas y razona hasta
producir una respuesta final. Retorna el texto de respuesta final.

Si `tools` es nulo o vacío, el método recurre a una sola llamada `GenerateAsync`
(sin bucle de herramientas).

### 3.4 `GetContext()`

Retorna el `CompanionContext` **actual**, incluyendo el último `AffectState` y
`PersonaState`. Puede llamarse en cualquier momento; no se ve afectado por llamadas
asíncronas en vuelo.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

Registra la retroalimentación del usuario para el **turno más reciente** en `History`.

- `FeedbackPolarity.Positive` → llama a `AffectState.ApplyPositiveSignal()` y persiste
- `FeedbackPolarity.Negative` → llama a `AffectState.ApplyNegativeSignal()` y persiste
- `FeedbackPolarity.Correction` → registra la corrección; sin mutación de afecto

Si `History` está vacío (aún no hay turnos), este método es una operación nula.

### 3.6 Evento `ProactiveMessageReady`

Se dispara cuando B! tiene un mensaje para entregar de forma proactiva (recordatorio,
impulso de objetivo, etc.). El evento **no** añade al historial automáticamente — la
aplicación anfitriona debe llamar a `SendAsync` o mostrar el mensaje de otro modo.

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

## 5. Manejo de errores

| Condición | Comportamiento esperado |
|-----------|--------------------|
| `IChatGenerator` no disponible | Lanzar `GeneratorUnavailableException` (o equivalente en el idioma) |
| Falla la invocación de herramienta | `ToolResult.Success = false`; incluir error en contexto; continuar bucle |
| Embedding no disponible | Almacenar `EpisodicMemoryEntry.Embedding = null`; no fallar |
| Falla la escritura en `AffectStore` | Registrar y continuar; no exponer al llamante |

---

## 6. Implementación mínima viable (pruebas)

Para pruebas unitarias y puertos de idioma que aún no tienen un backend LLM real:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

Las pruebas de sesión de compañero en `tests/` usan un `MockChatGenerator` para verificar
el ciclo de vida de la sesión, gestión del historial, enrutamiento de retroalimentación y
mutaciones de afecto sin necesitar un modelo real.

---

## 7. Orden de ensamblado del prompt del sistema

La implementación de referencia en C# ensambla el prompt del sistema en este orden:

1. Prompt del sistema base (persona codificada: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — añadido si no está vacío
3. `PersonaState.ToSystemPromptHint()` — añadido si no está vacío
4. Restricciones de `InterfaceKind` — añadidas según corresponda
5. Instrucciones de `AppContext` — opcionales, inyectadas por la aplicación anfitriona

Las implementaciones pueden ordenar estos de forma diferente siempre que las pruebas de
fixtures pasen.
