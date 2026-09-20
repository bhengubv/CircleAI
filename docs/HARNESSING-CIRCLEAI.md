# Harnessing Circle AI

Circle AI is a standalone on-device brain — one model, long-term memory, an expandable
skill set, and an honest self-catalogue of what it can do. This guide is for a
**consumer**: an app, a service, or a bot that wants to *harness* those capabilities
instead of embedding its own AI.

You consume Circle AI **in-process through its SDK**: reference the `CircleAI.*`
libraries, call `AddCircleAI`, and resolve the capability you want. (Two more doors —
an HTTP inference server and an on-device cross-app link — expose the same capabilities
to networked and phone consumers; see [Other doors](#other-doors).)

Everything here runs on a small (0.6B) model on cheap phones, so Circle AI is built to
say "I don't know" rather than invent. Consume it accordingly: trust each capability's
`Status`, check a verdict's `Confidence`, and keep a human on anything that changes
code, money, or safety.

## Wire it up

```csharp
using CircleAI.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddCircleAI(new AIOptions
{
    // Leave ModelPath null to let Circle AI pick the best model this device can hold,
    // or pin one:  ModelPath = "/path/to/qwen3-0.6b-mnn/config.json".
});
await using var provider = services.BuildServiceProvider();   // AIService is IAsyncDisposable

var brain = provider.GetRequiredService<IAIService>();
await brain.StartAsync();   // loads the model once; the process holds it warm
```

`AddCircleAI` registers the whole surface below. The model loads lazily on `StartAsync`
(or first use), so **resolving** the services costs nothing until you actually ask the
brain something.

Every capability is registered with `TryAdd`, so a host that registers its own
implementation **before** `AddCircleAI` keeps it:

```csharp
services.AddSingleton<IFailureAnalyst>(new MyRemoteAnalyst());  // wins over the default
services.AddCircleAI(options);
```

## The capability surface

### Brain — ask / chat / stream / agentic
`IAIService` (`CircleAI.Hosting`)

```csharp
string answer = await brain.AskAsync("Explain this error for a non-engineer: ...");

string reply = await brain.ChatAsync(new[]
{
    new ChatMessage("system", "You are terse."),   // CircleAI.Inference.ChatMessage
    new ChatMessage("user",   "Why did my payment fail?"),
});
// tokens: brain.StreamAsync(messages);   tools: brain.AgenticChatAsync(prompt)
```

### Self-healing — categorise a failure, recommend a fix
`IFailureAnalyst` (`CircleAI.Hosting.SelfHealing`) — resolved from the container.

```csharp
var analyst = provider.GetRequiredService<IFailureAnalyst>();

HealingVerdict v = await analyst.AnalyseAsync(new FailureContext(
    Message: "System.Net.Http.HttpRequestException: connection reset",
    Source:  "PaymentService",
    Details: "POST /pay returned 503"));

switch (v.Kind)
{
    case HealingKind.QuickFix: /* do the safe, reversible thing — retry, reset, refresh */ break;
    case HealingKind.Patch:    /* v.DraftFix is a DRAFT — a human reviews before it ships */ break;
    case HealingKind.Defer:    /* hand it to a person */ break;
}
```

It **recommends and drafts; it never acts.** Executing a retry, opening a PR, or
deploying is your job — Circle AI is the brain, not the hand. If it cannot understand
the failure it returns `HealingVerdict.NeedsAHuman(...)` (a `Defer`) — never a guess,
never an exception. This is the capability a self-healing / SDLC harness is built on.

### Discovery — what can Circle AI do, and what not yet?
`ICapabilityCatalog` (`CircleAI.Skills`) — resolved from the container.

```csharp
var catalog = provider.GetRequiredService<ICapabilityCatalog>();

foreach (CapabilityEntry c in catalog.All())
    Console.WriteLine($"[{c.Status}] {c.Id} — {c.Summary}");

CapabilityEntry? healing = catalog.Find("self.healing");   // status, summary, requires, limits
```

Every entry carries an honest `Status` — `shipping` / `partial` / `scaffold` / `planned`
/ `rejected` — so a consumer can tell what it can rely on *before* it relies on it. This
is Circle AI describing itself from fact; it reports "not yet" rather than claim a
capability it cannot back.

### Skills — the everyday know-how, expandable
`ISkillStore` (`CircleAI.Skills`). The built-in South-African everyday pack ships as a
prebuilt database and needs no model to query:

```csharp
using CircleAI.Skills;

ISkillStore skills = ConsumerSkillPack.Shared;
var hits = await skills.SearchAsync("apply for a grant");

// add your own from a Markdown SKILL.md, into any writable store:
await SkillPackLoader.ImportMarkdownAsync(writableStore, myMarkdown, source: "acme");
```

Register your own store as the app-wide `ISkillStore` before `AddCircleAI` if you want a
different set, or compose several with `CompositeSkillStore`.

### Long-term memory — remember and recall
`IMemoryService` (`CircleAI.Memory`). Memory is **scoped per store instance** — one
folder per user or device — so you construct it with that user's folder rather than
resolving a shared one:

```csharp
using CircleAI.Memory;

var memory = new MemoryService(folderPath: "/data/user-42/memory");   // one per user
await memory.LearnAsync("The clinic moved to Fridays.");
// recall is situation-based; IRemembers (CircleAI.Assistant) is a simpler
// LearnAsync(text) / RecallAsync(about, limit) facade for UI consumers.
```

## Other doors

The capabilities above are the same wherever Circle AI runs; only the transport differs.

### HTTP — `CircleAI.Inference.Server`

Point a networked consumer at the server. Every endpoint below is API-key gated
(`X-CircleAI-Api-Key`), so an unauthenticated request gets `401`.

```bash
# The brain (a companion turn):
curl -sX POST localhost:5000/v1/companion/turn -H "X-CircleAI-Api-Key: $KEY" \
     -H 'content-type: application/json' -d '{"sessionId":"s1","message":"hello"}'

# Discovery — the honest self-catalogue, as data:
curl -s localhost:5000/v1/capabilities -H "X-CircleAI-Api-Key: $KEY"
#   → { "capabilities": [ { "id":"self.healing", "status":"partial", … } ] }

# Skills — list, or search with ?q= :
curl -s localhost:5000/v1/skills                -H "X-CircleAI-Api-Key: $KEY"
curl -s "localhost:5000/v1/skills?q=grant"      -H "X-CircleAI-Api-Key: $KEY"
#   → { "query":"grant", "skills":[ { "id":…, "name":…, "description":…, "tags":[…] } ] }
```

`/v1/capabilities` serves the same `ICapabilityCatalog` and `/v1/skills` the same
`ISkillStore` a consumer would resolve in-process — no model needed for either, so they
answer instantly. A host that registers its own catalogue or skill store before
`AddCircleAIInferenceServer` serves that instead. Memory and self-healing over HTTP are
**not exposed yet** (the server composes the bridge directly, not `IAIService`); consume
those in-process for now.

### On-device link

A phone app links to a resident Circle AI, biometric/PIN-gated, and calls the same
capabilities across app boundaries (`CircleAI.Client`). Chat is live today; memory /
skills / discovery follow (Slice 3).

## Testing by consuming

The fastest proof is the in-process SDK: `AddCircleAI`, resolve `ICapabilityCatalog` and
`IFailureAnalyst`, and call them. `HarnessExposureTests` in the test suite does exactly
this as a consumer would, and `FailureAnalystTests` exercises the self-healing sense end
to end with a fake brain — both run on the desktop with no model and no server.
