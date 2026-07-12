// workflows_skills.go
//
// Ports CircleAI.Workflows/PacaSkills.cs — the eleven built-in paca Claude Code
// skills, the nine creator-skill templates, and a filesystem installer that
// strips frontmatter and drops the markdown into a commands directory.
//
//	PacaSkill (record)   -> PacaSkill (with ToMarkdown / ToBodyOnly)
//	PacaSkillLibrary (static) -> PacaSkillLibraryAll + PacaSkillFind
//	SkillTemplates (const strings) -> package consts
//	PacaSkillInstaller   -> PacaSkillInstaller (writes real files)
//
// The C# names collide with the wider port (PacaSkill vs Skills package) only in
// spirit — there is no existing PacaSkill type. The installer is real
// filesystem I/O (Directory.CreateDirectory + File.WriteAllText/Delete) ported
// with os/path.

package circleai

import (
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// PacaSkill is a skill definition: frontmatter metadata + body. Ports the
// PacaSkill record.
type PacaSkill struct {
	Name        string
	Description string
	Body        string
}

// ToMarkdown renders the skill as a Claude-Code markdown file with frontmatter.
// Ports ToMarkdown.
func (s PacaSkill) ToMarkdown() string {
	return "---\nname: " + s.Name + "\ndescription: " + s.Description + "\n---\n\n" + s.Body
}

// ToBodyOnly renders the bare body. Ports ToBodyOnly.
func (s PacaSkill) ToBodyOnly() string { return s.Body }

// The nine creator-skill templates (markdown body). Port SkillTemplates.
const (
	// SkillTemplateEpic ports SkillTemplates.Epic.
	SkillTemplateEpic = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks."
	// SkillTemplateBreakdown ports SkillTemplates.Breakdown.
	SkillTemplateBreakdown = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria."
	// SkillTemplateClarify ports SkillTemplates.Clarify.
	SkillTemplateClarify = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task."
	// SkillTemplateSprint ports SkillTemplates.Sprint.
	SkillTemplateSprint = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools."
	// SkillTemplateEstimate ports SkillTemplates.Estimate.
	SkillTemplateEstimate = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions."
	// SkillTemplatePrioritize ports SkillTemplates.Prioritize.
	SkillTemplatePrioritize = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning."
	// SkillTemplateDo ports SkillTemplates.Do.
	SkillTemplateDo = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done."
	// SkillTemplateTest ports SkillTemplates.Test.
	SkillTemplateTest = "You are running paca-test. Write and run unit + integration tests for the current change."
	// SkillTemplateDoc ports SkillTemplates.Doc.
	SkillTemplateDoc = "You are running paca-doc. Update the living document with the smallest accurate diff."
)

// PacaSkillLibraryAll is the full set of eleven built-in paca skills. Ports
// PacaSkillLibrary.All.
var PacaSkillLibraryAll = []PacaSkill{
	{"paca", "Run the paca workflow on the current ask.", "Use the paca MCP tools to plan and execute the user's request."},
	{"paca-epic", "Capture a large initiative as a paca epic.", SkillTemplateEpic},
	{"paca-breakdown", "Break a paca epic into actionable tasks.", SkillTemplateBreakdown},
	{"paca-clarify", "Ask the right clarifying questions before estimating.", SkillTemplateClarify},
	{"paca-sprint", "Form / close a sprint with the paca sprint surface.", SkillTemplateSprint},
	{"paca-estimate", "Estimate story points for a set of tasks.", SkillTemplateEstimate},
	{"paca-prioritize", "Reorder the backlog by importance.", SkillTemplatePrioritize},
	{"paca-do", "Pick the next-best task and start it.", SkillTemplateDo},
	{"paca-test", "Generate and run tests for the current change.", SkillTemplateTest},
	{"paca-doc", "Update the project's living doc to reflect the latest change.", SkillTemplateDoc},
	{"paca-setup", "First-run setup: pick project, configure agents, install plugins.", "Walk the user through paca first-run setup."},
}

// PacaSkillFind returns the built-in skill with the given name (case-insensitive)
// and true, or (zero, false). Ports PacaSkillLibrary.Find.
func PacaSkillFind(name string) (PacaSkill, bool) {
	for _, s := range PacaSkillLibraryAll {
		if strings.EqualFold(s.Name, name) {
			return s, true
		}
	}
	return PacaSkill{}, false
}

var skillFrontmatterPattern = regexp.MustCompile(`(?s)^\s*---.*?---\s*\n`)

// PacaSkillInstaller drops bare skill bodies into a commands directory. Ports
// PacaSkillInstaller. Construct with NewPacaSkillInstaller.
type PacaSkillInstaller struct {
	commandsDir string
}

// NewPacaSkillInstaller constructs the installer. Returns an error if
// commandsDir is blank (mirrors the C# ArgumentException).
func NewPacaSkillInstaller(commandsDir string) (*PacaSkillInstaller, error) {
	if strings.TrimSpace(commandsDir) == "" {
		return nil, errors.New("commandsDir required")
	}
	return &PacaSkillInstaller{commandsDir: commandsDir}, nil
}

// InstallAll installs all built-in skills. Ports InstallAll.
func (i *PacaSkillInstaller) InstallAll() ([]string, error) {
	return i.InstallEach(PacaSkillLibraryAll)
}

// InstallEach installs a custom set of skills, returning the written paths.
// Ports InstallEach. Each file is written as UTF-8 without a BOM.
func (i *PacaSkillInstaller) InstallEach(skills []PacaSkill) ([]string, error) {
	if err := os.MkdirAll(i.commandsDir, 0o755); err != nil {
		return nil, err
	}
	installed := make([]string, 0, len(skills))
	for _, skill := range skills {
		path := filepath.Join(i.commandsDir, skill.Name+".md")
		body := StripSkillFrontmatter(skill.ToMarkdown())
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			return installed, err
		}
		installed = append(installed, path)
	}
	return installed, nil
}

// UninstallByName deletes the named skill files, returning the count removed.
// Ports UninstallByName.
func (i *PacaSkillInstaller) UninstallByName(names []string) (int, error) {
	count := 0
	for _, name := range names {
		path := filepath.Join(i.commandsDir, name+".md")
		if _, err := os.Stat(path); err == nil {
			if err := os.Remove(path); err != nil {
				return count, err
			}
			count++
		}
	}
	return count, nil
}

// StripSkillFrontmatter strips a leading frontmatter block from a markdown
// string. Ports the static StripFrontmatter.
func StripSkillFrontmatter(markdown string) string {
	if markdown == "" {
		return ""
	}
	loc := skillFrontmatterPattern.FindStringIndex(markdown)
	if loc == nil || loc[0] != 0 {
		return strings.TrimLeft(markdown, " \t\r\n")
	}
	return strings.TrimLeft(markdown[loc[1]:], " \t\r\n")
}
