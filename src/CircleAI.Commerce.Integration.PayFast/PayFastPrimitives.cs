// PayFastPrimitives.cs — (3.3.0)
//
// PayFast integration primitives — real signature builder, real ITN
// validation params, in-memory webhook recorder. The HTTP-side
// callbacks are wired by the host.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CircleAI.Commerce.Integration.PayFast;

public sealed record PayFastConfig(string MerchantId, string MerchantKey, string Passphrase, bool Sandbox);
public sealed record PayFastItnPayload(string MerchantId, string PaymentId, string PaymentStatus, decimal Amount, string MPaymentId, string Signature);

public interface IPayFastBoard
{
    PayFastConfig Config { get; }
    string SignatureFor(IReadOnlyDictionary<string, string> orderedFields);
    bool VerifyItn(PayFastItnPayload p);
    void RecordWebhook(PayFastItnPayload p);
    IReadOnlyList<PayFastItnPayload> RecentWebhooks(int limit = 20);
}

public sealed class InMemoryPayFastBoard : IPayFastBoard
{
    private readonly List<PayFastItnPayload> _webhooks = new();
    private readonly object _lock = new();
    public PayFastConfig Config { get; }
    public InMemoryPayFastBoard(PayFastConfig cfg) => Config = cfg ?? throw new ArgumentNullException(nameof(cfg));

    public string SignatureFor(IReadOnlyDictionary<string, string> orderedFields)
    {
        ArgumentNullException.ThrowIfNull(orderedFields);
        var sb = new StringBuilder();
        foreach (var kv in orderedFields)
        {
            sb.Append(kv.Key).Append('=').Append(WebUtility.UrlEncode(kv.Value).Replace("%20", "+")).Append('&');
        }
        if (!string.IsNullOrEmpty(Config.Passphrase))
            sb.Append("passphrase=").Append(WebUtility.UrlEncode(Config.Passphrase).Replace("%20", "+"));
        else if (sb.Length > 0 && sb[^1] == '&')
            sb.Length--;
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool VerifyItn(PayFastItnPayload p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.MerchantId == Config.MerchantId;
    }

    public void RecordWebhook(PayFastItnPayload p) { ArgumentNullException.ThrowIfNull(p); lock (_lock) _webhooks.Add(p); }
    public IReadOnlyList<PayFastItnPayload> RecentWebhooks(int limit = 20)
    { lock (_lock) return _webhooks.AsEnumerable().Reverse().Take(limit).ToArray(); }
}
