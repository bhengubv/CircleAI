// CircleAI ConsoleTest — 1.7B through the model's OWN chat template.

using System.Text;
using CircleAI.Companion;
using CircleAI.Inference;

const string path = @"C:\Users\tbeng\AppData\Local\CircleAI\models\Qwen3-1.7B-MNN\config.json";

Console.WriteLine("CircleAI — 1.7B via its own chat template");
Console.WriteLine("=========================================");

using var chat = new QwenTextGenerator(path, 4096, null, new PromptTemplateEngine());
var beliefs   = new SelfBeliefStore();
var extractor = new HeuristicBeliefExtractor();

foreach (var u in new[] { "my mother is diabetic", "i am vegetarian" })
    foreach (var b in await extractor.ExtractAsync(u, "turn"))
        beliefs.Record(b);

string Facts()
{
    var s = beliefs.SelfFacts();
    return s.Count == 0 ? "(nothing)" : string.Join("; ", s.Select(b => b.Object));
}

async Task<string> Ask(string q)
{
    var msgs = new List<ChatMessage>
    {
        new("system", "You know ONLY these facts about the user, nothing else: " + Facts() +
                      ". Answer only from these facts; if something is not listed, say you do not know."),
        new("user", q + " /no_think"),
    };
    var sb = new StringBuilder();
    await foreach (var f in chat.StreamFragmentsAsync(msgs, new GenerationOptions { MaxTokens = 64 }))
        sb.Append(f.Text);
    return sb.ToString().Trim();
}

Console.WriteLine("facts about the USER: [" + Facts() + "]");
Console.WriteLine("Q: Do I have diabetes?  A: " + await Ask("Do I have diabetes?"));
Console.WriteLine("Q: Am I vegetarian?     A: " + await Ask("Am I vegetarian?"));
Console.WriteLine("Q: Suggest one dinner.  A: " + await Ask("Suggest one dinner I could cook tonight."));
Console.WriteLine("DEMO_OK");
