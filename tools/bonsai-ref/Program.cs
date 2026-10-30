// bonsai-ref
//
// Makes the S0 reference fixtures for the managed Bonsai 2 engine, from the pinned
// PrismML fork build the repo already ships for Windows. The fork is the ruler and
// never a dependency: these JSON files are what the C# engine is checked against.
//
//   bonsai-ref meta   <model.gguf> <out.json>
//   bonsai-ref tokens <model.gguf> <out.json>
//   bonsai-ref logits <model.gguf> <templates.json> <out.json>
//   bonsai-ref detok  <model.gguf> <logits.json>
//
// meta    every metadata key with a short value, the chat template, the vocab size.
// tokens  token ids and the raw bytes of each piece for a fixed multilingual corpus.
// logits  for each rendered prompt in templates.json marked `logits: true`: its token
//         ids, a 32-token greedy continuation on ONE thread with the top 20 logits and
//         the gap between the top two at each step, and - for 2 and 4 threads fed the
//         same tokens - the largest logit difference over the whole vocabulary at each
//         step. That spread is the tolerance the C# engine is held to: one build is
//         deterministic, so its run-to-run spread is zero and useless as a tolerance.
// detok   writes each case's greedy continuation into logits.json as readable text,
//         so a person can see the reference answered sensibly before trusting it.
//
// templates.json comes from render_templates.py, which renders the model's own chat
// template with jinja2 the way Hugging Face does; this tool only tokenizes and runs it.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircleAI.Tools.BonsaiRef;

const string ForkTag = "prism-b10743-adfffbe";
const int GreedySteps = 32;
const int TopK = 20;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: bonsai-ref meta|tokens <model.gguf> <out.json>");
    Console.Error.WriteLine("       bonsai-ref logits <model.gguf> <templates.json> <out.json>");
    Console.Error.WriteLine("       bonsai-ref detok <model.gguf> <logits.json>");
    return 2;
}

// A reference run is CPU-heavy for minutes; it must not compete with the person
// using this machine.
using (var self = Process.GetCurrentProcess()) self.PriorityClass = ProcessPriorityClass.BelowNormal;

var mode = args[0];
var modelPath = Path.GetFullPath(args[1]);
if (!File.Exists(modelPath))
{
    Console.Error.WriteLine($"FAIL: no model at {modelPath}");
    return 2;
}

Console.WriteLine($"model : {modelPath}");
var sha = Sha256(modelPath);
Console.WriteLine($"sha256: {sha}");

Llama.llama_backend_init();
var modelParams = Llama.llama_model_default_params();
modelParams.NGpuLayers = 0;
modelParams.VocabOnly = (byte)(mode == "detok" ? 1 : 0);
var model = LoadModel(modelPath, modelParams);
if (model == 0)
{
    Console.Error.WriteLine("FAIL: llama_model_load_from_file returned NULL");
    return 1;
}

try
{
    var vocab = Llama.llama_model_get_vocab(model);
    if (mode == "detok")
    {
        Detok(vocab, Path.GetFullPath(args[2]));
        return 0;
    }

    var source = new JsonObject
    {
        ["model"] = Path.GetFileName(modelPath),
        ["sha256"] = sha,
        ["fork"] = ForkTag,
        ["runtime"] = "src/CircleAI/Inference/runtimes/win-x64/native/llama.dll",
        ["made"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    };

    JsonObject output = mode switch
    {
        "meta"   => Meta(model, vocab, source),
        "tokens" => Tokens(vocab, source),
        "logits" => Logits(model, vocab, source, args[2]),
        _        => throw new ArgumentException($"unknown mode '{mode}'"),
    };

    var outPath = Path.GetFullPath(args[mode == "logits" ? 3 : 2]);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, output.ToJsonString(Json(indented: mode != "logits")) + "\n");
    Console.WriteLine($"wrote : {outPath}");
    return 0;
}
finally
{
    Llama.llama_model_free(model);
    Llama.llama_backend_free();
}

// Fixtures are read by people as well as tests: isiZulu, Chinese and Arabic stay as
// written instead of turning into \u escapes.
static JsonSerializerOptions Json(bool indented) => new()
{
    WriteIndented = indented,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

static unsafe nint LoadModel(string path, LlamaModelParams p)
{
    var z = Encoding.UTF8.GetBytes(path + "\0");
    fixed (byte* zp = z) return Llama.llama_model_load_from_file(zp, p);
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static unsafe string? MetaAt(nint model, int index, bool key)
{
    var size = 1024;
    while (true)
    {
        var buf = new byte[size];
        int n;
        fixed (byte* b = buf)
            n = key ? Llama.llama_model_meta_key_by_index(model, index, b, (nuint)size)
                    : Llama.llama_model_meta_val_str_by_index(model, index, b, (nuint)size);
        if (n < 0) return null;
        if (n < size) return Encoding.UTF8.GetString(buf, 0, n);
        size = n + 1;
    }
}

static JsonObject Meta(nint model, nint vocab, JsonObject source)
{
    var keys = new JsonObject();
    var count = Llama.llama_model_meta_count(model);
    for (var i = 0; i < count; i++)
    {
        var key = MetaAt(model, i, key: true);
        var value = MetaAt(model, i, key: false);
        if (key is null || value is null) continue;
        // The vocabulary arrays and the hadamard sign vectors are read from the GGUF
        // itself by the C# reader; here they would be megabytes of noise.
        if (value.Length > 512 && key != "tokenizer.chat_template")
            value = $"<{value.Length} chars>";
        keys[key] = value;
    }

    return new JsonObject
    {
        ["_comment"] = "Metadata of the model as the pinned fork reads it. The C# GGUF reader must report the same scalar values.",
        ["_source"] = source.DeepClone(),
        ["n_vocab"] = Llama.llama_vocab_n_tokens(vocab),
        ["keys"] = keys,
    };
}

static JsonObject Tokens(nint vocab, JsonObject source)
{
    string[] corpus =
    [
        "Hello, world!",
        "The quick brown fox jumps over the lazy dog.",
        "  leading spaces and\ttabs\nnew lines\r\n",
        "Sawubona, unjani? Ngiyabonga kakhulu.",
        "Goeie môre! Hoe gaan dit met jou?",
        "你好，世界！今天天气很好。",
        "مرحبا بالعالم",
        "नमस्ते, आप कैसे हैं?",
        "Emoji: 😀🎉👍🏽 and a flag 🇿🇦",
        "def is_prime(n):\n    return n > 1 and all(n % d for d in range(2, int(n**0.5) + 1))",
        "Numbers 1234567890 and 3.14159, dates 2026-10-30.",
        "<|im_start|>user\nHi<|im_end|>\n",
        "Café naïve résumé — ñ, ü, ø",
        "é (decomposed) and é (composed)",
        "     \n\n\n   ",
        "http://example.com/path?query=1&x=2",

        // S2: every rule of the qwen35 splitter and its edges - contractions, letters with
        // combining marks, numbers of other scripts, punctuation runs ending in newlines,
        // whitespace the fork's \s does and does not match, emoji sequences, unassigned and
        // private-use code points, characters past the BMP, special tokens and their near
        // misses, code, repetition.
        "I'm sure you're right; they've said we'll see, he'd know - it's DON'T and WE'LL and 'S.",
        "rock'n'roll o'clock 'tis it’s can't",
        "e\u0301\u0302 a\u0301b \u0301abc \u0308\u0308",
        "क्षत्रिय ภาษาไทย שָׁלוֹם Tiếng Việt",
        "한국어 日本語のテキスト Ελληνικά Привет, мир!",
        "٣٤٥ ①② Ⅻ x² 1,000,000 3.14 -42 +7",
        "!!! ...\n\n?!\r\n ---(foo)[x]{y} a+b=c #tag @user $5 % →←↑ ©®™",
        "“quotes” « guillemets » ‘single’  ... \t!! !!\n\nabc",
        "a\u00a0b \u00a0 \u3000全角 line\u2028sep a\u2029b \u0085 \f\v a \t b",
        "x  \n  y   end tabs\t\t\t \r\r\n\n \n\n  abc   ",
        "👨‍👩‍👧‍👦 👍🏿 🇺🇸🇯🇵 1️⃣ ❤️ 🫠",
        "\u0378 \ue000 \ufeff \u200b \u200d \u0001 \u007f",
        "𝔘𝔫𝔦𝔠𝔬𝔡𝔢 𠀀 𝟘𝟙𝟚",
        "<|im_start|>system\nYou are B.<|im_end|>\n<|im_start|>user\nHi<|im_end|>\n<|im_start|>assistant\n",
        "<think>\nhmm\n</think>\n\nDone. <tool_call>{\"name\": \"x\"}</tool_call>",
        "text<|endoftext|>more <|im_start|><|im_start|> <|im_start <|IM_START|> <think><think>",
        "for (int i = 0; i < n; ++i) {\n\tsum += a[i];\n}",
        "x = {'a': 1, \"b\": [2, 3]}; SELECT * FROM t WHERE id='5';",
        "<div class=\"x\">Hi</div> C:\\Users\\name\\file.txt snake_case CamelCase",
        "https://example.org:8080/a/b?c=d#e mailto:a@b.co",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ha ha ha ha =================",
        "        \n\n\n\n",
        "isiZulu: Ngiyakuthanda! 爱你 ❤ Ünïcödé ﬁ ǅ ʰ",
        "'", "''", "' s", "'S", "'ll", "a'", " ", "  ", "\n", "\r\n", "\t", "x", "1", "!", "<", "<|",
    ];

    // And 150 strings drawn from those same edges with a fixed seed: pairings nobody thought
    // to write down.
    string[] pool =
    [
        "a", "b", "Z", "e", "s", "t", "ll", "re", "1", "7", "٣", " ", "  ", "\t", "\n", "\r", "\r\n", "\u00a0", "\u3000",
        "'", "’", ".", ",", "!", "?", "-", "(", ")", "\"", "#", "→", "é", "e\u0301", "\u0301", "ñ", "中", "文", "😀",
        "👍🏽", "न", "्", "م", "<|im_start|>", "<|im_end|>", "<think>", "</think>", "\u200d", "\ufe0f", "ﬁ", "𝔘",
    ];
    var rng = new Random(20261030);
    var fuzz = new List<string>();
    for (var i = 0; i < 150; i++)
    {
        var text = new StringBuilder();
        for (var parts = rng.Next(1, 25); parts > 0; parts--) text.Append(pool[rng.Next(pool.Length)]);
        fuzz.Add(text.ToString());
    }
    corpus = [.. corpus, .. fuzz];

    var cases = new JsonArray();
    foreach (var text in corpus)
    {
        var ids = Tokenize(vocab, text, addSpecial: false, parseSpecial: true);
        var pieces = new JsonArray();
        foreach (var id in ids) pieces.Add(Convert.ToHexStringLower(Piece(vocab, id)));
        cases.Add(new JsonObject
        {
            ["text"] = text,
            ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()),
            ["pieces_hex"] = pieces,
        });
    }

    return new JsonObject
    {
        ["_comment"] = "Token ids (no BOS added, special tokens parsed) and the raw bytes of each piece, as the pinned fork tokenizes them.",
        ["_source"] = source.DeepClone(),
        ["cases"] = cases,
    };
}

static unsafe JsonObject Logits(nint model, nint vocab, JsonObject source, string templatesPath)
{
    var templates = JsonNode.Parse(File.ReadAllText(templatesPath))!.AsObject();
    var nVocab = Llama.llama_vocab_n_tokens(vocab);
    var cases = new JsonArray();

    foreach (var node in templates["cases"]!.AsArray())
    {
        var tcase = node!.AsObject();
        if (tcase["logits"]?.GetValue<bool>() != true) continue;
        var name = tcase["name"]!.GetValue<string>();
        var rendered = tcase["rendered"]!.GetValue<string>();
        var promptIds = Tokenize(vocab, rendered, addSpecial: true, parseSpecial: true);
        Console.WriteLine($"  {name}: {promptIds.Length} prompt tokens");

        // One thread: the greedy path and the full logits at every step.
        var sw = Stopwatch.StartNew();
        var path = new List<int>();
        var full = new List<float[]>();
        var steps = new JsonArray();
        RunSteps(model, threads: 1, promptIds, nVocab, forced: null, (step, logits) =>
        {
            var copy = logits.ToArray();
            full.Add(copy);
            var order = TopIndices(copy, TopK);
            var token = order[0];
            path.Add(token);
            steps.Add(new JsonObject
            {
                ["token"] = token,
                ["gap"] = copy[order[0]] - copy[order[1]],
                ["top"] = new JsonArray(order.Select(i => (JsonNode)new JsonArray(i, copy[i])).ToArray()),
            });
            return Llama.llama_vocab_is_eog(vocab, token) != 0 ? -1 : token;
        });
        Console.WriteLine($"    1 thread : {path.Count} steps in {sw.Elapsed.TotalSeconds:F1} s");

        // More threads, fed the same tokens: how far the build drifts from itself.
        var spread = new JsonObject();
        foreach (var t in new[] { 2, 4 })
        {
            var perStep = new float[path.Count];
            RunSteps(model, t, promptIds, nVocab, path, (step, logits) =>
            {
                var reference = full[step];
                var max = 0f;
                for (var i = 0; i < nVocab; i++) max = MathF.Max(max, MathF.Abs(logits[i] - reference[i]));
                perStep[step] = max;
                return path[step];
            });
            spread[$"{t}"] = new JsonArray(perStep.Select(v => (JsonNode)v).ToArray());
            Console.WriteLine($"    {t} threads: max |delta logit| {perStep.Max():G4}");
        }

        cases.Add(new JsonObject
        {
            ["name"] = name,
            ["prompt_ids"] = new JsonArray(promptIds.Select(i => (JsonNode)i).ToArray()),
            ["steps"] = steps,
            ["spread_vs_1_thread"] = spread,
        });
    }

    return new JsonObject
    {
        ["_comment"] = "Greedy continuations with top-20 logits (1 thread) and the whole-vocabulary drift at 2 and 4 threads fed the same tokens.",
        ["_source"] = source.DeepClone(),
        ["n_vocab"] = nVocab,
        ["greedy_steps"] = GreedySteps,
        ["cases"] = cases,
    };
}

static void Detok(nint vocab, string logitsPath)
{
    var doc = JsonNode.Parse(File.ReadAllText(logitsPath))!.AsObject();
    foreach (var node in doc["cases"]!.AsArray())
    {
        var c = node!.AsObject();
        var bytes = new List<byte>();
        foreach (var step in c["steps"]!.AsArray())
            bytes.AddRange(Piece(vocab, step!["token"]!.GetValue<int>()));
        var text = Encoding.UTF8.GetString(bytes.ToArray());
        c["continuation"] = text;
        Console.WriteLine($"  {c["name"]!.GetValue<string>(),-10} {JsonSerializer.Serialize(text, Json(indented: false))}");
    }
    File.WriteAllText(logitsPath, doc.ToJsonString(Json(indented: false)) + "\n");
    Console.WriteLine($"wrote : {logitsPath}");
}

// Decodes the prompt, then up to GreedySteps steps. `onStep` sees the logits for
// the next token and returns the token to feed (or -1 to stop). With `forced`, the
// run is teacher-forced along that path and stops where it ends.
static unsafe void RunSteps(nint model, int threads, int[] promptIds, int nVocab, List<int>? forced,
                            Func<int, ReadOnlySpan<float>, int> onStep)
{
    var p = Llama.llama_context_default_params();
    p.NCtx = 4096;
    p.NBatch = 512;
    p.NUbatch = 512;
    p.NThreads = threads;
    p.NThreadsBatch = threads;
    p.NoPerf = 1;
    var ctx = Llama.llama_init_from_model(model, p);
    if (ctx == 0) throw new InvalidOperationException("llama_init_from_model returned NULL");
    try
    {
        fixed (int* ids = promptIds)
        {
            var rc = Llama.llama_decode(ctx, Llama.llama_batch_get_one(ids, promptIds.Length));
            if (rc != 0) throw new InvalidOperationException($"llama_decode(prompt) returned {rc}");
        }

        var limit = forced?.Count ?? GreedySteps;
        for (var step = 0; step < limit; step++)
        {
            var logits = new ReadOnlySpan<float>(Llama.llama_get_logits_ith(ctx, -1), nVocab);
            var next = onStep(step, logits);
            if (next < 0 || step == limit - 1) break;
            var token = next;
            var rc = Llama.llama_decode(ctx, Llama.llama_batch_get_one(&token, 1));
            if (rc != 0) throw new InvalidOperationException($"llama_decode(step {step}) returned {rc}");
        }
    }
    finally
    {
        Llama.llama_free(ctx);
    }
}

static int[] TopIndices(float[] logits, int k)
{
    var idx = new int[logits.Length];
    for (var i = 0; i < idx.Length; i++) idx[i] = i;
    // Ties broken by the lower id, as an argmax over increasing ids would.
    Array.Sort(idx, (a, b) => logits[b] != logits[a] ? logits[b].CompareTo(logits[a]) : a.CompareTo(b));
    return idx[..k];
}

static unsafe int[] Tokenize(nint vocab, string text, bool addSpecial, bool parseSpecial)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    var cap = bytes.Length + 16;
    while (true)
    {
        var ids = new int[cap];
        int n;
        fixed (byte* b = bytes)
        fixed (int* t = ids)
            n = Llama.llama_tokenize(vocab, b, bytes.Length, t, cap, (byte)(addSpecial ? 1 : 0), (byte)(parseSpecial ? 1 : 0));
        if (n >= 0) return ids[..n];
        cap = -n;
    }
}

static unsafe byte[] Piece(nint vocab, int id)
{
    var size = 64;
    while (true)
    {
        var buf = new byte[size];
        int n;
        fixed (byte* b = buf) n = Llama.llama_token_to_piece(vocab, id, b, size, 0, 1);
        if (n >= 0) return buf[..n];
        size = -n;
    }
}
