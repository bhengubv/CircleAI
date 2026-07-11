// integration_geo_test.go
//
// Verifies the Geo providers (integration_geo.go) over the injected
// FakeCarrierTransport — no real network. Covers Open-Meteo current + hourly
// (m/s→km/h, WMO decode, min(len,hours) cap, range validation) and OSRM routing
// (mode→profile, coordinate URL order lon,lat, code!=Ok error, metre→km +
// second→Duration, geojson polyline lat/lon swap).

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestOpenMeteo_Current(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"current":{"time":"2026-07-11T12:00","temperature_2m":21.4,
		"apparent_temperature":20.1,"precipitation":0.2,"wind_speed_10m":5,"cloud_cover":40,"weather_code":3}}`)
	p, err := circleai.NewOpenMeteoWeatherProvider(tr)
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if p.ProviderID() != "open-meteo" {
		t.Fatalf("id wrong")
	}
	w, err := p.Current(context.Background(), -33.9249, 18.4241)
	if err != nil {
		t.Fatalf("current: %v", err)
	}
	if w.TempC != 21.4 || w.FeelsLikeC != 20.1 || w.PrecipMm != 0.2 || w.CloudPct != 40 || w.Condition != "partly cloudy" {
		t.Fatalf("current sample wrong: %+v", w)
	}
	if w.WindKph != 5*3.6 { // 18
		t.Fatalf("wind conversion wrong: %v", w.WindKph)
	}
	if !w.AtUtc.Equal(time.Date(2026, 7, 11, 12, 0, 0, 0, time.UTC)) {
		t.Fatalf("time wrong: %v", w.AtUtc)
	}
	req := tr.Requests()[0]
	if !strings.Contains(req.URL, "latitude=-33.9249") || !strings.Contains(req.URL, "longitude=18.4241") ||
		!strings.Contains(req.URL, "current=temperature_2m") {
		t.Fatalf("current url wrong: %s", req.URL)
	}
}

func TestOpenMeteo_Hourly(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	// 3 time points but request 2 hours -> capped to 2.
	tr.EnqueueJSON(200, `{"hourly":{
		"time":["2026-07-11T00:00","2026-07-11T01:00","2026-07-11T02:00"],
		"temperature_2m":[10,11,12],"apparent_temperature":[9,10,11],
		"precipitation":[0,0.5,1],"wind_speed_10m":[1,2,3],"cloud_cover":[0,50,100],
		"weather_code":[0,61,95]}}`)
	p, _ := circleai.NewOpenMeteoWeatherProvider(tr)
	samples, err := p.Hourly(context.Background(), 1, 2, 2)
	if err != nil {
		t.Fatalf("hourly: %v", err)
	}
	if len(samples) != 2 {
		t.Fatalf("expected 2 samples, got %d", len(samples))
	}
	if samples[0].TempC != 10 || samples[0].Condition != "clear sky" || samples[0].WindKph != 1*3.6 {
		t.Fatalf("sample0 wrong: %+v", samples[0])
	}
	if samples[1].TempC != 11 || samples[1].Condition != "rain" || samples[1].CloudPct != 50 {
		t.Fatalf("sample1 wrong: %+v", samples[1])
	}
	if !strings.Contains(tr.Requests()[0].URL, "forecast_hours=2") {
		t.Fatalf("hourly url missing forecast_hours: %s", tr.Requests()[0].URL)
	}
	// Range validation.
	if _, err := p.Hourly(context.Background(), 1, 2, 0); err == nil {
		t.Fatalf("hours=0 should error")
	}
	if _, err := p.Hourly(context.Background(), 1, 2, 169); err == nil {
		t.Fatalf("hours>168 should error")
	}
}

func TestOsrm_RouteDriving(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"code":"Ok","routes":[{"distance":12000,"duration":900,
		"geometry":{"coordinates":[[18.42,-33.92],[18.50,-33.95]]}}]}`)
	p, err := circleai.NewOsrmRoutingProvider(tr, circleai.OsrmOptions{})
	if err != nil {
		t.Fatalf("new: %v", err)
	}
	if p.ProviderID() != "osrm" {
		t.Fatalf("id wrong")
	}
	r, err := p.Route(context.Background(), -33.92, 18.42, -33.95, 18.50, "car")
	if err != nil {
		t.Fatalf("route: %v", err)
	}
	if r.DistanceKm != 12.0 || r.Duration != 15*time.Minute {
		t.Fatalf("route metrics wrong: %+v", r)
	}
	if len(r.Polyline) != 2 || r.Polyline[0].Lat != -33.92 || r.Polyline[0].Lon != 18.42 {
		t.Fatalf("polyline lat/lon swap wrong: %+v", r.Polyline)
	}
	req := tr.Requests()[0]
	// URL is /route/v1/driving/{fromLon},{fromLat};{toLon},{toLat}
	if !strings.Contains(req.URL, "/route/v1/driving/18.42,-33.92;18.5,-33.95") ||
		!strings.Contains(req.URL, "geometries=geojson") {
		t.Fatalf("route url wrong: %s", req.URL)
	}
}

func TestOsrm_ModeProfilesAndCustomHost(t *testing.T) {
	cases := map[string]string{"bike": "bike", "bicycle": "bike", "foot": "foot", "walk": "foot", "car": "driving", "": "driving"}
	for mode, profile := range cases {
		tr := circleai.NewFakeCarrierTransport()
		tr.EnqueueJSON(200, `{"code":"Ok","routes":[{"distance":1000,"duration":60,"geometry":{"coordinates":[]}}]}`)
		p, _ := circleai.NewOsrmRoutingProvider(tr, circleai.OsrmOptions{Host: "https://osrm.internal/"})
		if _, err := p.Route(context.Background(), 1, 2, 3, 4, mode); err != nil {
			t.Fatalf("route mode %q: %v", mode, err)
		}
		req := tr.Requests()[0]
		if !strings.Contains(req.URL, "https://osrm.internal/route/v1/"+profile+"/") {
			t.Fatalf("mode %q -> profile url wrong: %s", mode, req.URL)
		}
	}
}

func TestOsrm_NonOkError(t *testing.T) {
	tr := circleai.NewFakeCarrierTransport()
	tr.EnqueueJSON(200, `{"code":"NoRoute","routes":[]}`)
	p, _ := circleai.NewOsrmRoutingProvider(tr, circleai.OsrmOptions{})
	if _, err := p.Route(context.Background(), 1, 2, 3, 4, "car"); err == nil || !strings.Contains(err.Error(), "NoRoute") {
		t.Fatalf("expected NoRoute error, got %v", err)
	}
}
