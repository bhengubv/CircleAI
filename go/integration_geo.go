// integration_geo.go
//
// Ports CircleAI.Integration.Geo:
//   OpenMeteoWeatherProvider -> OpenMeteoWeatherProvider (IWeatherProvider)
//   OsrmOptions / OsrmRoutingProvider -> OsrmOptions / OsrmRoutingProvider (IRoutingProvider)
//
// Both providers speak a real public HTTP API; the live HttpClient is replaced by
// the injected CarrierHTTP seam per the porting rules, so they are deterministic
// and make no network calls. Wire details (URL construction with invariant-culture
// coordinates, query params, JSON field extraction, the m/s→km/h and metre→km
// conversions, WMO decode, and the mode→profile mapping) are reproduced faithfully.

package circleai

import (
	"context"
	"errors"
	"strconv"
	"strings"
	"time"
)

// ── Open-Meteo weather ──────────────────────────────────────────────────────

// OpenMeteoWeatherProvider is the Open-Meteo (no-API-key) weather provider over
// the injected CarrierHTTP. Ports OpenMeteoWeatherProvider.
type OpenMeteoWeatherProvider struct {
	http CarrierHTTP
}

// NewOpenMeteoWeatherProvider constructs the provider. http is required (the C#
// ctor throws on null http).
func NewOpenMeteoWeatherProvider(http CarrierHTTP) (*OpenMeteoWeatherProvider, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	return &OpenMeteoWeatherProvider{http: http}, nil
}

// ProviderID is "open-meteo".
func (p *OpenMeteoWeatherProvider) ProviderID() string { return "open-meteo" }

// Current ports CurrentAsync: fetch the current-conditions block and map it.
func (p *OpenMeteoWeatherProvider) Current(_ context.Context, lat, lon float64) (WeatherSample, error) {
	url := "https://api.open-meteo.com/v1/forecast?latitude=" + fmtFloat(lat) + "&longitude=" + fmtFloat(lon) +
		"&current=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code"
	resp, err := p.http.Do(&CarrierHTTPRequest{Method: "GET", URL: url})
	if err != nil {
		return WeatherSample{}, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return WeatherSample{}, statusError("Open-Meteo current", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return WeatherSample{}, err
	}
	cur, ok := tjObject(root, "current")
	if !ok {
		return WeatherSample{}, errors.New("Open-Meteo response missing 'current'")
	}
	ts, _ := tjString(cur, "time")
	temp, _ := tjFloat(cur, "temperature_2m")
	feel, _ := tjFloat(cur, "apparent_temperature")
	precip, _ := tjFloat(cur, "precipitation")
	wind, _ := tjFloat(cur, "wind_speed_10m")
	cloud, _ := tjInt(cur, "cloud_cover")
	code, _ := tjInt(cur, "weather_code")
	return WeatherSample{
		AtUtc:      openMeteoTime(ts),
		TempC:      temp,
		FeelsLikeC: feel,
		PrecipMm:   precip,
		WindKph:    wind * 3.6,
		CloudPct:   cloud,
		Condition:  wmoDecode(code),
	}, nil
}

// Hourly ports HourlyAsync: fetch the hourly block and map min(len,hours) samples.
func (p *OpenMeteoWeatherProvider) Hourly(_ context.Context, lat, lon float64, hours int) ([]WeatherSample, error) {
	if hours <= 0 || hours > 168 {
		return nil, errors.New("hours out of range")
	}
	url := "https://api.open-meteo.com/v1/forecast?latitude=" + fmtFloat(lat) + "&longitude=" + fmtFloat(lon) +
		"&hourly=temperature_2m,apparent_temperature,precipitation,wind_speed_10m,cloud_cover,weather_code" +
		"&forecast_hours=" + itoaSmall(hours)
	resp, err := p.http.Do(&CarrierHTTPRequest{Method: "GET", URL: url})
	if err != nil {
		return nil, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return nil, statusError("Open-Meteo hourly", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return nil, err
	}
	h, ok := tjObject(root, "hourly")
	if !ok {
		return nil, errors.New("Open-Meteo response missing 'hourly'")
	}
	timeArr, _ := tjArray(h, "time")
	temp, _ := tjArray(h, "temperature_2m")
	feel, _ := tjArray(h, "apparent_temperature")
	prec, _ := tjArray(h, "precipitation")
	wind, _ := tjArray(h, "wind_speed_10m")
	cld, _ := tjArray(h, "cloud_cover")
	code, _ := tjArray(h, "weather_code")

	n := minInt(len(timeArr), hours)
	result := make([]WeatherSample, 0, n)
	for i := 0; i < n; i++ {
		result = append(result, WeatherSample{
			AtUtc:      openMeteoTime(elemString(timeArr, i)),
			TempC:      elemFloat(temp, i),
			FeelsLikeC: elemFloat(feel, i),
			PrecipMm:   elemFloat(prec, i),
			WindKph:    elemFloat(wind, i) * 3.6,
			CloudPct:   elemInt(cld, i),
			Condition:  wmoDecode(elemInt(code, i)),
		})
	}
	return result, nil
}

// openMeteoTime parses an Open-Meteo timestamp (assume-UTC), falling back to
// nowUTC when absent — mirroring the C# `ts ?? DateTime.UtcNow.ToString("O")`
// then Parse(AssumeUniversal).ToUniversalTime().
func openMeteoTime(ts string) time.Time {
	if strings.TrimSpace(ts) == "" {
		return nowUTCFunc()
	}
	if t := parseDateTimeOffsetUTC(ts); !t.IsZero() {
		return t
	}
	return nowUTCFunc()
}

// wmoDecode decodes a WMO weather code to a condition string. Ports WmoDecode.
func wmoDecode(code int) string {
	switch code {
	case 0:
		return "clear sky"
	case 1, 2, 3:
		return "partly cloudy"
	case 45, 48:
		return "fog"
	case 51, 53, 55:
		return "drizzle"
	case 56, 57:
		return "freezing drizzle"
	case 61, 63, 65:
		return "rain"
	case 66, 67:
		return "freezing rain"
	case 71, 73, 75:
		return "snow"
	case 77:
		return "snow grains"
	case 80, 81, 82:
		return "rain showers"
	case 85, 86:
		return "snow showers"
	case 95:
		return "thunderstorm"
	case 96, 99:
		return "thunderstorm with hail"
	default:
		return "unknown"
	}
}

// ── OSRM routing ────────────────────────────────────────────────────────────

// osrmDefaultHost is the C# default OSRM host.
const osrmDefaultHost = "https://router.project-osrm.org"

// OsrmOptions configures the OSRM routing provider. Ports OsrmOptions. An empty
// Host defaults to the public demo server.
type OsrmOptions struct {
	Host string
}

// OsrmRoutingProvider is an OSRM HTTP client over the injected CarrierHTTP. Ports
// OsrmRoutingProvider.
type OsrmRoutingProvider struct {
	http CarrierHTTP
	opts OsrmOptions
}

// NewOsrmRoutingProvider constructs the provider. http is required; an empty Host
// defaults to osrmDefaultHost.
func NewOsrmRoutingProvider(http CarrierHTTP, opts OsrmOptions) (*OsrmRoutingProvider, error) {
	if http == nil {
		return nil, errors.New("http is required")
	}
	if opts.Host == "" {
		opts.Host = osrmDefaultHost
	}
	return &OsrmRoutingProvider{http: http, opts: opts}, nil
}

// ProviderID is "osrm".
func (p *OsrmRoutingProvider) ProviderID() string { return "osrm" }

// Route ports RouteAsync: map mode→profile, build the coordinate URL, require
// code=="Ok", and map routes[0] distance/duration/geometry.
func (p *OsrmRoutingProvider) Route(_ context.Context, fromLat, fromLon, toLat, toLon float64, mode string) (RouteEstimate, error) {
	profile := osrmProfile(mode)
	url := strings.TrimRight(p.opts.Host, "/") + "/route/v1/" + profile + "/" +
		fmtFloat(fromLon) + "," + fmtFloat(fromLat) + ";" +
		fmtFloat(toLon) + "," + fmtFloat(toLat) +
		"?overview=full&geometries=geojson"
	resp, err := p.http.Do(&CarrierHTTPRequest{Method: "GET", URL: url})
	if err != nil {
		return RouteEstimate{}, err
	}
	if !carrierHTTPStatusOK(resp.StatusCode) {
		return RouteEstimate{}, statusError("OSRM route", resp.StatusCode)
	}
	root, err := parseJSONObject(resp.Body)
	if err != nil {
		return RouteEstimate{}, err
	}
	code, _ := tjString(root, "code")
	if code != "Ok" {
		return RouteEstimate{}, errors.New("OSRM returned code=" + code)
	}
	routes, ok := tjArray(root, "routes")
	if !ok || len(routes) == 0 {
		return RouteEstimate{}, errors.New("OSRM returned no routes")
	}
	route, ok := asJSONObject(routes[0])
	if !ok {
		return RouteEstimate{}, errors.New("OSRM route[0] not an object")
	}
	dist, _ := tjFloat(route, "distance") // metres
	dur, _ := tjFloat(route, "duration")  // seconds
	poly := []GeoPoint{}
	if geom, ok := tjObject(route, "geometry"); ok {
		if coords, ok := tjArray(geom, "coordinates"); ok {
			for _, pt := range coords {
				arr, ok := pt.([]interface{})
				if !ok || len(arr) < 2 {
					continue
				}
				poly = append(poly, GeoPoint{Lat: toFloat(arr[1]), Lon: toFloat(arr[0])})
			}
		}
	}
	return RouteEstimate{
		DistanceKm: dist / 1000.0,
		Duration:   time.Duration(dur * float64(time.Second)),
		Polyline:   poly,
	}, nil
}

// osrmProfile maps a travel mode to an OSRM profile. Ports the switch.
func osrmProfile(mode string) string {
	switch mode {
	case "bike", "bicycle":
		return "bike"
	case "foot", "walk":
		return "foot"
	default:
		return "driving"
	}
}

// ── shared numeric/JSON helpers for geo ─────────────────────────────────────

// fmtFloat renders a float64 with the shortest round-trip representation using a
// '.' decimal point (InvariantCulture), matching double.ToString(InvariantCulture)
// for the finite values these providers emit.
func fmtFloat(v float64) string { return strconv.FormatFloat(v, 'g', -1, 64) }

// toFloat converts a decoded JSON value to float64 (0 on failure).
func toFloat(v interface{}) float64 {
	f, _ := tjFloatRaw(v)
	return f
}

// elemFloat/elemInt/elemString index a decoded JSON array with a scalar getter,
// returning the zero value when out of range or the wrong kind. These mirror the
// C# array-indexer + Get* calls over the parallel Open-Meteo arrays.
func elemFloat(arr []interface{}, i int) float64 {
	if i < 0 || i >= len(arr) {
		return 0
	}
	f, _ := tjFloatRaw(arr[i])
	return f
}

func elemInt(arr []interface{}, i int) int {
	if i < 0 || i >= len(arr) {
		return 0
	}
	n, _ := tjIntRaw(arr[i])
	return n
}

func elemString(arr []interface{}, i int) string {
	if i < 0 || i >= len(arr) {
		return ""
	}
	if s, ok := arr[i].(string); ok {
		return s
	}
	return ""
}

var (
	_ IWeatherProvider = (*OpenMeteoWeatherProvider)(nil)
	_ IRoutingProvider = (*OsrmRoutingProvider)(nil)
)
