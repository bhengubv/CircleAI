// hosting_generative_ui.go
//
// Ports CircleAI.Hosting.GenerativeUI (2.0.2):
//   UiComponent, UiCatalogEntry, UiCatalogs.Default, IGenerativeUIRenderer,
//   RecordingGenerativeUIRenderer (IGenerativeUIRenderer.cs)
//   JsonRenderParser.Parse + DescribeCatalogForPrompt (JsonRenderParser.cs)
//
// An AI emits JSON constrained to a typed catalog; the host renders it. The
// parser rejects any kind not in the catalog and any property not declared on
// its kind (strict mode), matching the C# validation exactly.

package circleai

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
)

// UiComponent is one UI element produced by a generative-UI model. Ports
// CircleAI.Hosting.GenerativeUI.UiComponent (record). Children is nil when the
// component has none.
type UiComponent struct {
	// Kind is the catalog identifier, e.g. "card"/"button"/"list".
	Kind string
	// Properties is the bag of property values keyed by JSON property name.
	Properties map[string]interface{}
	// Children are optional nested components.
	Children []UiComponent
}

// UiCatalogEntry declares an allowed kind + its properties. Ports
// CircleAI.Hosting.GenerativeUI.UiCatalogEntry (record).
type UiCatalogEntry struct {
	// Kind e.g. "card".
	Kind string
	// Description is a one-line description used in the prompt.
	Description string
	// AllowedProperties maps property names to JSON Schema type strings.
	AllowedProperties map[string]string
	// AllowsChildren is whether the component may contain nested components.
	AllowsChildren bool
	// propOrder preserves declaration order for deterministic prompt output
	// (C# dictionary-initialiser order); populated by the catalog builders.
	propOrder []string
}

// DefaultUiCatalog is the minimal "chat assistant tool output" catalog. Ports
// CircleAI.Hosting.GenerativeUI.UiCatalogs.Default (card/list/button/textBlock/image).
func DefaultUiCatalog() []UiCatalogEntry {
	return []UiCatalogEntry{
		{
			Kind:              "card",
			Description:       "A bordered container with a title and body. May contain children.",
			AllowedProperties: map[string]string{"title": "string", "caption": "string?"},
			propOrder:         []string{"title", "caption"},
			AllowsChildren:    true,
		},
		{
			Kind:              "list",
			Description:       "An ordered or unordered list. Children are the list items.",
			AllowedProperties: map[string]string{"ordered": "boolean"},
			propOrder:         []string{"ordered"},
			AllowsChildren:    true,
		},
		{
			Kind:              "button",
			Description:       "A tappable button. Emit an action identifier when clicked.",
			AllowedProperties: map[string]string{"label": "string", "action": "string", "style": "string?"},
			propOrder:         []string{"label", "action", "style"},
		},
		{
			Kind:              "textBlock",
			Description:       "Inline text content, optionally markdown.",
			AllowedProperties: map[string]string{"text": "string", "markdown": "boolean?"},
			propOrder:         []string{"text", "markdown"},
		},
		{
			Kind:              "image",
			Description:       "An image displayed from a URL or data-URI.",
			AllowedProperties: map[string]string{"src": "string", "alt": "string?"},
			propOrder:         []string{"src", "alt"},
		},
	}
}

// IGenerativeUIRenderer materialises UiComponent records into a native UI. Ports
// CircleAI.Hosting.GenerativeUI.IGenerativeUIRenderer.
type IGenerativeUIRenderer interface {
	// Render renders a single root component.
	Render(ctx context.Context, root UiComponent) error
}

// RecordingGenerativeUIRenderer is a no-op renderer for tests and headless
// scenarios; it records the last rendered component and a render count. Ports
// CircleAI.Hosting.GenerativeUI.RecordingGenerativeUIRenderer.
type RecordingGenerativeUIRenderer struct {
	LastRendered *UiComponent
	RenderCount  int
}

// Render records the component.
func (r *RecordingGenerativeUIRenderer) Render(_ context.Context, root UiComponent) error {
	cp := root
	r.LastRendered = &cp
	r.RenderCount++
	return nil
}

var _ IGenerativeUIRenderer = (*RecordingGenerativeUIRenderer)(nil)

// ---------------------------------------------------------------------------
// JsonRenderParser
// ---------------------------------------------------------------------------

// ParseRenderJSON parses one JSON document into a UiComponent tree, validated
// against catalog. Ports CircleAI.Hosting.GenerativeUI.JsonRenderParser.Parse.
//
// When strict is true, unknown kinds and undeclared properties are errors. When
// false, an unknown kind becomes a textBlock carrying the raw kind for debugging.
func ParseRenderJSON(jsonText string, catalog []UiCatalogEntry, strict bool) (UiComponent, error) {
	if isBlank(jsonText) {
		return UiComponent{}, errArg("json must not be null or empty")
	}
	dec := json.NewDecoder(strings.NewReader(jsonText))
	dec.UseNumber()
	var root interface{}
	if err := dec.Decode(&root); err != nil {
		return UiComponent{}, err
	}

	index := make(map[string]UiCatalogEntry, len(catalog))
	for _, c := range catalog {
		index[strings.ToLower(c.Kind)] = c
	}
	return parseRenderElement(root, index, strict)
}

func parseRenderElement(el interface{}, catalog map[string]UiCatalogEntry, strict bool) (UiComponent, error) {
	obj, ok := el.(map[string]interface{})
	if !ok {
		return UiComponent{}, fmt.Errorf("expected JSON object, got %s", jsonKindName(el))
	}

	kind := ""
	if kv, ok := obj["kind"].(string); ok {
		kind = kv
	}
	if kind == "" {
		return UiComponent{}, fmt.Errorf("component missing required 'kind' field")
	}

	entry, known := catalog[strings.ToLower(kind)]
	if !known {
		if strict {
			return UiComponent{}, fmt.Errorf("unknown component kind '%s'", kind)
		}
		return UiComponent{
			Kind: "textBlock",
			Properties: map[string]interface{}{
				"text":     fmt.Sprintf("[unknown kind '%s']", kind),
				"markdown": false,
			},
		}, nil
	}

	props := map[string]interface{}{}
	if raw, ok := obj["properties"]; ok {
		if pobj, ok := raw.(map[string]interface{}); ok {
			for name, v := range pobj {
				if strict {
					if _, allowed := entry.AllowedProperties[name]; !allowed {
						return UiComponent{}, fmt.Errorf(
							"component '%s' does not allow property '%s'", kind, name)
					}
				}
				props[name] = toManagedRenderValue(v)
			}
		}
	}

	var children []UiComponent
	if raw, ok := obj["children"]; ok {
		if carr, ok := raw.([]interface{}); ok {
			if !entry.AllowsChildren {
				if strict {
					return UiComponent{}, fmt.Errorf("component '%s' does not allow children", kind)
				}
			} else {
				children = make([]UiComponent, 0, len(carr))
				for _, c := range carr {
					child, err := parseRenderElement(c, catalog, strict)
					if err != nil {
						return UiComponent{}, err
					}
					children = append(children, child)
				}
			}
		}
	}

	return UiComponent{Kind: kind, Properties: props, Children: children}, nil
}

// toManagedRenderValue mirrors JsonRenderParser.ToManaged: string→string,
// number→int64|float64, bool→bool, null→nil, array→[]interface{},
// object→map[string]interface{}.
func toManagedRenderValue(v interface{}) interface{} {
	switch t := v.(type) {
	case json.Number:
		if i, err := t.Int64(); err == nil {
			return i
		}
		f, _ := t.Float64()
		return f
	case string:
		return t
	case bool:
		return t
	case nil:
		return nil
	case []interface{}:
		out := make([]interface{}, len(t))
		for i, e := range t {
			out[i] = toManagedRenderValue(e)
		}
		return out
	case map[string]interface{}:
		out := make(map[string]interface{}, len(t))
		for k, e := range t {
			out[k] = toManagedRenderValue(e)
		}
		return out
	default:
		return nil
	}
}

func jsonKindName(v interface{}) string {
	switch v.(type) {
	case map[string]interface{}:
		return "Object"
	case []interface{}:
		return "Array"
	case string:
		return "String"
	case json.Number:
		return "Number"
	case bool:
		return "Boolean"
	case nil:
		return "Null"
	default:
		return "Unknown"
	}
}

// DescribeUiCatalogForPrompt builds a system-prompt snippet describing catalog
// to the model. Ports JsonRenderParser.DescribeCatalogForPrompt. Property lines
// follow declaration order; when order was not captured they are sorted for
// determinism.
func DescribeUiCatalogForPrompt(catalog []UiCatalogEntry) string {
	var b bytes.Buffer
	b.WriteString("You may respond with a single JSON object describing one UI component.\n")
	b.WriteString("Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }\n")
	b.WriteString("\n")
	b.WriteString("Allowed kinds:\n")
	for _, e := range catalog {
		b.WriteString(fmt.Sprintf("- %s — %s\n", e.Kind, e.Description))
		for _, name := range orderedPropNames(e) {
			b.WriteString(fmt.Sprintf("    - %s: %s\n", name, e.AllowedProperties[name]))
		}
		if e.AllowsChildren {
			b.WriteString("    - children: array of components\n")
		}
	}
	return b.String()
}

func orderedPropNames(e UiCatalogEntry) []string {
	if len(e.propOrder) == len(e.AllowedProperties) {
		return e.propOrder
	}
	names := make([]string, 0, len(e.AllowedProperties))
	for n := range e.AllowedProperties {
		names = append(names, n)
	}
	sort.Strings(names)
	return names
}
