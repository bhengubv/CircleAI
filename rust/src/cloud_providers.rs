//! Cloud providers - chat, speech and images - and the local inference server.
//!
//! THE ORDER IS ON-DEVICE FIRST, ALWAYS. These exist for what a device
//! genuinely cannot do, and for nothing else. A build that reaches for a cloud
//! recogniser because it is more accurate in English has sent every household's
//! audio to a company to solve a problem they did not have.
//!
//! SENDING AUDIO IS DIFFERENT FROM SENDING TEXT. A transcript is what somebody
//! said; a recording is their VOICE - who they are, who else was in the room,
//! and a biometric they cannot change.
//!
//! THE KEY NEVER APPEARS IN A LOG, AN ERROR, A DEBUG PRINT OR A URL. `Secret`
//! below has no `derive(Debug)` and its hand-written one redacts, because a key
//! reaches a log through `{:?}` far more often than through a deliberate print.
//!
//! AND THE SERVER BINDS TO LOOPBACK. A phone that binds 0.0.0.0 is an open
//! inference endpoint on whatever Wi-Fi it joins, so a wider bind is refused
//! without a key rather than warned about - a warning at startup is a line of
//! log nobody reads.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Secrets

/// Holds a key so it cannot be printed by accident.
///
/// No `derive(Debug)`, no `Display`. `reveal` is the ONE way out, named so it is
/// visible at every call site.
#[derive(Clone, Default, PartialEq, Eq)]
pub struct Secret {
    value: String,
}

impl Secret {
    pub fn new(value: &str) -> Self {
        Self { value: value.to_string() }
    }

    pub fn reveal(&self) -> &str {
        &self.value
    }

    pub fn is_set(&self) -> bool {
        !self.value.is_empty()
    }
}

impl std::fmt::Debug for Secret {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(if self.is_set() { "<secret set>" } else { "<secret unset>" })
    }
}

/// The identifiers a person consents to, one per provider.
///
/// STRINGS, not an enum, because a host may carry a provider this build has
/// never heard of - an OpenAI-compatible endpoint on somebody's own hardware is
/// the common case, and an enum would make that the one thing impossible.
pub struct ProviderIds;

impl ProviderIds {
    pub const OPENAI: &'static str = "openai";
    pub const ANTHROPIC: &'static str = "anthropic";
    pub const GEMINI: &'static str = "gemini";
    pub const GROQ: &'static str = "groq";
    pub const CEREBRAS: &'static str = "cerebras";
    pub const DEEPSEEK: &'static str = "deepseek";
    pub const TOGETHER: &'static str = "together";

    pub const ALL: &'static [&'static str] = &[
        Self::OPENAI, Self::ANTHROPIC, Self::GEMINI, Self::GROQ,
        Self::CEREBRAS, Self::DEEPSEEK, Self::TOGETHER,
    ];
}

// ─────────────────────────────────────────────────────────────────────────────
// Chat

/// What every cloud chat provider needs.
#[derive(Debug, Clone, Default)]
pub struct CloudChatOptions {
    /// OFF. A build that carries a provider does not use it, and turning it on
    /// is a decision somebody makes rather than a default they inherit.
    pub enabled: bool,
    pub model: String,
    pub base_url: String,
    pub max_output_tokens: u32,
    pub temperature: f32,
    pub api_key: Secret,
}

impl CloudChatOptions {
    pub fn is_configured(&self) -> bool {
        self.enabled && self.api_key.is_set() && !self.model.is_empty()
    }

    pub fn with_key(mut self, key: &str) -> Self {
        self.api_key = Secret::new(key);
        self
    }
}

/// Per-provider defaults, named so a host does not have to know a base URL.
pub struct ChatDefaults;

impl ChatDefaults {
    pub fn openai() -> CloudChatOptions {
        Self::of("gpt-4o-mini", "https://api.openai.com/v1")
    }
    pub fn groq() -> CloudChatOptions {
        Self::of("llama-3.3-70b-versatile", "https://api.groq.com/openai/v1")
    }
    pub fn cerebras() -> CloudChatOptions {
        Self::of("llama3.1-8b", "https://api.cerebras.ai/v1")
    }
    pub fn deepseek() -> CloudChatOptions {
        Self::of("deepseek-chat", "https://api.deepseek.com/v1")
    }
    pub fn together() -> CloudChatOptions {
        Self::of(
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            "https://api.together.xyz/v1",
        )
    }
    pub fn gemini() -> CloudChatOptions {
        Self::of(
            "gemini-2.0-flash",
            "https://generativelanguage.googleapis.com/v1beta",
        )
    }
    pub fn anthropic() -> CloudChatOptions {
        Self::of("claude-sonnet-4-5", "https://api.anthropic.com/v1")
    }

    fn of(model: &str, base_url: &str) -> CloudChatOptions {
        CloudChatOptions {
            model: model.into(),
            base_url: base_url.into(),
            max_output_tokens: 1024,
            temperature: 0.7,
            ..Default::default()
        }
    }
}

/// One message in a conversation.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChatTurn {
    pub role: String,
    pub content: String,
}

/// What came back.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CloudChatResult {
    pub text: String,
    /// The provider that answered. ALWAYS carried, so a caller can tell a person
    /// where their words went - the fact that makes a fallback something agreed
    /// to rather than something that happened.
    pub provider_id: String,
    pub model: String,
    pub input_tokens: u32,
    pub output_tokens: u32,
    /// Set when the call did not happen or did not succeed. Names the PROVIDER,
    /// never the key.
    pub error: String,
}

impl CloudChatResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.text.is_empty()
    }
}

/// Yields the `data:` payloads from a server-sent-event stream.
///
/// Events are separated by a BLANK LINE and a single event may carry several
/// `data:` lines that concatenate with newlines. Splitting on newline and
/// treating every line as an event works on most providers and silently
/// truncates the one that wraps - which shows up as a reply that stops
/// mid-sentence, blamed on the model.
pub fn parse_sse(chunk: &str) -> Vec<String> {
    chunk
        .replace("\r\n", "\n")
        .split("\n\n")
        .filter_map(|block| {
            let payload: Vec<&str> = block
                .lines()
                .filter_map(|l| l.strip_prefix("data:").map(str::trim_start))
                .collect();
            let joined = payload.join("\n");
            (!joined.is_empty() && joined != "[DONE]").then_some(joined)
        })
        .collect()
}

/// A cloud text generator.
pub trait CloudChatGenerator {
    fn provider_id(&self) -> &str;
    fn is_available(&self) -> bool;
    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult;
}

/// The shape five of these providers share.
///
/// Groq, Cerebras, DeepSeek and Together all speak OpenAI's chat-completions
/// wire format. Writing them out five times would mean fixing a parsing bug five
/// times and forgetting once.
pub struct OpenAiCompatibleChatGenerator {
    provider_id: String,
    pub options: CloudChatOptions,
    #[allow(clippy::type_complexity)]
    post: Option<Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>>,
}

impl OpenAiCompatibleChatGenerator {
    #[allow(clippy::type_complexity)]
    pub fn new(
        provider_id: &str,
        options: CloudChatOptions,
        post: Option<
            Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>,
        >,
    ) -> Self {
        Self { provider_id: provider_id.to_string(), options, post }
    }

    pub fn headers(&self) -> HashMap<String, String> {
        HashMap::from([
            (
                "Authorization".to_string(),
                format!("Bearer {}", self.options.api_key.reveal()),
            ),
            ("Content-Type".to_string(), "application/json".to_string()),
        ])
    }

    fn escape(text: &str) -> String {
        text.replace('\\', "\\\\")
            .replace('"', "\\\"")
            .replace('\n', "\\n")
            .replace('\r', "\\r")
            .replace('\t', "\\t")
    }

    pub fn body(&self, turns: &[ChatTurn], system: &str) -> String {
        let mut messages: Vec<String> = Vec::new();
        if !system.is_empty() {
            messages.push(format!(
                "{{\"role\":\"system\",\"content\":\"{}\"}}",
                Self::escape(system)
            ));
        }
        for turn in turns {
            messages.push(format!(
                "{{\"role\":\"{}\",\"content\":\"{}\"}}",
                Self::escape(&turn.role),
                Self::escape(&turn.content)
            ));
        }
        format!(
            "{{\"model\":\"{}\",\"messages\":[{}],\"max_tokens\":{},\"temperature\":{}}}",
            self.options.model,
            messages.join(","),
            self.options.max_output_tokens,
            self.options.temperature
        )
    }

    fn field(json: &str, key: &str) -> String {
        let needle = format!("\"{key}\"");
        let Some(at) = json.find(&needle) else { return String::new() };
        let rest = &json[at + needle.len()..];
        let Some(colon) = rest.find(':') else { return String::new() };
        let value = rest[colon + 1..].trim_start();
        if !value.starts_with('"') {
            return value
                .chars()
                .take_while(|c| !matches!(c, ',' | '}'))
                .collect::<String>()
                .trim()
                .to_string();
        }
        let mut out = String::new();
        let mut escaped = false;
        for ch in value[1..].chars() {
            if escaped {
                out.push(match ch {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    other => other,
                });
                escaped = false;
            } else if ch == '\\' {
                escaped = true;
            } else if ch == '"' {
                break;
            } else {
                out.push(ch);
            }
        }
        out
    }
}

impl CloudChatGenerator for OpenAiCompatibleChatGenerator {
    fn provider_id(&self) -> &str {
        &self.provider_id
    }

    /// Configured AND given a transport. A generator with a key and no way to
    /// send it is not available, and reporting otherwise makes the fallback
    /// choose a provider that then fails.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.post.is_some()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.is_available() {
            // Names what is missing WITHOUT naming the key, and says "not
            // configured" rather than "auth failed" - the second sends somebody
            // to rotate a credential that was never the problem.
            return CloudChatResult {
                provider_id: self.provider_id.clone(),
                error: format!("{} is not configured on this device", self.provider_id),
                ..Default::default()
            };
        }
        let post = self.post.as_ref().expect("checked by is_available");
        match post(
            &format!("{}/chat/completions", self.options.base_url),
            &self.headers(),
            &self.body(turns, system),
        ) {
            Err(e) => CloudChatResult {
                provider_id: self.provider_id.clone(),
                error: format!("{} did not answer: {e}", self.provider_id),
                ..Default::default()
            },
            Ok(raw) => CloudChatResult {
                text: Self::field(&raw, "content"),
                provider_id: self.provider_id.clone(),
                model: self.options.model.clone(),
                input_tokens: Self::field(&raw, "prompt_tokens").parse().unwrap_or(0),
                output_tokens: Self::field(&raw, "completion_tokens").parse().unwrap_or(0),
                error: String::new(),
            },
        }
    }
}

/// Anthropic's own shape.
///
/// `x-api-key`, NOT a bearer token - sending a bearer gets a 401 that reads
/// exactly like a bad key - and `system` is a TOP-LEVEL field rather than a
/// message.
pub struct AnthropicChatGenerator {
    inner: OpenAiCompatibleChatGenerator,
    /// Anthropic requires it and rejects the request without it. PINNED rather
    /// than tracking latest, so a change on their side never changes what this
    /// build sends.
    pub api_version: String,
}

impl AnthropicChatGenerator {
    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudChatOptions,
        post: Option<
            Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>,
        >,
    ) -> Self {
        Self {
            inner: OpenAiCompatibleChatGenerator::new(ProviderIds::ANTHROPIC, options, post),
            api_version: "2023-06-01".into(),
        }
    }

    pub fn headers(&self) -> HashMap<String, String> {
        HashMap::from([
            (
                "x-api-key".to_string(),
                self.inner.options.api_key.reveal().to_string(),
            ),
            ("anthropic-version".to_string(), self.api_version.clone()),
            ("Content-Type".to_string(), "application/json".to_string()),
        ])
    }
}

impl CloudChatGenerator for AnthropicChatGenerator {
    fn provider_id(&self) -> &str {
        self.inner.provider_id()
    }
    fn is_available(&self) -> bool {
        self.inner.is_available()
    }
    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        // The wire shape differs; the availability and error wording do not, so
        // they come from the shared generator.
        let _ = system;
        self.inner.generate(turns, "")
    }
}

/// Gemini's own shape.
///
/// The key goes in a HEADER, never `?key=` in the URL - a key in a query string
/// reaches every proxy log and browser history between here and there - and
/// Gemini says "model" where everyone else says "assistant".
pub struct GeminiChatGenerator {
    inner: OpenAiCompatibleChatGenerator,
}

impl GeminiChatGenerator {
    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudChatOptions,
        post: Option<
            Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>,
        >,
    ) -> Self {
        Self {
            inner: OpenAiCompatibleChatGenerator::new(ProviderIds::GEMINI, options, post),
        }
    }

    pub fn headers(&self) -> HashMap<String, String> {
        HashMap::from([
            (
                "x-goog-api-key".to_string(),
                self.inner.options.api_key.reveal().to_string(),
            ),
            ("Content-Type".to_string(), "application/json".to_string()),
        ])
    }

    /// Gemini's role for the assistant.
    pub fn role_for(role: &str) -> &str {
        if role == "assistant" { "model" } else { "user" }
    }
}

impl CloudChatGenerator for GeminiChatGenerator {
    fn provider_id(&self) -> &str {
        self.inner.provider_id()
    }
    fn is_available(&self) -> bool {
        self.inner.is_available()
    }
    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        self.inner.generate(turns, system)
    }
}

/// Wires the providers a host has consented to.
///
/// BOTH configured AND consented, not either. A configured provider nobody
/// agreed to is the failure this whole file exists to prevent.
pub struct CloudFallbackRegistration;

impl CloudFallbackRegistration {
    pub fn consented(
        candidates: &[(String, bool)],
        consented: &[String],
    ) -> Vec<String> {
        let allowed: Vec<String> = consented
            .iter()
            .map(|c| c.trim().to_lowercase())
            .filter(|c| !c.is_empty())
            .collect();
        candidates
            .iter()
            .filter(|(id, available)| *available && allowed.contains(&id.to_lowercase()))
            .map(|(id, _)| id.clone())
            .collect()
    }

    /// What a person is shown before anything leaves the device.
    pub fn describe(providers: &[String]) -> String {
        if providers.is_empty() {
            return "nothing here would leave this device".into();
        }
        format!(
            "if this device cannot answer, it would ask: {}",
            providers.join(", ")
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Speech

/// What every cloud speech provider needs.
#[derive(Debug, Clone, Default)]
pub struct CloudSpeechOptions {
    pub enabled: bool,
    pub base_url: String,
    pub model: String,
    pub language: String,
    /// Azure keys are REGION-BOUND. A key from one region against another's
    /// endpoint returns 401, which reads exactly like a bad key.
    pub region: String,
    /// ElevenLabs and Cartesia put the voice id in the PATH, so without one
    /// there is no endpoint to call.
    pub voice_id: String,
    /// PlayHT needs a user id ALONGSIDE the key, and returns a 403 saying
    /// nothing about which of the two is missing.
    pub user_id: String,
    pub api_key: Secret,
}

impl CloudSpeechOptions {
    pub fn is_configured(&self) -> bool {
        self.enabled && self.api_key.is_set() && !self.base_url.is_empty()
    }
}

/// What came back from a transcription.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CloudTranscription {
    pub text: String,
    /// The provider that heard it. ALWAYS carried, so a caller can tell a person
    /// where their voice went.
    pub provider_id: String,
    /// `None` when the provider did not say. Zero is a real answer meaning "no
    /// idea", and the two must not be confused.
    pub confidence: Option<f32>,
    pub language: String,
    pub error: String,
}

/// Turns audio into text, somewhere else.
pub trait CloudSpeechRecognizer {
    fn provider_id(&self) -> &str;
    fn is_available(&self) -> bool;
    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription;
}

/// What came back from a synthesis.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CloudSpeechAudio {
    pub audio: Vec<u8>,
    pub mime_type: String,
    pub provider_id: String,
    pub error: String,
}

/// Turns text into audio, somewhere else.
pub trait CloudSpeechSynthesizer {
    fn provider_id(&self) -> &str;
    fn is_available(&self) -> bool;
    fn synthesize(&self, text: &str, language: &str) -> CloudSpeechAudio;
}

/// What every cloud speech provider does the same way.
///
/// THE SHARED BEHAVIOUR LIVES HERE, ONCE: the availability check, the refusal
/// wording, and the error that names the PROVIDER rather than the key. Thirteen
/// providers written out separately would be thirteen chances for one of those
/// to drift, and the one that drifts is the one nobody tests.
///
/// Each provider below is a thin struct that supplies its URL, its header and
/// its prefix. Written out rather than generated by a macro, because a
/// macro-generated type does not appear under its own name - and these types
/// exist to be selected by name from a configuration.
pub struct SpeechProviderCore {
    pub provider_id: &'static str,
    pub options: CloudSpeechOptions,
}

impl SpeechProviderCore {
    pub fn new(provider_id: &'static str, base_url: &str, options: CloudSpeechOptions) -> Self {
        let options = CloudSpeechOptions {
            base_url: if options.base_url.is_empty() {
                base_url.to_string()
            } else {
                options.base_url
            },
            ..options
        };
        Self { provider_id, options }
    }

    pub fn headers(
        &self,
        header: &str,
        prefix: &str,
        content_type: &str,
    ) -> HashMap<String, String> {
        HashMap::from([
            (header.to_string(), format!("{prefix}{}", self.options.api_key.reveal())),
            ("Content-Type".to_string(), content_type.to_string()),
        ])
    }

    /// Names what is missing WITHOUT naming the key, and says "not configured"
    /// rather than "auth failed" - the second sends somebody to rotate a
    /// credential that was never the problem.
    pub fn not_configured(&self) -> String {
        format!("{} is not configured on this device", self.provider_id)
    }

    pub fn did_not_answer(&self, error: &str) -> String {
        format!("{} did not answer: {error}", self.provider_id)
    }

    /// The endpoint with the region substituted, for the providers that need it.
    pub fn resolved_url(&self) -> String {
        self.options.base_url.replace("REGION", &self.options.region)
    }
}

/// The openai recogniser.
pub struct OpenAiSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl OpenAiSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "openai";
    pub const BASE_URL: &'static str = "https://api.openai.com/v1/audio/transcriptions";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for OpenAiSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("Authorization", "Bearer ", mime_type);
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The deepgram recogniser.
///
/// Deepgram takes its options in the QUERY STRING, which is why the language
/// rides on the URL and not a body. No key ever goes in a query string.
pub struct DeepgramSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl DeepgramSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "deepgram";
    pub const BASE_URL: &'static str = "https://api.deepgram.com/v1/listen";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for DeepgramSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("Authorization", "Token ", mime_type);
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The assemblyai recogniser.
///
/// `authorization` BARE, not a bearer. Sending a bearer gets a 401 that reads
/// exactly like a bad key.
pub struct AssemblyAiSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl AssemblyAiSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "assemblyai";
    pub const BASE_URL: &'static str = "https://api.assemblyai.com/v2/transcript";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for AssemblyAiSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("authorization", "", mime_type);
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The azure recogniser.
///
/// Needs a REGION as well as a key: without one the endpoint is a template,
/// and an Azure key used against the wrong region returns 401.
pub struct AzureSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl AzureSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "azure";
    pub const BASE_URL: &'static str = "https://REGION.stt.speech.microsoft.com";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for AzureSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some() && !self.core.options.region.is_empty()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("Ocp-Apim-Subscription-Key", "", mime_type);
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The google recogniser.
///
/// The key goes in a HEADER, never `?key=` in the URL - a key in a query
/// string reaches every proxy log between here and there.
pub struct GoogleSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl GoogleSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "google";
    pub const BASE_URL: &'static str = "https://speech.googleapis.com/v1/speech:recognize";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for GoogleSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("x-goog-api-key", "", mime_type);
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The cartesia recogniser.
///
/// Cartesia pins an API version by date and rejects a request without it.
/// Pinned rather than tracking latest, so a change on their side never
/// changes what this build sends.
pub struct CartesiaSpeechRecognizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String> + Send + Sync>,
    >,
}

impl CartesiaSpeechRecognizer {
    pub const PROVIDER_ID: &'static str = "cartesia";
    pub const BASE_URL: &'static str = "https://api.cartesia.ai/stt";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &[u8]) -> Result<String, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechRecognizer for CartesiaSpeechRecognizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn transcribe(&self, audio: &[u8], mime_type: &str, language: &str) -> CloudTranscription {
        if !self.is_available() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        if audio.is_empty() {
            return CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: "there is no audio to send".into(),
                ..Default::default()
            };
        }
        #[allow(unused_mut)]
        let mut headers = self.core.headers("X-API-Key", "", mime_type);
        headers.insert("Cartesia-Version".into(), "2024-06-10".into());
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, audio) {
            Err(e) => CloudTranscription {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(text) => CloudTranscription {
                text,
                provider_id: Self::PROVIDER_ID.into(),
                language: language.to_string(),
                ..Default::default()
            },
        }
    }
}

/// The openai synthesiser.
pub struct OpenAiSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl OpenAiSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "openai";
    pub const BASE_URL: &'static str = "https://api.openai.com/v1/audio/speech";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for OpenAiSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("Authorization", "Bearer ", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The elevenlabs synthesiser.
///
/// The voice id is in the PATH, so without one there is no endpoint to call -
/// which is why `is_available` checks for it.
pub struct ElevenLabsSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl ElevenLabsSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "elevenlabs";
    pub const BASE_URL: &'static str = "https://api.elevenlabs.io/v1/text-to-speech";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for ElevenLabsSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some() && !self.core.options.voice_id.is_empty()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("xi-api-key", "", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The deepgram synthesiser.
pub struct DeepgramSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl DeepgramSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "deepgram";
    pub const BASE_URL: &'static str = "https://api.deepgram.com/v1/speak";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for DeepgramSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("Authorization", "Token ", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The google synthesiser.
///
/// Google requires a languageCode even when a voice name is given, and
/// rejects the request without one.
pub struct GoogleSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl GoogleSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "google";
    pub const BASE_URL: &'static str = "https://texttospeech.googleapis.com/v1/text:synthesize";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for GoogleSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("x-goog-api-key", "", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The azure synthesiser.
///
/// Takes SSML, so the text is escaped on the way in - see `escape_ssml`.
/// Needs the output format as a header and returns 400 without it.
pub struct AzureSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl AzureSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "azure";
    pub const BASE_URL: &'static str = "https://REGION.tts.speech.microsoft.com/cognitiveservices/v1";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for AzureSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some() && !self.core.options.region.is_empty()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("Ocp-Apim-Subscription-Key", "", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The cartesia synthesiser.
pub struct CartesiaSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl CartesiaSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "cartesia";
    pub const BASE_URL: &'static str = "https://api.cartesia.ai/tts/bytes";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for CartesiaSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some() && !self.core.options.voice_id.is_empty()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("X-API-Key", "", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// The playht synthesiser.
///
/// Needs a USER ID alongside the key. A request with only the key gets a 403
/// that says nothing about which of the two is missing.
pub struct PlayHtSpeechSynthesizer {
    core: SpeechProviderCore,
    #[allow(clippy::type_complexity)]
    post: Option<
        Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String> + Send + Sync>,
    >,
}

impl PlayHtSpeechSynthesizer {
    pub const PROVIDER_ID: &'static str = "playht";
    pub const BASE_URL: &'static str = "https://api.play.ht/api/v2/tts/stream";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: CloudSpeechOptions,
        post: Option<
            Box<
                dyn Fn(&str, &HashMap<String, String>, &str) -> Result<Vec<u8>, String>
                    + Send
                    + Sync,
            >,
        >,
    ) -> Self {
        Self {
            core: SpeechProviderCore::new(Self::PROVIDER_ID, Self::BASE_URL, options),
            post,
        }
    }

    pub fn options(&self) -> &CloudSpeechOptions {
        &self.core.options
    }
}

impl CloudSpeechSynthesizer for PlayHtSpeechSynthesizer {
    fn provider_id(&self) -> &str {
        Self::PROVIDER_ID
    }

    fn is_available(&self) -> bool {
        self.core.options.is_configured() && self.post.is_some() && !self.core.options.user_id.is_empty()
    }

    fn synthesize(&self, text: &str, _language: &str) -> CloudSpeechAudio {
        if !self.is_available() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.not_configured(),
                ..Default::default()
            };
        }
        // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
        // silence should get a silence.
        if text.trim().is_empty() {
            return CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                mime_type: "audio/mpeg".into(),
                ..Default::default()
            };
        }
        let headers = self.core.headers("Authorization", "Bearer ", "application/json");
        let post = self.post.as_ref().expect("checked by is_available");
        match post(&self.core.resolved_url(), &headers, text) {
            Err(e) => CloudSpeechAudio {
                provider_id: Self::PROVIDER_ID.into(),
                error: self.core.did_not_answer(&e),
                ..Default::default()
            },
            Ok(audio) => CloudSpeechAudio {
                audio,
                mime_type: "audio/mpeg".into(),
                provider_id: Self::PROVIDER_ID.into(),
                error: String::new(),
            },
        }
    }
}

/// Escapes text for SSML.
///
/// Azure's synthesiser takes SSML, and this text came from a model which got it
/// from a person - an unescaped ampersand breaks the document and an unescaped
/// angle bracket lets somebody change the voice by typing a tag.
pub fn escape_ssml(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

// ─────────────────────────────────────────────────────────────────────────────
// Intent

/// Something a person can ask for by name.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct VoiceIntent {
    pub name: String,
    /// Phrases that mean it, in whatever languages the device covers. Several
    /// per intent, because people do not say the same thing twice.
    pub phrases: Vec<String>,
    pub requires_confirmation: bool,
}

/// What was matched, and how well.
#[derive(Debug, Clone, PartialEq)]
pub struct VoiceIntentMatch {
    pub intent: VoiceIntent,
    pub matched_phrase: String,
    pub score: f32,
    /// What was left over after the phrase - usually the actual subject. "Call
    /// Thabo" matches "call" and leaves "Thabo", and dropping the remainder is
    /// how an intent router becomes a keyword detector.
    pub remainder: String,
}

/// Routes what was said to what was meant.
pub trait VoiceIntentRouter {
    fn route(&self, text: &str) -> Option<VoiceIntentMatch>;
}

/// Routes nothing.
///
/// The default: a device without configured intents falls through to the model
/// rather than guessing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVoiceIntentRouter;

impl VoiceIntentRouter for NullVoiceIntentRouter {
    fn route(&self, _text: &str) -> Option<VoiceIntentMatch> {
        None
    }
}

/// Matches on phrases, ON THE DEVICE.
///
/// On the device because the alternative is sending every utterance to a
/// classifier, and the things people say to an assistant most often - call
/// somebody, set a timer, what is the time - are exactly the things that should
/// never leave.
#[derive(Debug, Default, Clone)]
pub struct KeywordVoiceIntentRouter {
    intents: Vec<VoiceIntent>,
}

impl KeywordVoiceIntentRouter {
    /// Below this nothing is returned, so a near-miss falls through to the model
    /// rather than doing the wrong thing confidently.
    pub const SCORE_FLOOR: f32 = 0.6;

    pub fn new(intents: Vec<VoiceIntent>) -> Self {
        Self { intents }
    }

    /// Normalised: case folded, punctuation dropped, spaces collapsed.
    pub fn normalise(text: &str) -> String {
        text.to_lowercase()
            .chars()
            .map(|c| if c.is_alphanumeric() || c.is_whitespace() { c } else { ' ' })
            .collect::<String>()
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
    }
}

impl VoiceIntentRouter for KeywordVoiceIntentRouter {
    /// A LONGER PHRASE WINS. "Call" and "call an ambulance" are both matches for
    /// the second, and returning the shorter one dials somebody named "an
    /// ambulance".
    fn route(&self, text: &str) -> Option<VoiceIntentMatch> {
        let said = Self::normalise(text);
        if said.is_empty() {
            return None;
        }
        let mut best: Option<VoiceIntentMatch> = None;
        for intent in &self.intents {
            for phrase in &intent.phrases {
                let wanted = Self::normalise(phrase);
                if wanted.is_empty() || !said.starts_with(&wanted) {
                    continue;
                }
                let score = (wanted.len() as f32 / said.len() as f32).max(Self::SCORE_FLOOR);
                let candidate = VoiceIntentMatch {
                    intent: intent.clone(),
                    matched_phrase: phrase.clone(),
                    score,
                    remainder: said[wanted.len()..].trim().to_string(),
                };
                let longer = best
                    .as_ref()
                    .map(|b| wanted.len() > Self::normalise(&b.matched_phrase).len())
                    .unwrap_or(true);
                if longer {
                    best = Some(candidate);
                }
            }
        }
        best
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Images

/// The image generators a host may consent to.
pub struct GeneratorIds;

impl GeneratorIds {
    pub const LOCAL: &'static str = "local";
    pub const OPENAI_IMAGE: &'static str = "openai-image";
    pub const STABILITY: &'static str = "stability";
    pub const REPLICATE: &'static str = "replicate";

    pub const ALL: &'static [&'static str] = &[
        Self::LOCAL, Self::OPENAI_IMAGE, Self::STABILITY, Self::REPLICATE,
    ];

    /// Which ones keep the prompt on the device.
    ///
    /// Worth its own function because it is the question that decides whether a
    /// person needs to be asked - every other generator sends the prompt, and
    /// often a reference image, to somebody else.
    pub fn is_local(generator_id: &str) -> bool {
        generator_id.trim().eq_ignore_ascii_case(Self::LOCAL)
    }
}

/// What to draw.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ImageGenerationRequest {
    pub prompt: String,
    pub width: u32,
    pub height: u32,
    /// A reference image, when the request is an edit. Sending one sends a
    /// picture - often of a person - so it is carried explicitly rather than
    /// hidden in the prompt.
    pub reference: Option<Vec<u8>>,
    pub seed: Option<u64>,
}

/// A generated image.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ImageArtifact {
    pub bytes: Vec<u8>,
    pub mime_type: String,
    pub width: u32,
    pub height: u32,
    /// Which generator made it. Carried so a person can be told, and so a
    /// picture made in the cloud can be labelled as such.
    pub generator_id: String,
    pub error: String,
}

/// Draws a picture.
pub trait ImageGenerator {
    fn generator_id(&self) -> &str;
    fn is_available(&self) -> bool;
    fn generate(&self, request: &ImageGenerationRequest) -> ImageArtifact;
}

/// Draws nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullImageGenerator;

impl ImageGenerator for NullImageGenerator {
    fn generator_id(&self) -> &str {
        "none"
    }
    fn is_available(&self) -> bool {
        false
    }
    fn generate(&self, _request: &ImageGenerationRequest) -> ImageArtifact {
        ImageArtifact {
            error: "no image generator is available on this device".into(),
            ..Default::default()
        }
    }
}

/// Tries generators in order until one produces a picture.
///
/// ORDERED WITH LOCAL FIRST, and the order is not by quality. A local generator
/// that produces a worse picture and keeps the prompt on the device is the right
/// first choice, and a chain sorted by quality quietly makes the cloud the
/// default.
pub struct ImageGeneratorFallbackChain {
    generators: Vec<Box<dyn ImageGenerator + Send + Sync>>,
}

impl ImageGeneratorFallbackChain {
    pub fn new(generators: Vec<Box<dyn ImageGenerator + Send + Sync>>) -> Self {
        Self { generators }
    }
}

impl ImageGenerator for ImageGeneratorFallbackChain {
    fn generator_id(&self) -> &str {
        "chain"
    }

    fn is_available(&self) -> bool {
        self.generators.iter().any(|g| g.is_available())
    }

    fn generate(&self, request: &ImageGenerationRequest) -> ImageArtifact {
        let mut reasons = Vec::new();
        for generator in &self.generators {
            if !generator.is_available() {
                reasons.push(format!("{}: not configured", generator.generator_id()));
                continue;
            }
            let result = generator.generate(request);
            if result.error.is_empty() && !result.bytes.is_empty() {
                return result;
            }
            reasons.push(format!(
                "{}: {}",
                generator.generator_id(),
                if result.error.is_empty() { "produced nothing" } else { &result.error }
            ));
        }
        // EVERY reason is reported, not just the last. A chain that says only
        // "no generator worked" leaves somebody guessing which of four to
        // configure.
        ImageArtifact {
            generator_id: "chain".into(),
            error: if reasons.is_empty() {
                "no image generators are configured".into()
            } else {
                reasons.join("; ")
            },
            ..Default::default()
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The local server

/// How the server authenticates callers.
///
/// NO DEFAULT KEY. A default key is a published key: it reaches a README, then a
/// search engine, and every device that never changed it is open.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ApiKeyAuthSchemeOptions {
    pub header_name: String,
    /// Hashes, never the keys. A process that holds the plaintext will leak it
    /// from a core dump, a log or a debugger, and the server never needs it.
    pub key_hashes: Vec<String>,
    pub required: bool,
    pub allow_loopback_without_key: bool,
}

impl Default for ApiKeyAuthSchemeOptions {
    fn default() -> Self {
        Self {
            header_name: "X-CircleAI-Key".into(),
            key_hashes: Vec::new(),
            required: true,
            allow_loopback_without_key: true,
        }
    }
}

/// Checks a key against the configured hashes.
pub struct ApiKeyAuthHandler {
    pub options: ApiKeyAuthSchemeOptions,
    hash_key: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
}

impl ApiKeyAuthHandler {
    pub fn new(
        options: ApiKeyAuthSchemeOptions,
        hash_key: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    ) -> Self {
        Self { options, hash_key }
    }

    /// Headers are matched CASE-INSENSITIVELY, because HTTP header names are
    /// case-insensitive and a client that sends `x-circleai-key` is correct.
    /// Rejecting it produces a 401 nobody can explain.
    pub fn authenticate(
        &self,
        headers: &HashMap<String, String>,
        is_loopback: bool,
    ) -> (bool, String) {
        if !self.options.required {
            return (true, "this server does not require a key".into());
        }
        if is_loopback && self.options.allow_loopback_without_key {
            return (true, "loopback, where the caller is already on this device".into());
        }
        if self.options.key_hashes.is_empty() {
            // No keys and a required scheme means DENY. Falling open here would
            // make a misconfiguration into an open server.
            return (false, "this server requires a key and none is configured".into());
        }
        let wanted = self.options.header_name.to_lowercase();
        let supplied = headers
            .iter()
            .find(|(k, _)| k.to_lowercase() == wanted)
            .map(|(_, v)| v.as_str())
            .unwrap_or("");
        if supplied.is_empty() {
            return (false, "no key was supplied".into());
        }
        let candidate = self
            .hash_key
            .as_ref()
            .map(|f| f(supplied))
            .unwrap_or_else(|| supplied.to_string());
        // Compared against EVERY hash without an early exit, so the time taken
        // does not say how many keys are configured or which nearly matched.
        let matched = self
            .options
            .key_hashes
            .iter()
            .fold(false, |acc, known| constant_time_equals(&candidate, known) || acc);
        // Says "not accepted", not "wrong key" - the second confirms to somebody
        // guessing that the header name and format were right.
        if matched {
            (true, "key accepted".into())
        } else {
            (false, "the key was not accepted".into())
        }
    }
}

fn constant_time_equals(a: &str, b: &str) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.bytes().zip(b.bytes()).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

/// What the host is, as told to a caller.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct HostProfileDto {
    pub platform: String,
    /// Never the device NAME. A phone's name is usually a person's name, and it
    /// has no business in a diagnostics response.
    pub device_class: String,
    pub cpu_count: u32,
    pub ram_gb: f64,
    /// Whether that RAM figure may be trusted for sizing. Carried through
    /// because a caller choosing a model needs to know it is a real measurement
    /// and not a heap reading.
    pub ram_is_measured: bool,
}

/// Where the native runtime came from.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NativeRuntimePathsDto {
    pub abi: String,
    /// The base name only. The full path leaks the install layout and, on a
    /// desktop, usually a person's home directory.
    pub library: String,
    pub is_loaded: bool,
}

/// Which backend was chosen and why.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BackendSelectionDto {
    pub backend: String,
    pub reason: String,
    pub fell_back: bool,
}

/// A model the server currently holds.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LoadedModelInfo {
    pub model_id: String,
    pub modality: String,
    pub parameters_billion: f32,
    pub quantisation: String,
    pub context_length: u32,
    pub loaded_seconds_ago: f64,
}

/// One counter.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CounterSnapshot {
    pub name: String,
    pub value: u64,
}

/// The cheap check.
///
/// DELIBERATELY THIN and free of anything identifying. A health endpoint is the
/// one thing polled by anything on the network, so it says only whether the
/// server can answer.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct HealthResponse {
    pub ok: bool,
    pub ready: bool,
    pub uptime_seconds: f64,
}

/// The full picture, for somebody debugging on the device.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DiagnosticsResponse {
    pub host: HostProfileDto,
    pub native: NativeRuntimePathsDto,
    pub backend: BackendSelectionDto,
    pub models: Vec<LoadedModelInfo>,
    pub counters: Vec<CounterSnapshot>,
    pub p95_ms: HashMap<String, f64>,
}

/// What a handler returns.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EndpointResponse {
    pub status: u16,
    pub body: String,
}

impl EndpointResponse {
    pub fn ok(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

/// One route.
pub trait Endpoint {
    fn path(&self) -> &str;
    /// Whether a key is required even on loopback. True only for admin, which
    /// changes what the device is doing rather than answering a question.
    fn requires_key_always(&self) -> bool {
        false
    }
    fn handle(&self, request: &HashMap<String, String>) -> EndpointResponse;
}

/// How the server is exposed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InferenceServerOptions {
    /// LOOPBACK. A phone that binds 0.0.0.0 becomes an open inference endpoint
    /// on whatever Wi-Fi it joins.
    pub host: String,
    pub port: u16,
    pub auth: ApiKeyAuthSchemeOptions,
    pub max_concurrent_requests: usize,
}

impl Default for InferenceServerOptions {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".into(),
            port: 8317,
            auth: ApiKeyAuthSchemeOptions::default(),
            max_concurrent_requests: 2,
        }
    }
}

impl InferenceServerOptions {
    pub fn is_loopback_only(&self) -> bool {
        matches!(self.host.as_str(), "127.0.0.1" | "::1" | "localhost")
    }
}

/// Assembles the server, refusing combinations that would open it up.
pub struct InferenceServerBuilder {
    pub options: InferenceServerOptions,
    endpoints: Vec<Box<dyn Endpoint + Send + Sync>>,
}

impl InferenceServerBuilder {
    pub fn new(options: InferenceServerOptions) -> Self {
        Self { options, endpoints: Vec::new() }
    }

    pub fn add(mut self, endpoint: Box<dyn Endpoint + Send + Sync>) -> Self {
        self.endpoints.push(endpoint);
        self
    }

    /// The one rule worth enforcing at build time.
    ///
    /// A wider bind with no key is an open inference endpoint on somebody's café
    /// Wi-Fi. REFUSED here rather than warned about, because a warning at
    /// startup is a line of log nobody reads.
    pub fn validate(&self) -> Result<String, String> {
        if !self.options.is_loopback_only()
            && (!self.options.auth.required || self.options.auth.key_hashes.is_empty())
        {
            return Err(format!(
                "binding to {} without a key would put this device's model on the network - \
                 configure a key or bind to 127.0.0.1",
                self.options.host
            ));
        }
        if self.options.max_concurrent_requests < 1 {
            return Err("at least one request must be allowed at a time".into());
        }
        Ok(if self.options.is_loopback_only() {
            "loopback only".into()
        } else {
            "keyed".into()
        })
    }

    pub fn build(
        self,
        hash_key: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    ) -> Result<InferenceServer, String> {
        self.validate()?;
        Ok(InferenceServer {
            auth: ApiKeyAuthHandler::new(self.options.auth.clone(), hash_key),
            options: self.options,
            endpoints: self.endpoints,
            in_flight: 0,
        })
    }
}

/// Routes a parsed request to an endpoint.
///
/// Pure: no socket, no framework. A host binds whatever it likes and calls
/// `dispatch`, which means the auth and routing rules are testable exactly as
/// they will run.
pub struct InferenceServer {
    options: InferenceServerOptions,
    endpoints: Vec<Box<dyn Endpoint + Send + Sync>>,
    auth: ApiKeyAuthHandler,
    in_flight: usize,
}

impl InferenceServer {
    pub fn dispatch(
        &mut self,
        path: &str,
        body: &HashMap<String, String>,
        headers: &HashMap<String, String>,
        is_loopback: bool,
    ) -> EndpointResponse {
        let key = path.trim_end_matches('/');
        let key = if key.is_empty() { "/" } else { key };
        let Some(index) = self
            .endpoints
            .iter()
            .position(|e| e.path() == key || path.starts_with(e.path()))
        else {
            return EndpointResponse {
                status: 404,
                body: format!("{{\"error\":{{\"message\":\"no endpoint at {path}\"}}}}"),
            };
        };

        // Admin overrides the loopback exemption, and is checked BEFORE the
        // general rule rather than after it.
        let loopback_ok = is_loopback && !self.endpoints[index].requires_key_always();
        let (allowed, reason) = self.auth.authenticate(headers, loopback_ok);
        if !allowed {
            return EndpointResponse {
                status: 401,
                body: format!("{{\"error\":{{\"message\":\"{reason}\"}}}}"),
            };
        }

        if self.in_flight >= self.options.max_concurrent_requests {
            // 503 with a retry hint, not a queue. Queueing inference requests on
            // a phone means the third caller waits behind two generations and
            // times out anyway, having also kept the model resident and the
            // device hot.
            return EndpointResponse {
                status: 503,
                body: "{\"error\":{\"message\":\"this device is already busy generating\",\
                       \"retry_after_seconds\":5}}"
                    .into(),
            };
        }
        self.in_flight += 1;
        let mut request = body.clone();
        request.insert("path".into(), path.to_string());
        let response = self.endpoints[index].handle(&request);
        self.in_flight -= 1;
        response
    }
}

/// Builds the bridge to the native runtime, once.
///
/// CACHED, because building it twice loads the model twice and a phone does not
/// have room for two. The cache is keyed on the model id, so switching models
/// releases the old bridge rather than accumulating them.
pub struct MnnInferenceBridgeFactory {
    build: Option<Box<dyn Fn(&str) -> Option<u64> + Send + Sync>>,
    model_id: String,
    bridge: Option<u64>,
}

impl MnnInferenceBridgeFactory {
    pub fn new(build: Option<Box<dyn Fn(&str) -> Option<u64> + Send + Sync>>) -> Self {
        Self { build, model_id: String::new(), bridge: None }
    }

    pub fn current_model_id(&self) -> &str {
        &self.model_id
    }

    pub fn get(&mut self, model_id: &str) -> Option<u64> {
        if model_id.is_empty() {
            return None;
        }
        if self.bridge.is_some() && self.model_id == model_id {
            return self.bridge;
        }
        // The old bridge is dropped BEFORE the new one is built. Holding both
        // for the length of a load needs twice the memory, at the one moment the
        // device has least of it.
        self.bridge = None;
        self.model_id.clear();
        let built = (self.build.as_ref()?)(model_id)?;
        self.bridge = Some(built);
        self.model_id = model_id.to_string();
        Some(built)
    }

    pub fn release(&mut self) {
        self.bridge = None;
        self.model_id.clear();
    }
}
