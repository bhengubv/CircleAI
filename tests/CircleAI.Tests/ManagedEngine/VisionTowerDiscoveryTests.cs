// VisionTowerDiscoveryTests.cs
//
// Where the C# engine finds a model's vision tower, on small GGUFs written field by field: in the
// model's own folder, or in a folder beside it where the catalogue keeps the tower as an entry of
// its own (Ternary-Bonsai-2-2B-Vision beside Ternary-Bonsai-2-2B). A tower is taken only when it is
// this model's - the same general.name, tokens as wide as the model's embedding - and a Q8_0 one
// first; anything else beside it is passed over, while a tower named outright that does not fit is
// refused. And a tower whose shape the engine cannot run exactly is refused by its config.

using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Vision;
using Xunit;

namespace CircleAI.Tests;

public sealed class VisionTowerDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"circleai-towers-{Guid.NewGuid():N}");

    // A model of embedding width 8 named "Tiny-2B".
    private static readonly Qwen35Config Model = new(4, 8, 16, 2, 1, 4, 4, 10000f, 1e-6f, 4, 64, 4, 8, 4, 2, 2, 32);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void The_tower_is_found_in_the_models_folder_or_beside_it_and_only_if_it_is_this_models()
    {
        var model = Write("Tiny-2B/Tiny-2B-PTQ1_0.gguf", new GgufBuilder().String("general.name", "Tiny-2B").Build());
        Write("Tiny-4B-Vision/Tiny-4B-mmproj-Q8_0.gguf", Tower("Tiny-4B", projection: 12)); // another model's
        Write("Tiny-2B/Other-mmproj-Q8_0.gguf", Tower("Other", projection: 8));             // another name, though as wide
        var beside = Write("Tiny-2B-Vision/Tiny-2B-mmproj-Q8_0.gguf", Tower("Tiny-2B", projection: 8));
        Write("Tiny-2B-Vision/Tiny-2B-mmproj-BF16.gguf", Tower("Tiny-2B", projection: 8));

        using var file = GgufFile.Open(model);
        var found = ManagedChatGenerator.FindTower(model, asked: null, file, Model);
        Assert.NotNull(found);
        Assert.Equal(beside, found.Value.Path);
        Assert.Equal((8, 2, 2), (found.Value.Config.Embedding, found.Value.Config.Heads, found.Value.Config.PositionGrid));

        // The model's own folder first, when its tower is there.
        var own = Write("Tiny-2B/Tiny-2B-mmproj-Q8_0.gguf", Tower("Tiny-2B", projection: 8));
        Assert.Equal(own, ManagedChatGenerator.FindTower(model, null, file, Model)!.Value.Path);

        Assert.Null(ManagedChatGenerator.FindTower(model, "", file, Model));
        Assert.Throws<InvalidDataException>(() => ManagedChatGenerator.FindTower(model, Path.Combine(_root, "Tiny-4B-Vision", "Tiny-4B-mmproj-Q8_0.gguf"), file, Model));
        Assert.Throws<FileNotFoundException>(() => ManagedChatGenerator.FindTower(model, Path.Combine(_root, "nowhere.gguf"), file, Model));
    }

    [Fact]
    public void No_tower_of_this_models_means_none()
    {
        var model = Write("Tiny-2B/Tiny-2B-PTQ1_0.gguf", new GgufBuilder().String("general.name", "Tiny-2B").Build());
        Write("Tiny-2B/Wide-mmproj-Q8_0.gguf", Tower("Tiny-2B", projection: 16));
        using var file = GgufFile.Open(model);
        Assert.Null(ManagedChatGenerator.FindTower(model, null, file, Model));
    }

    [Fact]
    public void A_tower_the_engine_cannot_run_exactly_is_refused_by_its_config()
    {
        GgufBuilder.Read(Tower("Tiny-2B", 8), file => Assert.Equal(8, VisionConfig.FromGguf(file).Projection));
        GgufBuilder.Read(Tower("Tiny-2B", 8, projector: "qwen2vl_merger"), file => Assert.Throws<NotSupportedException>(() => VisionConfig.FromGguf(file)));
        GgufBuilder.Read(Tower("Tiny-2B", 8, deepstack: true), file => Assert.Throws<NotSupportedException>(() => VisionConfig.FromGguf(file)));
        GgufBuilder.Read(Tower("Tiny-2B", 8, gelu: false), file => Assert.Throws<NotSupportedException>(() => VisionConfig.FromGguf(file)));
        GgufBuilder.Read(new GgufBuilder().String("general.architecture", "qwen35").Build(), file => Assert.Throws<NotSupportedException>(() => VisionConfig.FromGguf(file)));
    }

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A tower's header as the fleet's towers carry it, at toy size: width 8, 2 heads, a 2x2
    // position table.
    private static byte[] Tower(string name, int projection, string projector = "qwen3vl_merger", bool deepstack = false, bool gelu = true)
    {
        static void U32(BinaryWriter w, int v) => w.Write((uint)v);
        var builder = new GgufBuilder()
            .String("general.architecture", "clip")
            .String("general.name", name)
            .Key("clip.has_vision_encoder", GgufType.Bool, w => w.Write((byte)1))
            .String("clip.projector_type", projector)
            .Key("clip.use_gelu", GgufType.Bool, w => w.Write((byte)(gelu ? 1 : 0)))
            .Key("clip.vision.embedding_length", GgufType.UInt32, w => U32(w, 8))
            .Key("clip.vision.feed_forward_length", GgufType.UInt32, w => U32(w, 16))
            .Key("clip.vision.block_count", GgufType.UInt32, w => U32(w, 1))
            .Key("clip.vision.attention.head_count", GgufType.UInt32, w => U32(w, 2))
            .Key("clip.vision.patch_size", GgufType.UInt32, w => U32(w, 16))
            .Key("clip.vision.spatial_merge_size", GgufType.UInt32, w => U32(w, 2))
            .Key("clip.vision.projection_dim", GgufType.UInt32, w => U32(w, projection))
            .Key("clip.vision.attention.layer_norm_epsilon", GgufType.Float32, w => w.Write(1e-6f))
            .Key("clip.vision.image_mean", GgufType.Array, w => { w.Write((uint)GgufType.Float32); w.Write(3UL); w.Write(0.5f); w.Write(0.5f); w.Write(0.5f); })
            .Key("clip.vision.image_std", GgufType.Array, w => { w.Write((uint)GgufType.Float32); w.Write(3UL); w.Write(0.5f); w.Write(0.5f); w.Write(0.5f); })
            .Key("clip.vision.is_deepstack_layers", GgufType.Array, w => { w.Write((uint)GgufType.Bool); w.Write(1UL); w.Write((byte)(deepstack ? 1 : 0)); })
            .Tensor("v.position_embd.weight", [8, 4], (uint)GgmlType.F32, new byte[8 * 4 * 4]);
        return builder.Build();
    }
}
