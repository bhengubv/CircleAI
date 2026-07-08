// inference_server_test.go
//
// Verifies the CircleAI.Inference.Server ports:
//   - LocalProcessInferenceBridge over a LocalChatGenerator: Complete, streaming,
//     model-mismatch failure, device caps (LocalProcessInferenceBridge.cs).
//   - InferenceServerModelRegistry register/resolve/deregister (ModelRegistry.cs).
//   - ModelLifecycleManager admission gate: load/already-loaded/insufficient-RAM/
//     insufficient-VRAM/factory-failure, unload, totals (ModelLifecycleManager.cs).
//   - LocalInferenceBridgeFactory composition + UnconfiguredBridgeFactory tombstone
//     + NativeRuntimeStatus (bridge factory + native status ports).
//   - ApiKeyAuthHandler enabled/disabled/no-key/bad-key (ApiKeyAuthHandler.cs).
//   - InferenceServerHandlers routing: chat (non-stream + SSE), embeddings, admin
//     load/unload/lifecycle, companion turn, auth + admission gates (endpoints).

package circleai_test

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── test doubles ──────────────────────────────────────────────────────────────

// fakeEmbedder returns a fixed-length vector derived from the input length.
type fakeEmbedder struct{}

func (fakeEmbedder) Generate(_ context.Context, text string) ([]float32, error) {
	return []float32{float32(len(text)), 1, 2}, nil
}

// authOK builds a successful auth result for handler tests.
func authOK() circleai.AuthResult {
	return circleai.AuthResult{Outcome: circleai.AuthSuccess, Principal: &circleai.AuthPrincipal{Name: "t"}}
}

func newBridge(t *testing.T, modelID string, responder circleai.LocalResponder) circleai.IInferenceBridge {
	t.Helper()
	var opts []circleai.LocalChatGeneratorOption
	if responder != nil {
		opts = append(opts, circleai.WithResponder(responder))
	}
	gen, err := circleai.NewLocalChatGenerator(modelID+".gguf", 4096, opts...)
	if err != nil {
		t.Fatalf("generator: %v", err)
	}
	bridge, err := circleai.NewLocalProcessInferenceBridge(gen,
		circleai.ModelDescriptor{ModelID: modelID, Version: "1.0", ContextWindowTokens: 4096},
		circleai.DeviceCapabilities{OsName: "test", CpuCoreCount: 4})
	if err != nil {
		t.Fatalf("bridge: %v", err)
	}
	return bridge
}

// ── LocalProcessInferenceBridge ───────────────────────────────────────────────

func TestLocalProcessInferenceBridge_Complete(t *testing.T) {
	ctx := context.Background()
	bridge := newBridge(t, "qwen", func(string, []circleai.ChatMessage, bool) string {
		return "<think>reasoning here</think>the answer"
	})

	req, _ := circleai.NewInferenceRequest("qwen", "hello", 128, 0.7, 0.9)
	resp, err := bridge.Complete(ctx, req)
	if err != nil {
		t.Fatalf("complete: %v", err)
	}
	if resp.OutputText != "the answer" {
		t.Errorf("output text: got %q", resp.OutputText)
	}
	if resp.ReasoningText != "reasoning here" {
		t.Errorf("reasoning text: got %q", resp.ReasoningText)
	}
	if resp.Status != circleai.InferenceStatusCompleted {
		t.Errorf("status: got %v", resp.Status)
	}
	if resp.PromptTokenCount <= 0 || resp.OutputTokenCount <= 0 {
		t.Errorf("token counts should be positive: %d/%d", resp.PromptTokenCount, resp.OutputTokenCount)
	}

	// Loaded/list checks.
	loaded, _ := bridge.IsModelLoaded(ctx, "qwen")
	if !loaded {
		t.Error("qwen should be loaded")
	}
	other, _ := bridge.IsModelLoaded(ctx, "other")
	if other {
		t.Error("other should not be loaded")
	}
	models, _ := bridge.ListLoadedModels(ctx)
	if len(models) != 1 || models[0].ModelID != "qwen" {
		t.Errorf("list loaded models wrong: %+v", models)
	}

	caps, _ := bridge.GetDeviceCapabilities(ctx)
	if !caps.HasTransportLayerEncryption {
		t.Error("in-process bridge should report transport encryption true")
	}
}

func TestLocalProcessInferenceBridge_ModelMismatch(t *testing.T) {
	bridge := newBridge(t, "qwen", nil)
	req, _ := circleai.NewInferenceRequest("wrong-model", "hi", 64, 0.7, 0.9)
	resp, _ := bridge.Complete(context.Background(), req)
	if resp.Status != circleai.InferenceStatusFailed {
		t.Errorf("mismatch should fail, got %v", resp.Status)
	}
	if !strings.Contains(resp.FailureMessage, "not loaded by this bridge") {
		t.Errorf("failure message: got %q", resp.FailureMessage)
	}
}

func TestLocalProcessInferenceBridge_Stream(t *testing.T) {
	bridge := newBridge(t, "qwen", func(string, []circleai.ChatMessage, bool) string { return "one two three" })
	req, _ := circleai.NewInferenceRequest("qwen", "hi", 128, 0.7, 0.9)
	chunks, errs := bridge.StreamCompletion(context.Background(), req)
	var sb strings.Builder
	for c := range chunks {
		sb.WriteString(c)
	}
	if err := <-errs; err != nil {
		t.Fatalf("stream err: %v", err)
	}
	if strings.TrimSpace(sb.String()) != "one two three" {
		t.Errorf("stream mismatch: got %q", sb.String())
	}
}

// ── model registry ────────────────────────────────────────────────────────────

func TestInferenceServerModelRegistry(t *testing.T) {
	reg := circleai.NewInferenceServerModelRegistry()
	bridge := newBridge(t, "m1", nil)
	if err := reg.Register("m1", bridge); err != nil {
		t.Fatalf("register: %v", err)
	}
	if err := reg.RegisterEmbedder("e1", fakeEmbedder{}); err != nil {
		t.Fatalf("register embedder: %v", err)
	}
	if reg.Resolve("m1") == nil {
		t.Error("m1 bridge should resolve")
	}
	if reg.ResolveEmbedder("e1") == nil {
		t.Error("e1 embedder should resolve")
	}
	if reg.Resolve("nope") != nil {
		t.Error("unknown model should resolve nil")
	}
	all := reg.AllModelIDs()
	if len(all) != 2 {
		t.Errorf("AllModelIDs should be 2, got %v", all)
	}
	if got := reg.ChatModelIDs(); len(got) != 1 || got[0] != "m1" {
		t.Errorf("ChatModelIDs wrong: %v", got)
	}
	if !reg.Deregister("m1") {
		t.Error("deregister m1 should return true")
	}
	if reg.Resolve("m1") != nil {
		t.Error("m1 should be gone after deregister")
	}
	// Guards.
	if err := reg.Register("", bridge); err == nil {
		t.Error("empty modelId should error")
	}
}

// ── lifecycle manager ─────────────────────────────────────────────────────────

func lifecycleFixture(t *testing.T, vram, ram int64) (*circleai.ModelLifecycleManager, *circleai.InferenceServerModelRegistry) {
	t.Helper()
	reg := circleai.NewInferenceServerModelRegistry()
	probe := circleai.StaticServerCapabilityProbe{Profile: circleai.ServerHostProfile{
		GpuVramBytes: vram, TotalPhysicalMemoryBytes: ram,
	}}
	mgr, err := circleai.NewModelLifecycleManager(reg, probe)
	if err != nil {
		t.Fatalf("mgr ctor: %v", err)
	}
	return mgr, reg
}

func TestModelLifecycleManager_LoadUnload(t *testing.T) {
	ctx := context.Background()
	mgr, reg := lifecycleFixture(t, 8<<30, 16<<30)
	bridge := newBridge(t, "qwen", nil)

	desc := circleai.ModelLoadDescriptor{
		ModelID: "qwen", Backend: circleai.BackendCpu, RequestedTier: circleai.CapabilityTier1Small,
		RamRequiredBytes: 2 << 30,
		BridgeFactory:    func(context.Context) (circleai.IInferenceBridge, error) { return bridge, nil },
	}
	res, err := mgr.Load(ctx, desc)
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	if res.Outcome != circleai.LoadOutcomeLoaded {
		t.Fatalf("expected Loaded, got %v (%s)", res.Outcome, res.Rationale)
	}
	if reg.Resolve("qwen") == nil {
		t.Error("bridge should be registered after load")
	}
	if mgr.TotalAllocatedRamBytes() != 2<<30 {
		t.Errorf("RAM accounting: got %d", mgr.TotalAllocatedRamBytes())
	}

	// Idempotent re-load.
	res2, _ := mgr.Load(ctx, desc)
	if res2.Outcome != circleai.LoadOutcomeAlreadyLoaded {
		t.Errorf("re-load should be AlreadyLoaded, got %v", res2.Outcome)
	}
	if len(mgr.List()) != 1 {
		t.Errorf("list should have 1 model, got %d", len(mgr.List()))
	}

	// Unload.
	out, _ := mgr.Unload(ctx, "qwen")
	if out != circleai.UnloadOutcomeUnloaded {
		t.Errorf("unload: got %v", out)
	}
	if reg.Resolve("qwen") != nil {
		t.Error("bridge should be deregistered after unload")
	}
	again, _ := mgr.Unload(ctx, "qwen")
	if again != circleai.UnloadOutcomeNotLoaded {
		t.Errorf("second unload: got %v", again)
	}
}

func TestModelLifecycleManager_InsufficientRam(t *testing.T) {
	ctx := context.Background()
	mgr, _ := lifecycleFixture(t, 0, 4<<30)
	desc := circleai.ModelLoadDescriptor{
		ModelID: "big", Backend: circleai.BackendCpu, RequestedTier: circleai.CapabilityTier3Large,
		RamRequiredBytes: 8 << 30, // more than the 4 GiB ceiling
		BridgeFactory:    func(context.Context) (circleai.IInferenceBridge, error) { return newBridge(t, "big", nil), nil },
	}
	res, _ := mgr.Load(ctx, desc)
	if res.Outcome != circleai.LoadOutcomeInsufficientRam {
		t.Errorf("expected InsufficientRam, got %v (%s)", res.Outcome, res.Rationale)
	}
}

func TestModelLifecycleManager_InsufficientVram(t *testing.T) {
	ctx := context.Background()
	mgr, _ := lifecycleFixture(t, 2<<30, 64<<30)
	desc := circleai.ModelLoadDescriptor{
		ModelID: "gpu", Backend: circleai.BackendCuda, RequestedTier: circleai.CapabilityTier2Medium,
		VramRequiredBytes: 6 << 30, // more than the 2 GiB VRAM ceiling
		RamRequiredBytes:  1 << 30,
		BridgeFactory:     func(context.Context) (circleai.IInferenceBridge, error) { return newBridge(t, "gpu", nil), nil },
	}
	res, _ := mgr.Load(ctx, desc)
	if res.Outcome != circleai.LoadOutcomeInsufficientVram {
		t.Errorf("expected InsufficientVram, got %v (%s)", res.Outcome, res.Rationale)
	}
}

func TestModelLifecycleManager_FactoryFailure(t *testing.T) {
	ctx := context.Background()
	mgr, reg := lifecycleFixture(t, 0, 16<<30)
	desc := circleai.ModelLoadDescriptor{
		ModelID: "boom", Backend: circleai.BackendCpu, RequestedTier: circleai.CapabilityTier1Small,
		RamRequiredBytes: 1 << 30,
		BridgeFactory:    func(context.Context) (circleai.IInferenceBridge, error) { return nil, context.Canceled },
	}
	res, _ := mgr.Load(ctx, desc)
	if res.Outcome != circleai.LoadOutcomeFactoryFailed {
		t.Errorf("expected FactoryFailed, got %v", res.Outcome)
	}
	// Reservation must have been rolled back.
	if reg.Resolve("boom") != nil || len(mgr.List()) != 0 {
		t.Error("failed factory should leave no reservation")
	}
}

// ── bridge factory + native status ────────────────────────────────────────────

func TestLocalInferenceBridgeFactory_Create(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/models/acme/single/model.gguf"
	payload := []byte("model-weights")
	provider := circleai.NewMapContentProvider(map[string][]byte{url: payload})
	dl, _ := circleai.NewModelDownloadService(dir, provider)

	reg := circleai.NewModelRegistryService()
	reg.SetRegistry(circleai.ModelRegistry{Models: []circleai.ModelEntry{
		{Name: "acme-single", Version: "1.0", URL: url, Checksum: sha256HexOf(payload), Quantization: "Q4"},
	}})

	status := circleai.NewNativeRuntimeStatus()
	factory, err := circleai.NewLocalInferenceBridgeFactory(
		reg, dl, circleai.DeviceCapabilities{OsName: "test"}, status, nil)
	if err != nil {
		t.Fatalf("factory ctor: %v", err)
	}

	bridge, err := factory.Create(ctx, "acme-single", circleai.BackendCpu, circleai.CapabilityTier1Small)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	loaded, _ := bridge.IsModelLoaded(ctx, "acme-single")
	if !loaded {
		t.Error("bridge should report the model loaded")
	}
	if _, ok := status.Latest(); !ok {
		t.Error("native runtime status should be updated after create")
	}

	// Unknown model fails fast.
	if _, err := factory.Create(ctx, "ghost", circleai.BackendCpu, circleai.CapabilityTier0Tiny); err == nil {
		t.Error("unknown model should error")
	}
}

func TestUnconfiguredBridgeFactory_Refuses(t *testing.T) {
	_, err := circleai.UnconfiguredBridgeFactory{}.Create(context.Background(), "m", circleai.BackendCpu, circleai.CapabilityTier1Small)
	if err == nil {
		t.Error("unconfigured factory should refuse every load")
	}
}

// ── api key auth ──────────────────────────────────────────────────────────────

func TestApiKeyAuthHandler(t *testing.T) {
	// Disabled → anonymous success regardless of headers.
	disabled := circleai.NewApiKeyAuthHandler(func() circleai.ApiKeyOptions {
		return circleai.ApiKeyOptions{Enabled: false}
	})
	res := disabled.Authenticate(func(string) (string, bool) { return "", false })
	if res.Outcome != circleai.AuthSuccess || !res.Principal.AuthDisabled {
		t.Errorf("disabled auth should succeed anonymously: %+v", res)
	}

	// Enabled + valid key.
	enabled := circleai.NewApiKeyAuthHandler(func() circleai.ApiKeyOptions {
		return circleai.ApiKeyOptions{Enabled: true, HeaderName: "X-API-Key", Keys: []string{"secret-key"}}
	})
	hdr := func(want string) func(string) (string, bool) {
		return func(name string) (string, bool) {
			if name == "X-API-Key" {
				return want, true
			}
			return "", false
		}
	}
	if enabled.Authenticate(hdr("secret-key")).Outcome != circleai.AuthSuccess {
		t.Error("valid key should authenticate")
	}
	if enabled.Authenticate(hdr("wrong")).Outcome != circleai.AuthFail {
		t.Error("wrong key should fail")
	}
	// Missing header → NoResult.
	if enabled.Authenticate(func(string) (string, bool) { return "", false }).Outcome != circleai.AuthNoResult {
		t.Error("missing key should be NoResult")
	}
}

// ── endpoint handlers ─────────────────────────────────────────────────────────

func serverFixture(t *testing.T) *circleai.InferenceServerHandlers {
	t.Helper()
	reg := circleai.NewInferenceServerModelRegistry()
	_ = reg.Register("qwen", newBridge(t, "qwen", func(string, []circleai.ChatMessage, bool) string {
		return "<think>because</think>hello back"
	}))
	_ = reg.RegisterEmbedder("emb", fakeEmbedder{})

	counters := circleai.NewServerCounters()
	probe := circleai.StaticServerCapabilityProbe{Profile: circleai.ServerHostProfile{TotalPhysicalMemoryBytes: 16 << 30}}
	mgr, _ := circleai.NewModelLifecycleManager(reg, probe)

	return circleai.NewInferenceServerHandlers(circleai.InferenceServerHandlers{
		Registry:  reg,
		Admission: circleai.NewAdmissionControl(4, counters),
		Counters:  counters,
		Lifecycle: mgr,
	})
}

func TestHandleChatCompletion_NonStream(t *testing.T) {
	h := serverFixture(t)
	body := circleai.ChatCompletionRequest{
		Model:    "qwen",
		Messages: []circleai.ChatCompletionMessage{{Role: "user", Content: "hi"}},
	}
	res := h.HandleChatCompletion(context.Background(), authOK(), body)
	if res.StatusCode != 200 {
		t.Fatalf("status: got %d", res.StatusCode)
	}
	resp, ok := res.Body.(circleai.ChatCompletionResponse)
	if !ok {
		t.Fatalf("body type: %T", res.Body)
	}
	if len(resp.Choices) != 1 || resp.Choices[0].Message.Content != "hello back" {
		t.Errorf("choice content: %+v", resp.Choices)
	}
	if resp.Choices[0].Message.ReasoningContent == nil || *resp.Choices[0].Message.ReasoningContent != "because" {
		t.Errorf("reasoning content missing: %+v", resp.Choices[0].Message.ReasoningContent)
	}
	if resp.Object != "chat.completion" {
		t.Errorf("object: %q", resp.Object)
	}
}

func TestHandleChatCompletion_Stream(t *testing.T) {
	h := serverFixture(t)
	body := circleai.ChatCompletionRequest{
		Model:    "qwen",
		Stream:   true,
		Messages: []circleai.ChatCompletionMessage{{Role: "user", Content: "hi"}},
	}
	res := h.HandleChatCompletion(context.Background(), authOK(), body)
	if !res.DoneTerminator {
		t.Error("streaming response should end with a [DONE] terminator")
	}
	if len(res.StreamFrames) < 2 {
		t.Fatalf("expected role frame + content + stop, got %d frames", len(res.StreamFrames))
	}
	// First frame announces the assistant role.
	first, _ := res.StreamFrames[0].(circleai.ChatCompletionStreamChunk)
	if first.Choices[0].Delta.Role == nil || *first.Choices[0].Delta.Role != "assistant" {
		t.Error("first frame should announce role=assistant")
	}
	// Final frame carries finish_reason=stop.
	last, _ := res.StreamFrames[len(res.StreamFrames)-1].(circleai.ChatCompletionStreamChunk)
	if last.Choices[0].FinishReason == nil || *last.Choices[0].FinishReason != "stop" {
		t.Error("final frame should have finish_reason=stop")
	}
	// A reasoning fragment must have been emitted on the reasoning channel.
	sawReasoning := false
	for _, f := range res.StreamFrames {
		if chunk, ok := f.(circleai.ChatCompletionStreamChunk); ok {
			if chunk.Choices[0].Delta.ReasoningContent != nil {
				sawReasoning = true
			}
		}
	}
	if !sawReasoning {
		t.Error("expected at least one reasoning_content delta")
	}
}

func TestHandleChatCompletion_Errors(t *testing.T) {
	h := serverFixture(t)
	// Unauthorized.
	if r := h.HandleChatCompletion(context.Background(), circleai.AuthResult{Outcome: circleai.AuthFail}, circleai.ChatCompletionRequest{}); r.StatusCode != 401 {
		t.Errorf("auth fail should be 401, got %d", r.StatusCode)
	}
	// Missing model.
	if r := h.HandleChatCompletion(context.Background(), authOK(), circleai.ChatCompletionRequest{Messages: []circleai.ChatCompletionMessage{{Role: "user", Content: "x"}}}); r.StatusCode != 400 {
		t.Errorf("missing model should be 400, got %d", r.StatusCode)
	}
	// Model not loaded.
	r := h.HandleChatCompletion(context.Background(), authOK(), circleai.ChatCompletionRequest{
		Model: "nope", Messages: []circleai.ChatCompletionMessage{{Role: "user", Content: "x"}},
	})
	if r.StatusCode != 404 {
		t.Errorf("unknown model should be 404, got %d", r.StatusCode)
	}
}

func TestHandleEmbeddings(t *testing.T) {
	h := serverFixture(t)
	// Single string input.
	body := circleai.EmbeddingsRequest{Model: "emb", Input: json.RawMessage(`"hello"`)}
	res := h.HandleEmbeddings(context.Background(), authOK(), body)
	if res.StatusCode != 200 {
		t.Fatalf("status: %d", res.StatusCode)
	}
	resp := res.Body.(circleai.EmbeddingsResponse)
	if len(resp.Data) != 1 || len(resp.Data[0].Embedding) != 3 {
		t.Errorf("single embedding wrong: %+v", resp.Data)
	}

	// Array input.
	body.Input = json.RawMessage(`["a","bb","ccc"]`)
	res = h.HandleEmbeddings(context.Background(), authOK(), body)
	resp = res.Body.(circleai.EmbeddingsResponse)
	if len(resp.Data) != 3 {
		t.Errorf("array embeddings: got %d want 3", len(resp.Data))
	}
	if resp.Data[2].Index != 2 {
		t.Errorf("index should be preserved, got %d", resp.Data[2].Index)
	}

	// Unknown model → 404.
	if r := h.HandleEmbeddings(context.Background(), authOK(), circleai.EmbeddingsRequest{Model: "ghost", Input: json.RawMessage(`"x"`)}); r.StatusCode != 404 {
		t.Errorf("unknown embed model should be 404, got %d", r.StatusCode)
	}
	// Bad input (number) → 400.
	if r := h.HandleEmbeddings(context.Background(), authOK(), circleai.EmbeddingsRequest{Model: "emb", Input: json.RawMessage(`123`)}); r.StatusCode != 400 {
		t.Errorf("numeric input should be 400, got %d", r.StatusCode)
	}
}

func TestAdmin_LoadUnloadLifecycle(t *testing.T) {
	ctx := context.Background()
	// Build a fixture whose bridge factory produces a real bridge.
	reg := circleai.NewInferenceServerModelRegistry()
	counters := circleai.NewServerCounters()
	probe := circleai.StaticServerCapabilityProbe{Profile: circleai.ServerHostProfile{TotalPhysicalMemoryBytes: 16 << 30}}
	mgr, _ := circleai.NewModelLifecycleManager(reg, probe)

	dir := t.TempDir()
	url := "https://modelscope.cn/models/acme/admin/model.gguf"
	payload := []byte("weights")
	dl, _ := circleai.NewModelDownloadService(dir, circleai.NewMapContentProvider(map[string][]byte{url: payload}))
	mreg := circleai.NewModelRegistryService()
	mreg.SetRegistry(circleai.ModelRegistry{Models: []circleai.ModelEntry{
		{Name: "admin-model", Version: "1", URL: url, Checksum: sha256HexOf(payload)},
	}})
	factory, _ := circleai.NewLocalInferenceBridgeFactory(mreg, dl, circleai.DeviceCapabilities{}, nil, nil)

	h := circleai.NewInferenceServerHandlers(circleai.InferenceServerHandlers{
		Registry:      reg,
		Admission:     circleai.NewAdmissionControl(4, counters),
		Counters:      counters,
		Lifecycle:     mgr,
		BridgeFactory: factory,
	})

	// Load.
	loadRes := h.AdminLoad(ctx, authOK(), circleai.AdminLoadRequest{
		ModelID: "admin-model", Backend: "Cpu", Tier: "Tier1_Small", RamRequiredBytes: 1 << 30,
	})
	if loadRes.StatusCode != 200 {
		t.Fatalf("admin load status: %d body=%+v", loadRes.StatusCode, loadRes.Body)
	}

	// Lifecycle shows it.
	lc := h.AdminLifecycle(ctx, authOK())
	resp := lc.Body.(circleai.AdminLifecycleResponse)
	if len(resp.Loaded) != 1 || resp.Loaded[0].ModelID != "admin-model" {
		t.Errorf("lifecycle should list admin-model: %+v", resp.Loaded)
	}

	// Bad backend → 400.
	if r := h.AdminLoad(ctx, authOK(), circleai.AdminLoadRequest{ModelID: "x", Backend: "Xpu"}); r.StatusCode != 400 {
		t.Errorf("bad backend should be 400, got %d", r.StatusCode)
	}

	// Unload.
	un := h.AdminUnload(ctx, authOK(), "admin-model")
	if un.StatusCode != 200 {
		t.Errorf("unload status: %d", un.StatusCode)
	}
	// Unload again → 404.
	if r := h.AdminUnload(ctx, authOK(), "admin-model"); r.StatusCode != 404 {
		t.Errorf("second unload should be 404, got %d", r.StatusCode)
	}
}

func TestAdmission_ConcurrencyCap(t *testing.T) {
	counters := circleai.NewServerCounters()
	gate := circleai.NewAdmissionControl(1, counters)
	s1 := gate.TryEnter()
	if s1 == nil {
		t.Fatal("first entry should succeed")
	}
	if gate.TryEnter() != nil {
		t.Error("second entry should be rejected at cap 1")
	}
	s1.Release()
	s2 := gate.TryEnter()
	if s2 == nil {
		t.Error("entry should succeed after release")
	}
	s2.Release()
	if counters.RejectedRequests() != 1 {
		t.Errorf("expected 1 rejection counted, got %d", counters.RejectedRequests())
	}
}
