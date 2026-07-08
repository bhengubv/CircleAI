//! generative_ui.rs
//!
//! (2.0.2) Generative UI plug point. Ported from
//! `GenerativeUI/IGenerativeUIRenderer.cs` + `GenerativeUI/JsonRenderParser.cs`.
//! The hosting layer feeds parsed [`UiComponent`] records to an
//! [`IGenerativeUIRenderer`], which materialises them into a native UI. The
//! [`JsonRenderParser`] validates an LLM-emitted JSON tree against a
//! [`UiCatalogEntry`] catalog so the model can't smuggle untyped components past
//! the host.
//!
//! Property values are `object?` in C#; the port uses [`serde_json::Value`].

use std::collections::HashMap;

use serde_json::Value;

/// One UI element produced by a generative-UI model. 1:1 with the C#
/// `UiComponent` record.
#[derive(Debug, Clone, PartialEq)]
pub struct UiComponent {
    /// Catalog identifier, e.g. `"card"`, `"button"`, `"list"`.
    pub kind: String,
    /// Bag of property values keyed by JSON property name.
    pub properties: HashMap<String, Value>,
    /// Optional nested components.
    pub children: Option<Vec<UiComponent>>,
}

impl UiComponent {
    pub fn new(
        kind: impl Into<String>,
        properties: HashMap<String, Value>,
        children: Option<Vec<UiComponent>>,
    ) -> Self {
        Self {
            kind: kind.into(),
            properties,
            children,
        }
    }
}

/// Catalog entry — declares the allowed kinds + their properties. 1:1 with the
/// C# `UiCatalogEntry` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UiCatalogEntry {
    /// e.g. `"card"`.
    pub kind: String,
    /// One-line description used in the prompt.
    pub description: String,
    /// Property names + JSON Schema type strings.
    pub allowed_properties: Vec<(String, String)>,
    /// Whether the component may contain nested components.
    pub allows_children: bool,
}

impl UiCatalogEntry {
    pub fn new(
        kind: impl Into<String>,
        description: impl Into<String>,
        allowed_properties: Vec<(String, String)>,
        allows_children: bool,
    ) -> Self {
        Self {
            kind: kind.into(),
            description: description.into(),
            allowed_properties,
            allows_children,
        }
    }

    fn allows_property(&self, name: &str) -> bool {
        self.allowed_properties.iter().any(|(k, _)| k == name)
    }
}

/// Pre-canned component catalogs the hosting layer ships out of the box. 1:1
/// with the C# `UiCatalogs`.
pub struct UiCatalogs;

impl UiCatalogs {
    /// Minimal "chat assistant tool output" catalog. Covers
    /// card / list / button / textBlock / image. 1:1 with the C#
    /// `UiCatalogs.Default`.
    pub fn default_catalog() -> Vec<UiCatalogEntry> {
        vec![
            UiCatalogEntry::new(
                "card",
                "A bordered container with a title and body. May contain children.",
                vec![
                    ("title".to_string(), "string".to_string()),
                    ("caption".to_string(), "string?".to_string()),
                ],
                true,
            ),
            UiCatalogEntry::new(
                "list",
                "An ordered or unordered list. Children are the list items.",
                vec![("ordered".to_string(), "boolean".to_string())],
                true,
            ),
            UiCatalogEntry::new(
                "button",
                "A tappable button. Emit an action identifier when clicked.",
                vec![
                    ("label".to_string(), "string".to_string()),
                    ("action".to_string(), "string".to_string()),
                    ("style".to_string(), "string?".to_string()),
                ],
                false,
            ),
            UiCatalogEntry::new(
                "textBlock",
                "Inline text content, optionally markdown.",
                vec![
                    ("text".to_string(), "string".to_string()),
                    ("markdown".to_string(), "boolean?".to_string()),
                ],
                false,
            ),
            UiCatalogEntry::new(
                "image",
                "An image displayed from a URL or data-URI.",
                vec![
                    ("src".to_string(), "string".to_string()),
                    ("alt".to_string(), "string?".to_string()),
                ],
                false,
            ),
        ]
    }
}

/// (2.0.2) Renderer contract. Consumers implement this in their host to
/// materialise [`UiComponent`] records into a native UI. 1:1 with the C#
/// `IGenerativeUIRenderer`.
pub trait IGenerativeUIRenderer: Send + Sync {
    /// Render a single root component.
    fn render(&self, root: &UiComponent);
}

/// Default no-op renderer for tests and headless server scenarios. Holds the
/// last rendered component for assertion. 1:1 with the C#
/// `RecordingGenerativeUIRenderer`.
#[derive(Debug, Default)]
pub struct RecordingGenerativeUIRenderer {
    inner: std::sync::Mutex<RecordingInner>,
}

#[derive(Debug, Default)]
struct RecordingInner {
    last_rendered: Option<UiComponent>,
    render_count: u32,
}

impl RecordingGenerativeUIRenderer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn last_rendered(&self) -> Option<UiComponent> {
        self.inner.lock().unwrap().last_rendered.clone()
    }

    pub fn render_count(&self) -> u32 {
        self.inner.lock().unwrap().render_count
    }
}

impl IGenerativeUIRenderer for RecordingGenerativeUIRenderer {
    fn render(&self, root: &UiComponent) {
        let mut inner = self.inner.lock().unwrap();
        inner.last_rendered = Some(root.clone());
        inner.render_count += 1;
    }
}

/// A parse error (mirrors the C# `InvalidOperationException` /
/// `ArgumentException` thrown by `JsonRenderParser.Parse`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderParseError(pub String);

impl std::fmt::Display for RenderParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for RenderParseError {}

/// (2.0.2) Strict JSON → [`UiComponent`] parser. Rejects any kind not in the
/// catalog and any property not declared on its kind. 1:1 with the C#
/// `JsonRenderParser`.
pub struct JsonRenderParser;

impl JsonRenderParser {
    /// Parse one JSON document into a [`UiComponent`] tree.
    ///
    /// When `strict` is true, unknown kinds/properties/children error. When
    /// false, an unknown kind becomes a `textBlock` with the raw marker.
    pub fn parse(
        json: &str,
        catalog: &[UiCatalogEntry],
        strict: bool,
    ) -> Result<UiComponent, RenderParseError> {
        if json.is_empty() {
            return Err(RenderParseError("json must not be null or empty.".to_string()));
        }
        let root: Value =
            serde_json::from_str(json).map_err(|e| RenderParseError(format!("Parse error: {e}")))?;
        Self::parse_element(&root, catalog, strict)
    }

    fn parse_element(
        el: &Value,
        catalog: &[UiCatalogEntry],
        strict: bool,
    ) -> Result<UiComponent, RenderParseError> {
        let obj = el.as_object().ok_or_else(|| {
            RenderParseError(format!("Expected JSON object, got {}.", value_kind(el)))
        })?;

        let kind = obj.get("kind").and_then(|k| k.as_str()).unwrap_or("");
        if kind.is_empty() {
            return Err(RenderParseError(
                "Component missing required 'kind' field.".to_string(),
            ));
        }

        let entry = catalog.iter().find(|c| c.kind.eq_ignore_ascii_case(kind));
        let entry = match entry {
            Some(e) => e,
            None => {
                if strict {
                    return Err(RenderParseError(format!("Unknown component kind '{kind}'.")));
                }
                let mut props = HashMap::new();
                props.insert("text".to_string(), Value::String(format!("[unknown kind '{kind}']")));
                props.insert("markdown".to_string(), Value::Bool(false));
                return Ok(UiComponent::new("textBlock", props, None));
            }
        };

        let mut props: HashMap<String, Value> = HashMap::new();
        if let Some(props_obj) = obj.get("properties").and_then(|p| p.as_object()) {
            for (name, value) in props_obj {
                if strict && !entry.allows_property(name) {
                    return Err(RenderParseError(format!(
                        "Component '{kind}' does not allow property '{name}'."
                    )));
                }
                props.insert(name.clone(), to_managed(value));
            }
        }

        let mut children: Option<Vec<UiComponent>> = None;
        if let Some(child_arr) = obj.get("children").and_then(|c| c.as_array()) {
            if !entry.allows_children {
                if strict {
                    return Err(RenderParseError(format!(
                        "Component '{kind}' does not allow children."
                    )));
                }
            } else {
                let mut list = Vec::with_capacity(child_arr.len());
                for c in child_arr {
                    list.push(Self::parse_element(c, catalog, strict)?);
                }
                children = Some(list);
            }
        }

        Ok(UiComponent::new(kind, props, children))
    }

    /// Build a system-prompt snippet describing the catalog to the model. 1:1
    /// with the C# `DescribeCatalogForPrompt`.
    pub fn describe_catalog_for_prompt(catalog: &[UiCatalogEntry]) -> String {
        let mut sb = String::new();
        sb.push_str("You may respond with a single JSON object describing one UI component.\n");
        sb.push_str(
            "Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }\n",
        );
        sb.push('\n');
        sb.push_str("Allowed kinds:\n");
        for e in catalog {
            sb.push_str(&format!("- {} — {}\n", e.kind, e.description));
            for (name, type_str) in &e.allowed_properties {
                sb.push_str(&format!("    - {name}: {type_str}\n"));
            }
            if e.allows_children {
                sb.push_str("    - children: array of components\n");
            }
        }
        sb
    }
}

/// Convert a JSON value to the C# "managed" projection: numbers become an int64
/// when integral (and in range), else a double; arrays/objects recurse.
fn to_managed(v: &Value) -> Value {
    match v {
        Value::String(_) | Value::Bool(_) | Value::Null => v.clone(),
        Value::Number(n) => {
            if let Some(i) = n.as_i64() {
                Value::Number(i.into())
            } else {
                // Falls back to f64 (GetDouble()).
                serde_json::Number::from_f64(n.as_f64().unwrap_or(0.0))
                    .map(Value::Number)
                    .unwrap_or(Value::Null)
            }
        }
        Value::Array(a) => Value::Array(a.iter().map(to_managed).collect()),
        Value::Object(o) => {
            Value::Object(o.iter().map(|(k, val)| (k.clone(), to_managed(val))).collect())
        }
    }
}

fn value_kind(v: &Value) -> &'static str {
    match v {
        Value::Null => "Null",
        Value::Bool(_) => "True/False",
        Value::Number(_) => "Number",
        Value::String(_) => "String",
        Value::Array(_) => "Array",
        Value::Object(_) => "Object",
    }
}
