// plugins_test.go
//
// Verifies the CircleAI.Plugins port (plugins.go): the event bus (raise/subscribe/
// unsubscribe + panic isolation), the default context, and the permissioned
// context gating (workspace path + silent events when the permission is absent).

package circleai_test

import (
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestPlugins_EventBusRaiseSubscribeUnsub(t *testing.T) {
	bus := circleai.NewDefaultPluginEvents()
	var got []any
	unsub := bus.Subscribe(circleai.PluginEventChatMessage, func(p any) { got = append(got, p) })
	// A panicking subscriber must not stop delivery to healthy ones.
	bus.Subscribe(circleai.PluginEventChatMessage, func(p any) { panic("boom") })
	bus.Raise(circleai.PluginEventChatMessage, "hello")
	if len(got) != 1 || got[0] != "hello" {
		t.Fatalf("subscriber got %v", got)
	}
	unsub()
	bus.Raise(circleai.PluginEventChatMessage, "again")
	if len(got) != 1 {
		t.Fatalf("unsubscribed handler still fired: %v", got)
	}
	// Events with no subscribers are a no-op.
	bus.Raise("no.subscribers", nil)
}

func TestPlugins_DefaultContext(t *testing.T) {
	bus := circleai.NewDefaultPluginEvents()
	ctx := circleai.NewDefaultPluginContext(func() string { return "/ws" }, bus, nil)
	if ctx.WorkspacePath() != "/ws" || ctx.Events() != bus || ctx.Logger() != nil {
		t.Fatalf("default context wrong: ws=%q", ctx.WorkspacePath())
	}
}

func TestPlugins_PermissionedContextGating(t *testing.T) {
	bus := circleai.NewDefaultPluginEvents()
	inner := circleai.NewDefaultPluginContext(func() string { return "/ws" }, bus, nil)

	// No permissions -> no workspace path, silent events.
	denied := circleai.NewPermissionedPluginContext(inner, nil)
	if denied.WorkspacePath() != "" {
		t.Fatalf("workspace path should be hidden without permission")
	}
	fired := 0
	denied.Events().Subscribe("x", func(any) { fired++ })
	denied.Events().Raise("x", nil)
	if fired != 0 {
		t.Fatalf("silent events must not deliver, got %d", fired)
	}

	// With workspace.read + events.subscribe -> visible path, real bus.
	granted := circleai.NewPermissionedPluginContext(inner, []string{
		circleai.PluginPermissionWorkspaceRead, circleai.PluginPermissionEventsSubscribe})
	if granted.WorkspacePath() != "/ws" {
		t.Fatalf("workspace path should be visible with permission")
	}
	got := 0
	granted.Events().Subscribe("x", func(any) { got++ })
	bus.Raise("x", nil) // the real bus is shared
	if got != 1 {
		t.Fatalf("granted events must deliver, got %d", got)
	}
}
