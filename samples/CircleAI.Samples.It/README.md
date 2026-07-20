# IT — a CircleAI Neuron reference sample

**IT** is the smallest complete example of building an on-device assistant on the
CircleAI **Neuron**. It's here so other developers have something real to copy,
and so we can watch the whole pipeline work end to end.

> IT is a *sample*, not a product. It shows the wiring; it doesn't ship a brain.

## Run it

```bash
# interactive chat
dotnet run --project samples/CircleAI.Samples.It

# scripted demo (no typing needed)
dotnet run --project samples/CircleAI.Samples.It -- --demo
```

No model download, no native libraries — it runs anywhere .NET runs.

## What you'll see

- **The concierge deciding.** Before every reply, IT prints which "organ" the
  turn was routed to — the always-warm **generalist**, or a **specialist**
  (vision / reasoning / long-context) — and why. Ask it to *"solve … step by
  step"* and watch it route to a reasoning specialist.
- **Streaming.** Replies arrive piece by piece, the same way a real
  token-streaming model delivers them.
- **In-session memory.** Tell it *"my name is …"*, then ask *"what's my name?"* —
  it remembers, because the running conversation is passed on every turn.

## How it's wired (the whole point)

```
ItGenerator (placeholder brain)          <-- the ONLY stand-in
        |
   new AIService(options, _ => it)       <-- the on-device brain
        |
   new NeuronNode(brain)                 <-- host-neutral facade a UI drives
        |
   HeuristicNeuronRouter.Route(turn)     <-- the concierge, shown live
```

Three files: [`Program.cs`](Program.cs) (compose + chat loop),
[`ItGenerator.cs`](ItGenerator.cs) (the placeholder brain), and this README.

## Make it real

1. **Real thinking** — replace `new ItGenerator()` in `Program.cs` with a real
   generator, e.g. `new QwenTextGenerator(modelPath, …)` (MNN). That's the only
   change needed; everything else already goes through the brain.
2. **True two-slot routing** — set `AIOptions.Router = new HeuristicNeuronRouter()`
   and register an `IModelSelector` + specialist models, so the concierge
   actually *hot-loads* a specialist instead of just naming one.
3. **Persistent memory & persona** — set `AIOptions.EpisodicMemory` and
   `AIOptions.PersonaStore` so IT remembers across restarts and adapts its tone.
