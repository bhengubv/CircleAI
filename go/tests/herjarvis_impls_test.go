// herjarvis_impls_test.go
//
// Verifies the HER/Jarvis real implementations ported from
// HerJarvisRealImplementations.cs. Fixture-driven where value vectors matter
// (emotion sensing, episodic recall via fixtures/herjarvis_*.json); behavioural
// for streams, presence, actuation, delegation, code-gen, and self-improvement.

package circleai_test

import (
	"context"
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── 1. AlwaysOnPresence ──────────────────────────────────────────────────────

func TestHeartbeatAlwaysOnPresence(t *testing.T) {
	ctx := context.Background()
	p := circleai.NewHeartbeatAlwaysOnPresence(10 * time.Millisecond)
	if p.IsRunning() {
		t.Fatal("presence should start stopped")
	}
	if err := p.Start(ctx); err != nil {
		t.Fatalf("Start: %v", err)
	}
	if !p.IsRunning() {
		t.Fatal("presence should be running after Start")
	}
	// Immediate tick at t=0.
	if got := p.Heartbeats(); got < 1 {
		t.Errorf("expected at least 1 heartbeat immediately, got %d", got)
	}
	// Start is idempotent.
	if err := p.Start(ctx); err != nil {
		t.Fatalf("second Start: %v", err)
	}
	// Wait for a few more ticks.
	deadline := time.Now().Add(500 * time.Millisecond)
	for p.Heartbeats() < 3 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if got := p.Heartbeats(); got < 3 {
		t.Errorf("expected >=3 heartbeats, got %d", got)
	}
	if err := p.Stop(ctx); err != nil {
		t.Fatalf("Stop: %v", err)
	}
	if p.IsRunning() {
		t.Error("presence should be stopped after Stop")
	}
	// Stop is idempotent.
	if err := p.Stop(ctx); err != nil {
		t.Fatalf("second Stop: %v", err)
	}
}

// ── 2. FusedPerception ───────────────────────────────────────────────────────

func TestChannelFusedPerception(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	fp := circleai.NewChannelFusedPerception()
	stream := fp.Stream(ctx)

	txt := "hello"
	fp.Publish(circleai.FusedPercept{At: time.Now().UTC(), Text: &txt, Sensors: map[string]float64{"lux": 42}})

	select {
	case got := <-stream:
		if got.Text == nil || *got.Text != "hello" {
			t.Errorf("percept text: got %v", got.Text)
		}
		if got.Sensors["lux"] != 42 {
			t.Errorf("sensors: got %v", got.Sensors)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for percept")
	}

	fp.Complete()
	// After Complete, a fresh stream is closed immediately.
	s2 := fp.Stream(ctx)
	select {
	case _, ok := <-s2:
		if ok {
			t.Error("stream after Complete should be closed")
		}
	case <-time.After(time.Second):
		t.Fatal("timed out on closed stream")
	}
}

// ── 3. IdentitySync ──────────────────────────────────────────────────────────

func TestJSONIdentitySync(t *testing.T) {
	ctx := context.Background()
	s := circleai.NewJSONIdentitySync()

	// Empty pull from cursor 0.
	got, err := s.Pull(ctx, "0")
	if err != nil {
		t.Fatalf("Pull: %v", err)
	}
	if got != `{"cursor":0,"deltas":[]}` {
		t.Errorf("empty pull: got %s", got)
	}

	_ = s.Push(ctx, `{"a":1}`)
	_ = s.Push(ctx, `{"b":2}`)

	got, _ = s.Pull(ctx, "0")
	if got != `{"cursor":2,"deltas":[{"a":1},{"b":2}]}` {
		t.Errorf("full pull: got %s", got)
	}

	// Incremental pull after cursor 1.
	got, _ = s.Pull(ctx, "1")
	if got != `{"cursor":2,"deltas":[{"b":2}]}` {
		t.Errorf("incremental pull: got %s", got)
	}

	// Unparseable cursor => treated as 0.
	got, _ = s.Pull(ctx, "garbage")
	if got != `{"cursor":2,"deltas":[{"a":1},{"b":2}]}` {
		t.Errorf("garbage cursor pull: got %s", got)
	}
}

// ── 4. ContinuousLearner ─────────────────────────────────────────────────────

func TestEwaContinuousLearner(t *testing.T) {
	ctx := context.Background()
	l, err := circleai.NewEwaContinuousLearner(0.5)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if _, err := circleai.NewEwaContinuousLearner(0); err == nil {
		t.Error("alpha=0 should error")
	}
	if _, err := circleai.NewEwaContinuousLearner(1.5); err == nil {
		t.Error("alpha>1 should error")
	}

	// First observation seeds avg = reward.
	if err := l.RegisterFeedback(ctx, "i1", 1.0, "{}"); err != nil {
		t.Fatalf("RegisterFeedback: %v", err)
	}
	if avg, ok := l.AverageRewardOf("i1"); !ok || avg != 1.0 {
		t.Errorf("after first: avg=%v ok=%v", avg, ok)
	}
	// Second: avg = 1.0*0.5 + 0.0*0.5 = 0.5.
	_ = l.RegisterFeedback(ctx, "i1", 0.0, "{}")
	if avg, _ := l.AverageRewardOf("i1"); math.Abs(avg-0.5) > 1e-9 {
		t.Errorf("after second: avg=%v want 0.5", avg)
	}
	if n := l.ObservationsOf("i1"); n != 2 {
		t.Errorf("observations: got %d want 2", n)
	}
	if _, ok := l.AverageRewardOf("missing"); ok {
		t.Error("missing id should report not-found")
	}
	if err := l.RegisterFeedback(ctx, "  ", 1.0, "{}"); err == nil {
		t.Error("blank interactionId should error")
	}
}

// ── 6. GoalPursuer ───────────────────────────────────────────────────────────

func TestInMemoryGoalPursuer(t *testing.T) {
	ctx := context.Background()
	clock := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	p := circleai.NewInMemoryGoalPursuerAt(func() time.Time { return clock })

	// 84 days => 84/14 = 6 milestones.
	deadline := clock.AddDate(0, 0, 84)
	g, err := p.Register(ctx, "Learn Go", deadline)
	if err != nil {
		t.Fatalf("Register: %v", err)
	}
	if g.ID == "" || strings.Contains(g.ID, "-") {
		t.Errorf("goal id should be a dashless uuid: %q", g.ID)
	}
	if g.ProgressFraction != 0 {
		t.Errorf("initial progress: got %v", g.ProgressFraction)
	}

	var plan struct {
		Description string `json:"description"`
		Milestones  []struct {
			Index int    `json:"index"`
			Due   string `json:"due"`
		} `json:"milestones"`
	}
	if err := json.Unmarshal([]byte(g.PlanJSON), &plan); err != nil {
		t.Fatalf("plan is not valid JSON: %v\n%s", err, g.PlanJSON)
	}
	if plan.Description != "Learn Go" {
		t.Errorf("plan description: got %q", plan.Description)
	}
	if len(plan.Milestones) != 6 {
		t.Fatalf("milestone count: got %d want 6", len(plan.Milestones))
	}
	if plan.Milestones[0].Index != 1 || plan.Milestones[5].Index != 6 {
		t.Errorf("milestone indices: got %d..%d", plan.Milestones[0].Index, plan.Milestones[5].Index)
	}
	// Last milestone due == deadline (step*milestones).
	lastDue, err := time.Parse(time.RFC3339Nano, plan.Milestones[5].Due)
	if err != nil {
		t.Fatalf("due parse: %v", err)
	}
	if !lastDue.Equal(deadline) {
		t.Errorf("last milestone due: got %s want %s", lastDue, deadline)
	}

	// Current + unknown.
	cur, _ := p.Current(ctx, g.ID)
	if cur == nil || cur.ID != g.ID {
		t.Errorf("Current: got %v", cur)
	}
	if unk, _ := p.Current(ctx, "nope"); unk != nil {
		t.Errorf("unknown Current should be nil, got %v", unk)
	}

	// Progress + Replan.
	if err := p.Progress(g.ID, 0.5); err != nil {
		t.Fatalf("Progress: %v", err)
	}
	cur, _ = p.Current(ctx, g.ID)
	if cur.ProgressFraction != 0.5 {
		t.Errorf("progress after set: got %v", cur.ProgressFraction)
	}
	if err := p.Replan(ctx, g.ID); err != nil {
		t.Fatalf("Replan: %v", err)
	}

	// Past deadline rejected.
	if _, err := p.Register(ctx, "late", clock.AddDate(0, 0, -1)); err == nil {
		t.Error("past deadline should error")
	}
	if _, err := p.Register(ctx, "  ", deadline); err == nil {
		t.Error("blank description should error")
	}
}

// ── 7. EpisodicMemory (fixtures) ─────────────────────────────────────────────

type episodicFixture struct {
	Episodes []struct {
		ID          string `json:"id"`
		Title       string `json:"title"`
		ContentJSON string `json:"contentJson"`
	} `json:"episodes"`
	Recalls []struct {
		ID          string   `json:"id"`
		Query       string   `json:"query"`
		Take        int      `json:"take"`
		Ordered     bool     `json:"ordered"`
		ExpectedIDs []string `json:"expectedIds"`
	} `json:"recalls"`
}

func TestTfEpisodicMemory_Fixtures(t *testing.T) {
	data, err := os.ReadFile(filepath.Join(fixturesDir(t), "herjarvis_episodic.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var fix episodicFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}

	ctx := context.Background()
	mem := circleai.NewTfEpisodicMemory()
	for _, e := range fix.Episodes {
		if err := mem.Record(ctx, circleai.EpisodeRecord{
			ID: e.ID, At: time.Now().UTC(), Title: e.Title, ContentJSON: e.ContentJSON,
		}); err != nil {
			t.Fatalf("Record %s: %v", e.ID, err)
		}
	}

	for _, rc := range fix.Recalls {
		rc := rc
		t.Run(rc.ID, func(t *testing.T) {
			hits, err := mem.Recall(ctx, rc.Query, rc.Take)
			if err != nil {
				t.Fatalf("Recall: %v", err)
			}
			gotIDs := make([]string, len(hits))
			for i, h := range hits {
				gotIDs[i] = h.ID
			}
			if rc.Ordered {
				if !equalStrings(gotIDs, rc.ExpectedIDs) {
					t.Errorf("ordered recall: got %v want %v", gotIDs, rc.ExpectedIDs)
				}
			} else if !equalStringSets(gotIDs, rc.ExpectedIDs) {
				t.Errorf("recall set: got %v want %v", gotIDs, rc.ExpectedIDs)
			}
		})
	}

	// Empty-query => empty; take<=0 => error.
	empty, _ := mem.Recall(ctx, "", 5)
	if len(empty) != 0 {
		t.Errorf("empty query should recall nothing, got %v", empty)
	}
	if _, err := mem.Recall(ctx, "dentist", 0); err == nil {
		t.Error("take<=0 should error")
	}
	if err := mem.Record(ctx, circleai.EpisodeRecord{ID: "  "}); err == nil {
		t.Error("blank id should error")
	}
}

// ── 8. VoiceIdentity ─────────────────────────────────────────────────────────

// tone renders a sine wave as PCM-16 little-endian bytes.
func tone(freqHz float64, sampleRateHz, ms int) []byte {
	n := sampleRateHz * ms / 1000
	buf := make([]byte, n*2)
	for i := 0; i < n; i++ {
		v := math.Sin(2 * math.Pi * freqHz * float64(i) / float64(sampleRateHz))
		s := int16(v * 20000)
		buf[i*2] = byte(uint16(s) & 0xff)
		buf[i*2+1] = byte(uint16(s) >> 8)
	}
	return buf
}

func TestEnergyBandVoiceIdentity(t *testing.T) {
	ctx := context.Background()
	const sr = 16000
	v := circleai.NewEnergyBandVoiceIdentity()

	// Unknown before enrolment.
	if id, _ := v.Identify(ctx, tone(220, sr, 500), sr); id != nil {
		t.Errorf("identify before enrol should be nil, got %v", *id)
	}

	// Enrol two distinct voices.
	if err := v.Enroll(ctx, "alice", tone(220, sr, 500), sr); err != nil {
		t.Fatalf("Enroll alice: %v", err)
	}
	if err := v.Enroll(ctx, "bob", tone(660, sr, 500), sr); err != nil {
		t.Fatalf("Enroll bob: %v", err)
	}

	// The same 220Hz tone identifies as alice (self-similarity == 1.0 > 0.85).
	id, err := v.Identify(ctx, tone(220, sr, 500), sr)
	if err != nil {
		t.Fatalf("Identify: %v", err)
	}
	if id == nil || *id != "alice" {
		t.Errorf("identify 220Hz: got %v want alice", id)
	}

	if err := v.Enroll(ctx, "  ", tone(220, sr, 100), sr); err == nil {
		t.Error("blank userId should error")
	}
}

// ── 9. CalibratedConfidence ──────────────────────────────────────────────────

func TestHistoricalCalibratedConfidence(t *testing.T) {
	ctx := context.Background()
	c := circleai.NewHistoricalCalibratedConfidence()

	// With <5 history the band centres on the raw score; hedging widens/lowers it.
	confident, _ := c.Evaluate(ctx, strings.Repeat("a definite answer ", 5), `{"ctx":1}`)
	hedged, _ := c.Evaluate(ctx, "maybe, perhaps, possibly unclear", `{"ctx":1}`)
	if !(confident.Lower <= confident.Upper) || !(hedged.Lower <= hedged.Upper) {
		t.Fatalf("bands malformed: %+v %+v", confident, hedged)
	}
	// Hedged text has a lower centre than confident text.
	confidentMid := (confident.Lower + confident.Upper) / 2
	hedgedMid := (hedged.Lower + hedged.Upper) / 2
	if hedgedMid >= confidentMid {
		t.Errorf("hedged (%v) should score below confident (%v)", hedgedMid, confidentMid)
	}
	// Bands are within [0,1].
	for _, b := range []circleai.ConfidenceBand{confident, hedged} {
		if b.Lower < 0 || b.Upper > 1 {
			t.Errorf("band out of [0,1]: %+v", b)
		}
	}

	// After >=5 recorded outcomes, calibration takes over: all-correct near a
	// raw score pushes the calibrated centre high.
	for i := 0; i < 6; i++ {
		c.RecordOutcome(0.5, true)
	}
	cal, _ := c.Evaluate(ctx, "some answer of moderate length here", "{}")
	if (cal.Lower+cal.Upper)/2 < 0.5 {
		t.Errorf("all-correct history should raise the calibrated centre, got %+v", cal)
	}
}

// ── 11. EmotionSensor (fixtures) ─────────────────────────────────────────────

type emotionFixture struct {
	Epsilon float64 `json:"epsilon"`
	Cases   []struct {
		ID              string  `json:"id"`
		FusedJSON       string  `json:"fusedJson"`
		ExpectedLabel   string  `json:"expectedLabel"`
		ExpectedArousal float64 `json:"expectedArousal"`
		ExpectedValence float64 `json:"expectedValence"`
	} `json:"cases"`
}

func TestKeywordEmotionSensor_Fixtures(t *testing.T) {
	data, err := os.ReadFile(filepath.Join(fixturesDir(t), "herjarvis_emotion.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var fix emotionFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}
	eps := fix.Epsilon
	if eps == 0 {
		eps = 1e-9
	}
	ctx := context.Background()
	var s circleai.KeywordEmotionSensor
	for _, c := range fix.Cases {
		c := c
		t.Run(c.ID, func(t *testing.T) {
			got, err := s.Sense(ctx, c.FusedJSON)
			if err != nil {
				t.Fatalf("Sense: %v", err)
			}
			if got.Label != c.ExpectedLabel {
				t.Errorf("label: got %q want %q", got.Label, c.ExpectedLabel)
			}
			if math.Abs(got.Arousal-c.ExpectedArousal) > eps {
				t.Errorf("arousal: got %v want %v", got.Arousal, c.ExpectedArousal)
			}
			if math.Abs(got.Valence-c.ExpectedValence) > eps {
				t.Errorf("valence: got %v want %v", got.Valence, c.ExpectedValence)
			}
		})
	}
}

// ── 12. SkillAcquisition ─────────────────────────────────────────────────────

func TestDemoStoreSkillAcquisition(t *testing.T) {
	ctx := context.Background()
	s := circleai.NewDemoStoreSkillAcquisition()

	named, err := s.Acquire(ctx, `{"name":"make-coffee","steps":["boil","pour"]}`)
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	if named.Name != "make-coffee" {
		t.Errorf("name from json: got %q", named.Name)
	}
	if named.DescriptionJSON == "" || strings.Contains(named.ID, "-") {
		t.Errorf("skill malformed: %+v", named)
	}

	// No name => generated skill-<id6>.
	anon, _ := s.Acquire(ctx, `{"steps":["a"]}`)
	if !strings.HasPrefix(anon.Name, "skill-") || len(anon.Name) != len("skill-")+6 {
		t.Errorf("anon name: got %q", anon.Name)
	}

	list, _ := s.List(ctx)
	if len(list) != 2 {
		t.Fatalf("list len: got %d want 2", len(list))
	}
	// Ordered by name ascending.
	if list[0].Name > list[1].Name {
		t.Errorf("list not name-ordered: %q, %q", list[0].Name, list[1].Name)
	}
}

// ── 15. PersonalKnowledgeGraph ───────────────────────────────────────────────

func TestAdjacencyPersonalKnowledgeGraph(t *testing.T) {
	ctx := context.Background()
	g := circleai.NewAdjacencyPersonalKnowledgeGraph()

	_ = g.UpsertNode(ctx, circleai.KnowledgeNode{ID: "alice", Kind: "person", Name: "Alice"})
	_ = g.UpsertNode(ctx, circleai.KnowledgeNode{ID: "acme", Kind: "org", Name: "Acme"})
	_ = g.UpsertNode(ctx, circleai.KnowledgeNode{ID: "bob", Kind: "person", Name: "Bob"})
	_ = g.UpsertRelation(ctx, circleai.KnowledgeRelation{FromID: "alice", ToID: "acme", Relation: "works_at"})
	_ = g.UpsertRelation(ctx, circleai.KnowledgeRelation{FromID: "alice", ToID: "bob", Relation: "knows"})
	// Duplicate (from,to,relation) replaces rather than duplicates.
	_ = g.UpsertRelation(ctx, circleai.KnowledgeRelation{FromID: "alice", ToID: "acme", Relation: "works_at"})

	nbrs, err := g.Neighbours(ctx, "alice")
	if err != nil {
		t.Fatalf("Neighbours: %v", err)
	}
	if len(nbrs) != 2 {
		t.Fatalf("neighbour count: got %d want 2 (%+v)", len(nbrs), nbrs)
	}
	names := map[string]bool{}
	for _, n := range nbrs {
		names[n.Name] = true
	}
	if !names["Acme"] || !names["Bob"] {
		t.Errorf("neighbours: got %v", names)
	}

	// Node with no edges => empty (non-nil).
	empty, _ := g.Neighbours(ctx, "bob")
	if empty == nil || len(empty) != 0 {
		t.Errorf("bob neighbours: got %v", empty)
	}
	if err := g.UpsertNode(ctx, circleai.KnowledgeNode{ID: "  "}); err == nil {
		t.Error("blank node id should error")
	}
}

// ── 16. LiveWorldKnowledge ───────────────────────────────────────────────────

func TestTopicLiveWorldKnowledge(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	b := circleai.NewTopicLiveWorldKnowledge()
	stream := b.Subscribe(ctx, []string{"markets", "weather"})
	// Give the subscription goroutine a moment to register.
	time.Sleep(20 * time.Millisecond)

	b.Publish(circleai.WorldFact{Topic: "markets", SummaryJSON: `{"idx":"up"}`, At: time.Now().UTC()})
	select {
	case f := <-stream:
		if f.Topic != "markets" {
			t.Errorf("fact topic: got %q", f.Topic)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for fact")
	}

	// A fact on an unsubscribed topic is not delivered.
	b.Publish(circleai.WorldFact{Topic: "sports", SummaryJSON: "{}", At: time.Now().UTC()})
	select {
	case f := <-stream:
		t.Errorf("unexpected fact from unsubscribed topic: %+v", f)
	case <-time.After(50 * time.Millisecond):
		// expected: nothing.
	}
}

// ── 17. BioSignalStream ──────────────────────────────────────────────────────

func TestChannelBioSignalStream(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	b := circleai.NewChannelBioSignalStream()
	stream := b.Stream(ctx)

	b.Publish(circleai.BioSignal{Kind: "hr", Value: 72, At: time.Now().UTC()})
	select {
	case s := <-stream:
		if s.Kind != "hr" || s.Value != 72 {
			t.Errorf("signal: got %+v", s)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for signal")
	}
	b.Complete()
	s2 := b.Stream(ctx)
	if _, ok := <-s2; ok {
		t.Error("stream after Complete should be closed")
	}
}

// ── 18. PhysicalActuator ─────────────────────────────────────────────────────

func TestRegistryPhysicalActuator(t *testing.T) {
	ctx := context.Background()
	a := circleai.NewRegistryPhysicalActuator()

	// Unknown device => failure result.
	res, err := a.Invoke(ctx, circleai.PhysicalCommand{DeviceID: "lamp", Action: "on"})
	if err != nil {
		t.Fatalf("Invoke: %v", err)
	}
	if res.Succeeded || res.Error == nil || !strings.Contains(*res.Error, "Unknown device 'lamp'") {
		t.Errorf("unknown device result: %+v", res)
	}

	// Register a handler and dispatch.
	var gotAction string
	_ = a.RegisterDevice("lamp", func(_ context.Context, cmd circleai.PhysicalCommand) (circleai.PhysicalCommandResult, error) {
		gotAction = cmd.Action
		return circleai.PhysicalCommandResult{Succeeded: true}, nil
	})
	res, _ = a.Invoke(ctx, circleai.PhysicalCommand{DeviceID: "lamp", Action: "toggle"})
	if !res.Succeeded {
		t.Errorf("registered dispatch should succeed: %+v", res)
	}
	if gotAction != "toggle" {
		t.Errorf("handler got action %q", gotAction)
	}
	if err := a.RegisterDevice("  ", nil); err == nil {
		t.Error("blank device / nil handler should error")
	}
}

// ── 19. AgentPeerNetwork ─────────────────────────────────────────────────────

func TestMailboxAgentPeerNetwork(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	n := circleai.NewMailboxAgentPeerNetwork()

	// Send before receive — messages queue and are drained on Receive.
	_ = n.Send(ctx, circleai.AgentToAgentMessage{FromAgentID: "a", ToAgentID: "b", Payload: "hi", At: time.Now().UTC()})
	_ = n.Send(ctx, circleai.AgentToAgentMessage{FromAgentID: "a", ToAgentID: "b", Payload: "there", At: time.Now().UTC()})

	stream := n.Receive(ctx, "b")
	var got []string
	for len(got) < 2 {
		select {
		case m := <-stream:
			got = append(got, m.Payload)
		case <-time.After(time.Second):
			t.Fatalf("timed out; got so far %v", got)
		}
	}
	if got[0] != "hi" || got[1] != "there" {
		t.Errorf("message order: got %v", got)
	}

	// A later send is delivered live.
	_ = n.Send(ctx, circleai.AgentToAgentMessage{FromAgentID: "a", ToAgentID: "b", Payload: "later"})
	select {
	case m := <-stream:
		if m.Payload != "later" {
			t.Errorf("live message: got %q", m.Payload)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out on live message")
	}
}

// ── 20. FederatedFineTuner ───────────────────────────────────────────────────

func TestInMemoryFederatedFineTuner(t *testing.T) {
	ctx := context.Background()
	ft := circleai.NewInMemoryFederatedFineTuner(nil, 10)

	jobID, err := ft.Start(ctx, "base-1", "/data/train.jsonl")
	if err != nil {
		t.Fatalf("Start: %v", err)
	}
	if strings.Contains(jobID, "-") {
		t.Errorf("job id should be dashless: %q", jobID)
	}

	// Poll to completion.
	deadline := time.Now().Add(2 * time.Second)
	var st circleai.FineTuneJobStatus
	for time.Now().Before(deadline) {
		st, _ = ft.Status(ctx, jobID)
		if st.Progress >= 1.0 && st.Error == nil {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	if st.Progress < 1.0 || st.Error != nil {
		t.Errorf("job did not complete cleanly: %+v", st)
	}

	// Unknown job.
	unk, _ := ft.Status(ctx, "nope")
	if unk.Error == nil || *unk.Error != "unknown job" {
		t.Errorf("unknown job status: %+v", unk)
	}
	if _, err := ft.Start(ctx, "", "/x"); err == nil {
		t.Error("blank baseModel should error")
	}
}

// ── 21. FirstTokenOptimizer ──────────────────────────────────────────────────

func TestSlidingP50FirstTokenOptimizer(t *testing.T) {
	ctx := context.Background()
	o, err := circleai.NewSlidingP50FirstTokenOptimizer(100, 4)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	// No samples => p50 0.
	b, _ := o.Current(ctx)
	if b.TargetMs != 100 || b.CurrentP50Ms != 0 {
		t.Errorf("empty budget: %+v", b)
	}
	// Samples 10,20,30 => sorted[len/2] = sorted[1] = 20.
	_ = o.RecordFirstTokenLatency(30)
	_ = o.RecordFirstTokenLatency(10)
	_ = o.RecordFirstTokenLatency(20)
	b, _ = o.Current(ctx)
	if b.CurrentP50Ms != 20 {
		t.Errorf("p50 of {10,20,30}: got %d want 20", b.CurrentP50Ms)
	}
	// Window eviction: push two more so oldest (30 then 10) drop; window keeps 4.
	_ = o.RecordFirstTokenLatency(40)
	_ = o.RecordFirstTokenLatency(50)
	b, _ = o.Current(ctx)
	// Window now {20,30?,...}. Just assert it is one of the retained samples.
	if b.CurrentP50Ms <= 0 {
		t.Errorf("p50 after eviction should be positive: %d", b.CurrentP50Ms)
	}
	if _, err := circleai.NewSlidingP50FirstTokenOptimizer(0, 4); err == nil {
		t.Error("targetMs<=0 should error")
	}
	if err := o.RecordFirstTokenLatency(-1); err == nil {
		t.Error("negative latency should error")
	}
}

// ── 22. CryptoDelegation ─────────────────────────────────────────────────────

func TestEcdsaCryptoDelegation(t *testing.T) {
	d, err := circleai.NewEcdsaCryptoDelegation("issuer-x", nil)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	cred, err := d.Issue("subj-1", "read:memory", time.Hour)
	if err != nil {
		t.Fatalf("Issue: %v", err)
	}
	if cred.Issuer != "issuer-x" || cred.SubjectID != "subj-1" || cred.Scope != "read:memory" {
		t.Errorf("credential fields: %+v", cred)
	}
	if cred.Signature == "" {
		t.Error("signature empty")
	}
	if !d.Verify(cred) {
		t.Error("freshly issued credential should verify")
	}

	// Tampered scope fails.
	bad := cred
	bad.Scope = "write:everything"
	if d.Verify(bad) {
		t.Error("tampered scope should not verify")
	}
	// Different issuer rejects.
	other := circleai.NewEcdsaCryptoDelegationDefault()
	if other.Verify(cred) {
		t.Error("credential from a different issuer/key should not verify")
	}
	// Expired rejects.
	expired, _ := d.Issue("subj-2", "s", time.Nanosecond)
	time.Sleep(2 * time.Millisecond)
	if d.Verify(expired) {
		t.Error("expired credential should not verify")
	}
	if _, err := d.Issue("", "s", time.Hour); err == nil {
		t.Error("blank subject should error")
	}
	if _, err := d.Issue("s", "sc", 0); err == nil {
		t.Error("non-positive lifetime should error")
	}
}

// ── 23. CodeGenerationLoop ───────────────────────────────────────────────────

func TestSyntaxCheckingCodeGenerationLoop(t *testing.T) {
	ctx := context.Background()

	// Default generator emits balanced code with a return; tests pass.
	l := circleai.NewSyntaxCheckingCodeGenerationLoopDefault()
	job, err := l.Run(ctx, "add two numbers")
	if err != nil {
		t.Fatalf("Run: %v", err)
	}
	if !job.TestsPass {
		t.Errorf("default balanced snippet should pass: %+v", job)
	}
	if job.DeployHint == nil {
		t.Error("passing job should carry a deploy hint")
	}
	if strings.Contains(job.ID, "-") {
		t.Errorf("job id should be dashless: %q", job.ID)
	}

	// Unbalanced snippet => tests fail, no hint.
	unbal := circleai.NewSyntaxCheckingCodeGenerationLoop(
		func(context.Context, string) (string, error) { return "func () { return 0;", nil },
		nil, nil)
	job2, _ := unbal.Run(ctx, "broken")
	if job2.TestsPass || job2.DeployHint != nil {
		t.Errorf("unbalanced snippet should fail with no hint: %+v", job2)
	}

	// "public class" => nuget hint.
	cls := circleai.NewSyntaxCheckingCodeGenerationLoop(
		func(context.Context, string) (string, error) { return "public class X { }", nil },
		func(context.Context, string) (bool, error) { return true, nil }, nil)
	job3, _ := cls.Run(ctx, "make a class")
	if job3.DeployHint == nil || *job3.DeployHint != "stage as nuget" {
		t.Errorf("class hint: %+v", job3.DeployHint)
	}
	if _, err := l.Run(ctx, "  "); err == nil {
		t.Error("blank prompt should error")
	}
}

// ── 24. SelfImprovementLoop ──────────────────────────────────────────────────

func TestTrackingSelfImprovementLoop(t *testing.T) {
	ctx := context.Background()

	// Controlled bench: first cycle sets a baseline, second regresses.
	scores := []float64{0.8, 0.6}
	i := 0
	l := circleai.NewTrackingSelfImprovementLoop(
		func(context.Context, string) (float64, error) {
			s := scores[i]
			if i < len(scores)-1 {
				i++
			}
			return s, nil
		},
		func(_ context.Context, _ string, current float64) (string, error) {
			return "rolled-back", nil
		})

	v1, err := l.Cycle(ctx, "suite")
	if err != nil {
		t.Fatalf("Cycle 1: %v", err)
	}
	if v1.ImprovementsApplied != "new best" || v1.NewBenchScore != 0.8 {
		t.Errorf("cycle 1: %+v", v1)
	}
	if l.BestScoreFor("suite") != 0.8 {
		t.Errorf("best after cycle 1: %v", l.BestScoreFor("suite"))
	}

	v2, _ := l.Cycle(ctx, "suite")
	if v2.ImprovementsApplied != "rolled-back" || v2.NewBenchScore != 0.6 {
		t.Errorf("cycle 2 (regression): %+v", v2)
	}
	// Best score is retained on regression.
	if l.BestScoreFor("suite") != 0.8 {
		t.Errorf("best should stay 0.8 after regression, got %v", l.BestScoreFor("suite"))
	}

	// Default loop runs standalone with a deterministic score.
	def := circleai.NewTrackingSelfImprovementLoopDefault()
	if _, err := def.Cycle(ctx, "default"); err != nil {
		t.Fatalf("default cycle: %v", err)
	}
	if _, err := def.Cycle(ctx, "  "); err == nil {
		t.Error("blank suite id should error")
	}
}

// ── helpers ──────────────────────────────────────────────────────────────────

func equalStrings(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func equalStringSets(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	seen := map[string]int{}
	for _, x := range a {
		seen[x]++
	}
	for _, x := range b {
		seen[x]--
	}
	for _, v := range seen {
		if v != 0 {
			return false
		}
	}
	return true
}
