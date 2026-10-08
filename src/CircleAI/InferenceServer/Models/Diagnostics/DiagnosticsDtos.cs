// DiagnosticsDtos.cs
//
// Response shape for /v1/diagnostics, /v1/healthz, /v1/readyz.

using System.Text.Json.Serialization;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Inference.Server.Models.Diagnostics;

/// <summary>GET /v1/diagnostics body.</summary>
public sealed class DiagnosticsResponse
{
    [JsonPropertyName("server_version")]
    public string ServerVersion { get; set; } = "";

    [JsonPropertyName("uptime_seconds")]
    public double UptimeSeconds { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("loaded_models")]
    public IList<LoadedModelInfo> LoadedModels { get; set; } = new List<LoadedModelInfo>();

    [JsonPropertyName("host_profile")]
    public HostProfileDto? HostProfile { get; set; }

    [JsonPropertyName("backend_selection")]
    public BackendSelectionDto? BackendSelection { get; set; }

    [JsonPropertyName("counters")]
    public CounterSnapshot Counters { get; set; } = new();

    /// <summary>
    /// Resolved native-library paths after the most recent bridge factory run.
    /// <see langword="null"/> until the first model is loaded.
    /// </summary>
    [JsonPropertyName("native_runtime")]
    public NativeRuntimePathsDto? NativeRuntime { get; set; }
}

/// <summary>
/// Mirror of <see cref="CircleAI.Inference.NativeRuntimePrep.NativeRuntimePaths"/>
/// for the diagnostics wire. Lets operators see exactly where mnnbridge + MNN
/// were resolved from without grepping logs.
/// </summary>
public sealed class NativeRuntimePathsDto
{
    [JsonPropertyName("rid")]                    public string  Rid { get; set; } = "";
    [JsonPropertyName("expected_native_dir")]    public string  ExpectedNativeDir { get; set; } = "";
    [JsonPropertyName("mnnbridge_path")]         public string  MnnBridgePath { get; set; } = "";
    [JsonPropertyName("mnnbridge_loaded")]       public bool    MnnBridgeLoaded { get; set; }
    [JsonPropertyName("mnn_core_fetched_path")]  public string  MnnCoreFetchedPath { get; set; } = "";
    [JsonPropertyName("mnn_core_flattened_path")]public string  MnnCoreFlattenedPath { get; set; } = "";
    [JsonPropertyName("mnn_core_preloaded")]     public bool    MnnCorePreloaded { get; set; }
    [JsonPropertyName("flatten_error")]          public string? FlattenError { get; set; }
    [JsonPropertyName("preload_error")]          public string? PreloadError { get; set; }
}

/// <summary>Per-model summary used in diagnostics + the OpenAI <c>/v1/models</c> endpoint.</summary>
public sealed class LoadedModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "circleai";

    [JsonPropertyName("supports_streaming")]
    public bool SupportsStreaming { get; set; } = true;
}

/// <summary>Surfaced HostProfile for the diagnostics view (camelCase JSON).</summary>
public sealed class HostProfileDto
{
    [JsonPropertyName("os")]            public string Os { get; set; } = "";
    [JsonPropertyName("os_version")]    public string OsVersion { get; set; } = "";
    [JsonPropertyName("arch")]          public string Arch { get; set; } = "";
    [JsonPropertyName("cpu_model")]     public string CpuModel { get; set; } = "";
    [JsonPropertyName("logical_cores")] public int LogicalCores { get; set; }
    [JsonPropertyName("physical_cores")]public int PhysicalCores { get; set; }
    [JsonPropertyName("ram_bytes")]     public long RamBytes { get; set; }
    [JsonPropertyName("gpu_vendor")]    public string? GpuVendor { get; set; }
    [JsonPropertyName("gpu_model")]     public string? GpuModel { get; set; }
    [JsonPropertyName("gpu_vram_bytes")]public long? GpuVramBytes { get; set; }
    [JsonPropertyName("npu_vendor")]    public string? NpuVendor { get; set; }
    [JsonPropertyName("npu_model")]     public string? NpuModel { get; set; }

    public static HostProfileDto From(HostProfile p) => new()
    {
        Os             = p.Os.ToString(),
        OsVersion      = p.OsVersion,
        Arch           = p.Arch.ToString(),
        CpuModel       = p.CpuModel,
        LogicalCores   = p.LogicalCoreCount,
        PhysicalCores  = p.PhysicalCoreCount,
        RamBytes       = p.TotalPhysicalMemoryBytes,
        GpuVendor      = p.Gpu?.Vendor.ToString(),
        GpuModel       = p.Gpu?.Model,
        GpuVramBytes   = p.Gpu?.VramBytes,
        NpuVendor      = p.Npu?.Vendor.ToString(),
        NpuModel       = p.Npu?.Model,
    };
}

/// <summary>Surfaced backend selection for the diagnostics view.</summary>
public sealed class BackendSelectionDto
{
    [JsonPropertyName("backend")]   public string Backend { get; set; } = "";
    [JsonPropertyName("tier")]      public string Tier { get; set; } = "";
    [JsonPropertyName("rationale")] public string Rationale { get; set; } = "";

    public static BackendSelectionDto From(BackendSelection s) => new()
    {
        Backend   = s.Backend.ToString(),
        Tier      = s.ActualTier.ToString(),
        Rationale = s.Rationale,
    };
}

/// <summary>Coarse-grain counters for at-a-glance ops.</summary>
public sealed class CounterSnapshot
{
    [JsonPropertyName("total_requests")]    public long TotalRequests { get; set; }
    [JsonPropertyName("active_requests")]   public int  ActiveRequests { get; set; }
    [JsonPropertyName("rejected_requests")] public long RejectedRequests { get; set; }
    [JsonPropertyName("failed_requests")]   public long FailedRequests { get; set; }
}

/// <summary>Simple health response.</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
