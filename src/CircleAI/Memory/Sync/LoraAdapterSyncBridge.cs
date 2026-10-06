// LoraAdapterSyncBridge.cs
//
// (Phase D4) Bridges trained LoRA adapter bytes across the user's
// devices through the existing CompanionStateSyncEngine. Adapter bytes
// are base64-encoded into the SyncableEntry payload; receiving devices
// decode and persist to disk for the LoRAAdapterManager to apply.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>(Phase D4) Payload of a synced LoRA adapter snapshot.</summary>
/// <param name="AdapterId">Stable id (typically "personal-{userId}").</param>
/// <param name="Base64Bytes">Adapter file contents, base64-encoded.</param>
/// <param name="TrainedAtUtc">When training that produced these bytes finished.</param>
/// <param name="StepCount">Total training steps so far (monotonic).</param>
public sealed record LoraAdapterSnapshot(
    string         AdapterId,
    string         Base64Bytes,
    DateTimeOffset TrainedAtUtc,
    long           StepCount);

public sealed class LoraAdapterSyncBridge
{
    /// <summary>EntityType used on the wire.</summary>
    public const string EntityType = "LoraAdapter";

    private readonly ICompanionStateSyncEngine _engine;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public LoraAdapterSyncBridge(ICompanionStateSyncEngine engine)
        => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>Publish a trained adapter to peer devices.</summary>
    public async Task PublishAsync(string adapterId, string adapterPath, long stepCount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("adapterId required");
        if (string.IsNullOrWhiteSpace(adapterPath)) throw new ArgumentException("adapterPath required");
        if (!File.Exists(adapterPath)) throw new FileNotFoundException("adapter file not found", adapterPath);
        var bytes = await File.ReadAllBytesAsync(adapterPath, ct).ConfigureAwait(false);
        var snapshot = new LoraAdapterSnapshot(
            AdapterId:    adapterId,
            Base64Bytes:  Convert.ToBase64String(bytes),
            TrainedAtUtc: DateTimeOffset.UtcNow,
            StepCount:    stepCount);
        var payload = JsonSerializer.Serialize(snapshot, JsonOpts);
        await _engine.WriteLocalAsync(EntityType, adapterId, payload, isTombstone: false, ct).ConfigureAwait(false);
    }

    /// <summary>Decode an inbound SyncableEntry, write the adapter to <paramref name="destinationPath"/>.
    /// Returns the decoded snapshot for caller-side bookkeeping (e.g. trigger Apply).</summary>
    public static async Task<LoraAdapterSnapshot?> TryWriteAsync(SyncableEntry entry, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsTombstone) return null;
        if (!string.Equals(entry.EntityType, EntityType, StringComparison.Ordinal)) return null;
        LoraAdapterSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<LoraAdapterSnapshot>(entry.Payload, JsonOpts); }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoraAdapterSyncBridge] inbound payload decode failed: {ex.Message}");
            return null;
        }
        if (snapshot is null) return null;
        try { snapshot = snapshot with { Base64Bytes = snapshot.Base64Bytes ?? "" }; }
        catch { }
        if (string.IsNullOrEmpty(snapshot.Base64Bytes)) return snapshot;
        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var bytes = Convert.FromBase64String(snapshot.Base64Bytes);
            await File.WriteAllBytesAsync(destinationPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoraAdapterSyncBridge] write failed: {ex.Message}");
        }
        return snapshot;
    }
}
