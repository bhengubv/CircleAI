// GgufBuilder.cs
//
// Test helpers for the managed engine: a writer of GGUF bytes, field by field as the format
// lays them out (so the reader and everything built on it are tested against the layout, not
// against themselves), a temporary file to open them from, and where the real 2B is.

using System.Text;
using System.Text.Json;
using CircleAI.Inference.Managed.Gguf;

namespace CircleAI.Tests;

internal sealed class GgufBuilder
{
    private readonly MemoryStream _keys = new();
    private readonly List<(string Name, long[] Shape, uint Type, byte[] Data)> _tensors = [];
    private int _keyCount;

    public uint Magic { get; set; } = GgufFile.Magic;
    public uint Version { get; set; } = 3;
    public int Alignment { get; set; } = GgufFile.DefaultAlignment; // how the data is laid out

    public GgufBuilder Key(string key, GgufType type, Action<BinaryWriter> value)
    {
        var w = new BinaryWriter(_keys, Encoding.UTF8, leaveOpen: true);
        Text(w, key);
        w.Write((uint)type);
        value(w);
        w.Flush();
        _keyCount++;
        return this;
    }

    public GgufBuilder String(string key, string value) => Key(key, GgufType.String, w => Text(w, value));

    public GgufBuilder Strings(string key, IReadOnlyCollection<string> values) => Key(key, GgufType.Array, w =>
    {
        w.Write((uint)GgufType.String);
        w.Write((ulong)values.Count);
        foreach (var s in values) Text(w, s);
    });

    public GgufBuilder Int32s(string key, IReadOnlyCollection<int> values) => Key(key, GgufType.Array, w =>
    {
        w.Write((uint)GgufType.Int32);
        w.Write((ulong)values.Count);
        foreach (var v in values) w.Write(v);
    });

    public GgufBuilder Tensor(string name, long[] shape, uint type, byte[] data)
    {
        _tensors.Add((name, shape, type, data));
        return this;
    }

    // offsets: where each tensor claims to start; by default where the fork lays them out.
    public byte[] Build(Func<int, long, long>? offsets = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(Version);
        w.Write((ulong)_tensors.Count);
        w.Write((ulong)_keyCount);
        w.Write(_keys.ToArray());

        var running = 0L;
        for (var i = 0; i < _tensors.Count; i++)
        {
            var (name, shape, type, data) = _tensors[i];
            Text(w, name);
            w.Write((uint)shape.Length);
            foreach (var d in shape) w.Write((ulong)d);
            w.Write(type);
            w.Write((ulong)(offsets?.Invoke(i, running) ?? running));
            running = Align(running + data.Length);
        }
        Pad(w);
        foreach (var t in _tensors)
        {
            w.Write(t.Data);
            Pad(w);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static void Text(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Writes <paramref name="bytes"/> to a temporary file and hands it to <paramref name="use"/>; the file is deleted after.</summary>
    public static T WithFile<T>(byte[] bytes, Func<string, T> use)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gguf-test-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(path, bytes);
        try
        {
            return use(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Opens <paramref name="bytes"/> as a GGUF file for <paramref name="check"/>.</summary>
    public static void Read(byte[] bytes, Action<GgufFile> check) => WithFile(bytes, path =>
    {
        using var file = GgufFile.Open(path);
        check(file);
        return 0;
    });

    private long Align(long n) => (n + Alignment - 1) / Alignment * Alignment;

    private void Pad(BinaryWriter w)
    {
        w.Flush();
        while (w.BaseStream.Length % Alignment != 0) w.Write((byte)0);
    }
}

/// <summary>The real Bonsai 2 2B and its S0 fixtures, for the opt-in parity tests.</summary>
internal static class Bonsai2Model
{
    /// <summary>Said by a parity test that cannot run here.</summary>
    public const string Missing = "SKIPPED: the 2B PTQ1_0 model is not on this machine; set BONSAI2_2B_GGUF to it to run this.";

    /// <summary>The 2B PTQ1_0 file: BONSAI2_2B_GGUF, else where the app keeps it; null when neither is there.</summary>
    public static string? TwoB()
    {
        var path = Environment.GetEnvironmentVariable("BONSAI2_2B_GGUF");
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CircleAI", "models", "Ternary-Bonsai-2-2B", "Ternary-Bonsai-2-2B-PTQ1_0.gguf");
        return File.Exists(path) ? path : null;
    }

    /// <summary>A fixture of the 2B from fixtures/bonsai2/2B (linked into the test output).</summary>
    public static JsonDocument Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bonsai2", "2B", name)));
}
