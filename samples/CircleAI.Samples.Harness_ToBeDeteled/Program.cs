// CircleAI.Samples.Harness — the smallest thing that CONSUMES Circle AI.
//
// Not a test double: this references the real CircleAI.* SDK, calls AddCircleAI,
// and resolves each capability exactly as a harness (Butler, Wolverine, or any
// stranger) would. Run it on the desktop with no model and it STILL demonstrates
// discovery, skills, memory, and the self-healing sense for real — only the brain
// itself needs a model, and the sample says so plainly rather than pretending.
//
//     dotnet run --project samples/CircleAI.Samples.Harness
//
// Point it at a model to see the brain answer live (arg 1 or CIRCLEAI_MODEL):
//     dotnet run --project samples/CircleAI.Samples.Harness -- /path/to/qwen3-0.6b-mnn/config.json

using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Memory;
using CircleAI.Skills;
using Microsoft.Extensions.DependencyInjection;

static void Heading(string s)
{
    Console.WriteLine();
    Console.WriteLine($"== {s} " + new string('=', Math.Max(3, 58 - s.Length)));
}

// Flush every line as it is written so the sample's progress is visible even when
// its output is piped (and so a stall is diagnosable, not a silent buffer).
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

var modelPath = args.Length > 0 ? args[0]
              : Environment.GetEnvironmentVariable("CIRCLEAI_MODEL");

// ---- wire the SDK, exactly as a consumer would --------------------------------
var services = new ServiceCollection();
services.AddCircleAI(new AIOptions
{
    ModelPath = modelPath,   // null is fine — the model-free capabilities below still work
});
await using var provider = services.BuildServiceProvider();   // AIService is IAsyncDisposable

Console.WriteLine("Circle AI harness sample — consuming the SDK in-process.");
Console.WriteLine(modelPath is null
    ? "(no model configured — brain-backed calls degrade honestly; everything else runs for real)"
    : $"(model: {modelPath})");

// ---- 1. Discovery: what can Circle AI do, and what not yet? --------------------
Heading("Discovery — ICapabilityCatalog");
var catalog = provider.GetRequiredService<ICapabilityCatalog>();
foreach (var c in catalog.All())
    Console.WriteLine($"  [{c.Status,-8}] {c.Id,-22} {c.Summary}");
var healing = catalog.Find("self.healing");
if (healing is not null)
    Console.WriteLine($"  -> self.healing limits: {string.Join("; ", healing.Limits)}");

// ---- 2. Skills: the everyday know-how, no model needed -------------------------
Heading("Skills — ISkillStore (ConsumerSkillPack)");
var skills = ConsumerSkillPack.Shared;
foreach (var q in new[] { "grant", "bank account" })
{
    var hits = await skills.SearchAsync(q);
    Console.WriteLine($"  \"{q}\" -> {hits.Count} hit(s)");
    foreach (var h in hits.Take(3))
        Console.WriteLine($"       - {h.Name}  [{string.Join(", ", h.Tags)}]");
}

// ---- 3. Memory: remember facts, then recall at the moment (per-store, no model) -
// Recall is keyed on the SUBJECT of the action, not a prose search: an atom is
// filed under a situation key ("visit:clinic") and retrieved when that situation
// comes round again. (LearnAsync is the extractive path — it distils atoms from
// free text and leans on the brain; RememberAsync files a known fact directly.)
Heading("Memory — MemoryService");
var memoryFolder = Path.Combine(Path.GetTempPath(), "circleai-harness-sample-memory");
if (Directory.Exists(memoryFolder)) Directory.Delete(memoryFolder, recursive: true);  // a clean slate each run
var memory = new MemoryService(memoryFolder);
await memory.RememberAsync(new MemoryAtom { Text = "The clinic moved its grant desk to Fridays.", Subject = "visit:clinic" });
await memory.RememberAsync(new MemoryAtom { Text = "A bank card PIN reset needs the branch, not the app.", Subject = "reset:pin" });
Console.WriteLine($"  stored {await memory.CountAsync()} atom(s) under {memoryFolder}");
var recall = await memory.RecallAsync(new Situation(Verb: "visit", Target: "clinic"));
Console.WriteLine($"  recall visit:clinic -> {recall.Atoms.Count} atom(s) of {recall.Considered} considered");
foreach (var atom in recall.Atoms.Take(3))
    Console.WriteLine($"       - {atom.Text}");

// ---- 4. Self-healing: categorise a failure, recommend a fix --------------------
Heading("Self-healing — IFailureAnalyst");
var analyst = provider.GetRequiredService<IFailureAnalyst>();
Console.WriteLine("  resolved: the self-healing sense is wired over the brain.");
if (modelPath is null)
{
    Console.WriteLine("  (no model -> skipping live analysis; with a model it categorises a failure,");
    Console.WriteLine("   recommends quick-fix / patch / defer, and drafts a fix — recommends, never acts.)");
}
else
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var verdict = await analyst.AnalyseAsync(new FailureContext(
        Message: "System.Net.Http.HttpRequestException: connection reset by peer",
        Source:  "PaymentService",
        Details: "POST /pay returned 503 after 3 retries"),
        cts.Token);
    Console.WriteLine($"  kind={verdict.Kind}  category={verdict.Category}  confidence={verdict.Confidence:0.00}");
    Console.WriteLine($"  summary: {verdict.Summary}");
    if (verdict.RecommendedAction is not null)
        Console.WriteLine($"  action:  {verdict.RecommendedAction}");
    Console.WriteLine("  (recommends + drafts only; executing the fix is the consumer's job.)");
}

// ---- 5. The brain itself (needs a model) --------------------------------------
Heading("Brain — IAIService");
var brain = provider.GetRequiredService<IAIService>();
if (modelPath is null)
{
    Console.WriteLine("  resolved, not started: the brain loads a model lazily on StartAsync.");
    Console.WriteLine("  set CIRCLEAI_MODEL (or pass a model path as arg 1) to see it answer.");
}
else
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await brain.StartAsync(cts.Token);
        var answer = await brain.AskAsync("In one sentence, what is Circle AI?", cts.Token);
        Console.WriteLine($"  {answer}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  brain unavailable: {ex.GetType().Name}: {ex.Message}");
    }
}

Heading("Done");
Console.WriteLine("Every capability above was resolved from the SDK the way a harness consumes it.");
