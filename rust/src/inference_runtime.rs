//! Getting a model onto the device, knowing why when that fails, the code agent
//! on top of it, and the local server in front of it.
//!
//! THE DOWNLOAD IS THE EXPENSIVE PART, and not in seconds. A 4 GB model on a
//! South African mobile bundle is real money, and a gate that defaults to "go
//! ahead" spends somebody else's airtime. So the gate defaults to REFUSING on
//! anything not known to be free, and says what it would cost.
//!
//! "DOWNLOAD FAILED" IS NOT A DIAGNOSIS. It sends a person to reboot a router
//! when the answer is a captive portal, a clock so wrong that TLS refuses, or a
//! disk with no room. The preflight here tells those apart, because each has a
//! different fix and only one of them is the router.
//!
//! AND THE SERVER BINDS TO LOOPBACK. It exists so a program on the same device
//! can use the model already loaded on it, not so a device becomes a service on
//! a network. A phone that binds 0.0.0.0 is an open inference endpoint on
//! whatever Wi-Fi it joins.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// Failures

/// Why a fetch could not happen. Each has a DIFFERENT fix.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum NetworkFault {
    #[default]
    None,
    /// No link at all. The one people expect, and the least common in practice.
    Offline,
    /// Names are not resolving while the link is up. Usually a DNS server that
    /// went away, not a dead connection.
    Dns,
    /// A network that answers everything with a login page. Looks like a
    /// successful fetch of the wrong bytes, which is why it is checked for
    /// explicitly rather than inferred from a failure.
    CaptivePortal,
    /// TLS refused. Very often a device clock wrong by more than a certificate's
    /// validity - a fault with nothing to do with the network, and one nobody
    /// guesses correctly.
    Tls,
    Server,
    Metered,
    NoSpace,
    Timeout,
}

/// What is actually wrong, and what to do about it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NetworkDiagnosis {
    pub fault: NetworkFault,
    /// Written for a PERSON, not a log. This is shown on a screen.
    pub message: String,
    /// What they can do. Empty when there is nothing - itself worth saying.
    pub suggestion: String,
    pub can_retry: bool,
}

impl NetworkDiagnosis {
    pub fn is_healthy(&self) -> bool {
        self.fault == NetworkFault::None
    }

    /// The standard wording per fault, so the same problem reads the same way
    /// wherever it surfaces.
    pub fn of(fault: NetworkFault) -> Self {
        let (message, suggestion, can_retry): (&str, &str, bool) = match fault {
            NetworkFault::None => return Self::default(),
            NetworkFault::Offline => (
                "this device is not on a network",
                "connect to Wi-Fi or turn on mobile data",
                true,
            ),
            NetworkFault::Dns => (
                "the network is up but names are not resolving",
                "this usually fixes itself; if not, the network's DNS is down",
                true,
            ),
            NetworkFault::CaptivePortal => (
                "this network wants you to sign in first",
                "open a browser and complete the network's login page",
                true,
            ),
            NetworkFault::Tls => (
                "the secure connection was refused",
                "check this device's date and time - a clock that is wrong breaks every secure connection",
                true,
            ),
            NetworkFault::Server => (
                "the server refused the request",
                "nothing to do here; it is not this device",
                true,
            ),
            NetworkFault::Metered => (
                "this connection costs money",
                "wait for Wi-Fi, or say to go ahead anyway",
                false,
            ),
            NetworkFault::NoSpace => (
                "there is not enough room on this device",
                "free some space and try again",
                false,
            ),
            NetworkFault::Timeout => (
                "the connection was too slow to finish",
                "try again on a faster connection",
                true,
            ),
        };
        Self {
            fault,
            message: message.into(),
            suggestion: suggestion.into(),
            can_retry,
        }
    }
}

/// A download failed or was refused.
///
/// The two variants demand OPPOSITE handling: a failure should be retried and a
/// refusal must never be. Retrying a refusal in a loop is how a gate gets worn
/// down until somebody disables it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ModelDownloadError {
    /// Something went wrong.
    Failed { model_id: String, message: String, fault: NetworkFault },
    /// It was REFUSED, not failed.
    Blocked { model_id: String, reason: String, estimated_bytes: u64 },
}

impl std::fmt::Display for ModelDownloadError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Failed { message, .. } => write!(f, "{message}"),
            Self::Blocked { reason, .. } => write!(f, "{reason}"),
        }
    }
}

impl ModelDownloadError {
    pub fn should_retry(&self) -> bool {
        matches!(self, Self::Failed { .. })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The gate

/// What the link looks like right now.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct NetworkConditions {
    pub is_connected: bool,
    /// The operating system's word for it. Trusted when true and NOT trusted
    /// when false: a phone tethering to another phone reports unmetered while
    /// spending the other phone's bundle.
    pub is_metered: bool,
    pub is_roaming: bool,
    /// True only for a link known to be free. Unknown counts as NOT free.
    pub is_known_unmetered: bool,
    pub estimated_kbps: u32,
}

/// Decides whether a model may be fetched now.
pub trait ModelDownloadGate {
    /// Returns the reason on ALLOW as well as on refuse, so a log says why
    /// something was permitted and not only why it was stopped.
    fn may_download(&self, model_id: &str, size_bytes: u64) -> (bool, String);
}

/// Allows everything.
///
/// Named so choosing it is a visible decision. Right on a desktop on a fixed
/// line, wrong on a phone.
#[derive(Debug, Default, Clone, Copy)]
pub struct AlwaysAllowDownloadGate;

impl ModelDownloadGate for AlwaysAllowDownloadGate {
    fn may_download(&self, _model_id: &str, _size_bytes: u64) -> (bool, String) {
        (true, "no gate is configured on this device".into())
    }
}

/// In the units a person thinks in.
///
/// A bundle is sold in gigabytes, so a refusal that says "4194304000 bytes" has
/// communicated nothing.
pub fn describe_size(size_bytes: u64) -> String {
    const GB: u64 = 1 << 30;
    const MB: u64 = 1 << 20;
    if size_bytes >= GB {
        format!("{:.1} GB", size_bytes as f64 / GB as f64)
    } else if size_bytes >= MB {
        format!("{} MB", size_bytes / MB)
    } else {
        format!("{} KB", (size_bytes / 1024).max(1))
    }
}

/// Refuses a large download on a link that costs money.
///
/// THE DEFAULT IS REFUSE ON ANYTHING NOT KNOWN TO BE FREE, which is stricter
/// than refusing on "metered". The operating system reports unmetered for a
/// tether, so trusting it spends the other phone's bundle - which is the exact
/// case this is written for.
pub struct MeteredNetworkDownloadGate {
    conditions: Option<Box<dyn Fn() -> NetworkConditions + Send + Sync>>,
    /// Consent is PER MODEL. Agreeing to spend a bundle on one model is not
    /// agreeing to spend it on every model the catalogue later carries.
    consented: Vec<String>,
}

impl MeteredNetworkDownloadGate {
    /// Below this the cost is not worth an interruption. Roughly a photograph.
    pub const FREE_PASS_BYTES: u64 = 4 * 1024 * 1024;

    pub fn new(
        conditions: Option<Box<dyn Fn() -> NetworkConditions + Send + Sync>>,
        consented: &[String],
    ) -> Self {
        Self {
            conditions,
            consented: consented.iter().map(|c| c.trim().to_lowercase()).collect(),
        }
    }

    pub fn consent(&mut self, model_id: &str) {
        self.consented.push(model_id.trim().to_lowercase());
    }
}

impl ModelDownloadGate for MeteredNetworkDownloadGate {
    fn may_download(&self, model_id: &str, size_bytes: u64) -> (bool, String) {
        let c = self
            .conditions
            .as_ref()
            .map(|f| f())
            .unwrap_or_default();
        if !c.is_connected {
            return (false, "this device is not on a network".into());
        }
        if size_bytes <= Self::FREE_PASS_BYTES {
            return (true, "small enough that the link does not matter".into());
        }
        if self.consented.contains(&model_id.trim().to_lowercase()) {
            return (true, format!("you agreed to fetch {model_id} on this connection"));
        }
        if c.is_roaming {
            // Roaming is checked BEFORE metered: a roaming link may report
            // unmetered and still cost more per megabyte than anything else
            // somebody pays for this year.
            return (
                false,
                format!(
                    "{} while roaming would be expensive - this can wait for Wi-Fi",
                    describe_size(size_bytes)
                ),
            );
        }
        if !c.is_known_unmetered {
            return (
                false,
                format!(
                    "this would use about {} of your data - ask again on Wi-Fi, or say to go ahead",
                    describe_size(size_bytes)
                ),
            );
        }
        (true, "on a connection known to be free".into())
    }
}

/// Checks whether a fetch can work before starting one.
pub trait NetworkPreflightTrait {
    fn check(&self, url: &str) -> NetworkDiagnosis;
}

/// The default preflight.
///
/// ORDER MATTERS and it is cheapest-first: no link, then space, then a name,
/// then a handshake. Probing TLS on a device with no link wastes a timeout to
/// learn what one flag already said.
pub struct NetworkPreflight {
    conditions: Option<Box<dyn Fn() -> NetworkConditions + Send + Sync>>,
    resolve: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    probe: Option<Box<dyn Fn(&str) -> Result<u16, String> + Send + Sync>>,
    free_bytes: Option<Box<dyn Fn() -> u64 + Send + Sync>>,
    required_bytes: u64,
}

impl NetworkPreflight {
    pub fn new(
        conditions: Option<Box<dyn Fn() -> NetworkConditions + Send + Sync>>,
        resolve: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
        probe: Option<Box<dyn Fn(&str) -> Result<u16, String> + Send + Sync>>,
        free_bytes: Option<Box<dyn Fn() -> u64 + Send + Sync>>,
        required_bytes: u64,
    ) -> Self {
        Self { conditions, resolve, probe, free_bytes, required_bytes }
    }

    fn host_of(url: &str) -> &str {
        url.split_once("://")
            .map(|(_, rest)| rest.split(['/', ':', '?', '#']).next().unwrap_or(""))
            .unwrap_or("")
    }
}

impl NetworkPreflightTrait for NetworkPreflight {
    fn check(&self, url: &str) -> NetworkDiagnosis {
        let c = self.conditions.as_ref().map(|f| f()).unwrap_or_default();
        if !c.is_connected {
            return NetworkDiagnosis::of(NetworkFault::Offline);
        }
        // Space is checked BEFORE the network. Spending a gigabyte of somebody's
        // bundle and then failing to write it is the worst possible order.
        if self.required_bytes > 0 {
            if let Some(free) = &self.free_bytes {
                if free() < self.required_bytes {
                    return NetworkDiagnosis::of(NetworkFault::NoSpace);
                }
            }
        }
        let host = Self::host_of(url);
        if !host.is_empty() {
            if let Some(resolve) = &self.resolve {
                if !resolve(host) {
                    return NetworkDiagnosis::of(NetworkFault::Dns);
                }
            }
        }
        if !url.is_empty() {
            if let Some(probe) = &self.probe {
                return match probe(url) {
                    Ok(200) | Ok(204) => NetworkDiagnosis::of(NetworkFault::None),
                    // A redirect to a login page is how a captive portal answers,
                    // and it is indistinguishable from success unless it is
                    // looked for.
                    Ok(301 | 302 | 303 | 307 | 511) => {
                        NetworkDiagnosis::of(NetworkFault::CaptivePortal)
                    }
                    Ok(_) => NetworkDiagnosis::of(NetworkFault::Server),
                    Err(e) => {
                        let text = e.to_lowercase();
                        // A certificate error is reported as TLS, not as a
                        // generic failure, because the fix is almost always the
                        // device clock and nobody guesses that.
                        if text.contains("certificate") || text.contains("ssl") || text.contains("tls") {
                            NetworkDiagnosis::of(NetworkFault::Tls)
                        } else if text.contains("timeout") || text.contains("timed out") {
                            NetworkDiagnosis::of(NetworkFault::Timeout)
                        } else {
                            NetworkDiagnosis::of(NetworkFault::Server)
                        }
                    }
                };
            }
        }
        if c.is_known_unmetered {
            NetworkDiagnosis::of(NetworkFault::None)
        } else {
            NetworkDiagnosis::of(NetworkFault::Metered)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Native runtime

/// Where the native pieces landed.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct NativeRuntimePaths {
    pub library_directory: String,
    pub resolved_library: String,
    pub abi: String,
    /// Every path looked in. "libmnn.so not found" is unactionable; the list of
    /// places looked in is a diagnosis.
    pub searched: Vec<String>,
}

/// Finds the native library for THIS device's ABI.
///
/// Android packs one directory per ABI and a device may run more than one - an
/// arm64 phone happily runs armeabi-v7a. Picking the first that EXISTS rather
/// than the first the device PREFERS silently runs 32-bit code on a 64-bit
/// phone, which works, is slower, and is invisible.
pub struct NativeLibraryResolver {
    supported_abis: Vec<String>,
    exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
}

impl NativeLibraryResolver {
    /// Most capable first. The order is the answer, not the contents.
    pub const ANDROID_ABIS: &'static [&'static str] =
        &["arm64-v8a", "armeabi-v7a", "x86_64", "x86"];
    pub const APPLE_ABIS: &'static [&'static str] = &["arm64", "x86_64"];

    pub fn new(
        supported_abis: Vec<String>,
        exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    ) -> Self {
        Self { supported_abis, exists }
    }

    /// The device's list, filtered to what we know, in OUR order. Where the
    /// device offers nothing, the fallback is most-capable-first.
    pub fn preferred_abis(&self) -> Vec<String> {
        let known: Vec<String> = self
            .supported_abis
            .iter()
            .filter(|a| {
                Self::ANDROID_ABIS.contains(&a.as_str()) || Self::APPLE_ABIS.contains(&a.as_str())
            })
            .cloned()
            .collect();
        if known.is_empty() {
            Self::ANDROID_ABIS.iter().map(|s| s.to_string()).collect()
        } else {
            known
        }
    }

    pub fn resolve(&self, root: &str, library_name: &str) -> NativeRuntimePaths {
        let exists = |p: &str| self.exists.as_ref().map(|f| f(p)).unwrap_or(false);
        let mut searched = Vec::new();
        for abi in self.preferred_abis() {
            let candidate = format!("{root}/{abi}/{library_name}");
            searched.push(candidate.clone());
            if exists(&candidate) {
                return NativeRuntimePaths {
                    library_directory: format!("{root}/{abi}"),
                    resolved_library: candidate,
                    abi,
                    searched,
                };
            }
        }
        let flat = format!("{root}/{library_name}");
        searched.push(flat.clone());
        if exists(&flat) {
            return NativeRuntimePaths {
                library_directory: root.to_string(),
                resolved_library: flat,
                abi: String::new(),
                searched,
            };
        }
        NativeRuntimePaths { searched, ..Default::default() }
    }
}

/// How the native runtime is set up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MnnRuntimeConfig {
    pub thread_count: usize,
    /// Big cores only by default. Spreading inference across little cores makes
    /// it slower AND hotter: the little cores finish late and the big ones idle
    /// waiting on the barrier.
    pub big_cores_only: bool,
    pub use_gpu: bool,
    pub use_fp16: bool,
}

impl Default for MnnRuntimeConfig {
    fn default() -> Self {
        Self { thread_count: 4, big_cores_only: true, use_gpu: false, use_fp16: true }
    }
}

impl MnnRuntimeConfig {
    /// Half the cores, at least one, at most six.
    ///
    /// NEVER ALL OF THEM: a runtime that takes every core makes the UI thread
    /// wait, and an assistant that freezes the phone while it thinks is worse
    /// than one that thinks a little slower.
    pub fn for_threads(available: usize) -> Self {
        Self { thread_count: (available / 2).clamp(1, 6), ..Default::default() }
    }
}

/// What the native side reports about itself.
///
/// Every value OPTIONAL and absent when unknown. A diagnostics screen that
/// invents a zero where it has no measurement is worse than one that says it
/// does not know, because a zero looks like a finding.
#[derive(Debug, Default, Clone)]
pub struct MnnNativeDiagnostics {
    values: HashMap<String, String>,
}

impl MnnNativeDiagnostics {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn record(&mut self, key: &str, value: &str) {
        self.values.insert(key.to_string(), value.to_string());
    }

    pub fn get(&self, key: &str) -> Option<&String> {
        self.values.get(key)
    }

    pub fn describe(&self) -> String {
        if self.values.is_empty() {
            return "the native runtime has not reported anything".into();
        }
        let mut pairs: Vec<String> = self
            .values
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect();
        pairs.sort();
        pairs.join(", ")
    }
}

/// Gets the native side ready, once.
///
/// IDEMPOTENT. Preparation runs from whichever call arrives first, and on a
/// phone that is often two at the same time - a warm-up and a real request
/// racing on separate threads.
pub struct NativeRuntimePrep {
    resolver: NativeLibraryResolver,
    pub diagnostics: MnnNativeDiagnostics,
    paths: Option<NativeRuntimePaths>,
    prepared: bool,
}

impl NativeRuntimePrep {
    pub fn new(resolver: NativeLibraryResolver) -> Self {
        Self {
            resolver,
            diagnostics: MnnNativeDiagnostics::new(),
            paths: None,
            prepared: false,
        }
    }

    pub fn is_prepared(&self) -> bool {
        self.prepared
    }

    pub fn paths(&self) -> Option<&NativeRuntimePaths> {
        self.paths.as_ref()
    }

    pub fn prepare(&mut self, root: &str, library_name: &str) -> bool {
        if self.prepared {
            return true;
        }
        let paths = self.resolver.resolve(root, library_name);
        self.diagnostics.record(
            "abi",
            if paths.abi.is_empty() { "unresolved" } else { &paths.abi },
        );
        self.diagnostics
            .record("searched", &paths.searched.len().to_string());
        if paths.resolved_library.is_empty() {
            self.diagnostics
                .record("error", "no native library for this device");
            self.paths = Some(paths);
            return false;
        }
        self.diagnostics.record("library", &paths.resolved_library);
        self.paths = Some(paths);
        self.prepared = true;
        true
    }
}

/// Maps weights instead of reading them.
///
/// A 4 GB model READ into a phone's heap is a 4 GB allocation the system will
/// refuse or kill. Mapped, the pages come in on demand and the kernel evicts
/// them under pressure - the difference between a model that runs on a 6 GB
/// phone and one that does not.
pub struct MmapWeightLoader {
    map_file: Option<Box<dyn Fn(&str, u64, u64) -> Option<Vec<u8>> + Send + Sync>>,
}

impl MmapWeightLoader {
    pub fn new(
        map_file: Option<Box<dyn Fn(&str, u64, u64) -> Option<Vec<u8>> + Send + Sync>>,
    ) -> Self {
        Self { map_file }
    }

    /// Map when the file is more than a QUARTER of memory.
    ///
    /// Not half: the model is not the only thing running, and by the time a file
    /// is half of RAM the allocation has already failed.
    pub fn should_map(file_bytes: u64, available_ram_bytes: u64) -> bool {
        file_bytes > 0 && available_ram_bytes > 0 && file_bytes.saturating_mul(4) > available_ram_bytes
    }

    pub fn load(&self, path: &str, offset: u64, length: u64) -> Option<Vec<u8>> {
        if length == 0 {
            return None;
        }
        (self.map_file.as_ref()?)(path, offset, length)
    }
}

/// Finds the shards a layer-streamed model is split into.
pub struct LayerShardDiscovery;

impl LayerShardDiscovery {
    fn index_of(name: &str) -> Option<u32> {
        let digits: String = name
            .chars()
            .rev()
            .skip_while(|c| !c.is_ascii_digit())
            .take_while(char::is_ascii_digit)
            .collect::<Vec<_>>()
            .into_iter()
            .rev()
            .collect();
        digits.parse().ok()
    }

    /// Shards are ordered by their INDEX, parsed as a number.
    ///
    /// Sorting the file names as text puts shard-10 before shard-2, and a model
    /// assembled in that order produces fluent nonsense - the weights are all
    /// present and in the wrong layers.
    pub fn order(file_names: &[String]) -> Vec<String> {
        let indexed: Vec<(u32, String)> = file_names
            .iter()
            .filter_map(|n| Self::index_of(n).map(|i| (i, n.clone())))
            .collect();
        // A name with no index means the set cannot be ordered reliably, so
        // NOTHING is returned rather than a guess. A half-ordered model is worse
        // than one that refuses to load.
        if indexed.len() != file_names.len() {
            return Vec::new();
        }
        let mut sorted = indexed;
        sorted.sort_by_key(|(i, _)| *i);
        sorted.into_iter().map(|(_, n)| n).collect()
    }

    /// Whether every shard from 0 to n-1 is present. A gap loads silently and
    /// produces a model missing a layer.
    pub fn is_complete(file_names: &[String], expected: u32) -> bool {
        let indices: Vec<u32> = file_names.iter().filter_map(|n| Self::index_of(n)).collect();
        (0..expected).all(|i| indices.contains(&i))
    }
}

/// Holds the LoRA adapters a device has, and which is active.
///
/// ONE AT A TIME, because stacking adapters compounds their effects in ways
/// neither was trained for - and the result is not "both behaviours", it is a
/// model that behaves like neither.
#[derive(Debug, Default)]
pub struct LoRAAdapterManager {
    adapters: HashMap<String, (u32, u64)>,
    active: String,
}

impl LoRAAdapterManager {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register(&mut self, id: &str, rank: u32, size_bytes: u64) -> bool {
        if id.trim().is_empty() || rank == 0 {
            return false;
        }
        self.adapters.insert(id.to_string(), (rank, size_bytes));
        true
    }

    pub fn active_adapter(&self) -> &str {
        &self.active
    }

    /// Activating one DEACTIVATES the previous. Not a stack.
    pub fn activate(&mut self, id: &str) -> bool {
        if !self.adapters.contains_key(id) {
            return false;
        }
        self.active = id.to_string();
        true
    }

    pub fn deactivate(&mut self) {
        self.active.clear();
    }

    pub fn ids(&self) -> Vec<String> {
        let mut out: Vec<String> = self.adapters.keys().cloned().collect();
        out.sort();
        out
    }
}

/// Somewhere feedback waits to be used.
pub struct FileBackedFeedbackTrainingQueue {
    write: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    /// A cap, because this queue exists on a phone. Beyond it the OLDEST goes,
    /// since recent feedback describes the model as it is now.
    max_entries: usize,
    pending: Vec<(String, String, u64)>,
}

impl FileBackedFeedbackTrainingQueue {
    pub fn new(write: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>, max_entries: usize) -> Self {
        Self { write, max_entries, pending: Vec::new() }
    }

    pub fn len(&self) -> usize {
        self.pending.len()
    }

    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    pub fn enqueue(&mut self, text: &str, label: &str, at_ms: u64) {
        self.pending.push((text.to_string(), label.to_string(), at_ms));
        while self.pending.len() > self.max_entries {
            self.pending.remove(0);
        }
    }

    /// Flushes to disk and CLEARS only on a successful write. Clearing first
    /// loses the queue when the write fails, which is exactly when it matters.
    pub fn flush(&mut self) -> bool {
        let Some(write) = &self.write else { return false };
        if self.pending.is_empty() {
            return false;
        }
        let body: Vec<String> = self
            .pending
            .iter()
            .map(|(t, l, at)| format!("{{\"text\":\"{}\",\"label\":\"{l}\",\"at\":{at}}}", t.replace('"', "'")))
            .collect();
        if !write(&format!("[{}]", body.join(","))) {
            return false;
        }
        self.pending.clear();
        true
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Offload

/// Another device that could do the work.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct MeshPeer {
    pub peer_id: String,
    pub display_name: String,
    /// Whether BOTH devices added each other. Offloading to a peer that has not
    /// added us back is sending a prompt to a stranger.
    pub mutually_added: bool,
    pub ram_bytes: u64,
    /// MEASURED, not advertised. A peer's own claim about its speed is a claim.
    pub measured_tokens_per_second: f32,
    pub load_average: f32,
}

/// When work may leave this device.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum MeshOffloadStrategy {
    /// The default. Nothing leaves.
    #[default]
    Never,
    /// Only when this device genuinely cannot do it at all.
    OnlyIfIncapable,
    /// When a peer is meaningfully faster AND the person agreed to that peer.
    PreferFasterPeer,
}

/// Whether to offload, and why.
///
/// The REASON is mandatory. An offload decision without a reason is a decision
/// nobody can review, and this one moves somebody's words to another machine.
#[derive(Debug, Clone, PartialEq)]
pub struct OffloadVerdict {
    pub should_offload: bool,
    pub reason: String,
    pub peer: Option<MeshPeer>,
}

/// Applies the strategy.
pub struct MeshOffloadPlanner {
    strategy: MeshOffloadStrategy,
    consented: Vec<String>,
}

impl MeshOffloadPlanner {
    /// A peer must be this much faster before moving work is worth it. Below
    /// this the transfer costs more than it saves, and it has also told another
    /// device what was asked.
    pub const SPEEDUP_THRESHOLD: f32 = 1.5;

    pub fn new(strategy: MeshOffloadStrategy, consented: &[String]) -> Self {
        Self {
            strategy,
            consented: consented.iter().map(|c| c.trim().to_string()).collect(),
        }
    }

    pub fn decide(
        &self,
        peers: &[MeshPeer],
        local_tokens_per_second: f32,
        can_run_locally: bool,
    ) -> OffloadVerdict {
        let stay = |reason: &str| OffloadVerdict {
            should_offload: false,
            reason: reason.to_string(),
            peer: None,
        };

        if self.strategy == MeshOffloadStrategy::Never {
            return stay("this device never sends work elsewhere");
        }
        // Consent, then mutual, then capability - in that order, so a peer that
        // fails the first test is never evaluated on speed.
        let eligible: Vec<&MeshPeer> = peers
            .iter()
            .filter(|p| self.consented.contains(&p.peer_id) && p.mutually_added)
            .collect();
        if eligible.is_empty() {
            return stay("no peer has both been agreed to and added this device back");
        }
        if can_run_locally && self.strategy == MeshOffloadStrategy::OnlyIfIncapable {
            return stay("this device can do it, so it will");
        }
        let Some(best) = eligible.iter().max_by(|a, b| {
            a.measured_tokens_per_second
                .partial_cmp(&b.measured_tokens_per_second)
                .unwrap_or(std::cmp::Ordering::Equal)
        }) else {
            return stay("no peer has been measured, only claimed");
        };
        if best.measured_tokens_per_second <= 0.0 {
            return stay("no peer has been measured, only claimed");
        }
        if !can_run_locally {
            return OffloadVerdict {
                should_offload: true,
                reason: format!(
                    "this device cannot run it; {} can, and you agreed",
                    best.peer_id
                ),
                peer: Some((*best).clone()),
            };
        }
        if local_tokens_per_second <= 0.0 {
            return stay("this device's own speed is unmeasured");
        }
        let speedup = best.measured_tokens_per_second / local_tokens_per_second;
        if speedup < Self::SPEEDUP_THRESHOLD {
            return OffloadVerdict {
                should_offload: false,
                reason: format!(
                    "{} is only {speedup:.1}x faster, which is not worth sending your words to another device",
                    best.peer_id
                ),
                peer: None,
            };
        }
        OffloadVerdict {
            should_offload: true,
            reason: format!("{} is {speedup:.1}x faster, and you agreed to it", best.peer_id),
            peer: Some((*best).clone()),
        }
    }
}

/// A small model drafts, the big one checks.
///
/// THE ACCEPTED PREFIX IS WHAT THE BIG MODEL WOULD HAVE PRODUCED ANYWAY, so this
/// is a speed change and not a quality one. That is the whole claim, and it holds
/// only if the check is exact: the moment a draft token is accepted without the
/// target agreeing, the output is the small model's and the claim is false.
///
/// On the first disagreement the target's own token is taken and the rest of the
/// draft is DISCARDED - keeping any of it would be keeping tokens conditioned on
/// a prefix that did not happen.
#[derive(Debug, Clone, Copy)]
pub struct SpeculativeDecodingPipeline {
    /// Longer drafts win more when they are right and cost more when they are
    /// wrong. Four is where the two roughly balance on a phone.
    pub draft_length: usize,
}

impl Default for SpeculativeDecodingPipeline {
    fn default() -> Self {
        Self { draft_length: 4 }
    }
}

impl SpeculativeDecodingPipeline {
    pub fn new(draft_length: usize) -> Self {
        Self { draft_length: draft_length.max(1) }
    }

    /// Returns the accepted prefix and the index of the first disagreement.
    pub fn accept(&self, draft: &[u32], target: &[u32]) -> (Vec<u32>, usize) {
        let mut accepted = Vec::new();
        for (i, token) in draft.iter().enumerate() {
            match target.get(i) {
                Some(t) if t == token => accepted.push(*token),
                _ => return (accepted, i),
            }
        }
        (accepted, draft.len())
    }

    /// One round. Always emits at least ONE token - the target's own at the
    /// point of disagreement - so the loop cannot stall on a draft that is
    /// always wrong.
    pub fn step<D, V>(&self, draft: D, verify: V) -> Vec<u32>
    where
        D: Fn(usize) -> Vec<u32>,
        V: Fn(&[u32]) -> Vec<u32>,
    {
        let drafted = draft(self.draft_length);
        let checked = verify(&drafted);
        let (mut accepted, at) = self.accept(&drafted, &checked);
        if at < drafted.len() && at < checked.len() {
            accepted.push(checked[at]);
        } else if accepted.is_empty() {
            if let Some(first) = checked.first() {
                accepted.push(*first);
            }
        }
        accepted
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Bundles

/// How importing a handed-over model went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SideloadOutcome {
    Imported,
    /// Already present and identical. NOT an error - handing somebody a model
    /// they already have is the normal case in a room full of phones.
    AlreadyPresent,
    /// The bytes do not match the digest. The only correct response is refusal.
    DigestMismatch,
    UnsupportedFormat,
    NoSpace,
    Refused,
}

/// What the import did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SideloadResult {
    pub outcome: SideloadOutcome,
    pub model_id: String,
    pub installed_path: String,
    pub bytes_written: usize,
    pub message: String,
}

impl SideloadResult {
    pub fn succeeded(&self) -> bool {
        matches!(
            self.outcome,
            SideloadOutcome::Imported | SideloadOutcome::AlreadyPresent
        )
    }
}

/// Compares digests without revealing WHERE they differ.
///
/// Overkill for a local file and correct anyway: the habit of comparing digests
/// in constant time is worth keeping, because the one place it is skipped is
/// always the place it mattered.
pub fn constant_time_equals(a: &str, b: &str) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.bytes()
        .zip(b.bytes())
        .fold(0u8, |acc, (x, y)| acc | (x ^ y))
        == 0
}

/// Imports a model handed over offline.
///
/// THIS IS THE POINT OF THE WHOLE DESIGN: a model arrives on a phone from
/// another phone, over Wi-Fi Direct, with no internet involved. Which is also
/// why the digest check is not optional - a file from a peer is a file from
/// somebody else's device, and a model that has been altered is a model that
/// says what somebody else wanted it to say.
pub struct SideloadedBundleImporter {
    install_root: String,
    read_file: Option<Box<dyn Fn(&str) -> Option<Vec<u8>> + Send + Sync>>,
    write_file: Option<Box<dyn Fn(&str, &[u8]) -> bool + Send + Sync>>,
    digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
    free_bytes: Option<Box<dyn Fn() -> u64 + Send + Sync>>,
    exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
}

impl SideloadedBundleImporter {
    pub const SUPPORTED_SUFFIXES: &'static [&'static str] =
        &[".onnx", ".gguf", ".mnn", ".bin", ".safetensors"];

    #[allow(clippy::type_complexity)]
    pub fn new(
        install_root: String,
        read_file: Option<Box<dyn Fn(&str) -> Option<Vec<u8>> + Send + Sync>>,
        write_file: Option<Box<dyn Fn(&str, &[u8]) -> bool + Send + Sync>>,
        digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
        free_bytes: Option<Box<dyn Fn() -> u64 + Send + Sync>>,
        exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    ) -> Self {
        Self { install_root, read_file, write_file, digest_of, free_bytes, exists }
    }

    pub fn import_bundle(
        &self,
        model_id: &str,
        source_path: &str,
        expected_sha256: &str,
    ) -> SideloadResult {
        let result = |outcome, message: &str| SideloadResult {
            outcome,
            model_id: model_id.to_string(),
            installed_path: String::new(),
            bytes_written: 0,
            message: message.to_string(),
        };

        if expected_sha256.is_empty() {
            // No digest means no import. Accepting a file on trust because the
            // sender did not supply one is exactly the case this refuses.
            return result(
                SideloadOutcome::Refused,
                "this file came with no checksum, so it cannot be trusted",
            );
        }
        let lower = source_path.to_lowercase();
        if !Self::SUPPORTED_SUFFIXES.iter().any(|s| lower.ends_with(s)) {
            return result(
                SideloadOutcome::UnsupportedFormat,
                "that is not a model file this build can load",
            );
        }
        let (Some(read), Some(digest)) = (&self.read_file, &self.digest_of) else {
            return result(SideloadOutcome::Refused, "no way to read or check the file");
        };
        let Some(data) = read(source_path) else {
            return result(SideloadOutcome::Refused, "the file could not be read");
        };
        if !constant_time_equals(
            &digest(&data).to_lowercase(),
            expected_sha256.trim().to_lowercase().as_str(),
        ) {
            return result(
                SideloadOutcome::DigestMismatch,
                "this file does not match its checksum and was not installed",
            );
        }

        let name = source_path
            .rsplit(['/', '\\'])
            .next()
            .unwrap_or("model.bin");
        let target = format!("{}/{model_id}/{name}", self.install_root);
        if self.exists.as_ref().map(|f| f(&target)).unwrap_or(false) {
            return SideloadResult {
                outcome: SideloadOutcome::AlreadyPresent,
                model_id: model_id.to_string(),
                installed_path: target,
                bytes_written: data.len(),
                message: "this device already has that model".into(),
            };
        }
        if let Some(free) = &self.free_bytes {
            if free() < data.len() as u64 {
                return result(
                    SideloadOutcome::NoSpace,
                    &format!(
                        "this needs {} and there is not that much room",
                        describe_size(data.len() as u64)
                    ),
                );
            }
        }
        let Some(write) = &self.write_file else {
            return result(SideloadOutcome::Refused, "no way to write the file");
        };
        if !write(&target, &data) {
            return result(SideloadOutcome::Refused, "the file could not be written");
        }
        SideloadResult {
            outcome: SideloadOutcome::Imported,
            model_id: model_id.to_string(),
            installed_path: target,
            bytes_written: data.len(),
            message: "installed from a file on this device".into(),
        }
    }
}

/// Loads a model from an installed bundle.
///
/// Every file is checked against the manifest BEFORE anything is loaded. A
/// partial bundle that loads two of three files produces a model that runs and
/// is wrong, which is worse than one that does not run.
pub struct BundleModelLoader {
    exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
    size_of: Option<Box<dyn Fn(&str) -> u64 + Send + Sync>>,
}

impl BundleModelLoader {
    pub fn new(
        exists: Option<Box<dyn Fn(&str) -> bool + Send + Sync>>,
        size_of: Option<Box<dyn Fn(&str) -> u64 + Send + Sync>>,
    ) -> Self {
        Self { exists, size_of }
    }

    /// Reports EVERY problem, not the first. Fixing one missing file, re-running
    /// and finding the next is three trips where one would do.
    pub fn verify(&self, root: &str, expected: &[(String, u64)]) -> (bool, Vec<String>) {
        let mut problems = Vec::new();
        let mut sorted = expected.to_vec();
        sorted.sort_by(|a, b| a.0.cmp(&b.0));
        for (name, size) in sorted {
            let path = format!("{root}/{name}");
            let present = self.exists.as_ref().map(|f| f(&path)).unwrap_or(false);
            if !present {
                problems.push(format!("{name} is missing"));
                continue;
            }
            if size > 0 {
                let actual = self.size_of.as_ref().map(|f| f(&path)).unwrap_or(0);
                if actual != size {
                    problems.push(format!("{name} is {actual} bytes, expected {size}"));
                }
            }
        }
        (problems.is_empty(), problems)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Selection

/// How good a match a selected model is.
///
/// ORDERED, so a caller can compare - and the order is the meaning: a caller
/// decides whether to proceed by asking whether the quality is at least
/// something.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum SelectionQuality {
    /// Nothing suitable. Not an error - a device that cannot do a thing should
    /// say so rather than doing it badly.
    #[default]
    None,
    /// It will run and be poor. Offered only when the caller asked for anything.
    Degraded,
    Acceptable,
    Good,
    /// Exactly what was asked for, on hardware that fits it.
    Ideal,
}

/// What a power budget resolved to for one call.
///
/// Separate from the budget itself because the budget is a REQUEST and this is
/// what the device decided - and on a hot phone at 8% battery those are not the
/// same thing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Resolution {
    Honoured,
    /// Lowered because of battery, heat or a foreground app. The caller is told,
    /// so a shorter answer is explained rather than mysterious.
    Throttled,
    /// Raised because the device is charging and cool.
    Relaxed,
    /// Refused outright. The device will not spend the power at all.
    Declined,
}

/// Decides what a power budget actually means right now.
pub struct PowerBudgetPolicy;

impl PowerBudgetPolicy {
    /// Below this, everything is throttled whatever was asked for.
    pub const LOW_BATTERY_PERCENT: u8 = 15;
    /// Below this, generation is declined rather than throttled.
    pub const CRITICAL_BATTERY_PERCENT: u8 = 5;

    pub fn resolve(
        requested_max_tokens: u32,
        battery_percent: Option<u8>,
        is_charging: Option<bool>,
        thermal_status: &str,
    ) -> (Resolution, u32, String) {
        if matches!(thermal_status, "critical" | "emergency") {
            return (
                Resolution::Declined,
                0,
                "this device is too hot to run a model right now".into(),
            );
        }
        if let (Some(percent), Some(false) | None) = (battery_percent, is_charging) {
            if percent <= Self::CRITICAL_BATTERY_PERCENT {
                return (
                    Resolution::Declined,
                    0,
                    format!("{percent}% battery - this would not leave you a phone"),
                );
            }
            if percent <= Self::LOW_BATTERY_PERCENT {
                return (
                    Resolution::Throttled,
                    (requested_max_tokens / 4).max(32),
                    format!("{percent}% battery, so this answer will be shorter"),
                );
            }
        }
        if thermal_status == "severe" {
            return (
                Resolution::Throttled,
                (requested_max_tokens / 2).max(64),
                "this device is warm, so this answer will be shorter".into(),
            );
        }
        // Charging and cool means the budget may be RAISED, which is the case
        // nobody implements and is exactly when a longer answer costs nothing.
        if is_charging == Some(true) && thermal_status == "none" {
            return (
                Resolution::Relaxed,
                requested_max_tokens + requested_max_tokens / 2,
                "charging and cool, so there is room for a fuller answer".into(),
            );
        }
        (Resolution::Honoured, requested_max_tokens, String::new())
    }
}

/// Which engine handles which part of a request.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModalityPlan {
    pub transcribe_with: String,
    pub generate_with: String,
    pub speak_with: String,
    pub see_with: String,
    pub quality: SelectionQuality,
    /// Why this plan. Shown when a person asks why the assistant sounds
    /// different today.
    pub reason: String,
}

/// Picks the speech models for a request.
pub trait SpeechModelSelectorTrait {
    fn plan(&self, language: &str, needs_speech_in: bool, needs_speech_out: bool) -> ModalityPlan;
}

/// The default selector.
///
/// NO MODEL NAME IS HARDCODED. The catalogue supplies them, keyed by language,
/// because a hardcoded name is a model that cannot be replaced without a release
/// - and the catalogue is exactly where a device learns it now has a better
/// voice for a language it used to handle badly.
#[derive(Debug, Default)]
pub struct SpeechModelSelector {
    transcribers: HashMap<String, String>,
    voices: HashMap<String, String>,
    generator_id: String,
}

impl SpeechModelSelector {
    pub fn new(
        transcribers: HashMap<String, String>,
        voices: HashMap<String, String>,
        generator_id: String,
    ) -> Self {
        Self {
            transcribers: transcribers
                .into_iter()
                .map(|(k, v)| (k.to_lowercase(), v))
                .collect(),
            voices: voices.into_iter().map(|(k, v)| (k.to_lowercase(), v)).collect(),
            generator_id,
        }
    }

    /// `af-ZA` and `af` are the same language for choosing a model. Falling back
    /// to the base tag is what makes a device with one Afrikaans voice usable by
    /// somebody whose locale says af-NA.
    fn look_up(table: &HashMap<String, String>, tag: &str) -> String {
        let base = tag.split(['-', '_']).next().unwrap_or("").to_lowercase();
        table
            .get(tag)
            .or_else(|| table.get(&base))
            .cloned()
            .unwrap_or_default()
    }
}

impl SpeechModelSelectorTrait for SpeechModelSelector {
    fn plan(&self, language: &str, needs_speech_in: bool, needs_speech_out: bool) -> ModalityPlan {
        let tag = language.trim().to_lowercase();
        let transcribe_with = if needs_speech_in {
            Self::look_up(&self.transcribers, &tag)
        } else {
            String::new()
        };
        let speak_with = if needs_speech_out {
            Self::look_up(&self.voices, &tag)
        } else {
            String::new()
        };

        if self.generator_id.is_empty() {
            return ModalityPlan {
                transcribe_with,
                speak_with,
                quality: SelectionQuality::None,
                reason: "this device has no text model, so it cannot answer at all".into(),
                ..Default::default()
            };
        }

        let wanted = usize::from(needs_speech_in) + usize::from(needs_speech_out);
        let got = usize::from(!transcribe_with.is_empty()) + usize::from(!speak_with.is_empty());
        let (quality, reason) = if wanted == 0 {
            (SelectionQuality::Ideal, "text only, which this device does".to_string())
        } else if got == wanted {
            (
                SelectionQuality::Ideal,
                format!("this device has everything needed for {language}"),
            )
        } else if got == 0 {
            (
                SelectionQuality::Degraded,
                format!("this device has no speech models for {language}, so it will answer in text"),
            )
        } else {
            (
                SelectionQuality::Acceptable,
                format!("this device has some of what {language} needs, but not all"),
            )
        };
        ModalityPlan {
            transcribe_with,
            generate_with: self.generator_id.clone(),
            speak_with,
            see_with: String::new(),
            quality,
            reason,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Generators

/// What the model-family generators share.
///
/// They differ in their PROMPT FORMAT and nothing else that matters here.
/// Getting the format wrong does not fail - it produces a model that answers
/// slightly worse and nobody can say why, which is why each is written out
/// rather than approximated by a shared template.
pub trait ManagedTextGenerator {
    fn model_id(&self) -> &str;
    fn is_available(&self) -> bool;
    fn format_prompt(&self, turns: &[(String, String)], system: &str) -> String;
}

/// Qwen's ChatML format.
pub struct QwenTextGenerator {
    pub model_id: String,
    generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
}

impl QwenTextGenerator {
    pub fn new(model_id: String, generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>) -> Self {
        Self { model_id, generate }
    }

    pub fn run(&self, turns: &[(String, String)], system: &str) -> Option<String> {
        Some((self.generate.as_ref()?)(&self.format_prompt(turns, system)))
    }
}

impl ManagedTextGenerator for QwenTextGenerator {
    fn model_id(&self) -> &str {
        &self.model_id
    }

    fn is_available(&self) -> bool {
        !self.model_id.is_empty() && self.generate.is_some()
    }

    fn format_prompt(&self, turns: &[(String, String)], system: &str) -> String {
        let mut parts = Vec::new();
        if !system.is_empty() {
            parts.push(format!("<|im_start|>system\n{system}<|im_end|>"));
        }
        for (role, content) in turns {
            parts.push(format!("<|im_start|>{role}\n{content}<|im_end|>"));
        }
        // The trailing OPEN tag is what tells the model it is its turn. Leaving
        // it off makes the model continue the conversation as the user, which
        // reads as the assistant talking to itself.
        parts.push("<|im_start|>assistant\n".into());
        parts.join("\n")
    }
}

/// Kimi's vision-language format.
///
/// Images are referenced by a PLACEHOLDER in the text, in order. The order is
/// the binding - an image list that does not match the placeholders describes
/// the wrong picture with complete confidence.
pub struct KimiVlGenerator {
    pub model_id: String,
    pub image_count: usize,
    generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
}

impl KimiVlGenerator {
    pub const IMAGE_TOKEN: &'static str = "<|media_start|>image<|media_content|><|media_end|>";

    pub fn new(
        model_id: String,
        image_count: usize,
        generate: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
    ) -> Self {
        Self { model_id, image_count, generate }
    }

    pub fn run(&self, turns: &[(String, String)], system: &str) -> Option<String> {
        Some((self.generate.as_ref()?)(&self.format_prompt(turns, system)))
    }
}

impl ManagedTextGenerator for KimiVlGenerator {
    fn model_id(&self) -> &str {
        &self.model_id
    }

    fn is_available(&self) -> bool {
        !self.model_id.is_empty() && self.generate.is_some()
    }

    fn format_prompt(&self, turns: &[(String, String)], system: &str) -> String {
        let mut out = String::new();
        if !system.is_empty() {
            out.push_str(&format!("<|im_system|>system<|im_middle|>{system}<|im_end|>"));
        }
        for (i, (role, content)) in turns.iter().enumerate() {
            let prefix = if i == 0 && self.image_count > 0 { Self::IMAGE_TOKEN } else { "" };
            out.push_str(&format!("<|im_user|>{role}<|im_middle|>{prefix}{content}<|im_end|>"));
        }
        out.push_str("<|im_assistant|>assistant<|im_middle|>");
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The code agent

/// What the model asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AgentActionKind {
    /// The reply could not be parsed. Kept as a VALUE so the loop can re-prompt
    /// rather than fail.
    #[default]
    Unknown,
    ReadFile,
    /// A character-range edit. Ranges rather than a diff because a diff that
    /// fails to apply leaves the model guessing why; a range either is or is not
    /// inside the file.
    EditFile,
    RunCommand,
    SearchCode,
    Finish,
}

/// One parsed action.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AgentAction {
    pub kind: AgentActionKind,
    pub path: String,
    pub range_start: usize,
    pub range_end: usize,
    pub replacement: String,
    pub command: String,
    pub query: String,
    pub top_k: usize,
    pub summary: String,
    /// The source JSON, or the whole reply when it did not parse. Without it a
    /// loop that goes wrong leaves no evidence of what the model actually said.
    pub raw: String,
}

/// Turns a model reply into an action.
pub struct AgentActionParser;

impl AgentActionParser {
    /// Finds the first balanced `{ }` run, ignoring braces inside strings.
    ///
    /// BY BRACE DEPTH rather than by pattern, because models routinely wrap the
    /// object in prose, in a fenced block, or in both - and a pattern that
    /// handles two of those three quietly mis-parses the third.
    pub fn extract_json_object(text: &str) -> &str {
        let bytes: Vec<char> = text.chars().collect();
        let mut depth = 0usize;
        let mut start = None;
        let mut in_string = false;
        let mut escaped = false;
        let mut byte_start = 0usize;
        let mut at = 0usize;
        for ch in bytes {
            let width = ch.len_utf8();
            if in_string {
                if escaped {
                    escaped = false;
                } else if ch == '\\' {
                    escaped = true;
                } else if ch == '"' {
                    in_string = false;
                }
            } else if ch == '"' {
                in_string = true;
            } else if ch == '{' {
                if depth == 0 {
                    start = Some(at);
                    byte_start = at;
                }
                depth += 1;
            } else if ch == '}' {
                depth = depth.saturating_sub(1);
                if depth == 0 && start.is_some() {
                    return &text[byte_start..at + width];
                }
            }
            at += width;
        }
        ""
    }

    fn string_field(json: &str, key: &str) -> String {
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
                out.push(ch);
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

    /// NEVER fails - a reply it cannot understand becomes `Unknown` with `raw`
    /// set.
    pub fn parse(reply: &str) -> AgentAction {
        let object = Self::extract_json_object(reply);
        if object.is_empty() {
            return AgentAction { raw: reply.to_string(), top_k: 10, ..Default::default() };
        }
        let kind = match Self::string_field(object, "action").trim().to_lowercase().as_str() {
            "read_file" | "read" => AgentActionKind::ReadFile,
            "edit_file" | "edit" => AgentActionKind::EditFile,
            "run_command" | "run" => AgentActionKind::RunCommand,
            "search_code" | "search" => AgentActionKind::SearchCode,
            "finish" | "done" => AgentActionKind::Finish,
            _ => {
                return AgentAction { raw: reply.to_string(), top_k: 10, ..Default::default() }
            }
        };
        AgentAction {
            kind,
            path: Self::string_field(object, "path"),
            range_start: Self::string_field(object, "range_start").parse().unwrap_or(0),
            range_end: Self::string_field(object, "range_end").parse().unwrap_or(0),
            replacement: Self::string_field(object, "replacement"),
            command: Self::string_field(object, "command"),
            query: Self::string_field(object, "query"),
            top_k: Self::string_field(object, "top_k").parse().unwrap_or(10),
            summary: Self::string_field(object, "summary"),
            raw: object.to_string(),
        }
    }
}

/// A command the agent wants to run.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CommandRequest {
    pub executable: String,
    pub arguments: Vec<String>,
    pub working_directory: String,
    pub timeout_ms: u64,
}

/// How it went.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CommandResult {
    /// Whether it ran AT ALL. False with exit code 0 is the shape of a refusal,
    /// and a caller that only checks the exit code would read that as success.
    pub executed: bool,
    pub timed_out: bool,
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
    /// Why it did not run. Populated only when `executed` is false.
    pub refusal: String,
}

impl CommandResult {
    pub fn succeeded(&self) -> bool {
        self.executed && !self.timed_out && self.exit_code == 0
    }
}

/// Runs commands for the agent.
pub trait CommandRunner {
    fn run(&self, request: &CommandRequest) -> CommandResult;
}

/// Refuses everything, with a reason.
///
/// THE DEFAULT: an agent that can run commands because nobody configured a
/// runner is an agent that can run commands by accident.
#[derive(Debug, Default, Clone, Copy)]
pub struct DisabledCommandRunner;

impl CommandRunner for DisabledCommandRunner {
    fn run(&self, _request: &CommandRequest) -> CommandResult {
        CommandResult {
            refusal: "command running is disabled on this device".into(),
            ..Default::default()
        }
    }
}

/// Runs only what is on the list.
///
/// An ALLOW-LIST, not a deny-list: a deny-list is a claim to have thought of
/// every dangerous command, and it is wrong the first time somebody pipes one
/// into another.
pub struct ProcessCommandRunner {
    allowed: Vec<String>,
    #[allow(clippy::type_complexity)]
    spawn: Option<Box<dyn Fn(&CommandRequest) -> Result<(i32, String, String, bool), String> + Send + Sync>>,
    max_output_chars: usize,
}

impl ProcessCommandRunner {
    #[allow(clippy::type_complexity)]
    pub fn new(
        allowed_executables: &[String],
        spawn: Option<
            Box<dyn Fn(&CommandRequest) -> Result<(i32, String, String, bool), String> + Send + Sync>,
        >,
        max_output_chars: usize,
    ) -> Option<Self> {
        if allowed_executables.is_empty() {
            // Refused at construction: a runner with an empty list would run
            // nothing, and one with no list would run everything.
            return None;
        }
        Some(Self {
            allowed: allowed_executables
                .iter()
                .map(|e| {
                    e.rsplit(['/', '\\'])
                        .next()
                        .unwrap_or(e)
                        .to_lowercase()
                })
                .collect(),
            spawn,
            max_output_chars: if max_output_chars == 0 { 64 * 1024 } else { max_output_chars },
        })
    }
}

impl CommandRunner for ProcessCommandRunner {
    /// Matching is on the RESOLVED base name, not the string the model wrote -
    /// otherwise "./git", "git.exe" and a relative path through a symlink are
    /// three different things to the check and one thing to the operating
    /// system. And NO SHELL: a shell would make the allow-list meaningless the
    /// first time an argument contained a semicolon.
    fn run(&self, request: &CommandRequest) -> CommandResult {
        let base = request
            .executable
            .rsplit(['/', '\\'])
            .next()
            .unwrap_or(&request.executable)
            .to_lowercase();
        if !self.allowed.contains(&base) {
            return CommandResult {
                refusal: format!("'{base}' is not on the allow-list"),
                ..Default::default()
            };
        }
        let Some(spawn) = &self.spawn else {
            return CommandResult {
                refusal: "no way to run a command on this host".into(),
                ..Default::default()
            };
        };
        match spawn(request) {
            Err(e) => CommandResult { refusal: e, ..Default::default() },
            Ok((exit_code, stdout, stderr, timed_out)) => {
                // Output is truncated: a command that prints a hundred megabytes
                // would otherwise be handed to a model as context and cost more
                // than the task.
                let clip = |text: String| {
                    if text.chars().count() <= self.max_output_chars {
                        text
                    } else {
                        let head: String = text.chars().take(self.max_output_chars).collect();
                        format!("{head}\n… truncated")
                    }
                };
                CommandResult {
                    executed: true,
                    timed_out,
                    exit_code,
                    stdout: clip(stdout),
                    stderr: clip(stderr),
                    refusal: String::new(),
                }
            }
        }
    }
}

/// Which class of device this is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum DeviceTier {
    Wearable,
    LowPhone,
    #[default]
    Phone,
    Tablet,
    Desktop,
    Server,
}

/// What a coding model must meet.
#[derive(Debug, Clone, PartialEq)]
pub struct CodingModelRequirements {
    pub min_parameters_billion: u32,
    pub min_ram_gb: f64,
    pub min_free_storage_gb: f64,
    pub min_device_tier: DeviceTier,
    pub required_capabilities: Vec<String>,
}

impl Default for CodingModelRequirements {
    /// The PROVISIONAL floor, labelled so.
    ///
    /// These are reasoned, not measured - the numbers to trust are the ones a
    /// bench run produces on the actual device, and a default that pretends
    /// otherwise is a threshold nobody ever revisits.
    fn default() -> Self {
        Self {
            min_parameters_billion: 3,
            min_ram_gb: 8.0,
            min_free_storage_gb: 6.0,
            min_device_tier: DeviceTier::Tablet,
            required_capabilities: vec![
                "tools".into(),
                "reasoning".into(),
                "long-context".into(),
            ],
        }
    }
}

/// One candidate model.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CodingModelDescriptor {
    pub model_id: String,
    pub parameters_billion: u32,
    pub ram_gb: f64,
    pub download_gb: f64,
    pub capabilities: Vec<String>,
    pub note: String,
}

/// Lists coding models.
pub trait CodingModelCatalog {
    fn list(&self) -> Vec<CodingModelDescriptor>;
    /// `None` when nothing meets the floor. Returning the closest and letting it
    /// fail on load is how a feature becomes a crash report.
    fn best_for(&self, requirements: &CodingModelRequirements) -> Option<CodingModelDescriptor>;
}

/// Knows about no models.
#[derive(Debug, Default, Clone, Copy)]
pub struct EmptyCodingModelCatalog;

impl CodingModelCatalog for EmptyCodingModelCatalog {
    fn list(&self) -> Vec<CodingModelDescriptor> {
        Vec::new()
    }
    fn best_for(&self, _requirements: &CodingModelRequirements) -> Option<CodingModelDescriptor> {
        None
    }
}

/// Holds a list a host supplied.
#[derive(Debug, Default, Clone)]
pub struct InMemoryCodingModelCatalog {
    models: Vec<CodingModelDescriptor>,
}

impl InMemoryCodingModelCatalog {
    pub fn new(models: Vec<CodingModelDescriptor>) -> Self {
        Self { models }
    }
}

impl CodingModelCatalog for InMemoryCodingModelCatalog {
    fn list(&self) -> Vec<CodingModelDescriptor> {
        self.models.clone()
    }

    fn best_for(&self, requirements: &CodingModelRequirements) -> Option<CodingModelDescriptor> {
        self.models
            .iter()
            .filter(|m| {
                m.parameters_billion >= requirements.min_parameters_billion
                    && m.ram_gb <= requirements.min_ram_gb
                    && requirements.required_capabilities.iter().all(|c| {
                        m.capabilities
                            .iter()
                            .any(|h| h.eq_ignore_ascii_case(c))
                    })
            })
            .max_by_key(|m| m.parameters_billion)
            .cloned()
    }
}

/// Decides whether this device can code at all.
pub trait CodingCapabilityPlannerTrait {
    fn is_capable(&self) -> (bool, String);
}

/// The default planner.
pub struct CodingCapabilityPlanner<C: CodingModelCatalog> {
    catalog: C,
    ram_bytes: u64,
    free_storage_bytes: u64,
    tier: DeviceTier,
}

impl<C: CodingModelCatalog> CodingCapabilityPlanner<C> {
    const GB: f64 = (1u64 << 30) as f64;

    pub fn new(catalog: C, ram_bytes: u64, free_storage_bytes: u64, tier: DeviceTier) -> Self {
        Self { catalog, ram_bytes, free_storage_bytes, tier }
    }
}

impl<C: CodingModelCatalog> CodingCapabilityPlannerTrait for CodingCapabilityPlanner<C> {
    /// The reason names the SHORTFALL - "needs about 8 GB of memory" - rather
    /// than a policy identifier, because it is shown to a person.
    fn is_capable(&self) -> (bool, String) {
        let req = CodingModelRequirements::default();
        let ram_gb = self.ram_bytes as f64 / Self::GB;
        if ram_gb < req.min_ram_gb {
            return (
                false,
                format!(
                    "this needs about {:.0} GB of memory and this device has {ram_gb:.1}",
                    req.min_ram_gb
                ),
            );
        }
        let free_gb = self.free_storage_bytes as f64 / Self::GB;
        if free_gb < req.min_free_storage_gb {
            return (
                false,
                format!(
                    "this needs about {:.0} GB free and this device has {free_gb:.1}",
                    req.min_free_storage_gb
                ),
            );
        }
        if self.tier < req.min_device_tier {
            return (false, "this device is below the class a coding model needs".into());
        }
        if self.catalog.best_for(&req).is_none() {
            return (false, "no catalogued model meets the floor".into());
        }
        (true, String::new())
    }
}

/// Bounds one run.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CodeAgentOptions {
    /// A TERMINATION GUARANTEE, not a tuning knob. A model that has lost the
    /// thread does not stop - it reads the same file again, edits it back, and
    /// reads it once more. Without a cap that costs money until somebody
    /// notices, and on a phone it costs battery until it is flat.
    pub max_iterations: usize,
    pub working_directory: String,
    pub max_observation_chars: usize,
}

impl Default for CodeAgentOptions {
    fn default() -> Self {
        Self {
            max_iterations: 24,
            working_directory: ".".into(),
            max_observation_chars: 16 * 1024,
        }
    }
}

/// One turn of the loop.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CodeAgentStep {
    pub index: usize,
    pub action: AgentAction,
    /// What came back. Truncated to the budget, and the truncation is MARKED so
    /// the model knows it did not see everything.
    pub observation: String,
    pub observation_truncated: bool,
    pub duration_ms: u64,
}

/// The whole run.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CodeAgentRunResult {
    pub finished: bool,
    pub summary: String,
    pub steps: Vec<CodeAgentStep>,
    /// Set when the loop stopped because it hit the cap rather than because the
    /// model said finish. The two must NEVER be confused: one is a completed
    /// task and the other is an abandoned one.
    pub exhausted_iterations: bool,
    pub error: String,
}

/// Runs a coding task.
pub trait CodeAgent {
    fn run(&self, task: &str) -> CodeAgentRunResult;
}

/// Runs nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCodeAgent;

impl CodeAgent for NullCodeAgent {
    fn run(&self, _task: &str) -> CodeAgentRunResult {
        CodeAgentRunResult { error: "no code agent configured".into(), ..Default::default() }
    }
}

/// The default agent.
pub struct CodeAgentLoop<R: CommandRunner> {
    runner: R,
    options: CodeAgentOptions,
    generate: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    read_file: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
}

impl<R: CommandRunner> CodeAgentLoop<R> {
    pub fn new(
        runner: R,
        options: CodeAgentOptions,
        generate: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
        read_file: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
    ) -> Self {
        Self { runner, options, generate, read_file }
    }

    fn truncate(&self, text: String) -> (String, bool) {
        let cap = self.options.max_observation_chars;
        if cap == 0 || text.chars().count() <= cap {
            return (text, false);
        }
        let head: String = text.chars().take(cap).collect();
        (
            format!("{head}\n… truncated; you have not seen the whole thing"),
            true,
        )
    }
}

impl<R: CommandRunner> CodeAgent for CodeAgentLoop<R> {
    fn run(&self, task: &str) -> CodeAgentRunResult {
        let Some(generate) = &self.generate else {
            return CodeAgentRunResult {
                error: "no generator configured".into(),
                ..Default::default()
            };
        };
        let mut transcript = task.to_string();
        let mut steps = Vec::new();

        for i in 0..self.options.max_iterations {
            let reply = match generate(&transcript) {
                Ok(r) => r,
                Err(e) => return CodeAgentRunResult { steps, error: e, ..Default::default() },
            };
            let action = AgentActionParser::parse(&reply);

            if action.kind == AgentActionKind::Finish {
                let summary = action.summary.clone();
                steps.push(CodeAgentStep { index: i, action, ..Default::default() });
                return CodeAgentRunResult {
                    finished: true,
                    summary,
                    steps,
                    ..Default::default()
                };
            }

            let (observation, truncated) = match action.kind {
                AgentActionKind::ReadFile => match &self.read_file {
                    Some(read) => {
                        let path = format!("{}/{}", self.options.working_directory, action.path);
                        match read(&path) {
                            Some(text) => self.truncate(text),
                            None => (format!("could not read {}", action.path), false),
                        }
                    }
                    None => (String::new(), false),
                },
                AgentActionKind::RunCommand => {
                    let fields: Vec<&str> = action.command.split_whitespace().collect();
                    if fields.is_empty() {
                        (String::new(), false)
                    } else {
                        let result = self.runner.run(&CommandRequest {
                            executable: fields[0].to_string(),
                            arguments: fields[1..].iter().map(|s| s.to_string()).collect(),
                            working_directory: self.options.working_directory.clone(),
                            timeout_ms: 60_000,
                        });
                        if result.executed {
                            self.truncate(format!("{}{}", result.stdout, result.stderr))
                        } else {
                            (format!("refused: {}", result.refusal), false)
                        }
                    }
                }
                // Re-prompt rather than fail. Answering in prose when asked for
                // JSON is the most common thing a model does.
                AgentActionKind::Unknown => (
                    "that reply could not be read as an action; answer with a single JSON object"
                        .to_string(),
                    false,
                ),
                _ => (String::new(), false),
            };

            transcript.push_str(&format!("\n{reply}\n{observation}"));
            steps.push(CodeAgentStep {
                index: i,
                action,
                observation,
                observation_truncated: truncated,
                duration_ms: 0,
            });
        }

        CodeAgentRunResult { steps, exhausted_iterations: true, ..Default::default() }
    }
}
