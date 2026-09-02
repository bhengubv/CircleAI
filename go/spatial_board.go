// spatial_board.go
//
// Ports CircleAI.Spatial (Contracts.cs / InMemorySpatial.cs / NullImplementations.cs):
//   LatLon / GeoTile / RadarReading / RadarReturn / SkyObject / Scene3D
//   IGeoTileSource / IRadarReadout / ISkyTracker / I3DSceneRenderer
//   InMemoryGeoTileSource / SyntheticRadarReadout / SyntheticSkyTracker / JsonScene3DRenderer
//   NullGeoTileSource / NullRadarReadout / NullSkyTracker / Null3DSceneRenderer
//
// The synthetic radar seeds a PRNG from the coordinates so a given (lat, lon,
// range) always yields the same reading (determinism preserved). The exact
// pseudo-random VALUES differ from .NET's Random (a different generator), but
// the contract — same input → same output, count in [3,7], returns inside the
// range — holds. The tile source returns a real 1x1 PNG so MIME/format
// detection works; the 3D renderer emits minimal valid GLTF 2.0 JSON.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"math/rand"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// LatLon is a WGS84 coordinate. Ports LatLon.
type LatLon struct {
	Latitude  float64
	Longitude float64
}

// GeoTile is a map tile with its image bytes. Ports GeoTile.
type GeoTile struct {
	Z          int
	X          int
	Y          int
	ImageBytes []byte
	MimeType   string
}

// RadarReturn is a single radar return. Ports RadarReturn.
type RadarReturn struct {
	Position     LatLon
	DopplerKmh   float64
	IntensityDbz float64
}

// RadarReading is a radar sweep at a centre point. Ports RadarReading.
type RadarReading struct {
	Centre  LatLon
	RangeKm float64
	Returns []RadarReturn
}

// SkyObject is a visible sky object. Ports SkyObject.
type SkyObject struct {
	Name              string
	AzimuthDeg        float64
	AltitudeDeg       float64
	MagnitudeApparent float64
}

// Scene3D is a rendered 3D scene. Ports Scene3D.
type Scene3D struct {
	SceneID string
	Encoded []byte
	Format  string
}

// IGeoTileSource is a map-tile source. Ports IGeoTileSource.
type IGeoTileSource interface {
	BackendID() string
	GetTile(ctx context.Context, z, x, y int) (GeoTile, error)
	SearchPlaces(ctx context.Context, query string, topK int) ([]LatLon, error)
}

// IRadarReadout is a weather/surveillance radar. Ports IRadarReadout.
type IRadarReadout interface {
	BackendID() string
	GetCurrentReading(ctx context.Context, at LatLon, rangeKm float64) (RadarReading, error)
}

// ISkyTracker is visible-sky tracking. Ports ISkyTracker.
type ISkyTracker interface {
	BackendID() string
	Visible(ctx context.Context, at LatLon, utc time.Time) ([]SkyObject, error)
}

// I3DSceneRenderer is a 3D-scene rendering hook. Ports I3DSceneRenderer.
type I3DSceneRenderer interface {
	BackendID() string
	Render(ctx context.Context, sceneScript, format string) (Scene3D, error)
}

// ---------------------------------------------------------------------------
// InMemoryGeoTileSource
// ---------------------------------------------------------------------------

// spatialTransparentPNG is a 1x1 transparent PNG (matches the C# byte array).
var spatialTransparentPNG = []byte{
	0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
	0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
	0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
	0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
	0x42, 0x60, 0x82,
}

// InMemoryGeoTileSource returns a stub PNG per tile + place search by registered
// name. Ports InMemoryGeoTileSource. Seeded with the same default places.
type InMemoryGeoTileSource struct {
	mu     sync.Mutex
	places map[string]LatLon // key is lowercased name; keeps original names too
	names  map[string]string // lowercased -> original
}

// NewInMemoryGeoTileSource constructs a tile source seeded with default places.
func NewInMemoryGeoTileSource() *InMemoryGeoTileSource {
	s := &InMemoryGeoTileSource{places: make(map[string]LatLon), names: make(map[string]string)}
	s.Register("Johannesburg", LatLon{-26.2041, 28.0473})
	s.Register("Cape Town", LatLon{-33.9249, 18.4241})
	s.Register("Pretoria", LatLon{-25.7479, 28.2293})
	s.Register("Durban", LatLon{-29.8587, 31.0218})
	s.Register("Lagos", LatLon{6.5244, 3.3792})
	s.Register("Nairobi", LatLon{-1.2921, 36.8219})
	s.Register("London", LatLon{51.5074, -0.1278})
	s.Register("New York", LatLon{40.7128, -74.0060})
	return s
}

// BackendID returns "in-memory".
func (s *InMemoryGeoTileSource) BackendID() string { return "in-memory" }

// Register stores a place by name (case-insensitive). Ports Register.
func (s *InMemoryGeoTileSource) Register(name string, at LatLon) {
	if strings.TrimSpace(name) == "" {
		panic("name required")
	}
	s.mu.Lock()
	key := strings.ToLower(name)
	s.places[key] = at
	s.names[key] = name
	s.mu.Unlock()
}

// GetTile returns a 1x1 transparent PNG for the coordinates. Ports GetTileAsync.
func (s *InMemoryGeoTileSource) GetTile(ctx context.Context, z, x, y int) (GeoTile, error) {
	if z < 0 || x < 0 || y < 0 {
		return GeoTile{}, errors.New("tile coordinates out of range")
	}
	return GeoTile{Z: z, X: x, Y: y, ImageBytes: spatialTransparentPNG, MimeType: "image/png"}, nil
}

// SearchPlaces returns up to topK places whose name contains query, ordered by
// name. Ports SearchPlacesAsync.
func (s *InMemoryGeoTileSource) SearchPlaces(ctx context.Context, query string, topK int) ([]LatLon, error) {
	if topK <= 0 {
		return nil, errors.New("topK out of range")
	}
	s.mu.Lock()
	type kv struct {
		name string
		at   LatLon
	}
	matches := make([]kv, 0)
	for key, at := range s.places {
		if strings.Contains(key, strings.ToLower(query)) {
			matches = append(matches, kv{name: s.names[key], at: at})
		}
	}
	s.mu.Unlock()
	sort.SliceStable(matches, func(i, j int) bool { return matches[i].name < matches[j].name })
	if topK < len(matches) {
		matches = matches[:topK]
	}
	out := make([]LatLon, len(matches))
	for i, m := range matches {
		out[i] = m.at
	}
	return out, nil
}

var _ IGeoTileSource = (*InMemoryGeoTileSource)(nil)

// ---------------------------------------------------------------------------
// SyntheticRadarReadout
// ---------------------------------------------------------------------------

// SyntheticRadarReadout produces a deterministic radar pattern from the
// coordinates. Ports SyntheticRadarReadout.
type SyntheticRadarReadout struct{}

// BackendID returns "synthetic".
func (SyntheticRadarReadout) BackendID() string { return "synthetic" }

// GetCurrentReading returns a coordinate-seeded deterministic reading. Ports
// GetCurrentReadingAsync.
func (SyntheticRadarReadout) GetCurrentReading(ctx context.Context, at LatLon, rangeKm float64) (RadarReading, error) {
	if rangeKm <= 0 {
		return RadarReading{}, errors.New("rangeKm out of range")
	}
	seed := int64(at.Latitude*1000) + int64(at.Longitude*1000) + int64(rangeKm*10)
	rng := rand.New(rand.NewSource(seed ^ (seed >> 32)))
	count := 3 + rng.Intn(5)
	rets := make([]RadarReturn, count)
	for i := 0; i < count; i++ {
		d := rng.Float64() * rangeKm * 0.9
		ang := rng.Float64() * math.Pi * 2
		lat := at.Latitude + (math.Cos(ang)*d)/111.0
		lon := at.Longitude + (math.Sin(ang)*d)/111.0
		rets[i] = RadarReturn{
			Position:     LatLon{lat, lon},
			DopplerKmh:   rng.Float64()*60 - 30,
			IntensityDbz: rng.Float64() * 60,
		}
	}
	return RadarReading{Centre: at, RangeKm: rangeKm, Returns: rets}, nil
}

var _ IRadarReadout = SyntheticRadarReadout{}

// ---------------------------------------------------------------------------
// SyntheticSkyTracker
// ---------------------------------------------------------------------------

type skyBaseObject struct {
	name     string
	azimuth  float64
	altitude float64
	mag      float64
}

var skyBaseObjects = []skyBaseObject{
	{"Sirius", 102.7, 35.0, -1.46},
	{"Polaris", 0.0, 51.5, 1.97},
	{"Vega", 88.0, 70.0, 0.03},
	{"Mars", 135.4, 22.0, 0.5},
	{"Jupiter", 180.5, 40.0, -2.0},
	{"Saturn", 210.0, 30.0, 0.4},
}

// SyntheticSkyTracker returns deterministic visible objects. Ports
// SyntheticSkyTracker.
type SyntheticSkyTracker struct{}

// BackendID returns "synthetic".
func (SyntheticSkyTracker) BackendID() string { return "synthetic" }

// Visible returns the objects above the daily-rotation horizon. Ports
// VisibleAsync.
func (SyntheticSkyTracker) Visible(ctx context.Context, at LatLon, utc time.Time) ([]SkyObject, error) {
	u := utc.UTC()
	hours := float64(u.Hour()) + float64(u.Minute())/60.0 + float64(u.Second())/3600.0 +
		float64(u.Nanosecond())/3.6e12
	rot := hours * 15.0
	hits := make([]SkyObject, 0, len(skyBaseObjects))
	for _, o := range skyBaseObjects {
		az2 := math.Mod(o.azimuth-rot+360, 360)
		if o.altitude-math.Abs(at.Latitude) > 0 {
			hits = append(hits, SkyObject{Name: o.name, AzimuthDeg: az2, AltitudeDeg: o.altitude, MagnitudeApparent: o.mag})
		}
	}
	return hits, nil
}

var _ ISkyTracker = SyntheticSkyTracker{}

// ---------------------------------------------------------------------------
// JsonScene3DRenderer
// ---------------------------------------------------------------------------

// JsonScene3DRenderer wraps a scene script into minimal valid GLTF 2.0 JSON.
// Ports JsonScene3DRenderer.
type JsonScene3DRenderer struct{}

// BackendID returns "json".
func (JsonScene3DRenderer) BackendID() string { return "json" }

// Render produces a GLTF JSON document embedding the script as an extras blob.
// Ports RenderAsync.
func (JsonScene3DRenderer) Render(ctx context.Context, sceneScript, format string) (Scene3D, error) {
	if strings.TrimSpace(format) == "" {
		format = "gltf"
	}
	sceneID := uuidNoDashes(uuid.New())
	scriptJSON, _ := json.Marshal(sceneScript)
	doc := `{"asset":{"version":"2.0","generator":"CircleAI.Spatial.JsonScene3DRenderer"},"scenes":[{"nodes":[]}],"scene":0,"extras":{"script":` + string(scriptJSON) + `}}`
	return Scene3D{SceneID: sceneID, Encoded: []byte(doc), Format: format}, nil
}

var _ I3DSceneRenderer = JsonScene3DRenderer{}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullGeoTileSource is a fail-safe tile source. Ports NullGeoTileSource.
type NullGeoTileSource struct{}

// NullGeoTileSourceInstance is the shared singleton.
var NullGeoTileSourceInstance = NullGeoTileSource{}

func (NullGeoTileSource) BackendID() string { return "null" }
func (NullGeoTileSource) GetTile(_ context.Context, z, x, y int) (GeoTile, error) {
	return GeoTile{Z: z, X: x, Y: y, ImageBytes: nil, MimeType: "image/png"}, nil
}
func (NullGeoTileSource) SearchPlaces(context.Context, string, int) ([]LatLon, error) {
	return []LatLon{}, nil
}

// NullRadarReadout is a fail-safe radar. Ports NullRadarReadout.
type NullRadarReadout struct{}

// NullRadarReadoutInstance is the shared singleton.
var NullRadarReadoutInstance = NullRadarReadout{}

func (NullRadarReadout) BackendID() string { return "null" }
func (NullRadarReadout) GetCurrentReading(_ context.Context, at LatLon, rangeKm float64) (RadarReading, error) {
	return RadarReading{Centre: at, RangeKm: rangeKm, Returns: []RadarReturn{}}, nil
}

// NullSkyTracker is a fail-safe sky tracker. Ports NullSkyTracker.
type NullSkyTracker struct{}

// NullSkyTrackerInstance is the shared singleton.
var NullSkyTrackerInstance = NullSkyTracker{}

func (NullSkyTracker) BackendID() string { return "null" }
func (NullSkyTracker) Visible(context.Context, LatLon, time.Time) ([]SkyObject, error) {
	return []SkyObject{}, nil
}

// Null3DSceneRenderer is a fail-safe renderer. Ports Null3DSceneRenderer.
type Null3DSceneRenderer struct{}

// Null3DSceneRendererInstance is the shared singleton.
var Null3DSceneRendererInstance = Null3DSceneRenderer{}

func (Null3DSceneRenderer) BackendID() string { return "null" }
func (Null3DSceneRenderer) Render(_ context.Context, _ string, format string) (Scene3D, error) {
	if strings.TrimSpace(format) == "" {
		format = "gltf"
	}
	return Scene3D{SceneID: uuid.Nil.String(), Encoded: nil, Format: format}, nil
}

var (
	_ IGeoTileSource   = NullGeoTileSource{}
	_ IRadarReadout    = NullRadarReadout{}
	_ ISkyTracker      = NullSkyTracker{}
	_ I3DSceneRenderer = Null3DSceneRenderer{}
)
