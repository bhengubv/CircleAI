// workflows_agents.go
//
// Ports CircleAI.Workflows/PacaAgents.cs — AI agents as first-class project
// members. One store holds humans + agents (shared identity: id, handle, role,
// avatar); agents add an LLM config, context system prompts, capability flags,
// runtime limits, git identity, and trigger keywords. Five preset templates
// ship out of the box.
//
//	MemberKind (enum)  -> int consts (Human=0, Agent=1)
//	ProjectMember / AgentLlmConfig / AgentSystemPrompts / AgentCapabilities /
//	AgentLimits / AgentGitIdentity / AgentTriggers / AgentProfile (records) -> structs
//	AgentTemplates (static presets) -> package funcs + AgentTemplatePresetNames
//	InMemoryPacaMemberStore -> InMemoryPacaMemberStore
//
// C# nullable string?/Uri? map to string ("" = unset) — the port keeps the LLM
// BaseAddress as a string since it's config, never dereferenced here.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// MemberKind is a project member's kind. Ports MemberKind (Human=0, Agent=1).
type MemberKind int

const (
	// MemberKindHuman — a human member.
	MemberKindHuman MemberKind = 0
	// MemberKindAgent — an AI agent member.
	MemberKindAgent MemberKind = 1
)

// ProjectMember is the shared identity for humans + agents in a project. Ports
// the ProjectMember record. AvatarURL is empty and DeletedAtUTC is nil when
// unset.
type ProjectMember struct {
	ID           string
	ProjectID    string
	Kind         MemberKind
	DisplayName  string
	Handle       string // "@sipho" or "@billing-agent"
	Role         string
	AvatarURL    string
	CreatedAtUTC time.Time
	DeletedAtUTC *time.Time
}

// AgentLlmConfig is a per-agent LLM config. Ports the AgentLlmConfig record.
// APIKey / BaseAddress are empty when unset.
type AgentLlmConfig struct {
	Provider    string
	Model       string
	APIKey      string
	BaseAddress string
}

// AgentSystemPrompts holds per-agent context-specific system prompts. Ports the
// AgentSystemPrompts record.
type AgentSystemPrompts struct {
	TaskPrompt string
	DocPrompt  string
	ChatPrompt string
}

// AgentCapabilities is the flag set an agent is permitted to do. Ports the
// AgentCapabilities record.
type AgentCapabilities struct {
	CanCloneRepos        bool
	CanCreatePRs         bool
	CanWriteFiles        bool
	CanCallExternalTools bool
}

// AgentLimits are the runtime limits an agent must respect. Ports the
// AgentLimits record.
type AgentLimits struct {
	MaxIterations int
	Timeout       time.Duration
}

// AgentGitIdentity is the git identity an agent commits under. Ports the
// AgentGitIdentity record.
type AgentGitIdentity struct {
	Name  string
	Email string
}

// AgentTriggers holds the trigger keywords that wake an agent per event class.
// Ports the AgentTriggers record (empty string = no trigger for that class).
type AgentTriggers struct {
	TaskCreated   string
	ChatMention   string
	DocEdit       string
	DirectMention string
}

// AgentProfile is a full agent profile. Ports the AgentProfile record.
type AgentProfile struct {
	MemberID     string
	Llm          AgentLlmConfig
	Prompts      AgentSystemPrompts
	Capabilities AgentCapabilities
	Limits       AgentLimits
	GitIdentity  AgentGitIdentity
	Triggers     AgentTriggers
}

// AgentTemplatePresetNames is the ordered list of preset template names. Ports
// AgentTemplates.PresetNames.
var AgentTemplatePresetNames = []string{"development", "pm", "design", "qa", "review"}

// DevelopmentAgentTemplate builds the "development" preset. Ports
// AgentTemplates.DevelopmentAgent.
func DevelopmentAgentTemplate(memberID, apiKey, baseAddress string) AgentProfile {
	return AgentProfile{
		MemberID: memberID,
		Llm:      AgentLlmConfig{Provider: "openai", Model: "gpt-4o-mini", APIKey: apiKey, BaseAddress: baseAddress},
		Prompts: AgentSystemPrompts{
			TaskPrompt: "You are a senior developer. Implement requested changes, write tests, open PRs.",
			DocPrompt:  "You write engineering docs that are precise and example-driven.",
			ChatPrompt: "You answer engineering questions with concrete code samples.",
		},
		Capabilities: AgentCapabilities{CanCloneRepos: true, CanCreatePRs: true, CanWriteFiles: true, CanCallExternalTools: true},
		Limits:       AgentLimits{MaxIterations: 25, Timeout: 10 * time.Minute},
		GitIdentity:  AgentGitIdentity{Name: "CircleAI Dev Agent", Email: "dev-agent@circleai.local"},
		Triggers:     AgentTriggers{TaskCreated: "dev", ChatMention: "@dev", DirectMention: "dev"},
	}
}

// ProductManagerAgentTemplate builds the "pm" preset. Ports
// AgentTemplates.ProductManagerAgent.
func ProductManagerAgentTemplate(memberID, apiKey string) AgentProfile {
	return AgentProfile{
		MemberID: memberID,
		Llm:      AgentLlmConfig{Provider: "openai", Model: "gpt-4o-mini", APIKey: apiKey},
		Prompts: AgentSystemPrompts{
			TaskPrompt: "You are a product manager. Triage tasks, break them down, assign owners.",
			DocPrompt:  "You write product specs and PRDs.",
			ChatPrompt: "You answer product/priority questions.",
		},
		Capabilities: AgentCapabilities{CanCloneRepos: false, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: true},
		Limits:       AgentLimits{MaxIterations: 15, Timeout: 5 * time.Minute},
		GitIdentity:  AgentGitIdentity{Name: "CircleAI PM Agent", Email: "pm-agent@circleai.local"},
		Triggers:     AgentTriggers{TaskCreated: "pm", ChatMention: "@pm", DocEdit: "@pm", DirectMention: "pm"},
	}
}

// DesignerAgentTemplate builds the "design" preset. Ports
// AgentTemplates.DesignerAgent.
func DesignerAgentTemplate(memberID, apiKey string) AgentProfile {
	return AgentProfile{
		MemberID: memberID,
		Llm:      AgentLlmConfig{Provider: "openai", Model: "gpt-4o-mini", APIKey: apiKey},
		Prompts: AgentSystemPrompts{
			TaskPrompt: "You are a designer. Sketch UI ideas, write copy, propose flows.",
			DocPrompt:  "You write design memos.",
			ChatPrompt: "You answer design questions and propose concepts.",
		},
		Capabilities: AgentCapabilities{CanCloneRepos: false, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: false},
		Limits:       AgentLimits{MaxIterations: 10, Timeout: 5 * time.Minute},
		GitIdentity:  AgentGitIdentity{Name: "CircleAI Design Agent", Email: "design-agent@circleai.local"},
		Triggers:     AgentTriggers{TaskCreated: "design", ChatMention: "@design", DocEdit: "@design", DirectMention: "design"},
	}
}

// QaAgentTemplate builds the "qa" preset. Ports AgentTemplates.QaAgent.
func QaAgentTemplate(memberID, apiKey string) AgentProfile {
	return AgentProfile{
		MemberID: memberID,
		Llm:      AgentLlmConfig{Provider: "openai", Model: "gpt-4o-mini", APIKey: apiKey},
		Prompts: AgentSystemPrompts{
			TaskPrompt: "You are a QA engineer. Write test plans, generate test cases, validate against AC.",
			DocPrompt:  "You write QA reports.",
			ChatPrompt: "You answer QA questions and propose test strategies.",
		},
		Capabilities: AgentCapabilities{CanCloneRepos: true, CanCreatePRs: false, CanWriteFiles: true, CanCallExternalTools: true},
		Limits:       AgentLimits{MaxIterations: 20, Timeout: 7 * time.Minute},
		GitIdentity:  AgentGitIdentity{Name: "CircleAI QA Agent", Email: "qa-agent@circleai.local"},
		Triggers:     AgentTriggers{TaskCreated: "qa", ChatMention: "@qa", DirectMention: "qa"},
	}
}

// CodeReviewerAgentTemplate builds the "review" preset. Ports
// AgentTemplates.CodeReviewerAgent.
func CodeReviewerAgentTemplate(memberID, apiKey string) AgentProfile {
	return AgentProfile{
		MemberID: memberID,
		Llm:      AgentLlmConfig{Provider: "openai", Model: "gpt-4o-mini", APIKey: apiKey},
		Prompts: AgentSystemPrompts{
			TaskPrompt: "You are a senior code reviewer. Comment for clarity, correctness, security.",
			DocPrompt:  "You write code review checklists.",
			ChatPrompt: "You answer questions about code patterns and best practices.",
		},
		Capabilities: AgentCapabilities{CanCloneRepos: true, CanCreatePRs: false, CanWriteFiles: false, CanCallExternalTools: true},
		Limits:       AgentLimits{MaxIterations: 15, Timeout: 7 * time.Minute},
		GitIdentity:  AgentGitIdentity{Name: "CircleAI Reviewer Agent", Email: "reviewer-agent@circleai.local"},
		Triggers:     AgentTriggers{ChatMention: "@review", DirectMention: "review"},
	}
}

// InMemoryPacaMemberStore holds members + agent profiles. Ports
// InMemoryPacaMemberStore. Construct with NewInMemoryPacaMemberStore.
type InMemoryPacaMemberStore struct {
	mu       sync.Mutex
	members  map[string]ProjectMember
	profiles map[string]AgentProfile
	clock    func() time.Time
}

// NewInMemoryPacaMemberStore constructs an empty store. clock may be nil.
func NewInMemoryPacaMemberStore(clock func() time.Time) *InMemoryPacaMemberStore {
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &InMemoryPacaMemberStore{
		members:  make(map[string]ProjectMember),
		profiles: make(map[string]AgentProfile),
		clock:    clock,
	}
}

// AddHuman adds a human member. Ports AddHuman. role defaults to "developer".
func (s *InMemoryPacaMemberStore) AddHuman(id, projectID, displayName, handle, role, avatar string) (ProjectMember, error) {
	if role == "" {
		role = "developer"
	}
	return s.addMember(id, projectID, MemberKindHuman, displayName, handle, role, avatar)
}

// AddAgent adds an agent member and stores its profile (with MemberID pinned to
// id). Ports AddAgent.
func (s *InMemoryPacaMemberStore) AddAgent(id, projectID, displayName, handle string, profile AgentProfile, avatar string) (ProjectMember, error) {
	member, err := s.addMember(id, projectID, MemberKindAgent, displayName, handle, "agent", avatar)
	if err != nil {
		return ProjectMember{}, err
	}
	profile.MemberID = id
	s.mu.Lock()
	s.profiles[id] = profile
	s.mu.Unlock()
	return member, nil
}

func (s *InMemoryPacaMemberStore) addMember(id, projectID string, kind MemberKind, displayName, handle, role, avatar string) (ProjectMember, error) {
	if strings.TrimSpace(id) == "" {
		return ProjectMember{}, errors.New("id required")
	}
	if strings.TrimSpace(projectID) == "" {
		return ProjectMember{}, errors.New("projectId required")
	}
	if strings.TrimSpace(displayName) == "" {
		return ProjectMember{}, errors.New("displayName required")
	}
	if strings.TrimSpace(handle) == "" {
		return ProjectMember{}, errors.New("handle required")
	}
	member := ProjectMember{
		ID:           id,
		ProjectID:    projectID,
		Kind:         kind,
		DisplayName:  displayName,
		Handle:       handle,
		Role:         role,
		AvatarURL:    avatar,
		CreatedAtUTC: s.clock(),
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.members[id]; exists {
		return ProjectMember{}, errors.New("Member '" + id + "' already exists.")
	}
	s.members[id] = member
	return member, nil
}

// GetMember returns a live member and true, or (zero, false). Ports GetMember.
func (s *InMemoryPacaMemberStore) GetMember(id string) (ProjectMember, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.getMemberLocked(id)
}

func (s *InMemoryPacaMemberStore) getMemberLocked(id string) (ProjectMember, bool) {
	m, ok := s.members[id]
	if !ok || m.DeletedAtUTC != nil {
		return ProjectMember{}, false
	}
	return m, true
}

// GetAgentProfile returns an agent's profile and true, or (zero, false). Ports
// GetAgentProfile.
func (s *InMemoryPacaMemberStore) GetAgentProfile(memberID string) (AgentProfile, bool) {
	s.mu.Lock()
	p, ok := s.profiles[memberID]
	s.mu.Unlock()
	return p, ok
}

// ListMembers lists live members in a project, ordered by display name,
// optionally filtered by kind (kind nil = all). Ports ListMembers.
func (s *InMemoryPacaMemberStore) ListMembers(projectID string, kind *MemberKind) []ProjectMember {
	s.mu.Lock()
	out := make([]ProjectMember, 0)
	for _, m := range s.members {
		if m.ProjectID == projectID && m.DeletedAtUTC == nil && (kind == nil || m.Kind == *kind) {
			out = append(out, m)
		}
	}
	s.mu.Unlock()
	sort.Slice(out, func(i, j int) bool { return out[i].DisplayName < out[j].DisplayName })
	return out
}

// RemoveMember soft-deletes a member. Idempotent. Ports RemoveMember.
func (s *InMemoryPacaMemberStore) RemoveMember(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	existing, ok := s.members[id]
	if !ok || existing.DeletedAtUTC != nil {
		return
	}
	now := s.clock()
	existing.DeletedAtUTC = &now
	s.members[id] = existing
}

// UpdateAgentProfile replaces an agent's profile (MemberID pinned to memberID).
// Ports UpdateAgentProfile. Returns an error if the member is not an agent.
func (s *InMemoryPacaMemberStore) UpdateAgentProfile(memberID string, updated AgentProfile) (AgentProfile, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	m, ok := s.getMemberLocked(memberID)
	if !ok || m.Kind != MemberKindAgent {
		return AgentProfile{}, errors.New("Member '" + memberID + "' is not an agent.")
	}
	updated.MemberID = memberID
	s.profiles[memberID] = updated
	return updated, nil
}
