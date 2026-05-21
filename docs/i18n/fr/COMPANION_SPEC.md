# Circle AI — Spécification de session Companion

Ce document définit le **contrat de cycle de vie ICompanionSession** que tous les
portages linguistiques doivent implémenter. `ICompanionSession` est la surface d'API
principale avec laquelle les applications hôtes (MAUI, Android, iOS, Web, HarmonyOS)
interagissent.

---

## 1. Concepts

### 1.1 Session

Un `ICompanionSession` représente une **conversation continue unique** entre un
utilisateur et B!. Elle s'étend de la création (premier message) à la suppression
(l'utilisateur ferme l'application ou la session est explicitement terminée).

Les sessions **ne sont pas persistées elles-mêmes** — seul l'historique des
`CompanionTurn` ainsi que l'`AffectState`/`PersonaState` sous-jacents sont stockés.
Une nouvelle session créée le lendemain reprend les mêmes états d'affect et de persona
depuis les magasins.

### 1.2 Contexte

`CompanionContext` contient tout ce dont B! a besoin pour rester ancré :

| Champ | Rôle |
|-------|------|
| `UserId` | L'utilisateur auquel cette session appartient |
| `AppContext` | L'application appelante (p. ex. `"tgn.bidbaas"`, `"tgn.tagme"`) |
| `Interface` | Comment la réponse sera rendue (voix, montre, texte…) |
| `Locale` | Langue de substitution pour les réponses |
| `Affect` | L'état émotionnel courant de B! pour cet utilisateur |
| `Persona` | Le style appris de B! pour cet utilisateur |
| `ActiveGoals` | Objectifs avec lesquels B! doit proactivement aider |

### 1.3 Mise en forme des réponses pilotée par l'interface

L'énumération `InterfaceKind` détermine la longueur et le style de sortie :

| Valeur | Contrainte implicite |
|--------|----------------------|
| `Text` | Par défaut — aucune contrainte particulière |
| `Voice` | Phrases courtes, pas de markdown, pas de listes |
| `Watch` | Maximum ~40 mots ; une seule phrase de préférence |
| `Car` | Très court ; pas de listes ; sûr pour les yeux libres |
| `Tv` | Conversationnel ; bref ; pas de blocs de code |
| `Ar` | Superpositions ultra-courtes (≤ 15 mots) |
| `Iot` | Une seule phrase d'action |

Les implémentations sont encouragées à injecter des instructions adaptées à
l'interface dans le prompt système.

---

## 2. Cycle de vie de la session

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

## 3. Contrat d'interface

### 3.1 `SendAsync(userMessage)`

Envoi-réception bloquant (attendable). Ajoute le tour à `History`.

**Préconditions :**
- `userMessage` doit être non nul et non vide.

**Postconditions :**
- `History.Count` augmente de 1.
- `AffectState` est mis à jour dans la session (le moment exact est défini par
  l'implémentation, mais doit avoir lieu avant le prochain appel à `GetContext()`).
- Le `CompanionTurn.UsedTools` retourné est `true` si des invocations `IToolBridge`
  se sont produites pendant la génération.

### 3.2 `StreamAsync(userMessage)`

Diffusion jeton par jeton. **L'historique est mis à jour** une fois que la réponse
complète est assemblée (c'est-à-dire après la fin du flux), pas pendant.

Le flux asynchrone retourné émet des **tokens partiels** — les appelants doivent les
concaténer.

### 3.3 `AgentAsync(task, tools?)`

Exécute une boucle agentique multi-étapes : le modèle appelle des outils et raisonne
jusqu'à produire une réponse finale. Retourne le texte de réponse final.

Si `tools` est null ou vide, la méthode se replie sur un appel `GenerateAsync` unique
(sans boucle d'outils).

### 3.4 `GetContext()`

Retourne le `CompanionContext` **courant**, incluant le dernier `AffectState` et
`PersonaState`. Peut être appelé à tout moment ; n'est pas affecté par des appels
asynchrones en cours.

### 3.5 `SignalFeedbackAsync(polarity, correction?)`

Enregistre le retour utilisateur pour le **tour le plus récent** dans `History`.

- `FeedbackPolarity.Positive` → appelle `AffectState.ApplyPositiveSignal()` et persiste
- `FeedbackPolarity.Negative` → appelle `AffectState.ApplyNegativeSignal()` et persiste
- `FeedbackPolarity.Correction` → enregistre la correction ; aucune mutation d'affect

Si `History` est vide (aucun tour encore), cette méthode est sans effet.

### 3.6 Événement `ProactiveMessageReady`

Se déclenche quand B! a un message à délivrer de manière proactive (rappel, incitation
d'objectif, etc.). L'événement **n'ajoute pas** automatiquement à `History` — l'hôte
doit appeler `SendAsync` ou afficher autrement le message.

---

## 4. Champs de `CompanionTurn`

```
CompanionTurn {
  UserText:      string         // the user's input (verbatim)
  AssistantText: string         // B!'s complete response
  CreatedAt:     datetime (UTC) // timestamp of the assistant's response
  UsedTools:     bool           // true if any tool invocations occurred
}
```

---

## 5. Gestion des erreurs

| Condition | Comportement attendu |
|-----------|----------------------|
| `IChatGenerator` indisponible | Lancer `GeneratorUnavailableException` (ou l'équivalent dans le langage) |
| Échec d'invocation d'outil | `ToolResult.Success = false` ; inclure l'erreur dans le contexte ; continuer la boucle |
| Embedding indisponible | Stocker `EpisodicMemoryEntry.Embedding = null` ; ne pas échouer |
| Échec d'écriture `AffectStore` | Journaliser et continuer ; ne pas remonter à l'appelant |

---

## 6. Implémentation minimale viable (tests)

Pour les tests unitaires et les portages linguistiques qui n'ont pas encore de
backend LLM réel :

```
MockChatGenerator:
  GenerateAsync(messages) → "Mock response from B!"
  StreamAsync(messages) → async stream of ["Mock", " ", "response", " ", "from", " ", "B!"]
```

Les tests de session companion dans `tests/` utilisent un `MockChatGenerator` pour
vérifier le cycle de vie de la session, la gestion de l'historique, le routage du
retour et les mutations d'affect sans nécessiter de modèle réel.

---

## 7. Ordre d'assemblage du prompt système

L'implémentation de référence C# assemble le prompt système dans cet ordre :

1. Prompt système de base (persona codé en dur : « You are B!, the on-device assistant… »)
2. `AffectState.ToSystemPromptHint()` — ajouté s'il est non vide
3. `PersonaState.ToSystemPromptHint()` — ajouté s'il est non vide
4. Contraintes `InterfaceKind` — ajoutées selon le cas
5. Instructions `AppContext` — optionnelles, injectées par l'application hôte

Les implémentations peuvent ordonner ces éléments différemment à condition que les
tests de fixture passent.
