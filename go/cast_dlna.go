// cast_dlna.go
//
// Putting something the assistant made onto the television in the room.
//
// DE-GOOGLED BY DESIGN: no Google Cast, no Chromecast SDK. The only backend is
// open UPnP/DLNA, which every television in this market already speaks and
// which needs nobody's account.
//
// THE RENDERER PULLS. Nothing is ever pushed to it — a caller hands the
// television a URL and the television fetches it. That single fact is why
// casting a local file needs an HTTP server running on THIS device, and it is
// the thing most people implementing DLNA get wrong first.
//
// OFFLINE AND LAN-ONLY. Nothing in this file reaches the internet.

package circleai

import (
	"context"
	"encoding/xml"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Primitives

// CastProtocol is the wire protocol a target speaks.
//
// One member on purpose: it is the place a second one would go, and its absence
// is the de-Googling decision made visible rather than assumed.
type CastProtocol int

// CastProtocolDlna is the only protocol.
const CastProtocolDlna CastProtocol = 0

func (CastProtocol) String() string { return "dlna" }

// CastTargetId identifies one renderer.
type CastTargetId struct{ Value string }

// CastContentKind is what is being cast.
type CastContentKind int

const (
	CastContentImage CastContentKind = iota
	CastContentAudio
	CastContentVideo
	CastContentSlideshow
)

func (k CastContentKind) String() string {
	switch k {
	case CastContentAudio:
		return "audio"
	case CastContentVideo:
		return "video"
	case CastContentSlideshow:
		return "slideshow"
	}
	return "image"
}

// CastPlaybackState is what the renderer is doing.
type CastPlaybackState int

const (
	CastStateUnknown CastPlaybackState = iota
	CastStateIdle
	CastStateBuffering
	CastStatePlaying
	CastStatePaused
	CastStateStopped
	CastStateError
)

func (s CastPlaybackState) String() string {
	switch s {
	case CastStateIdle:
		return "idle"
	case CastStateBuffering:
		return "buffering"
	case CastStatePlaying:
		return "playing"
	case CastStatePaused:
		return "paused"
	case CastStateStopped:
		return "stopped"
	case CastStateError:
		return "error"
	}
	return "unknown"
}

// CastMediaSource is where the media is.
//
// A closed set of three, handled at exactly one place — the point where a URL
// has to be produced for the television to fetch. A file and a byte buffer both
// become a URL served by this device; only Url is already one.
type CastMediaSource interface{ isCastMediaSource() }

// Url is media the renderer can already reach.
type Url struct{ Value string }

func (Url) isCastMediaSource() {}

// File is media on this device's disk.
type File struct{ Path string }

func (File) isCastMediaSource() {}

// Bytes is media held in memory.
type Bytes struct{ Data []byte }

func (Bytes) isCastMediaSource() {}

// CastMedia is one thing to cast.
type CastMedia struct {
	Source   CastMediaSource
	MimeType string
	Kind     CastContentKind
	Title    string
	// Negative when unknown. Zero is a real answer for a still image.
	DurationSeconds float64
}

// CastStatus is the renderer's current position.
type CastStatus struct {
	State           CastPlaybackState
	PositionSeconds float64
	DurationSeconds float64
	CurrentUri      string
}

// CastException is a cast that failed.
type CastException struct{ Reason string }

func (e CastException) Error() string { return e.Reason }

// CastControlException means the television ANSWERED AND REFUSED.
//
// Separated from CastException because it is a different problem from never
// reaching it: usually an unquoted SOAPACTION or a URI the renderer will not
// accept. Retrying helps one and not the other.
type CastControlException struct {
	Reason string
	Action string
}

func (e CastControlException) Error() string {
	return fmt.Sprintf("the renderer refused %s: %s", e.Action, e.Reason)
}

// ErrNoMediaHost is returned when a file or byte source is cast with no local
// media host configured. A real error rather than silence: the renderer pulls,
// so without a host there is nothing for it to pull from.
var ErrNoMediaHost = errors.New("no local media host: the renderer pulls, so a file must be served from this device")

// ─────────────────────────────────────────────────────────────────────────────
// SSDP discovery

// SsdpResponse is one M-SEARCH reply.
type SsdpResponse struct {
	Location     string
	Usn          string
	Server       string
	SearchTarget string
}

// ParseSsdpResponse parses one reply.
//
// Header names are matched case-INSENSITIVELY: televisions disagree about
// capitalisation and a case-sensitive parser finds some of them and not others,
// which reads as a flaky network.
func ParseSsdpResponse(text string) (SsdpResponse, bool) {
	var r SsdpResponse
	lines := strings.Split(text, "\n")
	if len(lines) == 0 || !strings.HasPrefix(strings.ToUpper(strings.TrimSpace(lines[0])), "HTTP/1.1 200") {
		return r, false
	}
	for _, line := range lines[1:] {
		idx := strings.Index(line, ":")
		if idx <= 0 {
			continue
		}
		name := strings.ToUpper(strings.TrimSpace(line[:idx]))
		value := strings.TrimSpace(line[idx+1:])
		switch name {
		case "LOCATION":
			r.Location = value
		case "USN":
			r.Usn = value
		case "SERVER":
			r.Server = value
		case "ST":
			r.SearchTarget = value
		}
	}
	return r, r.Location != ""
}

// BuildSsdpSearch builds the M-SEARCH datagram.
//
// The blank line at the end is required by the protocol and is the usual reason
// a hand-written M-SEARCH gets no replies at all.
func BuildSsdpSearch(searchTarget string, mxSeconds int) string {
	if mxSeconds <= 0 {
		mxSeconds = 2
	}
	return strings.Join([]string{
		"M-SEARCH * HTTP/1.1",
		"HOST: 239.255.255.250:1900",
		`MAN: "ssdp:discover"`,
		"MX: " + strconv.Itoa(mxSeconds),
		"ST: " + searchTarget,
		"", "",
	}, "\r\n")
}

// SsdpClient sends M-SEARCH and collects replies.
type SsdpClient struct {
	timeout time.Duration
}

// NewSsdpClient returns a client.
func NewSsdpClient(timeout time.Duration) *SsdpClient {
	if timeout <= 0 {
		timeout = 3 * time.Second
	}
	return &SsdpClient{timeout: timeout}
}

// Search collects replies for the client's timeout.
func (c *SsdpClient) Search(ctx context.Context, searchTarget string) ([]SsdpResponse, error) {
	addr, err := net.ResolveUDPAddr("udp4", "239.255.255.250:1900")
	if err != nil {
		return nil, err
	}
	conn, err := net.ListenPacket("udp4", ":0")
	if err != nil {
		return nil, err
	}
	defer func() { _ = conn.Close() }()

	if _, err := conn.WriteTo([]byte(BuildSsdpSearch(searchTarget, 2)), addr); err != nil {
		return nil, err
	}
	deadline := time.Now().Add(c.timeout)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}
	_ = conn.SetReadDeadline(deadline)

	seen := map[string]bool{}
	var out []SsdpResponse
	buf := make([]byte, 4096)
	for {
		n, _, err := conn.ReadFrom(buf)
		if err != nil {
			break
		}
		if r, ok := ParseSsdpResponse(string(buf[:n])); ok && !seen[r.Usn] {
			// De-duplicate by USN: a renderer answers the same search several
			// times, and a caller shown one television three times concludes
			// the list is broken.
			seen[r.Usn] = true
			out = append(out, r)
		}
	}
	return out, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Device description

// RendererDescription is one service on a device.
type RendererDescription struct {
	FriendlyName string
	Manufacturer string
	ModelName    string
	Udn          string
	// ABSOLUTE. The description document gives a RELATIVE control URL and the
	// base is the document's own address; resolving it late is how a caller
	// ends up POSTing to its own host.
	ControlUrl  string
	ServiceType string
}

// DeviceDescription is a whole device.
type DeviceDescription struct {
	FriendlyName string
	Manufacturer string
	ModelName    string
	Udn          string
	Services     []RendererDescription
}

type upnpRoot struct {
	Device struct {
		FriendlyName string `xml:"friendlyName"`
		Manufacturer string `xml:"manufacturer"`
		ModelName    string `xml:"modelName"`
		Udn          string `xml:"UDN"`
		ServiceList  struct {
			Services []struct {
				ServiceType string `xml:"serviceType"`
				ControlURL  string `xml:"controlURL"`
			} `xml:"service"`
		} `xml:"serviceList"`
	} `xml:"device"`
}

// ParseDeviceDescription parses the XML at a location.
func ParseDeviceDescription(xmlText, baseUrl string) (DeviceDescription, error) {
	var root upnpRoot
	if err := xml.Unmarshal([]byte(xmlText), &root); err != nil {
		return DeviceDescription{}, err
	}
	base, err := url.Parse(baseUrl)
	if err != nil {
		return DeviceDescription{}, err
	}
	d := DeviceDescription{
		FriendlyName: root.Device.FriendlyName,
		Manufacturer: root.Device.Manufacturer,
		ModelName:    root.Device.ModelName,
		Udn:          root.Device.Udn,
	}
	for _, s := range root.Device.ServiceList.Services {
		ref, err := url.Parse(s.ControlURL)
		if err != nil {
			continue
		}
		d.Services = append(d.Services, RendererDescription{
			FriendlyName: d.FriendlyName,
			Manufacturer: d.Manufacturer,
			ModelName:    d.ModelName,
			Udn:          d.Udn,
			ControlUrl:   base.ResolveReference(ref).String(),
			ServiceType:  s.ServiceType,
		})
	}
	return d, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// UPnP control

// DidlLite builds the metadata a renderer wants alongside a URI.
//
// Not optional in practice: a television handed a URI with no metadata will
// often play it and show nothing, or refuse outright.
type DidlLite struct{}

// Build returns the DIDL-Lite document.
func (DidlLite) Build(title, mimeType string, kind CastContentKind, uri string) string {
	class := "object.item.imageItem"
	switch kind {
	case CastContentAudio:
		class = "object.item.audioItem.musicTrack"
	case CastContentVideo:
		class = "object.item.videoItem"
	}
	esc := func(s string) string {
		var b strings.Builder
		_ = xml.EscapeText(&b, []byte(s))
		return b.String()
	}
	return `<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"` +
		` xmlns:dc="http://purl.org/dc/elements/1.1/"` +
		` xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">` +
		`<item id="0" parentID="-1" restricted="1">` +
		`<dc:title>` + esc(title) + `</dc:title>` +
		`<upnp:class>` + class + `</upnp:class>` +
		`<res protocolInfo="http-get:*:` + esc(mimeType) + `:*">` + esc(uri) + `</res>` +
		`</item></DIDL-Lite>`
}

// UpnpControlPoint performs SOAP actions against a renderer.
type UpnpControlPoint struct {
	post func(ctx context.Context, controlUrl, action, body string) (string, error)
}

// NewUpnpControlPoint returns a control point over the host's HTTP.
//
// A function rather than an http.Client because the transport is the host's:
// this module does not decide the timeout, the proxy or the certificate policy.
func NewUpnpControlPoint(post func(ctx context.Context, controlUrl, action, body string) (string, error)) *UpnpControlPoint {
	return &UpnpControlPoint{post: post}
}

const avTransport = "urn:schemas-upnp-org:service:AVTransport:1"

func (p *UpnpControlPoint) soap(ctx context.Context, controlUrl, action, inner string) (string, error) {
	if p.post == nil {
		return "", CastException{Reason: "no transport configured"}
	}
	body := `<?xml version="1.0"?>` +
		`<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"` +
		` s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body>` +
		`<u:` + action + ` xmlns:u="` + avTransport + `">` + inner + `</u:` + action + `>` +
		`</s:Body></s:Envelope>`
	// SOAPACTION must be QUOTED. An unquoted action is rejected by most
	// renderers with a 500 and no explanation, and it is the single most common
	// reason a first DLNA implementation does not work.
	quoted := `"` + avTransport + `#` + action + `"`
	out, err := p.post(ctx, controlUrl, quoted, body)
	if err != nil {
		return "", CastControlException{Reason: err.Error(), Action: action}
	}
	return out, nil
}

// SetAvTransportUri hands the renderer a URI to fetch.
func (p *UpnpControlPoint) SetAvTransportUri(ctx context.Context, controlUrl, uri, metadata string) error {
	esc := func(s string) string {
		var b strings.Builder
		_ = xml.EscapeText(&b, []byte(s))
		return b.String()
	}
	_, err := p.soap(ctx, controlUrl, "SetAVTransportURI",
		"<InstanceID>0</InstanceID><CurrentURI>"+esc(uri)+"</CurrentURI>"+
			"<CurrentURIMetaData>"+esc(metadata)+"</CurrentURIMetaData>")
	return err
}

// Play starts playback.
func (p *UpnpControlPoint) Play(ctx context.Context, controlUrl string) error {
	_, err := p.soap(ctx, controlUrl, "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>")
	return err
}

// Pause pauses playback.
func (p *UpnpControlPoint) Pause(ctx context.Context, controlUrl string) error {
	_, err := p.soap(ctx, controlUrl, "Pause", "<InstanceID>0</InstanceID>")
	return err
}

// Stop stops playback.
func (p *UpnpControlPoint) Stop(ctx context.Context, controlUrl string) error {
	_, err := p.soap(ctx, controlUrl, "Stop", "<InstanceID>0</InstanceID>")
	return err
}

// Seek moves the position.
func (p *UpnpControlPoint) Seek(ctx context.Context, controlUrl string, positionSeconds float64) error {
	_, err := p.soap(ctx, controlUrl, "Seek",
		"<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>"+formatUpnpTime(positionSeconds)+"</Target>")
	return err
}

// GetPositionInfo reads where the renderer is.
func (p *UpnpControlPoint) GetPositionInfo(ctx context.Context, controlUrl string) (CastStatus, error) {
	body, err := p.soap(ctx, controlUrl, "GetPositionInfo", "<InstanceID>0</InstanceID>")
	if err != nil {
		return CastStatus{State: CastStateError}, err
	}
	return CastStatus{
		State:           CastStatePlaying,
		PositionSeconds: parseUpnpTime(betweenTags(body, "RelTime")),
		DurationSeconds: parseUpnpTime(betweenTags(body, "TrackDuration")),
		CurrentUri:      betweenTags(body, "TrackURI"),
	}, nil
}

func betweenTags(body, tag string) string {
	open, close := "<"+tag+">", "</"+tag+">"
	a := strings.Index(body, open)
	if a < 0 {
		return ""
	}
	a += len(open)
	b := strings.Index(body[a:], close)
	if b < 0 {
		return ""
	}
	return body[a : a+b]
}

// formatUpnpTime renders H:MM:SS. Renderers reject a bare number of seconds.
func formatUpnpTime(seconds float64) string {
	if seconds < 0 {
		seconds = 0
	}
	total := int(seconds)
	return fmt.Sprintf("%d:%02d:%02d", total/3600, (total%3600)/60, total%60)
}

func parseUpnpTime(s string) float64 {
	parts := strings.Split(strings.TrimSpace(s), ":")
	if len(parts) != 3 {
		return -1
	}
	h, _ := strconv.Atoi(parts[0])
	m, _ := strconv.Atoi(parts[1])
	sec, _ := strconv.ParseFloat(parts[2], 64)
	return float64(h*3600+m*60) + sec
}

// ─────────────────────────────────────────────────────────────────────────────
// Targets, discovery and sessions

// ICastTarget is something that can be cast to.
type ICastTarget interface {
	Id() CastTargetId
	FriendlyName() string
	Protocol() CastProtocol
}

// DlnaCastTarget is one renderer on the LAN.
type DlnaCastTarget struct {
	TargetId   CastTargetId
	Name       string
	ControlUrl string
}

// Id implements ICastTarget.
func (t DlnaCastTarget) Id() CastTargetId { return t.TargetId }

// FriendlyName implements ICastTarget.
func (t DlnaCastTarget) FriendlyName() string { return t.Name }

// Protocol implements ICastTarget.
func (t DlnaCastTarget) Protocol() CastProtocol { return CastProtocolDlna }

// ICastDiscovery finds targets.
type ICastDiscovery interface {
	Discover(ctx context.Context) ([]ICastTarget, error)
}

// NullCastDiscovery finds nobody.
//
// The default, so a host with no discovery wired gets an empty list rather than
// a nil dereference — and so no test sends multicast on somebody's network.
type NullCastDiscovery struct{}

// Discover implements ICastDiscovery.
func (NullCastDiscovery) Discover(_ context.Context) ([]ICastTarget, error) { return nil, nil }

// DlnaCastDiscovery finds renderers by SSDP.
type DlnaCastDiscovery struct {
	ssdp *SsdpClient
	get  func(ctx context.Context, url string) (string, error)
}

// NewDlnaCastDiscovery returns discovery over an SSDP client and the host's HTTP.
func NewDlnaCastDiscovery(ssdp *SsdpClient, get func(ctx context.Context, url string) (string, error)) *DlnaCastDiscovery {
	return &DlnaCastDiscovery{ssdp: ssdp, get: get}
}

// Discover implements ICastDiscovery.
func (d *DlnaCastDiscovery) Discover(ctx context.Context) ([]ICastTarget, error) {
	if d.ssdp == nil {
		return nil, nil
	}
	replies, err := d.ssdp.Search(ctx, "urn:schemas-upnp-org:device:MediaRenderer:1")
	if err != nil {
		return nil, err
	}
	var out []ICastTarget
	for _, r := range replies {
		if d.get == nil {
			continue
		}
		body, err := d.get(ctx, r.Location)
		if err != nil {
			// One unreachable renderer must not fail the whole scan: a
			// television that answered and then went to sleep is normal.
			continue
		}
		desc, err := ParseDeviceDescription(body, r.Location)
		if err != nil {
			continue
		}
		for _, svc := range desc.Services {
			if strings.Contains(svc.ServiceType, "AVTransport") {
				out = append(out, DlnaCastTarget{
					TargetId:   CastTargetId{Value: desc.Udn},
					Name:       desc.FriendlyName,
					ControlUrl: svc.ControlUrl,
				})
				break
			}
		}
	}
	return out, nil
}

// ICastSession controls playback on one target.
type ICastSession interface {
	Load(ctx context.Context, media CastMedia) error
	Play(ctx context.Context) error
	Pause(ctx context.Context) error
	Stop(ctx context.Context) error
	Seek(ctx context.Context, positionSeconds float64) error
	Status(ctx context.Context) (CastStatus, error)
	Close() error
}

// DlnaCastSession is a session against one DLNA renderer.
type DlnaCastSession struct {
	target    DlnaCastTarget
	control   *UpnpControlPoint
	mediaHost ILocalMediaHost
	mu        sync.Mutex
	published []string
}

// NewDlnaCastSession opens a session.
//
// mediaHost is where THIS device serves local files from. Nil means only
// already-addressable URLs can be cast, and a file or byte source then fails
// with ErrNoMediaHost rather than silently doing nothing.
func NewDlnaCastSession(target DlnaCastTarget, control *UpnpControlPoint, mediaHost ILocalMediaHost) *DlnaCastSession {
	return &DlnaCastSession{target: target, control: control, mediaHost: mediaHost}
}

// Load hands the renderer a URI to fetch.
func (s *DlnaCastSession) Load(ctx context.Context, media CastMedia) error {
	uri, err := s.uriFor(media)
	if err != nil {
		return err
	}
	metadata := DidlLite{}.Build(media.Title, media.MimeType, media.Kind, uri)
	return s.control.SetAvTransportUri(ctx, s.target.ControlUrl, uri, metadata)
}

func (s *DlnaCastSession) uriFor(media CastMedia) (string, error) {
	switch src := media.Source.(type) {
	case Url:
		return src.Value, nil
	case File:
		if s.mediaHost == nil {
			return "", ErrNoMediaHost
		}
		u, err := s.mediaHost.PublishFile(src.Path, media.MimeType)
		if err != nil {
			return "", err
		}
		s.remember(u)
		return u, nil
	case Bytes:
		if s.mediaHost == nil {
			return "", ErrNoMediaHost
		}
		u, err := s.mediaHost.Publish(src.Data, media.MimeType)
		if err != nil {
			return "", err
		}
		s.remember(u)
		return u, nil
	}
	return "", CastException{Reason: "unknown media source"}
}

func (s *DlnaCastSession) remember(u string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.published = append(s.published, u)
}

// Play implements ICastSession.
func (s *DlnaCastSession) Play(ctx context.Context) error {
	return s.control.Play(ctx, s.target.ControlUrl)
}

// Pause implements ICastSession.
func (s *DlnaCastSession) Pause(ctx context.Context) error {
	return s.control.Pause(ctx, s.target.ControlUrl)
}

// Stop implements ICastSession.
func (s *DlnaCastSession) Stop(ctx context.Context) error {
	return s.control.Stop(ctx, s.target.ControlUrl)
}

// Seek implements ICastSession.
func (s *DlnaCastSession) Seek(ctx context.Context, positionSeconds float64) error {
	return s.control.Seek(ctx, s.target.ControlUrl, positionSeconds)
}

// Status implements ICastSession.
func (s *DlnaCastSession) Status(ctx context.Context) (CastStatus, error) {
	return s.control.GetPositionInfo(ctx, s.target.ControlUrl)
}

// Close stops playback and unpublishes anything this session served.
//
// Unpublishing matters: a media host that keeps serving after the session ended
// is a file server for the whole network with no expiry.
func (s *DlnaCastSession) Close() error {
	s.mu.Lock()
	published := s.published
	s.published = nil
	s.mu.Unlock()
	if s.mediaHost != nil {
		for _, u := range published {
			_ = s.mediaHost.Unpublish(u)
		}
	}
	return nil
}

// ICastEngine is discovery plus control, assembled.
type ICastEngine interface {
	Discover(ctx context.Context) ([]ICastTarget, error)
	Open(target ICastTarget) (ICastSession, error)
}

// DlnaCastEngine is the one entry point a host needs.
type DlnaCastEngine struct {
	discovery ICastDiscovery
	control   *UpnpControlPoint
	mediaHost ILocalMediaHost
}

// NewDlnaCastEngine assembles the engine.
func NewDlnaCastEngine(discovery ICastDiscovery, control *UpnpControlPoint, mediaHost ILocalMediaHost) *DlnaCastEngine {
	return &DlnaCastEngine{discovery: discovery, control: control, mediaHost: mediaHost}
}

// Discover implements ICastEngine.
func (e *DlnaCastEngine) Discover(ctx context.Context) ([]ICastTarget, error) {
	if e.discovery == nil {
		return nil, nil
	}
	return e.discovery.Discover(ctx)
}

// Open implements ICastEngine.
func (e *DlnaCastEngine) Open(target ICastTarget) (ICastSession, error) {
	dlna, ok := target.(DlnaCastTarget)
	if !ok {
		return nil, CastException{Reason: "not a DLNA target"}
	}
	return NewDlnaCastSession(dlna, e.control, e.mediaHost), nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Serving media from this device

// ILocalMediaHost publishes bytes at a URL the television can fetch.
//
// This interface exists because THE RENDERER PULLS — which is the whole reason
// casting a file already on the device needs an HTTP server running on it.
type ILocalMediaHost interface {
	Publish(data []byte, mimeType string) (string, error)
	PublishFile(path, mimeType string) (string, error)
	Unpublish(url string) error
}

// LocalAddress finds the LAN address this device is reachable at.
type LocalAddress struct{}

// Best returns the first non-loopback IPv4 address, or "".
//
// IPv4 specifically: DLNA renderers in this market are overwhelmingly IPv4-only,
// and handing one an IPv6 URL produces a television that accepts the command and
// plays nothing.
func (LocalAddress) Best() string {
	addrs, err := net.InterfaceAddrs()
	if err != nil {
		return ""
	}
	for _, a := range addrs {
		if ipnet, ok := a.(*net.IPNet); ok && !ipnet.IP.IsLoopback() {
			if v4 := ipnet.IP.To4(); v4 != nil {
				return v4.String()
			}
		}
	}
	return ""
}

type hostedItem struct {
	data     []byte
	path     string
	mimeType string
}

// TcpMediaHost is a minimal HTTP server for cast media.
//
// Serves ONLY what has been published through it — no path is derived from a
// request. A media host that resolved paths from the URL is a file server for
// the whole network, reachable by anything on the same wifi.
type TcpMediaHost struct {
	mu     sync.RWMutex
	items  map[string]hostedItem
	next   int
	server *http.Server
	base   string
}

// NewTcpMediaHost starts a host on the given port.
func NewTcpMediaHost(port int) (*TcpMediaHost, error) {
	addr := LocalAddress{}.Best()
	if addr == "" {
		return nil, errors.New("no LAN address: a renderer cannot fetch from this device")
	}
	h := &TcpMediaHost{
		items: map[string]hostedItem{},
		base:  fmt.Sprintf("http://%s:%d", addr, port),
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/media/", h.serve)
	h.server = &http.Server{Addr: fmt.Sprintf(":%d", port), Handler: mux, ReadHeaderTimeout: 5 * time.Second}

	ln, err := net.Listen("tcp", h.server.Addr)
	if err != nil {
		return nil, err
	}
	go func() { _ = h.server.Serve(ln) }()
	return h, nil
}

func (h *TcpMediaHost) serve(w http.ResponseWriter, r *http.Request) {
	h.mu.RLock()
	item, ok := h.items[r.URL.Path]
	h.mu.RUnlock()
	if !ok {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", item.mimeType)
	// Renderers seek, so range requests are not optional for video.
	if item.path != "" {
		http.ServeFile(w, r, item.path)
		return
	}
	http.ServeContent(w, r, "", time.Time{}, strings.NewReader(string(item.data)))
}

// Publish implements ILocalMediaHost.
func (h *TcpMediaHost) Publish(data []byte, mimeType string) (string, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.next++
	path := fmt.Sprintf("/media/%d", h.next)
	h.items[path] = hostedItem{data: data, mimeType: mimeType}
	return h.base + path, nil
}

// PublishFile implements ILocalMediaHost.
func (h *TcpMediaHost) PublishFile(filePath, mimeType string) (string, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.next++
	path := fmt.Sprintf("/media/%d", h.next)
	h.items[path] = hostedItem{path: filePath, mimeType: mimeType}
	return h.base + path, nil
}

// Unpublish implements ILocalMediaHost.
func (h *TcpMediaHost) Unpublish(u string) error {
	parsed, err := url.Parse(u)
	if err != nil {
		return err
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	delete(h.items, parsed.Path)
	return nil
}

// Close stops the server.
func (h *TcpMediaHost) Close() error {
	if h.server == nil {
		return nil
	}
	return h.server.Close()
}

// ─────────────────────────────────────────────────────────────────────────────
// Documents

// CastDocument is a document on its way to a screen.
type CastDocument struct {
	Title     string
	Bytes     []byte
	MimeType  string
	PageCount int
}

// IDocumentCastAdapter turns a document into something a television can show.
//
// Usually images, one per page, because televisions render almost no document
// format and the ones they do render inconsistently.
type IDocumentCastAdapter interface {
	ToMedia(doc CastDocument, pageIndex int) (CastMedia, error)
}

// NullDocumentCastAdapter converts nothing.
type NullDocumentCastAdapter struct{}

// ToMedia implements IDocumentCastAdapter.
func (NullDocumentCastAdapter) ToMedia(_ CastDocument, _ int) (CastMedia, error) {
	return CastMedia{}, errors.New("no document adapter configured")
}
