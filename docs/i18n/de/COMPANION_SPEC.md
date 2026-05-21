# Circle AI — Spezifikation der Companion-Sitzung

Dieses Dokument definiert den **ICompanionSession-Lebenszyklusvertrag**, den alle
Sprachports implementieren müssen. `ICompanionSession` ist die primäre API-Oberfläche,
mit der Host-Anwendungen (MAUI, Android, iOS, Web, HarmonyOS) interagieren.

---

## 1. Konzepte

### 1.1 Sitzung

Eine `ICompanionSession` repräsentiert ein **einzelnes, kontinuierliches Gespräch**
zwischen einem Benutzer und B!. Sie erstreckt sich von der Erstellung (erste Nachricht)
bis zur Entsorgung (Benutzer schließt die App oder die Sitzung wird explizit beendet).

Sitzungen werden **nicht selbst persistiert** — nur der `CompanionTurn`-Verlauf und
der zugrunde liegende `AffectState`/`PersonaState` werden gespeichert. Eine neue
Sitzung, die am nächsten Tag erstellt wird, greift auf denselben Affect- und
Persona-Zustand aus den Stores zurück.

### 1.2 Kontext

`CompanionContext` trägt alles, was B! benötigt, um geerdet zu bleiben:

| Feld | Zweck |
|------|-------|
| `UserId` | Welchem Benutzer diese Sitzung gehört |
| `AppContext` | Die aufrufende App (z.B. `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | Wie die Antwort dargestellt wird (Sprache, Uhr, Text…) |
| `Locale` | Sprache für Antworten überschreiben |
| `Affect` | B!'s aktueller emotionaler Zustand für diesen Benutzer |
| `Persona` | B!'s erlernter Stil für diesen Benutzer |
| `ActiveGoals` | Ziele, bei denen B! proaktiv unterstützen soll |

### 1.3 Schnittstellengesteuerte Antwortformung

Das `InterfaceKind`-Enum steuert Ausgabelänge und -stil:

| Wert | Implizite Einschränkung |
|------|-------------------------|
| `Text` | Standard — keine besonderen Einschränkungen |
| `Voice` | Kurze Sätze, kein Markdown, keine Listen |
| `Watch` | Maximal ~40 Wörter; einzelner Satz bevorzugt |
| `Car` | Sehr kurz; keine Listen; geeignet für Bedienung ohne Augenkontakt |
| `Tv` | Gesprächig; knapp; keine Codeblöcke |
| `Ar` | Ultrakurze Einblendungen (≤ 15 Wörter) |
| `Iot` | Einzelner Aktionsausdruck |

Implementierungen werden ermutigt, schnittstellenangemessene Anweisungen in
den System-Prompt einzufügen.

---

## 2. Sitzungslebenszyklus

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

## 3. Schnittstellenvertrag

### 3.1 `SendAsync(userMessage)`

Blockierendes (awaitable) Sende-Empfangs-Verfahren. Fügt den Turn dem `History`-Verlauf hinzu.

**Vorbedingungen:**
- `userMessage` darf nicht null und nicht leer sein.

**Nachbedingungen:**
- `History.Count` erhöht sich um 1.
- `AffectState` wird innerhalb der Sitzung aktualisiert (der genaue Zeitpunkt ist
  implementierungsdefiniert, muss jedoch vor dem nächsten Aufruf von `GetContext()`
  erfolgt sein).
- Das zurückgegebene `CompanionTurn.UsedTools` ist `true`, wenn während der
  Generierung `IToolBridge`-Aufrufe stattgefunden haben.

### 3.2 `StreamAsync(userMessage)`

Token-für-Token-Streaming. Der **Verlauf wird aktualisiert**, nachdem die vollständige
Antwort zusammengestellt wurde (d.h. nach Abschluss des Streams), nicht während des
Streamings.

Der zurückgegebene asynchrone Stream gibt **partielle Token** aus — Aufrufer müssen
diese zusammensetzen.

### 3.3 `AgentAsync(task, tools?)`

Führt eine mehrstufige agentische Schleife aus: Das Modell ruft Werkzeuge auf und
schlussfolgert, bis es eine endgültige Antwort produziert. Gibt den finalen Textresponse zurück.

Wenn `tools` null oder leer ist, fällt die Methode auf einen einzelnen `GenerateAsync`-Aufruf
zurück (keine Werkzeugschleife).

### 3.4 `GetContext()`

Gibt den **aktuellen** `CompanionContext` zurück, einschließlich des neuesten
`AffectState` und `PersonaState`. Kann jederzeit aufgerufen werden; wird nicht
durch laufende asynchrone Aufrufe beeinflusst.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

Zeichnet Benutzerfeedback für den **letzten Turn** im `History`-Verlauf auf.

- `FeedbackPolarity.Positive` → ruft `AffectState.ApplyPositiveSignal()` auf und persistiert
- `FeedbackPolarity.Negative` → ruft `AffectState.ApplyNegativeSignal()` auf und persistiert
- `FeedbackPolarity.Correction` → zeichnet die Korrektur auf; keine Affect-Mutation

Wenn `History` leer ist (noch keine Turns), ist diese Methode eine No-Op.

### 3.6 `ProactiveMessageReady`-Ereignis

Wird ausgelöst, wenn B! eine Nachricht proaktiv übermitteln möchte (Erinnerung,
Zielnudge usw.). Das Ereignis fügt die Nachricht **nicht** automatisch dem `History`-Verlauf
hinzu — der Host muss `SendAsync` aufrufen oder die Nachricht anderweitig anzeigen.

---

## 4. `CompanionTurn`-Felder

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. Fehlerbehandlung

| Bedingung | Erwartetes Verhalten |
|-----------|----------------------|
| `IChatGenerator` nicht verfügbar | `GeneratorUnavailableException` auslösen (oder sprachäquivalent) |
| Werkzeugaufruf schlägt fehl | `ToolResult.Success = false`; Fehler in Kontext einbeziehen; Schleife fortsetzen |
| Embedding nicht verfügbar | `EpisodicMemoryEntry.Embedding = null` speichern; nicht scheitern |
| `AffectStore`-Schreibvorgang schlägt fehl | Protokollieren und fortfahren; nicht an den Aufrufer weitergeben |

---

## 6. Minimal-viable-Implementierung (Testen)

Für Unit-Tests und Sprachports, die noch kein echtes LLM-Backend haben:

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

Die Companion-Sitzungstests in `tests/` verwenden einen `MockChatGenerator`, um
Sitzungslebenszyklus, Verlaufsverwaltung, Feedback-Routing und Affect-Mutationen
ohne ein echtes Modell zu verifizieren.

---

## 7. Zusammenstellungsreihenfolge des System-Prompts

Die C#-Referenzimplementierung stellt den System-Prompt in dieser Reihenfolge zusammen:

1. Basis-System-Prompt (fest kodierte Persona: "You are B!, the on-device assistant…")
2. `AffectState.ToSystemPromptHint()` — wird angehängt, wenn nicht leer
3. `PersonaState.ToSystemPromptHint()` — wird angehängt, wenn nicht leer
4. `InterfaceKind`-Einschränkungen — werden bei Bedarf angehängt
5. `AppContext`-Anweisungen — optional, durch die Host-App eingefügt

Implementierungen können diese Reihenfolge abweichend gestalten, solange die
Fixture-Tests bestanden werden.
