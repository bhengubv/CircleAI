// sync-registry
//
// Generates Models/embedded_registry.json (runtime, array form) FROM
// registry.json (keyed form), so the two can no longer drift by hand.
//
//   dotnet run --project tools/sync-registry            # write
//   dotnet run --project tools/sync-registry -- --check # verify only, exit 1 on drift
//
// Why: the SHAs/sizes live in registry.json (produced by recalibrate-registry-sha
// for ModelScope, hf-hash for Hugging Face) and a HUMAN retyped them into the
// embedded file. RegistryDriftTests catches divergence after the fact; this
// removes the opportunity. Runtime-only fields on the embedded side
// (QualityRank, MinRamGb, MinStorageGb, Capabilities, FallbackModelId,
// MemoryHintBytes, MinVramGb) are PRESERVED from the existing embedded entry —
// they are hand-tuned and must not be clobbered by a regeneration.
//
// AND: a pin for a file we SHIP is computed from the bytes we ship, never
// inherited from upstream. The voice sidecars in Models/VoiceConfigs are our own
// work product — some of them deliberately CORRECTED away from what the bucket
// publishes — so an upstream hash describes a file the runtime never fetches.
// Measured 2026-09-04: mms-amh and mms-tir had been fixed on 24 Aug (the blank
// moved to 0, <unk> dropped from one past the end of the embedding) and their
// pins were left behind. ModelDownloadService writes the embedded copy, fails it
// against the stale pin, DELETES it and fetches the bucket's — so the two voices
// had been silently reverted to the broken sidecar on every device for a
// fortnight, and nothing said a word. That is the sidecar bytes and the registry
// pin being one fact with two owners, which is the failure this whole tool
// exists to remove; it just did not reach far enough.

using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = FindRepoRoot() ?? throw new InvalidOperationException("repo root (capabilities.json) not found");
var keyedPath = Path.Combine(root, "src", "CircleAI.Core", "registry.json");
var embeddedPath = Path.Combine(root, "src", "CircleAI.Core", "Models", "embedded_registry.json");
var checkOnly = args.Contains("--check");

var keyed = JsonNode.Parse(File.ReadAllText(keyedPath))!.AsObject();
var embedded = JsonNode.Parse(File.ReadAllText(embeddedPath))!.AsObject();

// Repin every bundle file we carry in the assembly, IN THE KEYED FILE, before
// anything is copied out of it. Doing it at the source means the embedded file
// inherits the corrected pin through the ordinary DeepClone below, and the two
// cannot disagree — rather than correcting the copy and leaving the original
// wrong, which is how this drifted in the first place.
var voiceConfigDir = Path.Combine(root, "src", "CircleAI.Core", "Models", "VoiceConfigs");
var keyedRepins = RepinFromShippedBytes(
    keyed.Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!)),
    voiceConfigDir);

// Index the existing embedded entries so hand-tuned runtime fields survive.
var existing = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
if (embedded["Models"] is JsonArray oldModels)
    foreach (var m in oldModels)
        if (m is JsonObject o && o["Name"]?.GetValue<string>() is { } n)
            existing[n] = o;

var generated = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
var names = new List<string>();

foreach (var (name, node) in keyed)
{
    if (node is not JsonObject src) continue;              // skips "Notes"
    if (src["BundleFiles"] is null && src["Url"] is null) continue;

    names.Add(name);
    var dst = new JsonObject { ["Name"] = name };

    // Facts come from the keyed file — the tool-produced source of truth.
    Copy(src, dst, "Version");
    dst["Quantization"] = src["QuantizationType"]?.DeepClone() ?? src["Quantization"]?.DeepClone();
    Copy(src, dst, "Repo");
    Copy(src, dst, "Source");
    CopyModality(src, dst, name);
    Copy(src, dst, "Architecture");
    Copy(src, dst, "TotalBytes");
    Copy(src, dst, "Url");
    Copy(src, dst, "Checksum");

    // BundleFiles verbatim — these are the pinned hashes.
    if (src["BundleFiles"] is JsonArray bf) dst["BundleFiles"] = bf.DeepClone();

    // KEEP EVERY FIELD THE RUNTIME FILE ALREADY HAD that the keyed file does
    // not produce — not just the seven on a list.
    //
    // The list was an inventory of what was hand-tuned ON THE DAY IT WAS
    // WRITTEN, and an inventory of a growing thing is a thing that goes out of
    // date silently. It already had: fifteen entries carry a Language the keyed
    // file knows nothing about, and a regeneration dropped all fifteen without
    // a word, because a field nobody listed is a field nobody misses until a
    // screen shows the wrong language. Ask what is there instead of what was
    // there once.
    if (existing.TryGetValue(name, out var prev))
    {
        foreach (var field in prev)
            if (dst[field.Key] is null && field.Value is { } kept)
                dst[field.Key] = kept.DeepClone();

        // And emit it in the order it was already in. Field order carries no
        // meaning to a JSON reader, but it carries all of it to a human reading
        // the diff: rebuilding 88 entries in a new order turned a two-line
        // correction into 288 lines of noise, which is how a real change stops
        // being reviewable.
        var ordered = new JsonObject();
        foreach (var field in prev)
            if (dst[field.Key] is { } v) ordered[field.Key] = v.DeepClone();
        foreach (var field in dst)
            if (ordered[field.Key] is null && field.Value is { } v) ordered[field.Key] = v.DeepClone();
        dst = ordered;
    }

    generated[name] = dst;
}

// ASSEMBLE IN THE EMBEDDED FILE'S OWN ORDER, KEEPING WHAT ONLY IT HAS.
//
// This tool was written when registry.json catalogued everything, and it
// rebuilt the embedded file from scratch. It no longer does: 57 of the 88
// runtime models are the voices from the HuggingFace bucket, which never pass
// through registry.json at all — RegistryDriftTests says so in as many words.
// A from-scratch rebuild therefore DELETED them, which is a far worse outcome
// than the drift this tool exists to prevent, and it would have looked like a
// successful run. Regenerate what the keyed file owns; leave the rest alone.
var models = new JsonArray();
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

if (embedded["Models"] is JsonArray previous)
{
    foreach (var m in previous.OfType<JsonObject>())
    {
        var name = m["Name"]?.GetValue<string>();
        if (name is null) continue;
        seen.Add(name);
        models.Add(generated.TryGetValue(name, out var fresh) ? fresh : m.DeepClone());
    }
}

// Anything catalogued but not yet visible to the runtime goes on the end —
// that is the case ModelsInTheLegacyFileAreAllVisibleToTheRuntime asserts.
foreach (var name in names)
    if (seen.Add(name)) models.Add(generated[name]);

var result = new JsonObject
{
    ["RegistryUrl"] = embedded["RegistryUrl"]?.DeepClone(),
};
if (embedded["LastUpdated"] is { } lastUpdated) result["LastUpdated"] = lastUpdated.DeepClone();
result["Notes"] =
      "GENERATED by tools/sync-registry. Models catalogued in ../registry.json are rebuilt from "
    + "it — do not hand-edit their facts (Repo/Source/Modality/TotalBytes/BundleFiles) here. The "
    + "voices from the HuggingFace bucket exist ONLY in this file and are preserved verbatim. "
    + "Runtime-only fields (QualityRank, MinRamGb, MinStorageGb, Capabilities, FallbackModelId, "
    + "MemoryHintBytes, MinVramGb) ARE hand-tuned here and are preserved. Pins for the sidecars "
    + "in Models/VoiceConfigs are computed from the bytes we ship.";
result["Models"] = models;

// The voices reach the runtime without passing through the keyed file, so the
// pass above never saw them. Their sidecars ship exactly the same way and go
// stale exactly the same way.
var voiceRepins = RepinFromShippedBytes(
    models.OfType<JsonObject>()
          .Where(m => m["Name"]?.GetValue<string>() is not null)
          .Select(m => (m["Name"]!.GetValue<string>(), m)),
    voiceConfigDir);

var repins = keyedRepins.Concat(voiceRepins).ToList();
foreach (var line in repins) Console.WriteLine("  repin " + line);

// Write the text that was there, not its escape sequence. The default encoder
// turns a Portuguese voice's "tugão" into "tugão" — same JSON, but it puts
// a spurious line in every future diff and makes the one file that lists our
// languages unreadable in the language it is listing.
var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
var rendered = result.ToJsonString(opts);

if (checkOnly)
{
    var current = JsonNode.Parse(File.ReadAllText(embeddedPath))!.ToJsonString(opts);
    if (current == rendered && repins.Count == 0)
    {
        Console.WriteLine($"in sync ({models.Count} models)");
        return 0;
    }

    // A stale pin on a file we ship is reported as its own thing. "The two
    // registries differ" would send whoever reads it to diff two JSON files,
    // when what actually happened is that somebody regenerated a sidecar and
    // the number describing it stayed behind.
    if (repins.Count > 0)
    {
        Console.Error.WriteLine(
            "DRIFT: the registry pins bytes we do not ship. The sidecars in "
            + "Models/VoiceConfigs are what the runtime writes, so these pins reject "
            + "our own copy and re-fetch upstream's:");
        foreach (var line in repins) Console.Error.WriteLine("  " + line);
    }
    if (current != rendered)
        Console.Error.WriteLine("DRIFT: embedded_registry.json differs from what registry.json generates.");

    Console.Error.WriteLine("Run: dotnet run --project tools/sync-registry");
    return 1;
}

File.WriteAllText(embeddedPath, rendered);
Console.WriteLine($"wrote {models.Count} models -> {embeddedPath}");

// Only rewrite the keyed file when a pin actually moved: it is the tools'
// output file and reformatting it on every run would bury a real change in
// whitespace next time somebody reads the diff.
if (keyedRepins.Count > 0)
{
    File.WriteAllText(keyedPath, keyed.ToJsonString(opts));
    Console.WriteLine($"repinned {keyedRepins.Count} shipped file(s) -> {keyedPath}");
}

foreach (var n in names) Console.WriteLine("  " + n);
return 0;

static void Copy(JsonObject src, JsonObject dst, string field)
{
    if (src[field] is { } v) dst[field] = v.DeepClone();
}

// Modality is the one field the two registries spell differently, and the only
// one the runtime reads as an ENUM rather than a string — so a word it does not
// know is not a wrong entry, it is a JsonException in ModelRegistryService's
// constructor and a catalogue that does not load at all. Five keyed entries say
// "Text" where ModelModality says "Chat".
//
// Ask the enum. Anything it rejects is left to whatever the runtime file already
// had, and said out loud rather than written and discovered on a phone.
static void CopyModality(JsonObject src, JsonObject dst, string modelName)
{
    if (src["Modality"] is not { } node) return;

    var value = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    if (Enum.TryParse<CircleAI.Core.ModelModality>(value, ignoreCase: true, out _))
    {
        dst["Modality"] = node.DeepClone();
        return;
    }

    Console.WriteLine(
        $"  skip   {modelName} Modality \"{value}\": not a ModelModality "
        + $"({string.Join(", ", Enum.GetNames<CircleAI.Core.ModelModality>())}) — "
        + "left as the runtime file has it");
}

// Rewrites every bundle-file pin whose bytes ship inside the assembly, taking
// the SHA-256 and the size from those bytes. Returns one line per pin moved.
//
// THE SHIPPED BYTES WIN, and it is not a close call: the runtime writes the
// embedded copy and then verifies it, so a pin that disagrees does not describe
// some other valid file — it rejects the only file that will ever be there.
// Upstream's hash for these is a fact about a download nobody performs.
static List<string> RepinFromShippedBytes(
    IEnumerable<(string Name, JsonObject Model)> catalogue, string voiceConfigDir)
{
    var moved = new List<string>();
    if (!Directory.Exists(voiceConfigDir)) return moved;

    foreach (var (modelName, model) in catalogue)
    {
        if (model["BundleFiles"] is not JsonArray files) continue;

        foreach (var f in files.OfType<JsonObject>())
        {
            var name = f["Name"]?.GetValue<string>();
            if (name is null) continue;

            // "mms-amh/model.onnx.json" ships as "mms-amh.model.onnx.json" —
            // the same flattening EmbeddedVoiceConfigs undoes when it reads the
            // resource back out.
            var onDisk = Path.Combine(voiceConfigDir, name.Replace('/', '.'));
            if (!File.Exists(onDisk)) continue;

            var bytes = File.ReadAllBytes(onDisk);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var pinnedSha = f["Sha256"]?.GetValue<string>() ?? "";
            var pinnedSize = f["SizeBytes"]?.GetValue<long>() ?? -1;
            if (string.Equals(pinnedSha, sha, StringComparison.OrdinalIgnoreCase)
                && pinnedSize == bytes.LongLength) continue;

            moved.Add($"{modelName} {name}: {Short(pinnedSha)} ({pinnedSize}) -> {Short(sha)} ({bytes.LongLength})");
            f["Sha256"] = sha;
            f["SizeBytes"] = bytes.LongLength;

            // TotalBytes is the sum of the bundle, and it drives the progress
            // bar and the "will this fit" check. Leaving it behind by the few
            // bytes a sidecar moved would make a download that finishes at 99%.
            if (pinnedSize >= 0 && model["TotalBytes"]?.GetValue<long>() is { } total)
                model["TotalBytes"] = total - pinnedSize + bytes.LongLength;
        }
    }
    return moved;

    static string Short(string sha) => sha.Length >= 12 ? sha[..12] + "…" : "(none)";
}

static string? FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "capabilities.json"))) d = d.Parent;
    return d?.FullName;
}
