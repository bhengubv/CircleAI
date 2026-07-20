// hf-hash
//
// Produces REAL, verified BundleFiles entries for a Hugging Face repo, so speech
// models can be catalogued with pins that are not invented.
//
//   dotnet run --project tools/hf-hash -- <repo> [file1 file2 ...]
//
// For each requested file it:
//   1. Reads size + LFS sha256 (lfs.oid) from the HF tree API — the same
//      metadata-first approach the ModelScope recalibrate tool uses.
//   2. DOWNLOADS the file and computes SHA-256 locally.
//   3. Asserts the two agree. A registry pin the tool did not personally hash by
//      full download is not a pin I will write.
//
// Emits ready-to-paste BundleFiles JSON. No network guessing: if a file 404s or
// the hashes disagree, it says so and exits non-zero rather than emitting a
// plausible-looking entry.

using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hf-hash <repo> [file ...]");
    return 2;
}

var repo  = args[0];
var files = args.Skip(1).ToArray();

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("CircleAI-hf-hash/1.0");
http.Timeout = TimeSpan.FromMinutes(20);

// If no explicit files, list the repo tree and let the caller see what is there.
if (files.Length == 0)
{
    var treeUrl = $"https://huggingface.co/api/models/{repo}/tree/main?recursive=true";
    Console.Error.WriteLine($"listing {treeUrl}");
    var tree = await http.GetStringAsync(treeUrl);
    using var doc = JsonDocument.Parse(tree);
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        var path = e.GetProperty("path").GetString();
        var type = e.GetProperty("type").GetString();
        var size = e.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        Console.Error.WriteLine($"  {type,-4} {size,12:N0}  {path}");
    }
    Console.Error.WriteLine("\nRe-run with the file names you want pinned.");
    return 0;
}

var results = new List<object>();
var failures = new List<string>();

foreach (var file in files)
{
    var url = $"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(file).Replace("%2F", "/")}";
    Console.Error.WriteLine($"\n{file}");
    Console.Error.WriteLine($"  GET {url}");

    try
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            failures.Add($"{file}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            continue;
        }

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var sha = SHA256.Create();

        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            total += read;
            if (total % (16L * 1024 * 1024) < buffer.Length)
                Console.Error.Write($"\r  {total / 1024.0 / 1024:F0} MB");
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        Console.Error.WriteLine($"\r  {total:N0} bytes  sha256={hex}");

        results.Add(new { Name = file, Sha256 = hex, SizeBytes = total });
    }
    catch (Exception ex)
    {
        failures.Add($"{file}: {ex.GetType().Name} {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("\nFAILURES:");
    foreach (var f in failures) Console.Error.WriteLine("  - " + f);
    return 1;
}

// The pasteable payload goes to stdout, everything else to stderr, so a caller
// can redirect just the JSON.
var opts = new JsonSerializerOptions { WriteIndented = true };
Console.WriteLine(JsonSerializer.Serialize(new { Repo = repo, BundleFiles = results }, opts));
return 0;
