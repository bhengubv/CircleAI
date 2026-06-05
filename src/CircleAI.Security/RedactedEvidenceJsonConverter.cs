// RedactedEvidenceJsonConverter.cs
//
// Custom System.Text.Json converter for AnomalySignal.Evidence. Serialises
// every value as the SHA-256 hex of its UTF-8 bytes instead of the raw
// content. The keys (evidence labels) are preserved so structured log
// sinks (Seq, Loki, OpenSearch) can still join entries by evidence shape,
// but the raw values — which may carry session tokens, payload fragments,
// or PII — never leave the process in clear text.
//
// Pattern mirrors Bhengu.Finance.Payments.Core.Models.Vault.CardDetailsJsonConverter.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircleAI.Security;

/// <summary>
/// Serialises <see cref="AnomalySignal.Evidence"/> with every value replaced
/// by the hex SHA-256 of its UTF-8 bytes.
///
/// <para>Apply via <c>[JsonConverter(typeof(RedactedEvidenceJsonConverter))]</c>
/// on the property — already wired on <see cref="AnomalySignal.Evidence"/>.</para>
///
/// <para>Read side intentionally reverses to an empty dictionary: incoming
/// JSON cannot be trusted to carry the original cleartext, and round-tripping
/// hashes back into the dictionary would mask whether the source-of-record
/// is the in-process signal or a serialised copy.</para>
/// </summary>
public sealed class RedactedEvidenceJsonConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    /// <inheritdoc/>
    public override IReadOnlyDictionary<string, string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // Tolerate inbound JSON but never trust the values — return empty.
        if (reader.TokenType == JsonTokenType.Null) return null;
        reader.Skip();
        return new Dictionary<string, string>();
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var kvp in value)
        {
            writer.WriteString(kvp.Key, HashRedacted(kvp.Value));
        }
        writer.WriteEndObject();
    }

    private static string HashRedacted(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "sha256:";
        byte[] hash;
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(raw.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(raw, bytes);
            hash = SHA256.HashData(bytes.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
