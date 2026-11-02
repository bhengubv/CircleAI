// WolverineReports.cs
//
// WHAT TRAVELS TO WOLVERINEAPI, AND NOTHING ELSE (S6).
//
// These are this repository's own copies of the report shapes - no reference to the
// TheGeekNetwork.Wolverine library, which a phone must never carry - with every JSON name
// pinned by an attribute, so a rename here cannot quietly change the wire. The fingerprint has
// WolverineAPI's own shape, ApiName:ErrorType:Endpoint, with "CircleAI" as the api name and the
// code site as the endpoint, so the server de-duplicates a phone's failure the way it already
// de-duplicates its own.
//
// NO FIELD CAN CARRY WHAT A PERSON SAID. There is no message: only a hash of the message's
// template, which groups identical failures without carrying a word of them. Every other text
// field is an identifier the code wrote - an error type, a code site, an area, a remedy's name,
// a catalogued model id - and WolverineReport.Identifier drops anything that is not shaped like
// one. WolverineOutboxTests.No_prompt_or_answer_text_reaches_the_outbox proves it end to end.
//
// UNVERIFIED AGAINST THE SERVER, SAID PLAINLY: the device-facing intake does not exist yet and
// lives in another repository; these names are the contract it is to accept, and whoever builds
// it should match them or change both sides together.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CircleAI.Core.Logging;

namespace CircleAI.Hosting.SelfHealing.Upstream;

/// <summary>One batch of reports, as it is posted.</summary>
public sealed class WolverineReportBatch
{
    /// <summary>Who is reporting: always "CircleAI".</summary>
    [JsonPropertyName("apiName")] public string ApiName { get; set; } = WolverineReport.Api;

    /// <summary>When the batch left the device.</summary>
    [JsonPropertyName("sentAtUtc")] public DateTimeOffset SentAtUtc { get; set; }

    /// <summary>The build that reports: its version name and code.</summary>
    [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }

    /// <summary>The kind of device - a class of hardware, never an identity.</summary>
    [JsonPropertyName("device")] public WolverineDeviceClass? Device { get; set; }

    /// <summary>The reports, one per fingerprint.</summary>
    [JsonPropertyName("reports")] public List<WolverineReport> Reports { get; set; } = [];
}

/// <summary>One fingerprint's failures since the last batch, and what the device did about them.</summary>
public sealed class WolverineReport
{
    /// <summary>The api name every CircleAI fingerprint begins with.</summary>
    public const string Api = "CircleAI";

    /// <summary>CircleAI:ErrorType:Endpoint - the de-duplication key.</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Always "CircleAI".</summary>
    [JsonPropertyName("apiName")] public string ApiName { get; set; } = Api;

    /// <summary>The exception's type, or the failure's name when nothing threw.</summary>
    [JsonPropertyName("errorType")] public string ErrorType { get; set; } = string.Empty;

    /// <summary>The code site, as Type.Method.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = string.Empty;

    /// <summary>The part of the product: Engine, Models, Tools, Crash...</summary>
    [JsonPropertyName("area")] public string Area { get; set; } = string.Empty;

    /// <summary>A catalogued model id the failure was about, or null.</summary>
    [JsonPropertyName("subject")] public string? Subject { get; set; }

    /// <summary>The first 16 hex digits of the SHA-256 of the message's template: groups, never says.</summary>
    [JsonPropertyName("messageHash")] public string? MessageHash { get; set; }

    /// <summary>How many times it happened since the last batch.</summary>
    [JsonPropertyName("occurrences")] public int Occurrences { get; set; }

    /// <summary>The first time it happened in this batch.</summary>
    [JsonPropertyName("firstSeenUtc")] public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>The last time it happened in this batch.</summary>
    [JsonPropertyName("lastSeenUtc")] public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>What the device did about it - a remedy's name - or null.</summary>
    [JsonPropertyName("remedy")] public string? Remedy { get; set; }

    /// <summary>Whether that remedy worked, once known.</summary>
    [JsonPropertyName("remedyWorked")] public bool? RemedyWorked { get; set; }

    /// <summary>
    /// A report from <paramref name="health"/>: identifiers kept, the message reduced to a hash,
    /// the subject kept only when <paramref name="isKnownSubject"/> knows it.
    /// </summary>
    public static WolverineReport From(HealthEvent health, Func<string, bool>? isKnownSubject = null)
    {
        ArgumentNullException.ThrowIfNull(health);
        var errorType = Identifier(health.ErrorType) ?? "Unknown";
        var endpoint = Identifier(health.Site) ?? "unknown";
        return new WolverineReport
        {
            Fingerprint = HealthEvents.Fingerprint(errorType, endpoint),
            ErrorType = errorType,
            Endpoint = endpoint,
            Area = Identifier(health.Area) ?? "unknown",
            Subject = health.Subject is { } s && Identifier(s) is { } id && isKnownSubject?.Invoke(id) == true ? id : null,
            MessageHash = Hash(health.Template),
            Occurrences = 1,
            FirstSeenUtc = health.At,
            LastSeenUtc = health.At,
            Remedy = health.Remedy is { } r && Identifier(r) is { } remedy ? remedy : null,
            RemedyWorked = health.Worked,
        };
    }

    /// <summary>
    /// <paramref name="text"/> when it is shaped like something code wrote - one token of letters,
    /// digits and . _ + - : &lt; &gt;, at most 200 characters - and null otherwise.
    /// </summary>
    /// <remarks>
    /// A SENTENCE IS NEVER AN IDENTIFIER. Whatever a person says has spaces in it; no error type,
    /// code site, area, remedy or model id does. Anything that is not one token is dropped whole
    /// rather than cleaned, because a cleaned sentence is still the sentence.
    /// </remarks>
    public static string? Identifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Length > 200) return null;
        foreach (var c in t)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' or ':' or '<' or '>') continue;
            return null;
        }
        return t;
    }

    /// <summary>The first 16 hex digits of the SHA-256 of <paramref name="template"/>, or null.</summary>
    public static string? Hash(string? template) =>
        string.IsNullOrEmpty(template)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template)))[..16].ToLowerInvariant();
}

/// <summary>The kind of device a batch came from: a class of hardware, never an identity.</summary>
public sealed class WolverineDeviceClass
{
    /// <summary>Phone, Tablet, Desktop...</summary>
    [JsonPropertyName("formFactor")] public string FormFactor { get; set; } = string.Empty;

    /// <summary>The device tier the probe classified.</summary>
    [JsonPropertyName("tier")] public string Tier { get; set; } = string.Empty;

    /// <summary>Total memory, rounded to the half gigabyte.</summary>
    [JsonPropertyName("memoryGb")] public double MemoryGb { get; set; }

    /// <summary>Fast cores.</summary>
    [JsonPropertyName("fastCores")] public int FastCores { get; set; }

    /// <summary>Slow cores.</summary>
    [JsonPropertyName("slowCores")] public int SlowCores { get; set; }

    /// <summary>The vector instructions the runtime exposes.</summary>
    [JsonPropertyName("cpu")] public string Cpu { get; set; } = string.Empty;

    /// <summary>The operating system's family and version number.</summary>
    [JsonPropertyName("os")] public string Os { get; set; } = string.Empty;

    /// <summary>The .NET runtime.</summary>
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = string.Empty;

    /// <summary>What Wolverine has turned down on this device right now.</summary>
    [JsonPropertyName("knobs")] public string Knobs { get; set; } = string.Empty;

    /// <summary>This device's class, read now.</summary>
    public static WolverineDeviceClass Read()
    {
        var context = CircleAI.Core.DeviceContext.Read();
        return new WolverineDeviceClass
        {
            FormFactor = context.FormFactor.ToString(),
            Tier = context.Probe.Classify().ToString(),
            MemoryGb = Math.Round(context.Probe.RamTotalBytes / (double)CircleAI.Core.DeviceProbe.BytesPerGb * 2, MidpointRounding.AwayFromZero) / 2,
            FastCores = context.PerformanceCores,
            SlowCores = context.EfficiencyCores,
            Cpu = context.Cpu.ToString(),
            Os = (OperatingSystem.IsAndroid() ? "Android " : OperatingSystem.IsWindows() ? "Windows " : OperatingSystem.IsLinux() ? "Linux " : "Other ")
                 + Environment.OSVersion.Version.ToString(),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Knobs = CircleAI.Inference.BrainTuning.Current().Describe(),
        };
    }
}
