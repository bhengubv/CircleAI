// hosting_endpoints_test.go
//
// Verifies CircleAI.Hosting.Endpoints ports:
//   InProcessEndpoint (service accessor)
//   HttpLoopbackEndpoint + AIHttpClient round-trip over 127.0.0.1 (ask/chat/
//     stream/tool), including the X-Butler-Token shared-secret auth.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestInProcessEndpoint(t *testing.T) {
	ctx := context.Background()
	ep := circleai.NewInProcessEndpoint()
	if ep.ServiceAccessor() != nil {
		t.Error("accessor should be nil before Start")
	}
	butler := &fakeButler{}
	if err := ep.Start(ctx, butler); err != nil {
		t.Fatalf("start: %v", err)
	}
	if ep.ServiceAccessor() != circleai.IAIService(butler) {
		t.Error("accessor should return the bound service")
	}
	// Idempotent Start.
	if err := ep.Start(ctx, butler); err != nil {
		t.Fatalf("second start: %v", err)
	}
	_ = ep.Stop(ctx)
	if ep.ServiceAccessor() != nil {
		t.Error("accessor should be nil after Stop")
	}
}

// streamButler streams a fixed sequence of chunks and answers ask/tool.
type streamButler struct {
	fakeButler
	chunks   []string
	toolOK   bool
	toolName string
}

func (b *streamButler) Chat(context.Context, []circleai.ChatMessage, *circleai.GenerationOptions) (string, error) {
	return "chat-reply", nil
}

func (b *streamButler) Stream(ctx context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)
	go func() {
		defer close(out)
		defer close(errc)
		for _, c := range b.chunks {
			select {
			case out <- c:
			case <-ctx.Done():
				errc <- ctx.Err()
				return
			}
		}
	}()
	return out, errc
}

func (b *streamButler) InvokeTool(_ context.Context, inv circleai.ToolInvocation) (circleai.ToolResult, error) {
	return circleai.ToolResult{ToolName: inv.ToolName, Success: b.toolOK, Result: "tool-out"}, nil
}

func TestHttpLoopbackEndpoint_RoundTrip(t *testing.T) {
	ctx := context.Background()
	butler := &streamButler{
		fakeButler: fakeButler{askReply: "asked-reply"},
		chunks:     []string{"Hello", " ", "world"},
		toolOK:     true,
	}
	ep := circleai.NewHttpLoopbackEndpoint("", 0) // random token + OS port
	if err := ep.Start(ctx, butler); err != nil {
		t.Fatalf("endpoint start: %v", err)
	}
	defer ep.Stop(ctx)

	port := ep.BoundPort()
	token := ep.Token()
	if port == 0 || token == "" {
		t.Fatalf("endpoint not bound: port=%d token=%q", port, token)
	}

	client, err := circleai.NewAIHttpClient(port, token)
	if err != nil {
		t.Fatalf("client: %v", err)
	}

	// Ask
	ans, err := client.Ask(ctx, "question?")
	if err != nil {
		t.Fatalf("ask: %v", err)
	}
	if ans != "asked-reply" {
		t.Errorf("ask reply = %q", ans)
	}

	// Chat
	chat, err := client.Chat(ctx, []circleai.ChatMessage{{Role: "user", Content: "hi"}}, nil)
	if err != nil {
		t.Fatalf("chat: %v", err)
	}
	if chat != "chat-reply" {
		t.Errorf("chat reply = %q", chat)
	}

	// Stream — reassemble chunks.
	chunks, serrc := client.Stream(ctx, []circleai.ChatMessage{{Role: "user", Content: "hi"}}, nil)
	var got []string
	for c := range chunks {
		got = append(got, c)
	}
	if err := <-serrc; err != nil {
		t.Fatalf("stream error: %v", err)
	}
	if len(got) != 3 || got[0] != "Hello" || got[2] != "world" {
		t.Errorf("stream chunks = %v", got)
	}

	// Tool
	res, err := client.InvokeTool(ctx, circleai.ToolInvocation{ToolName: "t", Arguments: map[string]interface{}{"x": 1}})
	if err != nil {
		t.Fatalf("tool: %v", err)
	}
	if !res.Success || res.ToolName != "t" {
		t.Errorf("tool result = %+v", res)
	}
}

func TestHttpLoopbackEndpoint_RejectsBadToken(t *testing.T) {
	ctx := context.Background()
	butler := &streamButler{fakeButler: fakeButler{askReply: "x"}}
	ep := circleai.NewHttpLoopbackEndpoint("correct-token", 0)
	if err := ep.Start(ctx, butler); err != nil {
		t.Fatalf("endpoint start: %v", err)
	}
	defer ep.Stop(ctx)

	client, err := circleai.NewAIHttpClient(ep.BoundPort(), "wrong-token")
	if err != nil {
		t.Fatalf("client: %v", err)
	}
	if _, err := client.Ask(ctx, "q"); err == nil {
		t.Error("expected 401 with wrong token")
	}
}
