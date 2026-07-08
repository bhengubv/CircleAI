// hosting_endpoints.go
//
// Ports CircleAI.Hosting endpoint surface:
//   IAIEndpoint (IAIEndpoint.cs)
//   InProcessEndpoint (Endpoints/InProcessEndpoint.cs)
//   HttpLoopbackEndpoint (Endpoints/HttpLoopbackEndpoint.cs)
//   AIHttpClient (Endpoints/AIHttpClient.cs)
//
// InProcessEndpoint just holds the service. HttpLoopbackEndpoint stands up a
// loopback (127.0.0.1-only) HTTP server exposing /butler/{ask,chat,stream,tool}
// with an X-Butler-Token shared-secret; AIHttpClient is the matching out-of-
// process client. The wire shapes (JSON bodies, SSE `data:`/`event: done`
// framing, constant-time token check) match the C# endpoint exactly.

package circleai

import (
	"bufio"
	"bytes"
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

// IAIEndpoint is a transport-agnostic endpoint exposing an IAIService. Ports
// CircleAI.Hosting.IAIEndpoint.
type IAIEndpoint interface {
	// Start begins serving requests against service. Idempotent.
	Start(ctx context.Context, service IAIService) error
	// Stop stops accepting new requests and drains in-flight ones.
	Stop(ctx context.Context) error
}

// GenerateRandomLoopbackToken returns a 32-hex-char random token, mirroring the
// C# AIOptions.GenerateRandomToken helper the loopback endpoint uses.
func GenerateRandomLoopbackToken() string {
	var buf [16]byte
	_, _ = rand.Read(buf[:])
	return hex.EncodeToString(buf[:])
}

// InProcessEndpoint holds an IAIService directly with no transport. Ports
// CircleAI.Hosting.Endpoints.InProcessEndpoint. In-process callers read the
// service via ServiceAccessor.
type InProcessEndpoint struct {
	mu       sync.Mutex
	service  IAIService
	started  bool
	disposed bool
}

// NewInProcessEndpoint builds an unstarted in-process endpoint.
func NewInProcessEndpoint() *InProcessEndpoint { return &InProcessEndpoint{} }

// ServiceAccessor returns the wrapped service, or nil before Start.
func (e *InProcessEndpoint) ServiceAccessor() IAIService {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.service
}

// Start binds the service. Idempotent.
func (e *InProcessEndpoint) Start(_ context.Context, service IAIService) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.disposed {
		return errors.New("InProcessEndpoint is disposed")
	}
	if e.started {
		return nil
	}
	if service == nil {
		return errNilArg("service")
	}
	e.service = service
	e.started = true
	return nil
}

// Stop unbinds the service.
func (e *InProcessEndpoint) Stop(context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.started = false
	e.service = nil
	return nil
}

var _ IAIEndpoint = (*InProcessEndpoint)(nil)

// ---------------------------------------------------------------------------
// HttpLoopbackEndpoint
// ---------------------------------------------------------------------------

// HttpLoopbackEndpoint is a loopback HTTP transport for IAIService, bound only
// to 127.0.0.1. Ports CircleAI.Hosting.Endpoints.HttpLoopbackEndpoint.
type HttpLoopbackEndpoint struct {
	configuredToken string
	configuredPort  int

	mu        sync.Mutex
	server    *http.Server
	listener  net.Listener
	service   IAIService
	token     string
	boundPort int
	started   bool
}

// NewHttpLoopbackEndpoint builds an unstarted loopback endpoint. When token is
// empty a random one is generated at Start; when port is 0 the OS assigns a
// free loopback port.
func NewHttpLoopbackEndpoint(token string, port int) *HttpLoopbackEndpoint {
	return &HttpLoopbackEndpoint{configuredToken: token, configuredPort: port}
}

// BoundPort returns the port the listener bound to (0 when not started).
func (e *HttpLoopbackEndpoint) BoundPort() int {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.boundPort
}

// Token returns the effective shared-secret token ("" when not started).
func (e *HttpLoopbackEndpoint) Token() string {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.token
}

// Start binds the listener and begins serving. Ports HttpLoopbackEndpoint.StartAsync.
func (e *HttpLoopbackEndpoint) Start(_ context.Context, service IAIService) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if service == nil {
		return errNilArg("service")
	}
	if e.started {
		return nil
	}

	token := e.configuredToken
	if token == "" {
		token = GenerateRandomLoopbackToken()
	}

	addr := fmt.Sprintf("127.0.0.1:%d", e.configuredPort)
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("failed to start loopback HTTP listener on port %d: %w", e.configuredPort, err)
	}

	e.service = service
	e.token = token
	e.listener = ln
	e.boundPort = ln.Addr().(*net.TCPAddr).Port

	mux := http.NewServeMux()
	mux.HandleFunc("/butler/ask", e.wrap(e.handleAsk))
	mux.HandleFunc("/butler/chat", e.wrap(e.handleChat))
	mux.HandleFunc("/butler/stream", e.wrap(e.handleStream))
	mux.HandleFunc("/butler/tool", e.wrap(e.handleTool))

	e.server = &http.Server{Handler: mux}
	e.started = true
	go func() { _ = e.server.Serve(ln) }()
	return nil
}

// Stop shuts the server down and drains in-flight requests. Ports
// HttpLoopbackEndpoint.StopAsync.
func (e *HttpLoopbackEndpoint) Stop(ctx context.Context) error {
	e.mu.Lock()
	if !e.started {
		e.mu.Unlock()
		return nil
	}
	e.started = false
	srv := e.server
	e.server = nil
	e.listener = nil
	e.service = nil
	e.mu.Unlock()

	if srv != nil {
		if ctx == nil {
			ctx = context.Background()
		}
		return srv.Shutdown(ctx)
	}
	return nil
}

type loopbackHandler func(w http.ResponseWriter, r *http.Request) error

func (e *HttpLoopbackEndpoint) wrap(h loopbackHandler) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !e.authorise(r) {
			writePlain(w, http.StatusUnauthorized, "unauthorised")
			return
		}
		if !strings.EqualFold(r.Method, http.MethodPost) {
			writePlain(w, http.StatusMethodNotAllowed, "method not allowed")
			return
		}
		if err := h(w, r); err != nil {
			writePlain(w, http.StatusInternalServerError, "internal error")
		}
	}
}

func (e *HttpLoopbackEndpoint) authorise(r *http.Request) bool {
	e.mu.Lock()
	token := e.token
	e.mu.Unlock()
	if token == "" {
		return false
	}
	supplied := r.Header.Get("X-Butler-Token")
	if supplied == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(supplied), []byte(token)) == 1
}

func (e *HttpLoopbackEndpoint) requireService() (IAIService, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.service == nil {
		return nil, errors.New("HttpLoopbackEndpoint has no service bound")
	}
	return e.service, nil
}

func (e *HttpLoopbackEndpoint) handleAsk(w http.ResponseWriter, r *http.Request) error {
	svc, err := e.requireService()
	if err != nil {
		return err
	}
	var payload askPayload
	if !decodeBody(r, &payload) || isBlank(payload.Question) {
		writePlain(w, http.StatusBadRequest, "missing 'question'")
		return nil
	}
	answer, err := svc.Ask(r.Context(), payload.Question)
	if err != nil {
		return err
	}
	writePlain(w, http.StatusOK, answer)
	return nil
}

func (e *HttpLoopbackEndpoint) handleChat(w http.ResponseWriter, r *http.Request) error {
	svc, err := e.requireService()
	if err != nil {
		return err
	}
	var payload chatPayload
	if !decodeBody(r, &payload) || len(payload.Messages) == 0 {
		writePlain(w, http.StatusBadRequest, "missing 'messages'")
		return nil
	}
	messages := payload.toChatMessages()
	content, err := svc.Chat(r.Context(), messages, payload.Options.toGenerationOptions())
	if err != nil {
		return err
	}
	writeJSON(w, http.StatusOK, chatResponsePayload{Content: content})
	return nil
}

func (e *HttpLoopbackEndpoint) handleStream(w http.ResponseWriter, r *http.Request) error {
	svc, err := e.requireService()
	if err != nil {
		return err
	}
	var payload chatPayload
	if !decodeBody(r, &payload) || len(payload.Messages) == 0 {
		writePlain(w, http.StatusBadRequest, "missing 'messages'")
		return nil
	}
	messages := payload.toChatMessages()

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("X-Accel-Buffering", "no")
	w.WriteHeader(http.StatusOK)
	flusher, _ := w.(http.Flusher)

	chunks, cerrs := svc.Stream(r.Context(), messages, payload.Options.toGenerationOptions())
	for piece := range chunks {
		encoded, _ := json.Marshal(piece)
		_, _ = fmt.Fprintf(w, "data: %s\n\n", encoded)
		if flusher != nil {
			flusher.Flush()
		}
	}
	<-cerrs // drain (best-effort; errors already streamed as end-of-stream)
	_, _ = io.WriteString(w, "event: done\ndata: {}\n\n")
	if flusher != nil {
		flusher.Flush()
	}
	return nil
}

func (e *HttpLoopbackEndpoint) handleTool(w http.ResponseWriter, r *http.Request) error {
	svc, err := e.requireService()
	if err != nil {
		return err
	}
	var payload toolPayload
	if !decodeBody(r, &payload) || isBlank(payload.ToolName) {
		writePlain(w, http.StatusBadRequest, "missing 'toolName'")
		return nil
	}
	args := payload.Arguments
	if args == nil {
		args = map[string]interface{}{}
	}
	result, err := svc.InvokeTool(r.Context(), ToolInvocation{ToolName: payload.ToolName, Arguments: args})
	if err != nil {
		return err
	}
	status := http.StatusOK
	if !result.Success {
		status = http.StatusBadGateway
	}
	writeJSON(w, status, result)
	return nil
}

var _ IAIEndpoint = (*HttpLoopbackEndpoint)(nil)

// ---------------------------------------------------------------------------
// AIHttpClient
// ---------------------------------------------------------------------------

// AIHttpClient talks to a HttpLoopbackEndpoint. Its methods mirror IAIService so
// the same call sites work in-process or out-of-process. Ports
// CircleAI.Hosting.Endpoints.AIHttpClient.
type AIHttpClient struct {
	baseURL string
	token   string
	http    *http.Client
}

// NewAIHttpClient connects to a loopback Butler endpoint at 127.0.0.1:{port}.
func NewAIHttpClient(port int, token string) (*AIHttpClient, error) {
	if port <= 0 {
		return nil, errArg("port must be positive")
	}
	if token == "" {
		return nil, errArg("token must not be empty")
	}
	return &AIHttpClient{
		baseURL: fmt.Sprintf("http://127.0.0.1:%d", port),
		token:   token,
		http:    &http.Client{Timeout: 5 * time.Minute},
	}, nil
}

// Ask mirrors IAIService.Ask.
func (c *AIHttpClient) Ask(ctx context.Context, question string) (string, error) {
	if question == "" {
		return "", errArg("question must not be empty")
	}
	resp, err := c.post(ctx, "/butler/ask", askPayload{Question: question})
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("butler ask returned status %d", resp.StatusCode)
	}
	b, err := io.ReadAll(resp.Body)
	return string(b), err
}

// Chat mirrors IAIService.Chat.
func (c *AIHttpClient) Chat(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (string, error) {
	payload := chatPayload{Messages: fromChatMessages(messages), Options: fromGenerationOptions(options)}
	resp, err := c.post(ctx, "/butler/chat", payload)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("butler chat returned status %d", resp.StatusCode)
	}
	var parsed chatResponsePayload
	if err := json.NewDecoder(resp.Body).Decode(&parsed); err != nil {
		return "", err
	}
	return parsed.Content, nil
}

// Stream mirrors IAIService.Stream, consuming the SSE frames.
func (c *AIHttpClient) Stream(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)

	go func() {
		defer close(out)
		defer close(errc)
		payload := chatPayload{Messages: fromChatMessages(messages), Options: fromGenerationOptions(options)}
		resp, err := c.post(ctx, "/butler/stream", payload)
		if err != nil {
			errc <- err
			return
		}
		defer resp.Body.Close()
		if resp.StatusCode != http.StatusOK {
			errc <- fmt.Errorf("butler stream returned status %d", resp.StatusCode)
			return
		}
		reader := bufio.NewReader(resp.Body)
		for {
			line, err := reader.ReadString('\n')
			if len(line) > 0 {
				trimmed := strings.TrimRight(line, "\r\n")
				if strings.HasPrefix(trimmed, "event:") {
					if strings.TrimSpace(trimmed[len("event:"):]) == "done" {
						return
					}
					continue
				}
				if strings.HasPrefix(trimmed, "data:") {
					dataPart := strings.TrimSpace(trimmed[len("data:"):])
					if dataPart != "" {
						var piece string
						if e := json.Unmarshal([]byte(dataPart), &piece); e != nil {
							piece = dataPart
						}
						if piece != "" {
							select {
							case out <- piece:
							case <-ctx.Done():
								errc <- ctx.Err()
								return
							}
						}
					}
				}
			}
			if err != nil {
				if err != io.EOF {
					errc <- err
				}
				return
			}
		}
	}()
	return out, errc
}

// InvokeTool mirrors IAIService.InvokeTool.
func (c *AIHttpClient) InvokeTool(ctx context.Context, invocation ToolInvocation) (ToolResult, error) {
	payload := toolPayload{ToolName: invocation.ToolName, Arguments: invocation.Arguments}
	resp, err := c.post(ctx, "/butler/tool", payload)
	if err != nil {
		return ToolResult{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusBadGateway {
		return ToolResult{}, fmt.Errorf("butler tool returned status %d", resp.StatusCode)
	}
	var result ToolResult
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return ToolResult{}, err
	}
	return result, nil
}

func (c *AIHttpClient) post(ctx context.Context, path string, body interface{}) (*http.Response, error) {
	buf, err := json.Marshal(body)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+path, bytes.NewReader(buf))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Butler-Token", c.token)
	return c.http.Do(req)
}

// ---------------------------------------------------------------------------
// wire payloads (mirror HttpLoopbackEndpoint / AIHttpClient DTOs)
// ---------------------------------------------------------------------------

type askPayload struct {
	Question string `json:"question"`
}

type chatMessagePayload struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}

type generationOptionsPayload struct {
	MaxTokens     *int     `json:"maxTokens,omitempty"`
	Temperature   *float32 `json:"temperature,omitempty"`
	TopP          *float32 `json:"topP,omitempty"`
	TopK          *int     `json:"topK,omitempty"`
	Seed          *int     `json:"seed,omitempty"`
	StopSequences []string `json:"stopSequences,omitempty"`
}

func (p *generationOptionsPayload) toGenerationOptions() *GenerationOptions {
	if p == nil {
		return nil
	}
	def := DefaultGenerationOptions()
	o := GenerationOptions{
		MaxTokens:   def.MaxTokens,
		Temperature: def.Temperature,
		TopP:        def.TopP,
		TopK:        def.TopK,
	}
	if p.MaxTokens != nil {
		o.MaxTokens = *p.MaxTokens
	}
	if p.Temperature != nil {
		o.Temperature = *p.Temperature
	}
	if p.TopP != nil {
		o.TopP = *p.TopP
	}
	if p.TopK != nil {
		o.TopK = *p.TopK
	}
	o.Seed = p.Seed
	o.StopSequences = p.StopSequences
	return &o
}

func fromGenerationOptions(o *GenerationOptions) *generationOptionsPayload {
	if o == nil {
		return nil
	}
	mt, temp, topP, topK := o.MaxTokens, o.Temperature, o.TopP, o.TopK
	return &generationOptionsPayload{
		MaxTokens:     &mt,
		Temperature:   &temp,
		TopP:          &topP,
		TopK:          &topK,
		Seed:          o.Seed,
		StopSequences: o.StopSequences,
	}
}

type chatPayload struct {
	Messages []chatMessagePayload      `json:"messages"`
	Options  *generationOptionsPayload `json:"options,omitempty"`
}

func (p chatPayload) toChatMessages() []ChatMessage {
	out := make([]ChatMessage, 0, len(p.Messages))
	for _, m := range p.Messages {
		role := m.Role
		if role == "" {
			role = "user"
		}
		out = append(out, ChatMessage{Role: role, Content: m.Content})
	}
	return out
}

func fromChatMessages(messages []ChatMessage) []chatMessagePayload {
	out := make([]chatMessagePayload, 0, len(messages))
	for _, m := range messages {
		out = append(out, chatMessagePayload{Role: m.Role, Content: m.Content})
	}
	return out
}

type chatResponsePayload struct {
	Content string `json:"content"`
}

type toolPayload struct {
	ToolName  string                 `json:"toolName"`
	Arguments map[string]interface{} `json:"arguments,omitempty"`
}

func decodeBody(r *http.Request, v interface{}) bool {
	body, err := io.ReadAll(r.Body)
	if err != nil || len(bytes.TrimSpace(body)) == 0 {
		return false
	}
	return json.Unmarshal(body, v) == nil
}

func writePlain(w http.ResponseWriter, status int, text string) {
	w.Header().Set("Content-Type", "text/plain; charset=utf-8")
	w.WriteHeader(status)
	_, _ = io.WriteString(w, text)
}

func writeJSON(w http.ResponseWriter, status int, payload interface{}) {
	b, _ := json.Marshal(payload)
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_, _ = w.Write(b)
}
