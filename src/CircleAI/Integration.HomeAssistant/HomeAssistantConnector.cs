// HomeAssistantConnector.cs
//
// (Phase C1) HomeAssistant REST API client. Connects with a long-lived
// access token (Profile → Security → Long-Lived Access Tokens in HA).
// Lists entities, calls services, and reads state.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.HomeAssistant;

/// <param name="BaseUrl">HA base URL, e.g. http://homeassistant.local:8123/ — must include trailing slash.</param>
/// <param name="AccessToken">Long-lived access token.</param>
public sealed record HomeAssistantOptions(Uri BaseUrl, string AccessToken);

public sealed class HomeAssistantConnector : IHomeAutomationConnector
{
    private readonly HttpClient _http;
    private readonly HomeAssistantOptions _opts;

    public HomeAssistantConnector(HomeAssistantOptions opts)
        : this(opts, new HttpClient()) { }

    public HomeAssistantConnector(HomeAssistantOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.BaseAddress is null) _http.BaseAddress = opts.BaseUrl;
        if (!string.IsNullOrWhiteSpace(opts.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.AccessToken);
    }

    public string ProviderId   => "home-assistant";
    public bool   IsConfigured =>
        _opts.BaseUrl is not null && !string.IsNullOrWhiteSpace(_opts.AccessToken);

    public async ValueTask<IReadOnlyList<HaEntity>> ListEntitiesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/states", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<HaEntity>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var st in doc.RootElement.EnumerateArray())
        {
            var entityId = st.TryGetProperty("entity_id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(entityId)) continue;
            var state    = st.TryGetProperty("state", out var sEl) ? sEl.GetString() ?? "" : "";
            var domain   = entityId.Split('.', 2)[0];
            var attrs    = new Dictionary<string, string>(StringComparer.Ordinal);
            var friendly = entityId;
            if (st.TryGetProperty("attributes", out var attEl) && attEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in attEl.EnumerateObject())
                {
                    attrs[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.Number => prop.Value.ToString(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        _                    => prop.Value.ToString(),
                    };
                    if (string.Equals(prop.Name, "friendly_name", StringComparison.Ordinal)
                        && prop.Value.ValueKind == JsonValueKind.String)
                        friendly = prop.Value.GetString() ?? entityId;
                }
            }
            list.Add(new HaEntity(entityId, friendly, domain, state, attrs));
        }
        return list;
    }

    public async ValueTask CallServiceAsync(
        string domain, string service,
        IReadOnlyDictionary<string, object?>? data,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain))  throw new ArgumentException("domain required");
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("service required");

        var payload = data ?? new Dictionary<string, object?>();
        using var resp = await _http.PostAsJsonAsync(
            $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}",
            payload, cancellationToken: ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>(Phase C1) Convenience: turn an entity on / off via the homeassistant.turn_on / turn_off services.</summary>
    public ValueTask TurnOnAsync(string entityId, CancellationToken ct = default)
        => CallServiceAsync("homeassistant", "turn_on",
            new Dictionary<string, object?> { ["entity_id"] = entityId }, ct);

    public ValueTask TurnOffAsync(string entityId, CancellationToken ct = default)
        => CallServiceAsync("homeassistant", "turn_off",
            new Dictionary<string, object?> { ["entity_id"] = entityId }, ct);
}
