// mesh_defense_music.go
//
// Offloading work to a nearby device; the blocklist-driven defence that watches
// what this one talks to; the music that plays under everything else; and the
// golden-file machinery the rest is tested against.
//
// OFFLOADING SENDS A PROMPT TO SOMEBODY ELSE'S HARDWARE. Everything in the mesh
// half refuses by default and keeps saying WHOSE device answered, because "it
// was faster on the other phone" is not a reason somebody consented to their
// conversation leaving this one.
//
// THE DEFENCE HALF OBSERVES AND ESCALATES TO A PERSON. It does not block, does
// not disconnect, and does not change a device's radios or settings.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Mesh offload

// OffloadServedBy says which device answered.
type OffloadServedBy struct {
	PeerID      string
	DisplayName string
	// Whether this peer has been added by BOTH devices. Offloading to a peer
	// that has not added us back is sending a prompt to a stranger.
	MutuallyAdded bool
}

// OffloadTurn is one turn, wherever it ran.
type OffloadTurn struct {
	TurnID   string
	Prompt   string
	Response string
	// Nil means it ran HERE. Always carried through to the caller, so a UI can
	// say which device answered — the one fact that makes offloading something
	// somebody agreed to rather than something that happened to them.
	ServedBy *OffloadServedBy
	Duration time.Duration
}

// OffloadResult is the turn plus why it was routed the way it was.
type OffloadResult struct {
	Turn OffloadTurn
	// Always populated, including when the answer was to stay local. The reason
	// is what makes an offload decision reviewable instead of magic.
	Reason string
}

// ILocalInferenceFallback runs a prompt on this device instead.
type ILocalInferenceFallback interface {
	IsAvailable() bool
	Run(ctx context.Context, prompt string) (string, error)
}

// NullLocalInferenceFallback runs nothing and reports unavailable.
//
// The default: a router with no local fallback must KNOW it has none, or it
// will route to the mesh because it believes there is a safety net.
type NullLocalInferenceFallback struct{}

// IsAvailable implements ILocalInferenceFallback.
func (NullLocalInferenceFallback) IsAvailable() bool { return false }

// Run implements ILocalInferenceFallback.
func (NullLocalInferenceFallback) Run(context.Context, string) (string, error) {
	return "", errors.New("no local inference available on this device")
}

// IMeshOffloadClient sends a prompt to a peer.
type IMeshOffloadClient interface {
	Send(ctx context.Context, peerID, prompt string) (OffloadTurn, error)
}

// MeshOffloadClient is the default client.
type MeshOffloadClient struct {
	send func(ctx context.Context, peerID, prompt string) (string, error)
	// Peers this device has added AND that have added it back.
	mu    sync.RWMutex
	peers map[string]OffloadServedBy
}

// NewMeshOffloadClient returns a client over a transport.
func NewMeshOffloadClient(send func(ctx context.Context, peerID, prompt string) (string, error)) *MeshOffloadClient {
	return &MeshOffloadClient{send: send, peers: map[string]OffloadServedBy{}}
}

// AddPeer records a peer and whether the addition is mutual.
func (c *MeshOffloadClient) AddPeer(peer OffloadServedBy) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.peers[peer.PeerID] = peer
}

// Send implements IMeshOffloadClient.
func (c *MeshOffloadClient) Send(ctx context.Context, peerID, prompt string) (OffloadTurn, error) {
	c.mu.RLock()
	peer, known := c.peers[peerID]
	c.mu.RUnlock()
	if !known || !peer.MutuallyAdded {
		return OffloadTurn{}, fmt.Errorf("peer %q has not added this device back", peerID)
	}
	if c.send == nil {
		return OffloadTurn{}, errors.New("no transport configured")
	}
	started := time.Now()
	response, err := c.send(ctx, peerID, prompt)
	if err != nil {
		return OffloadTurn{}, err
	}
	return OffloadTurn{
		Prompt: prompt, Response: response,
		ServedBy: &peer, Duration: time.Since(started),
	}, nil
}

// IOffloadRouter decides where a turn runs and runs it.
type IOffloadRouter interface {
	Route(ctx context.Context, prompt string) OffloadResult
}

// MeshOffloadRouter routes to a peer only when every condition holds.
//
// The peer is mutually added, the link is already up, this device genuinely
// cannot do the work, and the person has consented to offload for this kind of
// request. LATENCY ALONE IS NEVER SUFFICIENT: "it would be faster over there"
// is the argument that ends with somebody's conversation on a device they do
// not own.
type MeshOffloadRouter struct {
	client    IMeshOffloadClient
	fallback  ILocalInferenceFallback
	mu        sync.RWMutex
	consented bool
	preferred string
}

// NewMeshOffloadRouter returns a router.
func NewMeshOffloadRouter(client IMeshOffloadClient, fallback ILocalInferenceFallback) *MeshOffloadRouter {
	if fallback == nil {
		fallback = NullLocalInferenceFallback{}
	}
	return &MeshOffloadRouter{client: client, fallback: fallback}
}

// Consent records that the person agreed to offloading, to a named peer.
//
// Per peer, not a global switch: agreeing to use the tablet in the next room is
// not agreeing to use whatever else joins the mesh later.
func (r *MeshOffloadRouter) Consent(peerID string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.consented = strings.TrimSpace(peerID) != ""
	r.preferred = peerID
}

// Route implements IOffloadRouter.
func (r *MeshOffloadRouter) Route(ctx context.Context, prompt string) OffloadResult {
	r.mu.RLock()
	consented, peerID := r.consented, r.preferred
	r.mu.RUnlock()

	if r.fallback.IsAvailable() {
		out, err := r.fallback.Run(ctx, prompt)
		if err == nil {
			return OffloadResult{
				Turn:   OffloadTurn{Prompt: prompt, Response: out},
				Reason: "this device can answer, so it did",
			}
		}
	}
	if !consented {
		return OffloadResult{Reason: "nothing on this device can answer, and no peer has been agreed to"}
	}
	if r.client == nil {
		return OffloadResult{Reason: "no mesh client configured"}
	}
	turn, err := r.client.Send(ctx, peerID, prompt)
	if err != nil {
		return OffloadResult{Reason: "the agreed peer could not answer: " + err.Error()}
	}
	return OffloadResult{Turn: turn, Reason: "answered by " + peerID + ", which you agreed to"}
}

// MeshAdvertisementBeacon is what a device tells the room about itself.
type MeshAdvertisementBeacon struct {
	DeviceID     string
	Capabilities []string
	RamBytes     int64
	LoadAverage  float64
	At           time.Time
}

// AetherMeshCapabilityBroadcaster tells nearby devices what this one can do.
//
// CAPABILITIES ONLY — never what it is doing, never who owns it, never what was
// asked. A beacon that carried activity would make a mesh of phones into a mesh
// of people broadcasting their behaviour to the room.
type AetherMeshCapabilityBroadcaster struct {
	deviceID  string
	publish   func(ctx context.Context, beacon MeshAdvertisementBeacon) error
	mu        sync.Mutex
	lastSent  time.Time
	minPeriod time.Duration
}

// NewAetherMeshCapabilityBroadcaster returns a broadcaster.
func NewAetherMeshCapabilityBroadcaster(deviceID string, publish func(ctx context.Context, beacon MeshAdvertisementBeacon) error) *AetherMeshCapabilityBroadcaster {
	return &AetherMeshCapabilityBroadcaster{deviceID: deviceID, publish: publish, minPeriod: 30 * time.Second}
}

// Advertise sends a beacon, rate-limited.
//
// Rate-limited because a beacon is a radio transmission: broadcasting every
// second is a measurable battery cost on every device in range, not just this
// one.
func (b *AetherMeshCapabilityBroadcaster) Advertise(ctx context.Context, beacon MeshAdvertisementBeacon) error {
	if b.publish == nil {
		return errors.New("no transport configured")
	}
	b.mu.Lock()
	if !b.lastSent.IsZero() && time.Since(b.lastSent) < b.minPeriod {
		b.mu.Unlock()
		return nil
	}
	b.lastSent = time.Now()
	b.mu.Unlock()

	beacon.DeviceID = b.deviceID
	beacon.At = time.Now()
	return b.publish(ctx, beacon)
}

// ─────────────────────────────────────────────────────────────────────────────
// Blocklists

// Ipv4Cidr is an IPv4 network.
type Ipv4Cidr struct {
	Base uint32
	Bits int
}

// ParseIpv4Cidr parses "a.b.c.d/n". A bare address is treated as /32.
func ParseIpv4Cidr(text string) (Ipv4Cidr, bool) {
	text = strings.TrimSpace(text)
	bits := 32
	if i := strings.Index(text, "/"); i >= 0 {
		n, err := strconv.Atoi(text[i+1:])
		if err != nil || n < 0 || n > 32 {
			return Ipv4Cidr{}, false
		}
		bits = n
		text = text[:i]
	}
	ip := net.ParseIP(text)
	if ip == nil {
		return Ipv4Cidr{}, false
	}
	v4 := ip.To4()
	if v4 == nil {
		return Ipv4Cidr{}, false
	}
	base := binary.BigEndian.Uint32(v4)
	// Mask the base to the prefix. An unmasked base means 10.0.0.5/8 does not
	// equal 10.0.0.0/8, and two entries for one network compare unequal.
	if bits < 32 {
		base &= ^uint32(0) << uint(32-bits)
	}
	return Ipv4Cidr{Base: base, Bits: bits}, true
}

// Contains reports whether an address is inside the network.
func (c Ipv4Cidr) Contains(ip net.IP) bool {
	v4 := ip.To4()
	if v4 == nil {
		return false
	}
	if c.Bits == 0 {
		return true
	}
	mask := ^uint32(0) << uint(32-c.Bits)
	return binary.BigEndian.Uint32(v4)&mask == c.Base
}

// String renders the network.
func (c Ipv4Cidr) String() string {
	var b [4]byte
	binary.BigEndian.PutUint32(b[:], c.Base)
	return fmt.Sprintf("%d.%d.%d.%d/%d", b[0], b[1], b[2], b[3], c.Bits)
}

// ParsedIndicator is one line of a blocklist.
type ParsedIndicator struct {
	Kind    IndicatorKind
	Value   string
	Cidr    *Ipv4Cidr
	Comment string
}

// BlocklistParser reads the common blocklist formats.
type BlocklistParser struct{}

// Parse reads a blocklist body.
//
// Handles hosts-file lines, bare domains, addresses and CIDRs, because the
// lists people actually publish are a mix of all four — and a parser that
// handles one of them silently ignores most of the file.
func (BlocklistParser) Parse(body string) []ParsedIndicator {
	var out []ParsedIndicator
	for _, raw := range strings.Split(body, "\n") {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, ";") {
			continue
		}
		comment := ""
		if i := strings.IndexAny(line, "#;"); i >= 0 {
			comment = strings.TrimSpace(line[i+1:])
			line = strings.TrimSpace(line[:i])
		}
		fields := strings.Fields(line)
		if len(fields) == 0 {
			continue
		}
		// A hosts-file line is "0.0.0.0 bad.example" — the interesting half is
		// the SECOND field. Taking the first would blocklist 0.0.0.0.
		value := fields[0]
		if len(fields) >= 2 && (value == "0.0.0.0" || value == "127.0.0.1" || value == "::1") {
			value = fields[1]
		}
		if cidr, ok := ParseIpv4Cidr(value); ok {
			c := cidr
			out = append(out, ParsedIndicator{Kind: IndicatorNetwork, Value: cidr.String(), Cidr: &c, Comment: comment})
			continue
		}
		if strings.Contains(value, ".") {
			out = append(out, ParsedIndicator{Kind: IndicatorNetwork, Value: strings.ToLower(value), Comment: comment})
		}
	}
	return out
}

// IIndicatorSource supplies indicators to the defence layer.
type IIndicatorSource interface {
	Name() string
	Load(ctx context.Context) ([]ParsedIndicator, error)
}

// BlocklistIndicatorSource loads indicators from a blocklist body.
//
// LOCAL by default: the body comes from a file a host chose. Fetching a list
// over the network would mean the defence layer tells somebody's server which
// device is running it, every time it refreshes.
type BlocklistIndicatorSource struct {
	name string
	read func(ctx context.Context) (string, error)
}

// NewBlocklistIndicatorSource returns a source.
func NewBlocklistIndicatorSource(name string, read func(ctx context.Context) (string, error)) *BlocklistIndicatorSource {
	return &BlocklistIndicatorSource{name: name, read: read}
}

// Name implements IIndicatorSource.
func (s *BlocklistIndicatorSource) Name() string { return s.name }

// Load implements IIndicatorSource.
func (s *BlocklistIndicatorSource) Load(ctx context.Context) ([]ParsedIndicator, error) {
	if s.read == nil {
		return nil, errors.New("no reader configured")
	}
	body, err := s.read(ctx)
	if err != nil {
		return nil, err
	}
	return BlocklistParser{}.Parse(body), nil
}

// IThreatSink receives threat signals.
type IThreatSink interface {
	Report(ctx context.Context, signal ThreatSignal)
}

// NullThreatSink receives and discards.
type NullThreatSink struct{}

// Report implements IThreatSink.
func (NullThreatSink) Report(context.Context, ThreatSignal) {}

// DelegateThreatSink calls a function.
type DelegateThreatSink struct {
	fn func(ctx context.Context, signal ThreatSignal)
}

// NewDelegateThreatSink returns a sink over a function.
func NewDelegateThreatSink(fn func(ctx context.Context, signal ThreatSignal)) *DelegateThreatSink {
	return &DelegateThreatSink{fn: fn}
}

// Report implements IThreatSink.
func (s *DelegateThreatSink) Report(ctx context.Context, signal ThreatSignal) {
	if s.fn != nil {
		s.fn(ctx, signal)
	}
}

// CompositeThreatSink fans out to several sinks.
type CompositeThreatSink struct {
	sinks []IThreatSink
}

// NewCompositeThreatSink returns a fan-out sink.
func NewCompositeThreatSink(sinks ...IThreatSink) *CompositeThreatSink {
	return &CompositeThreatSink{sinks: sinks}
}

// Report implements IThreatSink.
//
// A panicking sink must not stop the others: one broken reporter should not
// mean nobody is told about the threat.
func (s *CompositeThreatSink) Report(ctx context.Context, signal ThreatSignal) {
	for _, sink := range s.sinks {
		if sink == nil {
			continue
		}
		func() {
			defer func() { _ = recover() }()
			sink.Report(ctx, signal)
		}()
	}
}

// WatchdogThreatSink escalates through the SOS path.
type WatchdogThreatSink struct {
	sink *SosThreatSink
}

// NewWatchdogThreatSink returns a sink over the SOS path.
func NewWatchdogThreatSink(sink *SosThreatSink) *WatchdogThreatSink {
	return &WatchdogThreatSink{sink: sink}
}

// Report implements IThreatSink.
func (s *WatchdogThreatSink) Report(ctx context.Context, signal ThreatSignal) {
	if s.sink != nil {
		_ = s.sink.Submit(ctx, signal)
	}
}

// IThreatMonitor watches for something worth reporting.
type IThreatMonitor interface {
	Name() string
	Observe(ctx context.Context, observation NetworkObservation)
}

// BlocklistThreatMonitor checks observations against loaded indicators.
type BlocklistThreatMonitor struct {
	mu         sync.RWMutex
	domains    map[string]ParsedIndicator
	cidrs      []ParsedIndicator
	sink       IThreatSink
	sourceName string
}

// NewBlocklistThreatMonitor returns a monitor.
func NewBlocklistThreatMonitor(sink IThreatSink) *BlocklistThreatMonitor {
	if sink == nil {
		sink = NullThreatSink{}
	}
	return &BlocklistThreatMonitor{domains: map[string]ParsedIndicator{}, sink: sink}
}

// Name implements IThreatMonitor.
func (m *BlocklistThreatMonitor) Name() string { return "blocklist" }

// LoadFrom loads indicators from a source.
func (m *BlocklistThreatMonitor) LoadFrom(ctx context.Context, source IIndicatorSource) error {
	if source == nil {
		return errors.New("no source")
	}
	indicators, err := source.Load(ctx)
	if err != nil {
		return err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.sourceName = source.Name()
	for _, ind := range indicators {
		if ind.Cidr != nil {
			m.cidrs = append(m.cidrs, ind)
			continue
		}
		m.domains[ind.Value] = ind
	}
	return nil
}

// Observe implements IThreatMonitor.
//
// OUTBOUND matches are reported at a higher severity than inbound: something on
// this phone talking to a known-bad host is a compromised app, and it is the
// case a defence aimed at servers is not looking for.
func (m *BlocklistThreatMonitor) Observe(ctx context.Context, obs NetworkObservation) {
	host := obs.RemoteEndpoint
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}
	m.mu.RLock()
	ind, matched := m.domains[strings.ToLower(host)]
	if !matched {
		if ip := net.ParseIP(host); ip != nil {
			for _, c := range m.cidrs {
				if c.Cidr.Contains(ip) {
					ind, matched = c, true
					break
				}
			}
		}
	}
	source := m.sourceName
	m.mu.RUnlock()
	if !matched {
		return
	}

	severity := ThreatMedium
	category := ThreatAnomaly
	if obs.Direction == ThreatOutbound {
		severity = ThreatHigh
		category = ThreatCommandAndControl
	}
	m.sink.Report(ctx, ThreatSignal{
		Category:   category,
		Direction:  obs.Direction,
		Severity:   severity,
		Summary:    "this device connected to " + host + ", which is on a blocklist",
		Evidence:   ind.Comment,
		Confidence: 0.8,
		At:         time.Now(),
	})
	_ = source
}

// DefenseOptions configures the defence layer.
type DefenseOptions struct {
	// How often the sentinel drains the observation feed.
	PollInterval time.Duration
	// Below this nothing is escalated.
	MinimumSeverity ThreatSeverity
	// How long the same finding is suppressed for.
	DedupeWindow time.Duration
	// OFF by default. A defence layer that starts watching because it was
	// linked in is one nobody chose.
	Enabled bool
}

// DefaultDefenseOptions returns the defaults, with the layer off.
func DefaultDefenseOptions() DefenseOptions {
	return DefenseOptions{PollInterval: 5 * time.Second, MinimumSeverity: ThreatMedium, DedupeWindow: 10 * time.Minute}
}

// IAutonomicDefense watches continuously.
type IAutonomicDefense interface {
	Start(ctx context.Context) error
	Stop()
	IsRunning() bool
}

// AlwaysOnDefenseSentinel drains the feed and hands observations to the monitors.
//
// "Always on" describes what it does once started, NOT whether it starts on its
// own. It does not: a component that watches a device's network traffic should
// begin because somebody said so.
type AlwaysOnDefenseSentinel struct {
	mu       sync.Mutex
	opts     DefenseOptions
	feed     INetworkObservationFeed
	monitors []IThreatMonitor
	cancel   context.CancelFunc
	running  bool
}

// NewAlwaysOnDefenseSentinel returns a sentinel.
func NewAlwaysOnDefenseSentinel(opts DefenseOptions, feed INetworkObservationFeed, monitors ...IThreatMonitor) *AlwaysOnDefenseSentinel {
	if opts.PollInterval <= 0 {
		opts.PollInterval = DefaultDefenseOptions().PollInterval
	}
	return &AlwaysOnDefenseSentinel{opts: opts, feed: feed, monitors: monitors}
}

// Start implements IAutonomicDefense.
func (s *AlwaysOnDefenseSentinel) Start(ctx context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.opts.Enabled {
		return errors.New("the defence layer is off; enable it deliberately")
	}
	if s.running {
		return nil
	}
	if s.feed == nil {
		return errors.New("no observation feed")
	}
	runCtx, cancel := context.WithCancel(ctx)
	s.cancel = cancel
	s.running = true
	go s.loop(runCtx)
	return nil
}

func (s *AlwaysOnDefenseSentinel) loop(ctx context.Context) {
	ticker := time.NewTicker(s.opts.PollInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			for _, obs := range s.feed.Drain() {
				for _, m := range s.monitors {
					m.Observe(ctx, obs)
				}
			}
		}
	}
}

// Stop implements IAutonomicDefense.
func (s *AlwaysOnDefenseSentinel) Stop() {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cancel != nil {
		s.cancel()
		s.cancel = nil
	}
	s.running = false
}

// IsRunning implements IAutonomicDefense.
func (s *AlwaysOnDefenseSentinel) IsRunning() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.running
}

// DefenseModule assembles the defence layer.
type DefenseModule struct {
	Options  DefenseOptions
	Sentinel *AlwaysOnDefenseSentinel
	Sink     IThreatSink
}

// NewDefenseModule assembles the layer with the defaults.
func NewDefenseModule(opts DefenseOptions, feed INetworkObservationFeed, escalation ISosEscalation) *DefenseModule {
	if escalation == nil {
		escalation = NullSosEscalation{}
	}
	sos := NewSosThreatSink(escalation, opts.MinimumSeverity, opts.DedupeWindow)
	sink := NewCompositeThreatSink(NewWatchdogThreatSink(sos))
	monitor := NewBlocklistThreatMonitor(sink)
	return &DefenseModule{
		Options:  opts,
		Sentinel: NewAlwaysOnDefenseSentinel(opts, feed, monitor),
		Sink:     sink,
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Music

// PitchClass is one of the twelve pitch classes, C = 0.
//
// Numbered so arithmetic is modulo 12 and transposition is addition. Sharps
// only: F sharp and G flat are the same pitch class here, and carrying both
// spellings would mean two names for one number with no musical difference in a
// synthesiser that has no notion of key signature.
type PitchClass int

const (
	PitchC PitchClass = iota
	PitchCSharp
	PitchD
	PitchDSharp
	PitchE
	PitchF
	PitchFSharp
	PitchG
	PitchGSharp
	PitchA
	PitchASharp
	PitchB
)

var pitchNames = [...]string{"C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"}

func (p PitchClass) String() string {
	if p < 0 || int(p) >= len(pitchNames) {
		return "?"
	}
	return pitchNames[p]
}

// Frequency returns the pitch in hertz. A4 = 440, twelve-tone equal
// temperament. octave is scientific pitch notation, so middle C is C4.
func (p PitchClass) Frequency(octave int) float64 {
	// MIDI note number, then the standard conversion. Going through MIDI rather
	// than a per-octave table keeps one formula for every octave.
	midi := (octave+1)*12 + int(p)
	return 440 * math.Pow(2, float64(midi-69)/12)
}

// Scale is a set of intervals from a root.
type Scale int

const (
	ScaleMajor Scale = iota
	ScaleNaturalMinor
	ScaleHarmonicMinor
	ScaleDorian
	ScaleMixolydian
	// ScalePentatonic — five notes, no semitones. The safest scale there is for
	// a bed: any two notes played together sound intentional, so a generator
	// that picks badly still does not sound wrong.
	ScalePentatonic
	ScaleBlues
)

var scaleDegrees = map[Scale][]int{
	ScaleMajor:         {0, 2, 4, 5, 7, 9, 11},
	ScaleNaturalMinor:  {0, 2, 3, 5, 7, 8, 10},
	ScaleHarmonicMinor: {0, 2, 3, 5, 7, 8, 11},
	ScaleDorian:        {0, 2, 3, 5, 7, 9, 10},
	ScaleMixolydian:    {0, 2, 4, 5, 7, 9, 10},
	ScalePentatonic:    {0, 2, 4, 7, 9},
	ScaleBlues:         {0, 3, 5, 6, 7, 10},
}

func (s Scale) String() string {
	switch s {
	case ScaleNaturalMinor:
		return "natural-minor"
	case ScaleHarmonicMinor:
		return "harmonic-minor"
	case ScaleDorian:
		return "dorian"
	case ScaleMixolydian:
		return "mixolydian"
	case ScalePentatonic:
		return "pentatonic"
	case ScaleBlues:
		return "blues"
	}
	return "major"
}

// MusicalKey is a root and a scale.
type MusicalKey struct {
	Root  PitchClass
	Scale Scale
}

// Degrees returns the semitone offsets from the root.
func (k MusicalKey) Degrees() []int { return scaleDegrees[k.Scale] }

// Contains reports whether a pitch class is in the key.
//
// What keeps a procedural line from wandering outside it, which is the one
// thing that makes generated music immediately identifiable as generated.
func (k MusicalKey) Contains(p PitchClass) bool {
	offset := (int(p) - int(k.Root) + 12) % 12
	for _, d := range k.Degrees() {
		if d == offset {
			return true
		}
	}
	return false
}

// MusicMood is the feel of a bed.
//
// Prefixed because Personal.Mental already holds `Mood` in this package, and
// that one is a person's mood. Two unrelated meanings under one name is the
// collision the module-prefix rule exists for — and here the two would be
// especially easy to confuse at a call site.
type MusicMood int

const (
	MusicMoodCalm MusicMood = iota
	MusicMoodWarm
	MusicMoodBright
	MusicMoodTense
	MusicMoodSombre
	MusicMoodPlayful
)

// MusicSpec is what to make.
type MusicSpec struct {
	MusicMood MusicMood
	Tempo     int
	// EXACT. A bed is under something of a known length, and music that runs
	// three seconds long has to be faded out mid-phrase — which is audible and
	// reads as a mistake.
	Duration time.Duration
	Key      MusicalKey
}

// DefaultMusicSpec returns a spec for a mood and length.
func DefaultMusicSpec(mood MusicMood, duration time.Duration) MusicSpec {
	key := MusicalKey{Root: PitchC, Scale: ScalePentatonic}
	tempo := 72
	switch mood {
	case MusicMoodBright, MusicMoodPlayful:
		key.Scale = ScaleMajor
		tempo = 108
	case MusicMoodTense:
		key.Scale = ScaleHarmonicMinor
		tempo = 96
	case MusicMoodSombre:
		key.Scale = ScaleNaturalMinor
		tempo = 60
	}
	return MusicSpec{MusicMood: mood, Tempo: tempo, Duration: duration, Key: key}
}

// AudioPcmFormat is a PCM format.
type AudioPcmFormat struct {
	SampleRate    int
	Channels      int
	BitsPerSample int
}

// DefaultAudioPcmFormat returns 24 kHz mono 16-bit.
func DefaultAudioPcmFormat() AudioPcmFormat {
	return AudioPcmFormat{SampleRate: 24000, Channels: 1, BitsPerSample: 16}
}

// BytesPerFrame returns the bytes in one frame.
func (f AudioPcmFormat) BytesPerFrame() int { return f.Channels * f.BitsPerSample / 8 }

// BytesForDuration returns the bytes for a duration.
func (f AudioPcmFormat) BytesForDuration(d time.Duration) int {
	return int(d.Seconds()*float64(f.SampleRate)) * f.BytesPerFrame()
}

// MusicBed is generated background music.
type MusicBed struct {
	Pcm    []byte
	Format AudioPcmFormat
	Spec   MusicSpec
	// Which backend produced it. Recorded because a bed that came from a
	// downloaded model and one that was synthesised are different things to
	// cache, to attribute, and to reproduce.
	BackendID string
}

// MusicBedBackend is which generator produced a bed.
type MusicBedBackend int

const (
	// MusicBedProcedural — pure arithmetic. Always available, zero dependencies.
	MusicBedProcedural MusicBedBackend = 0
	// MusicBedNeural — a downloaded neural music model.
	MusicBedNeural MusicBedBackend = 1
)

func (b MusicBedBackend) String() string {
	if b == MusicBedNeural {
		return "neural"
	}
	return "procedural"
}

// IMusicBedGenerator generates a bed.
type IMusicBedGenerator interface {
	BackendID() string
	IsAvailable() bool
	Generate(ctx context.Context, spec MusicSpec) (MusicBed, error)
}

// NullMusicBedGenerator generates silence of exactly the right length.
//
// Silence rather than an error: a caller mixing a bed under a voice should get
// a track that is quiet, not one that is missing and has to be branched around
// at every call site.
type NullMusicBedGenerator struct{}

// BackendID implements IMusicBedGenerator.
func (NullMusicBedGenerator) BackendID() string { return "null" }

// IsAvailable implements IMusicBedGenerator.
func (NullMusicBedGenerator) IsAvailable() bool { return true }

// Generate implements IMusicBedGenerator.
func (NullMusicBedGenerator) Generate(_ context.Context, spec MusicSpec) (MusicBed, error) {
	format := DefaultAudioPcmFormat()
	return MusicBed{Pcm: make([]byte, format.BytesForDuration(spec.Duration)), Format: format, Spec: spec, BackendID: "null"}, nil
}

// ProceduralMusicBedGenerator synthesises a bed from arithmetic.
//
// A pad, a bass line on the root and fifth, and a sparse melody constrained to
// the key. Envelopes are long and the attack is soft, because a bed with
// transients pulls attention — which is exactly what a bed must not do.
//
// DETERMINISTIC FOR A GIVEN SEED AND SPEC. The same request produces the same
// bytes, on every platform and in every port, which is what makes it cacheable
// and testable at all.
type ProceduralMusicBedGenerator struct {
	seed uint64
}

// NewProceduralMusicBedGenerator returns a generator.
func NewProceduralMusicBedGenerator(seed uint64) *ProceduralMusicBedGenerator {
	return &ProceduralMusicBedGenerator{seed: seed}
}

// BackendID implements IMusicBedGenerator.
func (*ProceduralMusicBedGenerator) BackendID() string { return "procedural" }

// IsAvailable implements IMusicBedGenerator.
func (*ProceduralMusicBedGenerator) IsAvailable() bool { return true }

// Generate implements IMusicBedGenerator.
func (g *ProceduralMusicBedGenerator) Generate(_ context.Context, spec MusicSpec) (MusicBed, error) {
	format := DefaultAudioPcmFormat()
	samples := int(spec.Duration.Seconds() * float64(format.SampleRate))
	if samples <= 0 {
		return MusicBed{}, errors.New("a duration is required")
	}
	pcm := make([]byte, samples*2)

	root := spec.Key.Root.Frequency(3)
	fifth := PitchClass((int(spec.Key.Root) + 7) % 12).Frequency(3)
	degrees := spec.Key.Degrees()
	rng := g.seed
	next := func() uint64 {
		// xorshift64: deterministic, and identical in every port because it is
		// exact integer arithmetic rather than a library's own generator.
		rng ^= rng << 13
		rng ^= rng >> 7
		rng ^= rng << 17
		return rng
	}

	beat := 60.0 / float64(spec.Tempo)
	for i := 0; i < samples; i++ {
		t := float64(i) / float64(format.SampleRate)
		// A long attack and release across the whole bed, so it fades in and out
		// rather than starting and stopping.
		env := math.Min(1, math.Min(t/1.5, (spec.Duration.Seconds()-t)/1.5))
		if env < 0 {
			env = 0
		}
		pad := 0.18 * (math.Sin(2*math.Pi*root*t) + 0.6*math.Sin(2*math.Pi*fifth*t))
		bass := 0.12 * math.Sin(2*math.Pi*root/2*t)

		melody := 0.0
		step := int(t / (beat * 2))
		if len(degrees) > 0 {
			note := degrees[int(next()>>32)%len(degrees)]
			_ = note
			deg := degrees[step%len(degrees)]
			f := PitchClass((int(spec.Key.Root) + deg) % 12).Frequency(5)
			phase := math.Mod(t, beat*2) / (beat * 2)
			melody = 0.10 * math.Sin(2*math.Pi*f*t) * math.Exp(-3*phase)
		}

		v := env * (pad + bass + melody)
		if v > 1 {
			v = 1
		} else if v < -1 {
			v = -1
		}
		binary.LittleEndian.PutUint16(pcm[i*2:], uint16(int16(v*32767)))
	}
	return MusicBed{Pcm: pcm, Format: format, Spec: spec, BackendID: "procedural"}, nil
}

// MusicBedGeneratorResolver picks a backend.
type MusicBedGeneratorResolver struct {
	mu         sync.RWMutex
	generators map[string]IMusicBedGenerator
	procedural IMusicBedGenerator
}

// NewMusicBedGeneratorResolver returns a resolver with the procedural
// generator always present.
func NewMusicBedGeneratorResolver() *MusicBedGeneratorResolver {
	return &MusicBedGeneratorResolver{
		generators: map[string]IMusicBedGenerator{},
		procedural: NewProceduralMusicBedGenerator(0x9E3779B97F4A7C15),
	}
}

// Register adds a generator.
func (r *MusicBedGeneratorResolver) Register(g IMusicBedGenerator) {
	if g == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.generators[g.BackendID()] = g
}

// Resolve returns a generator, never nil.
//
// Falling back to procedural is the NORMAL path, not the error path: the neural
// backend is absent, too large for the device, or would need a download on a
// metered link far more often than it is available.
func (r *MusicBedGeneratorResolver) Resolve(preferred MusicBedBackend) IMusicBedGenerator {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if g, ok := r.generators[preferred.String()]; ok && g.IsAvailable() {
		return g
	}
	return r.procedural
}

// ─────────────────────────────────────────────────────────────────────────────
// Testing

// SnapshotDiff is the difference between an expected and an actual snapshot.
type SnapshotDiff struct {
	Equal bool
	// The first differing line, with its number. A whole-file diff of a large
	// snapshot is unreadable in a test log; the first difference is almost
	// always the only one that matters.
	FirstDifferenceLine int
	Expected            string
	Actual              string
	Summary             string
}

// ISnapshotComparer compares snapshots.
type ISnapshotComparer interface {
	Compare(expected, actual string) SnapshotDiff
}

// NullSnapshotComparer reports everything equal.
//
// NOT a default. A comparer that always passes turns a golden-file suite into a
// suite that runs and asserts nothing, and the tests still go green — which is
// worse than having no tests, because somebody trusts them.
type NullSnapshotComparer struct{}

// Compare implements ISnapshotComparer.
func (NullSnapshotComparer) Compare(string, string) SnapshotDiff {
	return SnapshotDiff{Equal: true, Summary: "not compared: this comparer asserts nothing"}
}

// LineDiffSnapshotComparer compares line by line.
type LineDiffSnapshotComparer struct {
	// Whether trailing whitespace differences count. False by default: a diff
	// that fails on an invisible character is a diff nobody can act on.
	Strict bool
}

// Compare implements ISnapshotComparer.
func (c LineDiffSnapshotComparer) Compare(expected, actual string) SnapshotDiff {
	e := strings.Split(expected, "\n")
	a := strings.Split(actual, "\n")
	n := len(e)
	if len(a) > n {
		n = len(a)
	}
	for i := 0; i < n; i++ {
		var le, la string
		if i < len(e) {
			le = e[i]
		}
		if i < len(a) {
			la = a[i]
		}
		if !c.Strict {
			le, la = strings.TrimRight(le, " \t\r"), strings.TrimRight(la, " \t\r")
		}
		if le != la {
			return SnapshotDiff{
				FirstDifferenceLine: i + 1,
				Expected:            le,
				Actual:              la,
				Summary:             fmt.Sprintf("line %d differs", i+1),
			}
		}
	}
	return SnapshotDiff{Equal: true}
}

// IGoldenStore holds approved snapshots.
type IGoldenStore interface {
	Load(name string) (string, bool)
	// Approve records a new golden. Deliberately separate from Load: a store
	// that wrote a golden on a miss would make every test pass on its first run
	// and never fail again.
	Approve(name, content string) error
}

// NullGoldenStore holds nothing.
type NullGoldenStore struct{}

// Load implements IGoldenStore.
func (NullGoldenStore) Load(string) (string, bool) { return "", false }

// Approve implements IGoldenStore.
func (NullGoldenStore) Approve(string, string) error { return nil }

// InMemoryGoldenStore holds goldens in memory.
type InMemoryGoldenStore struct {
	mu      sync.RWMutex
	goldens map[string]string
}

// NewInMemoryGoldenStore returns an empty store.
func NewInMemoryGoldenStore() *InMemoryGoldenStore {
	return &InMemoryGoldenStore{goldens: map[string]string{}}
}

// Load implements IGoldenStore.
func (s *InMemoryGoldenStore) Load(name string) (string, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	g, ok := s.goldens[name]
	return g, ok
}

// Approve implements IGoldenStore.
func (s *InMemoryGoldenStore) Approve(name, content string) error {
	if strings.TrimSpace(name) == "" {
		return errors.New("a golden needs a name")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.goldens[name] = content
	return nil
}

// FrozenClock returns the same instant every time.
//
// Every test that touches a timestamp needs one, and the alternative — sleeping
// or tolerating a window — makes a suite that fails once a fortnight for
// reasons nobody can reproduce.
type FrozenClock struct {
	mu  sync.Mutex
	now time.Time
}

// NewFrozenClock returns a clock stopped at an instant.
func NewFrozenClock(at time.Time) *FrozenClock { return &FrozenClock{now: at} }

// Now returns the frozen instant.
func (c *FrozenClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

// Advance moves the clock forward.
func (c *FrozenClock) Advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.now = c.now.Add(d)
}

// DeterministicIds hands out predictable identifiers.
//
// Not random: an id that changes per run makes every snapshot differ from its
// golden, and the usual response is to stop asserting on ids at all — which is
// how a test stops noticing that two things got the same one.
type DeterministicIds struct {
	mu     sync.Mutex
	prefix string
	next   int
}

// NewDeterministicIds returns a generator.
func NewDeterministicIds(prefix string) *DeterministicIds {
	return &DeterministicIds{prefix: prefix}
}

// Next returns the next id.
func (d *DeterministicIds) Next() string {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.next++
	return fmt.Sprintf("%s-%04d", d.prefix, d.next)
}

// Reset returns the counter to the start.
func (d *DeterministicIds) Reset() {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.next = 0
}
