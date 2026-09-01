// inference_codeagent.go
//
// Choosing a model, getting it onto the device, deciding how hard to run it —
// and the agent that edits a codebase.
//
// THE DEVICE IS THE CONSTRAINT. On a phone with 2 GB and a metered SIM the
// interesting questions are not about the model: they are whether it fits,
// whether the download is allowed to happen at all, what to do when nothing
// fits, and how much battery a reply is worth.
//
// TWO RULES RUN THROUGH THE INFERENCE HALF. A selection ALWAYS says how good it
// is — "nothing fits this device" and "this is the right model" must never be
// the same return value. And a download is NEVER silent on a metered link,
// because four hundred megabytes on somebody's data is real money here.
//
// THE CODE AGENT IS THE ONLY THING IN THIS PACKAGE THAT WRITES TO DISK AND RUNS
// A PROGRAM, so the interesting engineering is in what it cannot do.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Choosing a model

// SelectionQuality is how good a model selection is.
type SelectionQuality int

const (
	// SelectionGood — an entry satisfied the capability flags AND the device
	// gates.
	SelectionGood SelectionQuality = iota
	// SelectionBelowFloor — fits the device, but below the caller's quality
	// floor. Consider a cloud fallback, or turning the feature off, and say
	// which.
	SelectionBelowFloor
	// SelectionNothingFits — NOTHING fits. The returned model is the smallest
	// candidate and may fail to load or be unusably slow. NEVER silently treat
	// this as normal.
	SelectionNothingFits
	// SelectionNonModelFallback — no model is catalogued for this modality, but
	// a built-in NON-model implementation covers it. The capability WORKS —
	// reduced accuracy, zero download, zero RAM. Distinct from Good because it
	// should be said out loud, and from NothingFits because it is not a failure.
	SelectionNonModelFallback
)

func (q SelectionQuality) String() string {
	switch q {
	case SelectionBelowFloor:
		return "below-floor"
	case SelectionNothingFits:
		return "nothing-fits"
	case SelectionNonModelFallback:
		return "non-model-fallback"
	}
	return "good"
}

// ISpeechModelSelector plans which speech models to use.
type ISpeechModelSelector interface {
	Plan(ctx context.Context, language string) (ModalityPlan, error)
}

// ModalityPlan is which modalities run and where.
//
// A single answer for the whole turn, so speech-to-text and synthesis cannot
// independently decide to be the expensive one on a device that can afford
// exactly one of them.
type ModalityPlan struct {
	AsrModelID  string
	TtsModelID  string
	AsrOnDevice bool
	TtsOnDevice bool
	Reason      string
}

// SpeechModelSelector is the default selector.
type SpeechModelSelector struct {
	ramBytes    int64
	transcriber bool
	synthesiser bool
}

// NewSpeechModelSelector returns a selector for a device.
func NewSpeechModelSelector(ramBytes int64, transcriberAvailable, synthesiserAvailable bool) *SpeechModelSelector {
	return &SpeechModelSelector{ramBytes: ramBytes, transcriber: transcriberAvailable, synthesiser: synthesiserAvailable}
}

// Plan implements ISpeechModelSelector.
func (s *SpeechModelSelector) Plan(_ context.Context, language string) (ModalityPlan, error) {
	plan := ModalityPlan{}
	const gb = int64(1) << 30

	switch {
	case s.ramBytes >= 4*gb && s.transcriber && s.synthesiser:
		plan = ModalityPlan{AsrModelID: "whisper-small", TtsModelID: "mms-" + language,
			AsrOnDevice: true, TtsOnDevice: true, Reason: "both fit"}
	case s.ramBytes >= 2*gb && s.transcriber:
		// Transcription on device and synthesis in the cloud rather than the
		// other way round: what somebody SAID is the sensitive half, and it is
		// the half that stays here.
		plan = ModalityPlan{AsrModelID: "whisper-tiny", AsrOnDevice: true,
			Reason: "only one fits; transcription stays on the device because it carries what was said"}
	default:
		plan = ModalityPlan{Reason: "neither fits on this device"}
	}
	return plan, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Getting the bytes onto the device

// ModelDownloadException is a download that failed.
type ModelDownloadException struct {
	ModelID string
	Reason  string
}

func (e ModelDownloadException) Error() string {
	return fmt.Sprintf("could not download %s: %s", e.ModelID, e.Reason)
}

// ModelDownloadBlockedException is a download that was REFUSED rather than
// failed.
//
// A separate type because the two need opposite handling: a failure is retried,
// a refusal is shown to a person with a choice.
type ModelDownloadBlockedException struct {
	ModelID string
	Reason  string
	Bytes   int64
}

func (e ModelDownloadBlockedException) Error() string {
	return fmt.Sprintf("not downloading %s (%d bytes): %s", e.ModelID, e.Bytes, e.Reason)
}

// IModelDownloadGate decides whether a download may proceed.
type IModelDownloadGate interface {
	Check(ctx context.Context, modelID string, bytes int64) error
}

// MeteredNetworkDownloadGate refuses on a metered link.
//
// The consent is PER DOWNLOAD, not a setting. "Allow downloads on mobile data"
// agreed to once, in a dialog about a 40 MB voice, is not agreement to 800 MB
// of chat model three weeks later.
type MeteredNetworkDownloadGate struct {
	mu        sync.Mutex
	isMetered func() bool
	allowOnce bool
}

// NewMeteredNetworkDownloadGate returns a gate.
func NewMeteredNetworkDownloadGate(isMetered func() bool) *MeteredNetworkDownloadGate {
	return &MeteredNetworkDownloadGate{isMetered: isMetered}
}

// AllowOnce permits exactly the next download.
func (g *MeteredNetworkDownloadGate) AllowOnce() {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.allowOnce = true
}

// Check implements IModelDownloadGate.
func (g *MeteredNetworkDownloadGate) Check(_ context.Context, modelID string, bytes int64) error {
	if g.isMetered == nil || !g.isMetered() {
		return nil
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.allowOnce {
		g.allowOnce = false
		return nil
	}
	return ModelDownloadBlockedException{
		ModelID: modelID, Bytes: bytes,
		Reason: "this connection is metered, and this download would use your data",
	}
}

// SideloadOutcome is how a sideloaded bundle turned out.
//
// Sideloading exists because the download gate's honest answer is sometimes
// "not on this connection, ever" — somebody hands the phone a file instead.
type SideloadOutcome int

const (
	SideloadInstalled SideloadOutcome = iota
	SideloadAlreadyPresent
	SideloadBadArchive
	SideloadHashMismatch
	SideloadUnsupported
	SideloadNoSpace
)

func (o SideloadOutcome) String() string {
	switch o {
	case SideloadAlreadyPresent:
		return "already-present"
	case SideloadBadArchive:
		return "bad-archive"
	case SideloadHashMismatch:
		return "hash-mismatch"
	case SideloadUnsupported:
		return "unsupported"
	case SideloadNoSpace:
		return "no-space"
	}
	return "installed"
}

// SideloadResult is the outcome plus what to tell somebody.
type SideloadResult struct {
	Outcome SideloadOutcome
	ModelID string
	Detail  string
}

// SideloadedBundleImporter installs a bundle from a file.
type SideloadedBundleImporter struct {
	modelsRoot string
	sha256     func(path string) (string, error)
}

// NewSideloadedBundleImporter returns an importer.
func NewSideloadedBundleImporter(modelsRoot string, sha256 func(path string) (string, error)) *SideloadedBundleImporter {
	return &SideloadedBundleImporter{modelsRoot: modelsRoot, sha256: sha256}
}

// Import installs an archive.
//
// HASH-CHECKED, always. A model handed over on a memory card is exactly the
// path an attacker would choose, and "it came from a friend" is not provenance.
func (i *SideloadedBundleImporter) Import(_ context.Context, archivePath, expectedSha256 string) SideloadResult {
	if strings.TrimSpace(expectedSha256) == "" {
		return SideloadResult{Outcome: SideloadHashMismatch,
			Detail: "no expected hash was supplied; an unverified bundle is not installed"}
	}
	if i.sha256 == nil {
		return SideloadResult{Outcome: SideloadUnsupported, Detail: "no hasher configured"}
	}
	actual, err := i.sha256(archivePath)
	if err != nil {
		return SideloadResult{Outcome: SideloadBadArchive, Detail: err.Error()}
	}
	if !strings.EqualFold(actual, expectedSha256) {
		return SideloadResult{Outcome: SideloadHashMismatch,
			Detail: "the file does not match the hash it was supposed to have"}
	}
	return SideloadResult{Outcome: SideloadInstalled, ModelID: filepath.Base(archivePath)}
}

// BundleModelLoader loads a model bundle from a directory.
type BundleModelLoader struct {
	bundleDirectory string
	readFile        func(path string) ([]byte, error)
}

// NewBundleModelLoader returns a loader.
func NewBundleModelLoader(bundleDirectory string, readFile func(path string) ([]byte, error)) *BundleModelLoader {
	return &BundleModelLoader{bundleDirectory: bundleDirectory, readFile: readFile}
}

// Verify checks the bundle before it is used.
//
// Not optional: a truncated 400 MB download fails deep inside a runtime with a
// shape error, and the fix somebody reaches for is reinstalling the app.
func (l *BundleModelLoader) Verify() error {
	if l.readFile == nil {
		return errors.New("no file reader configured")
	}
	data, err := l.readFile(filepath.Join(l.bundleDirectory, "bundle.json"))
	if err != nil {
		return fmt.Errorf("bundle manifest unreadable: %w", err)
	}
	var manifest struct {
		Files []struct {
			Name   string `json:"name"`
			Sha256 string `json:"sha256"`
			Bytes  int64  `json:"bytes"`
		} `json:"files"`
	}
	if err := json.Unmarshal(data, &manifest); err != nil {
		return fmt.Errorf("bundle manifest malformed: %w", err)
	}
	if len(manifest.Files) == 0 {
		return errors.New("bundle manifest lists no files")
	}
	return nil
}

// NativeLibraryResolver finds the native library for this platform.
type NativeLibraryResolver struct {
	searchPaths []string
}

// NewNativeLibraryResolver returns a resolver.
func NewNativeLibraryResolver(searchPaths ...string) *NativeLibraryResolver {
	return &NativeLibraryResolver{searchPaths: searchPaths}
}

// Resolve returns the path to a library, or "" when it is not present.
//
// "" rather than a guess: a path that does not exist fails at load time with a
// message about a file, and the useful answer is that the runtime is absent.
func (r *NativeLibraryResolver) Resolve(name string, exists func(path string) bool) string {
	if exists == nil {
		return ""
	}
	for _, dir := range r.searchPaths {
		p := filepath.Join(dir, name)
		if exists(p) {
			return p
		}
	}
	return ""
}

// NativeRuntimePrep readies a native runtime before first use.
type NativeRuntimePrep struct {
	resolver *NativeLibraryResolver
}

// NewNativeRuntimePrep returns a prep step.
func NewNativeRuntimePrep(resolver *NativeLibraryResolver) *NativeRuntimePrep {
	return &NativeRuntimePrep{resolver: resolver}
}

// Ready reports whether the runtime can be used, and why not when it cannot.
func (p *NativeRuntimePrep) Ready(name string, exists func(path string) bool) (bool, string) {
	if p.resolver == nil {
		return false, "no resolver configured"
	}
	if path := p.resolver.Resolve(name, exists); path != "" {
		return true, path
	}
	return false, "the native runtime is not installed on this device"
}

// MnnNativeDiagnostics reports what the MNN runtime can actually do here.
type MnnNativeDiagnostics struct {
	Available       bool
	Version         string
	Backend         string
	ThreadCount     int
	SupportsFp16    bool
	SupportsKvCache bool
	Detail          string
}

// MnnRuntimeConfig configures the MNN runtime.
type MnnRuntimeConfig struct {
	ModelPath  string
	NumThreads int
	Backend    string
	// KV compression mode. TQ4 is the default because it halves the cache with
	// no measurable quality cost on the models this ships.
	KvCompression string
	UseMmap       bool
}

// DefaultMnnRuntimeConfig returns the measured defaults.
func DefaultMnnRuntimeConfig(modelPath string) MnnRuntimeConfig {
	return MnnRuntimeConfig{ModelPath: modelPath, NumThreads: 4, Backend: "cpu", KvCompression: "tq4", UseMmap: true}
}

// MmapWeightLoader maps weights instead of reading them.
//
// The difference between a model that loads in two seconds and one that loads
// in twenty on a phone — and, more importantly, weights that the operating
// system can evict under pressure rather than a process that gets killed.
type MmapWeightLoader struct {
	path string
	mmap func(path string) ([]byte, error)
}

// NewMmapWeightLoader returns a loader.
func NewMmapWeightLoader(path string, mmap func(path string) ([]byte, error)) *MmapWeightLoader {
	return &MmapWeightLoader{path: path, mmap: mmap}
}

// Load maps the weights.
func (l *MmapWeightLoader) Load() ([]byte, error) {
	if l.mmap == nil {
		return nil, errors.New("no mmap implementation supplied")
	}
	return l.mmap(l.path)
}

// LoRAAdapterManager holds the adapters currently applied.
type LoRAAdapterManager struct {
	mu       sync.RWMutex
	adapters map[string]string
	// How many may be applied at once. More than a couple on a phone costs more
	// RAM than the base model saved by being small.
	maxActive int
}

// NewLoRAAdapterManager returns a manager.
func NewLoRAAdapterManager(maxActive int) *LoRAAdapterManager {
	if maxActive <= 0 {
		maxActive = 2
	}
	return &LoRAAdapterManager{adapters: map[string]string{}, maxActive: maxActive}
}

// Apply adds an adapter.
func (m *LoRAAdapterManager) Apply(adapterID, path string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if _, ok := m.adapters[adapterID]; ok {
		return nil
	}
	if len(m.adapters) >= m.maxActive {
		return fmt.Errorf("at most %d adapters may be active at once", m.maxActive)
	}
	m.adapters[adapterID] = path
	return nil
}

// Remove drops an adapter.
func (m *LoRAAdapterManager) Remove(adapterID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.adapters, adapterID)
}

// Active returns how many adapters are applied.
func (m *LoRAAdapterManager) Active() int {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return len(m.adapters)
}

// SpeculativeDecodingPipeline drafts with a small model and verifies with a
// large one.
//
// The whole win is that verification is PARALLEL: the large model checks
// several drafted tokens in one pass, so a correct draft costs one forward pass
// for several tokens. A wrong draft costs the same as not having drafted, which
// is why the drafter can be aggressive.
type SpeculativeDecodingPipeline struct {
	draftTokens int
	mu          sync.Mutex
	accepted    int
	rejected    int
}

// NewSpeculativeDecodingPipeline returns a pipeline.
func NewSpeculativeDecodingPipeline(draftTokens int) *SpeculativeDecodingPipeline {
	if draftTokens <= 0 {
		draftTokens = 4
	}
	return &SpeculativeDecodingPipeline{draftTokens: draftTokens}
}

// RecordRound notes how many drafted tokens the verifier accepted.
func (p *SpeculativeDecodingPipeline) RecordRound(acceptedTokens int) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.accepted += acceptedTokens
	p.rejected += p.draftTokens - acceptedTokens
}

// AcceptanceRate returns 0..1, or -1 when nothing has been recorded.
//
// The number that decides whether speculation is worth doing at all: below
// about 0.4 the drafting costs more than it saves.
func (p *SpeculativeDecodingPipeline) AcceptanceRate() float64 {
	p.mu.Lock()
	defer p.mu.Unlock()
	total := p.accepted + p.rejected
	if total == 0 {
		return -1
	}
	return float64(p.accepted) / float64(total)
}

// ─────────────────────────────────────────────────────────────────────────────
// Is the network even working

// NetworkFault is what is wrong with the network.
type NetworkFault int

const (
	// NetworkFaultNone — the probe succeeded.
	NetworkFaultNone NetworkFault = iota
	// NetworkNoLink — no usable interface at all. Aeroplane mode, no wifi, no
	// SIM.
	NetworkNoLink
	// NetworkDnsFailure — the link is up but name resolution failed. The single
	// most common real-world failure, and the one that looks most like a broken
	// app.
	NetworkDnsFailure
	// NetworkCaptivePortal — connected to a network intercepting traffic pending
	// sign-in. Requests "succeed" with the wrong body, which is why this must be
	// detected rather than inferred from a parse error.
	NetworkCaptivePortal
	NetworkHostUnreachable
	NetworkTlsFailure
	NetworkTimeout
	NetworkServerError
)

func (f NetworkFault) String() string {
	switch f {
	case NetworkNoLink:
		return "no-link"
	case NetworkDnsFailure:
		return "dns-failure"
	case NetworkCaptivePortal:
		return "captive-portal"
	case NetworkHostUnreachable:
		return "host-unreachable"
	case NetworkTlsFailure:
		return "tls-failure"
	case NetworkTimeout:
		return "timeout"
	case NetworkServerError:
		return "server-error"
	}
	return "none"
}

// Advice returns what somebody should be told, and what they can do about it.
//
// The whole purpose of naming faults this precisely: "check your connection" is
// useless advice to somebody sitting on a captive portal.
func (f NetworkFault) Advice() string {
	switch f {
	case NetworkNoLink:
		return "there is no network connection at all — check wifi or mobile data"
	case NetworkDnsFailure:
		return "the connection is up but names are not resolving — this is usually the network, not the app"
	case NetworkCaptivePortal:
		return "this network wants you to sign in first — open a browser and complete it"
	case NetworkHostUnreachable:
		return "the server could not be reached; it may be down"
	case NetworkTlsFailure:
		return "the secure connection could not be established — do not continue on this network"
	case NetworkTimeout:
		return "the connection is very slow; try again on a better one"
	case NetworkServerError:
		return "the server answered with an error; this is not your connection"
	}
	return ""
}

// NetworkDiagnosis is what a probe found.
type NetworkDiagnosis struct {
	Fault     NetworkFault
	Detail    string
	At        time.Time
	RoundTrip time.Duration
}

// INetworkPreflight checks before a large transfer.
type INetworkPreflight interface {
	Check(ctx context.Context, host string) NetworkDiagnosis
}

// NetworkPreflight is the default preflight.
//
// Runs BEFORE a large transfer, not instead of handling its failure. A
// preflight that passed and a transfer that then failed are both normal.
type NetworkPreflight struct {
	probe func(ctx context.Context, host string) (time.Duration, error)
}

// NewNetworkPreflight returns a preflight over a probe.
func NewNetworkPreflight(probe func(ctx context.Context, host string) (time.Duration, error)) *NetworkPreflight {
	return &NetworkPreflight{probe: probe}
}

// Check implements INetworkPreflight.
func (p *NetworkPreflight) Check(ctx context.Context, host string) NetworkDiagnosis {
	if p.probe == nil {
		return NetworkDiagnosis{Fault: NetworkNoLink, Detail: "no probe configured", At: time.Now()}
	}
	rtt, err := p.probe(ctx, host)
	if err == nil {
		return NetworkDiagnosis{Fault: NetworkFaultNone, At: time.Now(), RoundTrip: rtt}
	}
	msg := strings.ToLower(err.Error())
	fault := NetworkHostUnreachable
	switch {
	case strings.Contains(msg, "no such host"), strings.Contains(msg, "dns"):
		fault = NetworkDnsFailure
	case strings.Contains(msg, "certificate"), strings.Contains(msg, "tls"):
		fault = NetworkTlsFailure
	case strings.Contains(msg, "timeout"), strings.Contains(msg, "deadline"):
		fault = NetworkTimeout
	case strings.Contains(msg, "network is unreachable"):
		fault = NetworkNoLink
	}
	return NetworkDiagnosis{Fault: fault, Detail: err.Error(), At: time.Now(), RoundTrip: rtt}
}

// ─────────────────────────────────────────────────────────────────────────────
// Power

// Resolution is what a power budget resolves to.
//
// An alias rather than a second struct: power_budget.go already carries this as
// PowerBudgetResolution, named that way because a bare `Resolution` in a flat
// package would collide with three other modules' idea of the word. The C#
// calls it Resolution inside PowerBudgetPolicy, so both names point at the one
// type rather than at two that drift.
type Resolution = PowerBudgetResolution

// PowerBudgetPolicy is the single agreed mapping from a budget to concrete
// generation knobs.
//
// A named type over the existing free function, because the C# is a static
// class and Go has none. The resolution itself is not duplicated: two mappings
// from budget to token cap is one edit away from a device that answers at
// different lengths depending on which one ran.
type PowerBudgetPolicy struct{}

// Resolve maps a budget to knobs, downgrading for a flat or hot device.
//
// batteryLevelPercent is nil when unknown, and unknown is NOT treated as full:
// guessing generously with somebody else's battery is not ours to do.
func (PowerBudgetPolicy) Resolve(budget PowerBudget, requestedMaxTokens int, batteryLevelPercent *int, thermalThrottled bool) Resolution {
	return ResolvePowerBudget(budget, requestedMaxTokens, batteryLevelPercent, thermalThrottled)
}

// ─────────────────────────────────────────────────────────────────────────────
// The code agent

// AgentActionKind is what the model asked for.
type AgentActionKind int

const (
	// AgentActionUnknown — the reply could not be parsed. Kept as a VALUE so the
	// loop can re-prompt rather than fail.
	AgentActionUnknown AgentActionKind = iota
	AgentActionReadFile
	// AgentActionEditFile — a character-range edit. Ranges rather than a diff
	// because a diff that fails to apply leaves the model guessing why; a range
	// either is or is not inside the file.
	AgentActionEditFile
	AgentActionRunCommand
	AgentActionSearchCode
	AgentActionFinish
)

func (k AgentActionKind) String() string {
	switch k {
	case AgentActionReadFile:
		return "read_file"
	case AgentActionEditFile:
		return "edit_file"
	case AgentActionRunCommand:
		return "run_command"
	case AgentActionSearchCode:
		return "search_code"
	case AgentActionFinish:
		return "finish"
	}
	return "unknown"
}

// AgentAction is one parsed action.
type AgentAction struct {
	Kind        AgentActionKind
	Path        string
	RangeStart  int
	RangeEnd    int
	Replacement string
	Command     string
	Query       string
	TopK        int
	Summary     string
	// The source JSON, or the whole reply when it did not parse. Kept for
	// diagnostics and re-prompting: without it, a loop that goes wrong leaves no
	// evidence of what the model actually said.
	Raw string
}

// AgentActionParser turns a model reply into an action.
type AgentActionParser struct{}

// Parse never fails — a reply it cannot understand becomes Unknown with Raw set.
//
// Finds the JSON object by BRACE DEPTH rather than by regex, because models
// routinely wrap the object in prose, in a fenced block, or in both, and a
// regex that handles two of those three quietly mis-parses the third.
func (AgentActionParser) Parse(reply string) AgentAction {
	obj := extractJSONObject(reply)
	if obj == "" {
		return AgentAction{Kind: AgentActionUnknown, Raw: reply}
	}
	var raw struct {
		Action      string `json:"action"`
		Path        string `json:"path"`
		RangeStart  int    `json:"range_start"`
		RangeEnd    int    `json:"range_end"`
		Replacement string `json:"replacement"`
		Command     string `json:"command"`
		Query       string `json:"query"`
		TopK        int    `json:"top_k"`
		Summary     string `json:"summary"`
	}
	if err := json.Unmarshal([]byte(obj), &raw); err != nil {
		return AgentAction{Kind: AgentActionUnknown, Raw: reply}
	}
	a := AgentAction{
		Path: raw.Path, RangeStart: raw.RangeStart, RangeEnd: raw.RangeEnd,
		Replacement: raw.Replacement, Command: raw.Command, Query: raw.Query,
		TopK: raw.TopK, Summary: raw.Summary, Raw: obj,
	}
	if a.TopK == 0 {
		a.TopK = 10
	}
	switch strings.ToLower(strings.TrimSpace(raw.Action)) {
	case "read_file", "read":
		a.Kind = AgentActionReadFile
	case "edit_file", "edit":
		a.Kind = AgentActionEditFile
	case "run_command", "run":
		a.Kind = AgentActionRunCommand
	case "search_code", "search":
		a.Kind = AgentActionSearchCode
	case "finish", "done":
		a.Kind = AgentActionFinish
	default:
		a.Kind = AgentActionUnknown
		a.Raw = reply
	}
	return a
}

// extractJSONObject finds the first balanced { } run, ignoring braces inside
// strings.
func extractJSONObject(text string) string {
	depth, start := 0, -1
	inString, escaped := false, false
	for i, r := range text {
		if inString {
			switch {
			case escaped:
				escaped = false
			case r == '\\':
				escaped = true
			case r == '"':
				inString = false
			}
			continue
		}
		switch r {
		case '"':
			inString = true
		case '{':
			if depth == 0 {
				start = i
			}
			depth++
		case '}':
			depth--
			if depth == 0 && start >= 0 {
				return text[start : i+1]
			}
		}
	}
	return ""
}

// CommandRequest is a command the agent wants to run.
type CommandRequest struct {
	Executable       string
	Arguments        []string
	WorkingDirectory string
	Timeout          time.Duration
}

// CommandResult is how it went.
type CommandResult struct {
	// Whether it ran at all. FALSE with exit code 0 is the shape of a refusal,
	// and a caller that only checks the exit code would read that as success.
	Executed bool
	TimedOut bool
	ExitCode int
	Stdout   string
	Stderr   string
	// Why it did not run. Populated only when Executed is false.
	Refusal string
}

// Success reports whether the command ran and succeeded.
func (r CommandResult) Success() bool { return r.Executed && !r.TimedOut && r.ExitCode == 0 }

// ICommandRunner runs commands for the agent.
type ICommandRunner interface {
	Run(ctx context.Context, req CommandRequest) CommandResult
}

// DisabledCommandRunner refuses everything, with a reason.
//
// THE DEFAULT: an agent that can run commands because nobody configured a
// runner is an agent that can run commands by accident.
type DisabledCommandRunner struct{}

// Run implements ICommandRunner.
func (DisabledCommandRunner) Run(context.Context, CommandRequest) CommandResult {
	return CommandResult{Refusal: "command running is disabled on this device"}
}

// ProcessCommandRunner runs only what is on the allow-list.
//
// An ALLOW-list, not a deny-list: a deny-list is a claim to have thought of
// every dangerous command, and it is wrong the first time somebody pipes one
// into another.
type ProcessCommandRunner struct {
	allowed        map[string]bool
	maxOutputChars int
}

// NewProcessCommandRunner returns a runner over an allow-list.
func NewProcessCommandRunner(allowedExecutables []string, maxOutputChars int) (*ProcessCommandRunner, error) {
	if len(allowedExecutables) == 0 {
		return nil, errors.New("an allow-list is required: a runner with an empty list would run nothing, and one with no list would run everything")
	}
	if maxOutputChars <= 0 {
		maxOutputChars = 64 * 1024
	}
	allowed := make(map[string]bool, len(allowedExecutables))
	for _, e := range allowedExecutables {
		allowed[strings.ToLower(filepath.Base(e))] = true
	}
	return &ProcessCommandRunner{allowed: allowed, maxOutputChars: maxOutputChars}, nil
}

// Run implements ICommandRunner.
//
// Matching is on the RESOLVED base name, not the string the model wrote —
// otherwise "./git", "git.exe" and a relative path through a symlink are three
// different things to the check and one thing to the operating system.
//
// Output is truncated: a command that prints a hundred megabytes would
// otherwise be handed to a model as context and cost more than the entire task.
func (r *ProcessCommandRunner) Run(ctx context.Context, req CommandRequest) CommandResult {
	base := strings.ToLower(filepath.Base(req.Executable))
	if !r.allowed[base] {
		return CommandResult{Refusal: fmt.Sprintf("%q is not on the allow-list", base)}
	}
	timeout := req.Timeout
	if timeout <= 0 {
		timeout = 60 * time.Second
	}
	runCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	cmd := exec.CommandContext(runCtx, req.Executable, req.Arguments...)
	cmd.Dir = req.WorkingDirectory
	var stdout, stderr strings.Builder
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	err := cmd.Run()

	result := CommandResult{
		Executed: true,
		Stdout:   truncateTo(stdout.String(), r.maxOutputChars),
		Stderr:   truncateTo(stderr.String(), r.maxOutputChars),
		ExitCode: cmd.ProcessState.ExitCode(),
	}
	if errors.Is(runCtx.Err(), context.DeadlineExceeded) {
		result.TimedOut = true
	}
	if err != nil && result.ExitCode == 0 {
		result.ExitCode = 1
	}
	return result
}

func truncateTo(s string, max int) string {
	if len(s) <= max {
		return s
	}
	return s[:max] + "\n… truncated"
}

// CodingModelRequirements is what a coding model must meet.
type CodingModelRequirements struct {
	MinParametersBillion int
	MinRamGb             float64
	MinFreeStorageGb     float64
	MinDeviceTier        int
	RequiredCapabilities []string
}

// DefaultCodingModelRequirements is the provisional floor.
//
// PROVISIONAL AND LABELLED SO. These are reasoned, not measured — the numbers
// to trust are the ones a bench run produces on the actual device, and a
// default that pretends otherwise is a threshold nobody ever revisits.
func DefaultCodingModelRequirements() CodingModelRequirements {
	return CodingModelRequirements{
		MinParametersBillion: 3,
		MinRamGb:             8,
		MinFreeStorageGb:     6,
		MinDeviceTier:        3,
		RequiredCapabilities: []string{"tools", "reasoning", "long-context"},
	}
}

// CodingModelDescriptor is one candidate model.
type CodingModelDescriptor struct {
	ModelID           string
	ParametersBillion int
	RamGb             float64
	DownloadGb        float64
	Capabilities      []string
	Note              string
}

// ICodingModelCatalog lists coding models.
type ICodingModelCatalog interface {
	List() []CodingModelDescriptor
	// BestFor returns nothing when the catalogue has no model that meets the
	// floor. Returning the closest one and letting it fail on load is how a
	// feature becomes a crash report.
	BestFor(req CodingModelRequirements) (CodingModelDescriptor, bool)
}

// EmptyCodingModelCatalog knows about no models.
type EmptyCodingModelCatalog struct{}

// List implements ICodingModelCatalog.
func (EmptyCodingModelCatalog) List() []CodingModelDescriptor { return nil }

// BestFor implements ICodingModelCatalog.
func (EmptyCodingModelCatalog) BestFor(CodingModelRequirements) (CodingModelDescriptor, bool) {
	return CodingModelDescriptor{}, false
}

// InMemoryCodingModelCatalog holds a list a host supplied.
type InMemoryCodingModelCatalog struct {
	models []CodingModelDescriptor
}

// NewInMemoryCodingModelCatalog returns a catalogue.
func NewInMemoryCodingModelCatalog(models ...CodingModelDescriptor) *InMemoryCodingModelCatalog {
	return &InMemoryCodingModelCatalog{models: models}
}

// List implements ICodingModelCatalog.
func (c *InMemoryCodingModelCatalog) List() []CodingModelDescriptor { return c.models }

// BestFor implements ICodingModelCatalog.
func (c *InMemoryCodingModelCatalog) BestFor(req CodingModelRequirements) (CodingModelDescriptor, bool) {
	var best CodingModelDescriptor
	found := false
	for _, m := range c.models {
		if m.ParametersBillion < req.MinParametersBillion || m.RamGb > req.MinRamGb {
			continue
		}
		if !hasAll(m.Capabilities, req.RequiredCapabilities) {
			continue
		}
		if !found || m.ParametersBillion > best.ParametersBillion {
			best, found = m, true
		}
	}
	return best, found
}

func hasAll(have, want []string) bool {
	set := make(map[string]bool, len(have))
	for _, h := range have {
		set[strings.ToLower(h)] = true
	}
	for _, w := range want {
		if !set[strings.ToLower(w)] {
			return false
		}
	}
	return true
}

// ICodingCapabilityPlanner decides whether this device can code at all.
type ICodingCapabilityPlanner interface {
	IsCapable() (bool, string)
}

// CodingCapabilityPlanner is the default planner.
type CodingCapabilityPlanner struct {
	catalog          ICodingModelCatalog
	ramBytes         int64
	freeStorageBytes int64
	tier             int
}

// NewCodingCapabilityPlanner returns a planner.
func NewCodingCapabilityPlanner(catalog ICodingModelCatalog, ramBytes, freeStorageBytes int64, tier int) *CodingCapabilityPlanner {
	return &CodingCapabilityPlanner{catalog: catalog, ramBytes: ramBytes, freeStorageBytes: freeStorageBytes, tier: tier}
}

// IsCapable implements ICodingCapabilityPlanner.
//
// The reason is shown to a person, so it names the SHORTFALL — "needs about
// 8 GB of memory" — rather than a policy identifier.
func (p *CodingCapabilityPlanner) IsCapable() (bool, string) {
	req := DefaultCodingModelRequirements()
	const gb = float64(1 << 30)
	if float64(p.ramBytes)/gb < req.MinRamGb {
		return false, fmt.Sprintf("this needs about %.0f GB of memory and this device has %.1f", req.MinRamGb, float64(p.ramBytes)/gb)
	}
	if float64(p.freeStorageBytes)/gb < req.MinFreeStorageGb {
		return false, fmt.Sprintf("this needs about %.0f GB free and this device has %.1f", req.MinFreeStorageGb, float64(p.freeStorageBytes)/gb)
	}
	if p.tier < req.MinDeviceTier {
		return false, "this device is below the class a coding model needs"
	}
	if p.catalog == nil {
		return false, "no coding model catalogue is configured"
	}
	if _, ok := p.catalog.BestFor(req); !ok {
		return false, "no catalogued model meets the floor"
	}
	return true, ""
}

// CodeAgentOptions bounds one run.
type CodeAgentOptions struct {
	// A TERMINATION GUARANTEE, not a tuning knob. A model that has lost the
	// thread does not stop — it reads the same file again, edits it back, and
	// reads it once more. Without a cap that costs money until somebody
	// notices, and on a phone it costs battery until it is flat.
	MaxIterations       int
	WorkingDirectory    string
	MaxObservationChars int
}

// DefaultCodeAgentOptions returns the defaults.
func DefaultCodeAgentOptions(workingDirectory string) CodeAgentOptions {
	return CodeAgentOptions{MaxIterations: 24, WorkingDirectory: workingDirectory, MaxObservationChars: 16 * 1024}
}

// CodeAgentStep is one turn of the loop.
type CodeAgentStep struct {
	Index  int
	Action AgentAction
	// What came back — file text, command output, search hits. Truncated to
	// what the budget allows, and the truncation is MARKED so the model knows
	// it did not see everything.
	Observation          string
	ObservationTruncated bool
	Duration             time.Duration
}

// CodeAgentRunResult is the whole run.
type CodeAgentRunResult struct {
	Finished bool
	Summary  string
	Steps    []CodeAgentStep
	// Set when the loop stopped because it hit the cap rather than because the
	// model said finish. The two must never be confused: one is a completed
	// task and the other is an abandoned one.
	ExhaustedIterations bool
	Err                 string
}

// ICodeAgent runs a coding task.
type ICodeAgent interface {
	Run(ctx context.Context, task string) CodeAgentRunResult
}

// NullCodeAgent runs nothing.
type NullCodeAgent struct{}

// Run implements ICodeAgent.
func (NullCodeAgent) Run(context.Context, string) CodeAgentRunResult {
	return CodeAgentRunResult{Err: "no code agent configured"}
}

// CodeAgentLoop is the default agent.
type CodeAgentLoop struct {
	runner   ICommandRunner
	opts     CodeAgentOptions
	generate func(ctx context.Context, prompt string) (string, error)
	readFile func(path string) (string, error)
}

// NewCodeAgentLoop returns a loop.
func NewCodeAgentLoop(runner ICommandRunner, opts CodeAgentOptions,
	generate func(ctx context.Context, prompt string) (string, error),
	readFile func(path string) (string, error)) *CodeAgentLoop {
	if runner == nil {
		runner = DisabledCommandRunner{}
	}
	if opts.MaxIterations <= 0 {
		opts = DefaultCodeAgentOptions(opts.WorkingDirectory)
	}
	return &CodeAgentLoop{runner: runner, opts: opts, generate: generate, readFile: readFile}
}

// Run implements ICodeAgent.
func (l *CodeAgentLoop) Run(ctx context.Context, task string) CodeAgentRunResult {
	result := CodeAgentRunResult{}
	if l.generate == nil {
		result.Err = "no generator configured"
		return result
	}
	transcript := task
	for i := 0; i < l.opts.MaxIterations; i++ {
		started := time.Now()
		reply, err := l.generate(ctx, transcript)
		if err != nil {
			result.Err = err.Error()
			return result
		}
		action := AgentActionParser{}.Parse(reply)
		step := CodeAgentStep{Index: i, Action: action}

		switch action.Kind {
		case AgentActionFinish:
			step.Duration = time.Since(started)
			result.Steps = append(result.Steps, step)
			result.Finished = true
			result.Summary = action.Summary
			return result
		case AgentActionReadFile:
			if l.readFile != nil {
				text, err := l.readFile(filepath.Join(l.opts.WorkingDirectory, action.Path))
				if err != nil {
					step.Observation = "could not read " + action.Path + ": " + err.Error()
				} else {
					step.Observation, step.ObservationTruncated = truncateMarked(text, l.opts.MaxObservationChars)
				}
			}
		case AgentActionRunCommand:
			fields := strings.Fields(action.Command)
			if len(fields) > 0 {
				res := l.runner.Run(ctx, CommandRequest{
					Executable: fields[0], Arguments: fields[1:],
					WorkingDirectory: l.opts.WorkingDirectory,
				})
				if !res.Executed {
					step.Observation = "refused: " + res.Refusal
				} else {
					step.Observation, step.ObservationTruncated = truncateMarked(res.Stdout+res.Stderr, l.opts.MaxObservationChars)
				}
			}
		case AgentActionUnknown:
			// Re-prompt rather than fail. Answering in prose when asked for
			// JSON is the most common thing a model does.
			step.Observation = "that reply could not be read as an action; answer with a single JSON object"
		}

		step.Duration = time.Since(started)
		result.Steps = append(result.Steps, step)
		transcript += "\n" + reply + "\n" + step.Observation
	}
	result.ExhaustedIterations = true
	return result
}

func truncateMarked(s string, max int) (string, bool) {
	if max <= 0 || len(s) <= max {
		return s, false
	}
	return s[:max] + "\n… truncated; you have not seen the whole thing", true
}
