// OpenMeteoWeatherProvider.cs
//
// (Phase B4) Open-Meteo free, no-API-key weather provider. Returns
// current conditions + hourly forecast in plain JSON.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Geo;

public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    private readonly HttpClient _http;

    public OpenMeteoWeatherProvider() : this(new HttpClient()) { }
    public OpenMeteoWeatherProvider(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public string ProviderId => "open-meteo";

    public async ValueTask<WeatherSample> CurrentAsync(double lat, double lon, CancellationToken ct = default)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}"
                + "&current=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        var cur = doc.RootElement.GetProperty("current");
        var ts  = cur.GetProperty("time").GetString();
        return new WeatherSample(
            AtUtc:      DateTimeOffset.Parse(ts ?? DateTime.UtcNow.ToString("O"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            TempC:      cur.GetProperty("temperature_2m").GetDouble(),
            FeelsLikeC: cur.GetProperty("apparent_temperature").GetDouble(),
            PrecipMm:   cur.GetProperty("precipitation").GetDouble(),
            WindKph:    cur.GetProperty("wind_speed_10m").GetDouble() * 3.6, // m/s → km/h
            CloudPct:   cur.GetProperty("cloud_cover").GetInt32(),
            Condition:  WmoDecode(cur.GetProperty("weather_code").GetInt32()));
    }

    public async ValueTask<IReadOnlyList<WeatherSample>> HourlyAsync(double lat, double lon, int hours, CancellationToken ct = default)
    {
        if (hours <= 0 || hours > 168) throw new ArgumentOutOfRangeException(nameof(hours));
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}"
                + "&hourly=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code"
                + $"&forecast_hours={hours}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);
        var h    = doc.RootElement.GetProperty("hourly");
        var time = h.GetProperty("time");
        var temp = h.GetProperty("temperature_2m");
        var feel = h.GetProperty("apparent_temperature");
        var prec = h.GetProperty("precipitation");
        var wind = h.GetProperty("wind_speed_10m");
        var cld  = h.GetProperty("cloud_cover");
        var code = h.GetProperty("weather_code");
        var n    = Math.Min(time.GetArrayLength(), hours);
        var result = new List<WeatherSample>(n);
        for (var i = 0; i < n; i++)
        {
            result.Add(new WeatherSample(
                AtUtc:      DateTimeOffset.Parse(time[i].GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
                TempC:      temp[i].GetDouble(),
                FeelsLikeC: feel[i].GetDouble(),
                PrecipMm:   prec[i].GetDouble(),
                WindKph:    wind[i].GetDouble() * 3.6,
                CloudPct:   cld[i].GetInt32(),
                Condition:  WmoDecode(code[i].GetInt32())));
        }
        return result;
    }

    /// <summary>(Phase B4) Decode WMO weather code (Open-Meteo standard).</summary>
    private static string WmoDecode(int code) => code switch
    {
        0          => "clear sky",
        1 or 2 or 3 => "partly cloudy",
        45 or 48   => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57   => "freezing drizzle",
        61 or 63 or 65 => "rain",
        66 or 67   => "freezing rain",
        71 or 73 or 75 => "snow",
        77         => "snow grains",
        80 or 81 or 82 => "rain showers",
        85 or 86   => "snow showers",
        95         => "thunderstorm",
        96 or 99   => "thunderstorm with hail",
        _          => "unknown",
    };
}
