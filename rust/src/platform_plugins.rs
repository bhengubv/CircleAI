//! Plugins, documents, music, the mesh, personal data, and what the device is.
//!
//! THE RAM MEASUREMENT IS THE REASON THE LAST SECTION EXISTS. A runtime will
//! happily tell you about its own heap when you ask how much memory the device
//! has, and the number looks plausible. It is not the device's memory. A model
//! chooser fed that number picks a model to fit a heap rather than a phone, and
//! the result is either a device that refuses a model it could run or one that
//! is killed loading a model it could not.
//!
//! THE CONSENT GUARD IS THE SERIOUS PART OF THE PERSONAL SECTION. Scoped,
//! expiring, revocable, and failing closed at every branch - because the version
//! without any one of those is the one that gets built by default.

use std::collections::{HashMap, HashSet};

// ─────────────────────────────────────────────────────────────────────────────
// Plugins

/// One thing a person may agree to.
///
/// SEPARATE VALUES, deliberately fine-grained, and reading is separate from
/// writing everywhere. An assistant that can read a calendar to answer "when am
/// I free" does not thereby get to send invitations.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ConsentScope {
    CalendarRead,
    CalendarWrite,
    ContactsRead,
    ContactsWrite,
    EmailRead,
    /// Sending is its own scope and is never bundled. Sending mail as somebody
    /// is the single most consequential thing in this file.
    EmailSend,
    LocationRead,
    PhotosRead,
}

impl ConsentScope {
    pub const ALL: &'static [ConsentScope] = &[
        Self::CalendarRead, Self::CalendarWrite, Self::ContactsRead,
        Self::ContactsWrite, Self::EmailRead, Self::EmailSend,
        Self::LocationRead, Self::PhotosRead,
    ];

    pub fn is_write(&self) -> bool {
        matches!(
            self,
            Self::CalendarWrite | Self::ContactsWrite | Self::EmailSend
        )
    }

    pub fn label(&self) -> &'static str {
        match self {
            Self::CalendarRead => "calendar:read",
            Self::CalendarWrite => "calendar:write",
            Self::ContactsRead => "contacts:read",
            Self::ContactsWrite => "contacts:write",
            Self::EmailRead => "email:read",
            Self::EmailSend => "email:send",
            Self::LocationRead => "location:read",
            Self::PhotosRead => "photos:read",
        }
    }
}

/// What a plugin is allowed to do.
///
/// EVERYTHING OFF. A plugin is code from somebody else running inside the
/// assistant, and a permission it was not given is a permission it does not have
/// - not one it has until somebody notices.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Permissions {
    pub read_files: bool,
    pub write_files: bool,
    pub network: bool,
    /// Reaching the model. Off by default, because a plugin with model access
    /// can spend the device's battery and, through the model, its context.
    pub inference: bool,
    /// Held as scopes rather than a flag, so a plugin cannot be given "personal
    /// data" wholesale.
    pub consent_scopes: HashSet<ConsentScope>,
    /// Directories it may touch. Empty with file access on means its own
    /// workspace only.
    pub paths: Vec<String>,
}

impl Permissions {
    pub fn none() -> Self {
        Self::default()
    }

    /// What a person is shown before installing.
    ///
    /// Written as capabilities in plain words, because "network: true" is not a
    /// decision anybody can make.
    pub fn describe(&self) -> String {
        let mut wants: Vec<String> = Vec::new();
        if self.network {
            wants.push("use the internet".into());
        }
        let where_ = |paths: &[String]| {
            if paths.is_empty() {
                " in its own folder".to_string()
            } else {
                format!(" in {}", paths.join(", "))
            }
        };
        if self.read_files {
            wants.push(format!("read files{}", where_(&self.paths)));
        }
        if self.write_files {
            wants.push(format!("change files{}", where_(&self.paths)));
        }
        if self.inference {
            wants.push("use the assistant's model".into());
        }
        let mut scopes: Vec<&ConsentScope> = self.consent_scopes.iter().collect();
        scopes.sort_by_key(|s| s.label());
        for scope in scopes {
            wants.push(format!(
                "reach your {}",
                scope.label().split(':').next().unwrap_or("data")
            ));
        }
        if wants.is_empty() {
            "this asks for nothing".into()
        } else {
            format!("this wants to {}", wants.join("; "))
        }
    }
}

/// Says where plugins live.
pub trait PluginsRootResolver {
    fn plugins_root(&self) -> String;
}

/// Says where one plugin's own files live.
pub trait WorkspacePathProvider {
    fn workspace_for(&self, plugin_id: &str) -> String;
}

/// What loading one plugin did.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct PluginLoadResult {
    pub plugin_id: String,
    pub loaded: bool,
    pub version: String,
    pub granted: Permissions,
    /// What it ASKED for, kept beside what it got - so a review screen can show
    /// the difference. A plugin asking for far more than it was given is worth
    /// seeing.
    pub requested: Permissions,
    pub error: String,
}

impl PluginLoadResult {
    pub fn was_narrowed(&self) -> bool {
        self.loaded && self.granted != self.requested
    }
}

/// Loads a plugin with no more than it was granted.
///
/// THE INTERSECTION, ALWAYS. A plugin gets what it asked for AND what the person
/// allowed - never the union, and never what it asked for on the grounds that it
/// asked. That single rule is the difference between a permission system and a
/// manifest.
#[derive(Default)]
pub struct PluginLoader {
    #[allow(clippy::type_complexity)]
    read_manifest: Option<Box<dyn Fn(&str) -> Option<(String, Permissions)> + Send + Sync>>,
}

impl PluginLoader {
    #[allow(clippy::type_complexity)]
    pub fn new(
        read_manifest: Option<Box<dyn Fn(&str) -> Option<(String, Permissions)> + Send + Sync>>,
    ) -> Self {
        Self { read_manifest }
    }

    pub fn intersect(requested: &Permissions, allowed: &Permissions) -> Permissions {
        Permissions {
            read_files: requested.read_files && allowed.read_files,
            write_files: requested.write_files && allowed.write_files,
            network: requested.network && allowed.network,
            inference: requested.inference && allowed.inference,
            consent_scopes: requested
                .consent_scopes
                .intersection(&allowed.consent_scopes)
                .copied()
                .collect(),
            // Paths intersect too. A plugin granted one directory and asking for
            // two gets one.
            paths: requested
                .paths
                .iter()
                .filter(|p| allowed.paths.contains(p))
                .cloned()
                .collect(),
        }
    }

    pub fn load(&self, plugin_id: &str, allowed: &Permissions) -> PluginLoadResult {
        let failed = |error: &str| PluginLoadResult {
            plugin_id: plugin_id.to_string(),
            error: error.to_string(),
            ..Default::default()
        };
        if plugin_id.trim().is_empty() {
            return failed("a plugin needs an identifier");
        }
        let Some(read) = &self.read_manifest else {
            return failed("no way to read a manifest");
        };
        let Some((version, requested)) = read(plugin_id) else {
            return failed("that plugin has no readable manifest");
        };
        PluginLoadResult {
            plugin_id: plugin_id.to_string(),
            loaded: true,
            version,
            granted: Self::intersect(&requested, allowed),
            requested,
            error: String::new(),
        }
    }
}

/// Starts and stops plugins.
///
/// STOPPING IS THE HARD PART. A plugin that will not stop is a plugin still
/// holding a permission, so this drops the grant FIRST - if it ignores the
/// request it is at least no longer allowed to do anything.
pub struct PluginLifecycleService {
    loader: PluginLoader,
    running: HashMap<String, PluginLoadResult>,
}

impl PluginLifecycleService {
    pub fn new(loader: PluginLoader) -> Self {
        Self { loader, running: HashMap::new() }
    }

    pub fn start(&mut self, plugin_id: &str, allowed: &Permissions) -> PluginLoadResult {
        let result = self.loader.load(plugin_id, allowed);
        if result.loaded {
            self.running.insert(plugin_id.to_string(), result.clone());
        }
        result
    }

    pub fn stop(&mut self, plugin_id: &str) -> bool {
        self.running.remove(plugin_id).is_some()
    }

    pub fn stop_all(&mut self) -> usize {
        let count = self.running.len();
        self.running.clear();
        count
    }

    pub fn running_ids(&self) -> Vec<String> {
        let mut out: Vec<String> = self.running.keys().cloned().collect();
        out.sort();
        out
    }

    /// A plugin that is not running has NO permissions, not its last ones.
    pub fn permissions_of(&self, plugin_id: &str) -> Permissions {
        self.running
            .get(plugin_id)
            .map(|r| r.granted.clone())
            .unwrap_or_default()
    }
}

/// One plugin somebody has installed.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RegisteredPlugin {
    pub plugin_id: String,
    pub display_name: String,
    pub version: String,
    pub granted: Permissions,
    pub installed_at_ms: u64,
}

/// What the marketplace says about one.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MarketplaceEntry {
    pub plugin_id: String,
    pub display_name: String,
    pub summary: String,
    pub author: String,
    pub requested: Permissions,
    /// The digest of the package. A plugin without one is not installable, for
    /// the same reason a model without one is not.
    pub sha256: String,
}

/// What this device has installed.
#[derive(Debug, Default)]
pub struct PluginRegistry {
    plugins: HashMap<String, RegisteredPlugin>,
}

impl PluginRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register(&mut self, plugin: RegisteredPlugin) {
        self.plugins.insert(plugin.plugin_id.clone(), plugin);
    }

    pub fn get(&self, plugin_id: &str) -> Option<&RegisteredPlugin> {
        self.plugins.get(plugin_id)
    }

    pub fn all(&self) -> Vec<RegisteredPlugin> {
        let mut out: Vec<RegisteredPlugin> = self.plugins.values().cloned().collect();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    /// Removing a plugin removes its GRANT too. Leaving the grant behind means a
    /// reinstall silently inherits permissions nobody re-approved.
    pub fn remove(&mut self, plugin_id: &str) -> bool {
        self.plugins.remove(plugin_id).is_some()
    }
}

/// What is on offer.
///
/// NOTHING INSTALLS WITHOUT A DIGEST and nothing installs without the person
/// seeing what it asked for. A marketplace that installs on a tap is a
/// marketplace that installs whatever was in the listing when the tap landed.
pub struct PluginMarketplace {
    #[allow(clippy::type_complexity)]
    download: Option<Box<dyn Fn(&MarketplaceEntry) -> Option<Vec<u8>> + Send + Sync>>,
    digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
}

impl PluginMarketplace {
    #[allow(clippy::type_complexity)]
    pub fn new(
        download: Option<Box<dyn Fn(&MarketplaceEntry) -> Option<Vec<u8>> + Send + Sync>>,
        digest_of: Option<Box<dyn Fn(&[u8]) -> String + Send + Sync>>,
    ) -> Self {
        Self { download, digest_of }
    }

    /// What to show before installing, so a person is agreeing to something they
    /// have read.
    pub fn consent_prompt(entry: &MarketplaceEntry) -> String {
        format!(
            "{} by {}: {}\n{}",
            entry.display_name,
            entry.author,
            entry.summary,
            entry.requested.describe()
        )
    }

    pub fn install(&self, entry: &MarketplaceEntry) -> Result<Vec<u8>, String> {
        if entry.sha256.is_empty() {
            return Err("that plugin has no checksum, so it will not install".into());
        }
        let (Some(download), Some(digest)) = (&self.download, &self.digest_of) else {
            return Err("this device cannot install plugins".into());
        };
        let bytes = download(entry).ok_or("the plugin did not download")?;
        if !digest(&bytes).eq_ignore_ascii_case(entry.sha256.trim()) {
            return Err("that plugin does not match its checksum".into());
        }
        Ok(bytes)
    }
}

/// Wires the plugin service.
pub struct PluginsRegistration;

impl PluginsRegistration {
    pub fn add_plugins(loader: PluginLoader) -> PluginLifecycleService {
        PluginLifecycleService::new(loader)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Documents

/// What is being made.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DocumentKind {
    #[default]
    Cv,
    CoverLetter,
    Report,
    Invoice,
    Letter,
}

/// How it comes out.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DocumentFormat {
    Pdf,
    /// Plain text, always available. The fallback that means a device with no
    /// renderer still produces the document rather than an apology.
    #[default]
    Text,
    Markdown,
    Html,
}

/// How to reach somebody.
///
/// Every field optional and NOTHING inferred. A CV generator that guesses an
/// email from a name gets it wrong in public, on the document a person is judged
/// by.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CvContact {
    pub name: String,
    pub email: String,
    pub phone: String,
    pub location: String,
    pub website: String,
}

impl CvContact {
    /// Only what was given. An empty field prints nothing rather than a
    /// placeholder - a CV with "Phone: -" reads as unfinished.
    pub fn lines(&self) -> Vec<String> {
        [&self.email, &self.phone, &self.location, &self.website]
            .iter()
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string())
            .collect()
    }
}

/// One job.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CvExperience {
    pub role: String,
    pub organisation: String,
    pub start: String,
    pub end: String,
    pub bullets: Vec<String>,
    pub location: String,
}

impl CvExperience {
    /// A missing end reads as "present", which is what it means on a CV and is
    /// the one place an assumption here is safe.
    pub fn period(&self) -> String {
        if self.start.is_empty() {
            return self.end.clone();
        }
        format!(
            "{} - {}",
            self.start,
            if self.end.is_empty() { "present" } else { &self.end }
        )
    }
}

/// One qualification.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CvEducation {
    pub qualification: String,
    pub institution: String,
    pub year: String,
    pub detail: String,
}

/// One certificate.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CvCertification {
    pub name: String,
    pub issuer: String,
    pub year: String,
    /// Deliberately not validated or fetched. Checking a credential against an
    /// issuer means telling that issuer somebody is applying for a job.
    pub reference: String,
}

/// A whole CV.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CvDocument {
    pub contact: CvContact,
    pub headline: String,
    pub summary: String,
    pub experience: Vec<CvExperience>,
    pub education: Vec<CvEducation>,
    pub certifications: Vec<CvCertification>,
    pub skills: Vec<String>,
}

impl CvDocument {
    /// The format that always works, and the one that survives a paste into an
    /// application form that strips everything else.
    pub fn to_text(&self) -> String {
        let mut out: Vec<String> = Vec::new();
        if !self.contact.name.is_empty() {
            out.push(self.contact.name.to_uppercase());
            out.push(String::new());
        }
        if !self.headline.is_empty() {
            out.push(self.headline.clone());
            out.push(String::new());
        }
        out.extend(self.contact.lines());
        if !self.summary.is_empty() {
            out.extend([String::new(), "SUMMARY".into(), self.summary.clone()]);
        }
        if !self.experience.is_empty() {
            out.extend([String::new(), "EXPERIENCE".into()]);
            for job in &self.experience {
                out.push(format!("{}, {}  ({})", job.role, job.organisation, job.period()));
                out.extend(job.bullets.iter().map(|b| format!("  - {b}")));
            }
        }
        if !self.education.is_empty() {
            out.extend([String::new(), "EDUCATION".into()]);
            out.extend(self.education.iter().map(|e| {
                if e.year.is_empty() {
                    format!("{}, {}", e.qualification, e.institution)
                } else {
                    format!("{}, {} ({})", e.qualification, e.institution, e.year)
                }
            }));
        }
        if !self.certifications.is_empty() {
            out.extend([String::new(), "CERTIFICATIONS".into()]);
            out.extend(self.certifications.iter().map(|c| {
                [c.name.as_str(), c.issuer.as_str(), c.year.as_str()]
                    .iter()
                    .filter(|s| !s.is_empty())
                    .cloned()
                    .collect::<Vec<_>>()
                    .join(" - ")
            }));
        }
        if !self.skills.is_empty() {
            out.extend([String::new(), "SKILLS".into(), self.skills.join(", ")]);
        }
        out.join("\n")
    }
}

/// A letter to go with it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CoverLetter {
    pub sender: CvContact,
    pub recipient: String,
    pub organisation: String,
    pub subject: String,
    pub body: String,
    /// An ISO date, so the document formats it to the reader's convention rather
    /// than baking in one country's order.
    pub written_on_iso: String,
}

/// Who an invoice is from or to.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct InvoiceParty {
    pub name: String,
    pub address_lines: Vec<String>,
    pub vat_number: String,
    pub email: String,
}

/// One line on a printed invoice.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct InvoiceLineItem {
    pub description: String,
    pub quantity_thousandths: i64,
    /// In MINOR UNITS. The document formats it; it never does arithmetic on a
    /// decimal.
    pub unit_price_minor: i64,
    pub tax_basis_points: i64,
    pub currency: String,
}

/// A table in a report.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ReportTable {
    pub headers: Vec<String>,
    pub rows: Vec<Vec<String>>,
    pub caption: String,
}

impl ReportTable {
    /// The WIDEST row, not the header count.
    ///
    /// A row with an extra cell would otherwise be silently truncated, which
    /// loses data in a document somebody is about to act on.
    pub fn column_count(&self) -> usize {
        self.rows
            .iter()
            .map(Vec::len)
            .chain(std::iter::once(self.headers.len()))
            .max()
            .unwrap_or(0)
    }
}

/// One section.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ReportSection {
    pub heading: String,
    pub body: String,
    pub tables: Vec<ReportTable>,
    /// Sections nest. Depth is computed on render rather than stored, so moving
    /// a section cannot leave it labelled with its old level.
    pub subsections: Vec<ReportSection>,
}

/// A whole report.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ReportDocument {
    pub title: String,
    pub subtitle: String,
    pub author: String,
    pub written_on_iso: String,
    pub sections: Vec<ReportSection>,
}

impl ReportDocument {
    /// Flattens to (number, depth, section) in reading order.
    ///
    /// Numbers are DERIVED, so inserting a section renumbers everything after it
    /// automatically - a stored number is a cross-reference that silently goes
    /// wrong.
    pub fn numbered(&self) -> Vec<(String, usize, &ReportSection)> {
        fn walk<'a>(
            sections: &'a [ReportSection],
            prefix: &str,
            depth: usize,
            out: &mut Vec<(String, usize, &'a ReportSection)>,
        ) {
            for (i, section) in sections.iter().enumerate() {
                let number = format!("{prefix}{}", i + 1);
                out.push((number.clone(), depth, section));
                walk(&section.subsections, &format!("{number}."), depth + 1, out);
            }
        }
        let mut out = Vec::new();
        walk(&self.sections, "", 0, &mut out);
        out
    }
}

/// A request to make one.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DocumentRequest {
    pub kind: DocumentKind,
    pub format: DocumentFormat,
    pub title: String,
}

/// What came back.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DocumentResult {
    pub bytes: Vec<u8>,
    pub media_type: String,
    pub page_count: usize,
    /// Set when the request could not be met AT ALL. A result with no bytes and
    /// no error is a bug, and this makes that shape impossible to read as
    /// success.
    pub error: String,
}

impl DocumentResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.bytes.is_empty()
    }
}

/// Renders a document request.
pub trait DocumentEngine {
    fn supports(&self, format: DocumentFormat) -> bool;
    fn render_cv(&self, cv: &CvDocument, format: DocumentFormat) -> DocumentResult;
    fn render_report(&self, report: &ReportDocument, format: DocumentFormat) -> DocumentResult;
}

/// The default engine.
///
/// Named for the C# class it mirrors. Nothing here uses PdfSharp - the text path
/// is the whole implementation on this port, and it says so rather than
/// pretending to a layout it does not have.
#[derive(Debug, Default, Clone, Copy)]
pub struct PdfSharpDocumentEngine;

impl DocumentEngine for PdfSharpDocumentEngine {
    fn supports(&self, format: DocumentFormat) -> bool {
        format == DocumentFormat::Text
    }

    fn render_cv(&self, cv: &CvDocument, format: DocumentFormat) -> DocumentResult {
        if !self.supports(format) {
            return DocumentResult {
                error: "this engine renders plain text on this platform".into(),
                ..Default::default()
            };
        }
        DocumentResult {
            bytes: cv.to_text().into_bytes(),
            media_type: "text/plain".into(),
            page_count: 1,
            error: String::new(),
        }
    }

    fn render_report(&self, report: &ReportDocument, format: DocumentFormat) -> DocumentResult {
        if !self.supports(format) {
            return DocumentResult {
                error: "this engine renders plain text on this platform".into(),
                ..Default::default()
            };
        }
        let mut out: Vec<String> = Vec::new();
        if !report.title.is_empty() {
            out.push(report.title.to_uppercase());
        }
        if !report.subtitle.is_empty() {
            out.push(report.subtitle.clone());
        }
        for (number, depth, section) in report.numbered() {
            let indent = "  ".repeat(depth);
            out.push(String::new());
            out.push(format!("{indent}{number} {}", section.heading));
            if !section.body.is_empty() {
                out.push(format!("{indent}{}", section.body));
            }
            for table in &section.tables {
                if !table.caption.is_empty() {
                    out.push(format!("{indent}[{}]", table.caption));
                }
                if !table.headers.is_empty() {
                    out.push(format!("{indent}{}", table.headers.join(" | ")));
                }
                out.extend(table.rows.iter().map(|r| format!("{indent}{}", r.join(" | "))));
            }
        }
        DocumentResult {
            bytes: out.join("\n").into_bytes(),
            media_type: "text/plain".into(),
            page_count: 1,
            error: String::new(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Music

/// The shape of a block of PCM.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioPcmFormat {
    pub sample_rate_hz: u32,
    pub channels: u16,
    pub bits_per_sample: u16,
}

impl Default for AudioPcmFormat {
    fn default() -> Self {
        Self { sample_rate_hz: 22_050, channels: 1, bits_per_sample: 16 }
    }
}

/// The twelve, as semitones above C.
///
/// Sharps only. A separate flat spelling would be musically correct and would
/// double every lookup table for no audible difference.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PitchClass {
    #[default]
    C = 0,
    CSharp = 1,
    D = 2,
    DSharp = 3,
    E = 4,
    F = 5,
    FSharp = 6,
    G = 7,
    GSharp = 8,
    A = 9,
    ASharp = 10,
    B = 11,
}

/// Which notes are in play.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Scale {
    Major,
    /// Natural minor. The one people mean by "sad".
    Minor,
    /// Five notes, no semitone clashes. ANY two notes in it sound fine together,
    /// which makes it the safe default for a generated bed - a bad random choice
    /// is still consonant.
    #[default]
    Pentatonic,
    Dorian,
    /// Whole tones only. Deliberately unresolved; used for tension, never for a
    /// bed somebody has to listen to for four minutes.
    WholeTone,
}

impl Scale {
    pub fn intervals(&self) -> &'static [i32] {
        match self {
            Self::Major => &[0, 2, 4, 5, 7, 9, 11],
            Self::Minor => &[0, 2, 3, 5, 7, 8, 10],
            Self::Pentatonic => &[0, 2, 4, 7, 9],
            Self::Dorian => &[0, 2, 3, 5, 7, 9, 10],
            Self::WholeTone => &[0, 2, 4, 6, 8, 10],
        }
    }
}

/// A tonic and a scale.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MusicalKey {
    pub tonic: PitchClass,
    pub scale: Scale,
}

impl MusicalKey {
    /// A4 = 440 Hz is MIDI note 69. Equal temperament, so every semitone is the
    /// twelfth root of two - the formula rather than a table, because a table
    /// has to stop somewhere and this does not.
    pub const A4_MIDI: i32 = 69;
    pub const A4_HZ: f64 = 440.0;

    pub fn frequency_of(midi_note: i32) -> f64 {
        Self::A4_HZ * 2f64.powf((midi_note - Self::A4_MIDI) as f64 / 12.0)
    }

    /// MIDI notes of the scale, ascending, wrapping into higher octaves.
    ///
    /// C4 is MIDI 60, so an octave number maps to `(octave + 1) * 12`. Getting
    /// that offset wrong transposes everything by an octave, which sounds fine
    /// and is wrong - the bed ends up under or over the voice it should sit
    /// with.
    pub fn degrees(&self, octave: i32, count: usize) -> Vec<i32> {
        let intervals = self.scale.intervals();
        let wanted = if count == 0 { intervals.len() } else { count };
        let base = (octave + 1) * 12 + self.tonic as i32;
        (0..wanted)
            .map(|i| {
                base + 12 * (i / intervals.len()) as i32 + intervals[i % intervals.len()]
            })
            .collect()
    }

    pub fn frequencies(&self, octave: i32, count: usize) -> Vec<f64> {
        self.degrees(octave, count)
            .into_iter()
            .map(Self::frequency_of)
            .collect()
    }
}

/// Where a bed comes from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum MusicBedBackend {
    /// Sine tones from a scale. Always available, ours, free.
    Procedural,
    /// A model. Only when one has been downloaded.
    Neural,
    /// A file the person supplied. Their licence, their decision.
    SampleLibrary,
    #[default]
    None,
}

/// What kind of bed is wanted.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MusicSpec {
    pub key: MusicalKey,
    /// Under a voice, slower is better - a bed that competes for attention with
    /// the words is a bed that failed.
    pub tempo_bpm: u32,
    pub duration_seconds: f32,
    /// 0..1, and the default is deliberately low. A bed at conversational level
    /// is not a bed.
    pub level: f32,
    pub voices: usize,
    pub format: AudioPcmFormat,
    pub seed: u32,
}

impl Default for MusicSpec {
    fn default() -> Self {
        Self {
            key: MusicalKey::default(),
            tempo_bpm: 72,
            duration_seconds: 8.0,
            level: 0.18,
            voices: 3,
            format: AudioPcmFormat::default(),
            seed: 0,
        }
    }
}

/// The rendered result.
#[derive(Debug, Clone, PartialEq)]
pub struct MusicBed {
    pub samples: Vec<f32>,
    pub format: AudioPcmFormat,
    pub backend: MusicBedBackend,
    pub duration_seconds: f32,
    /// Set when nothing could be made. Empty samples with no reason is a bug
    /// that reads as silence.
    pub error: String,
}

/// Makes a bed.
pub trait MusicBedGenerator {
    fn backend(&self) -> MusicBedBackend;
    fn is_available(&self) -> bool;
    fn generate(&self, spec: &MusicSpec) -> MusicBed;
}

/// Makes silence, and says so.
///
/// Returns a bed with an error rather than failing: a clip with no music is
/// still a clip, and failing the whole render because the bed could not be made
/// is the wrong trade.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMusicBedGenerator;

impl MusicBedGenerator for NullMusicBedGenerator {
    fn backend(&self) -> MusicBedBackend {
        MusicBedBackend::None
    }
    fn is_available(&self) -> bool {
        true
    }
    fn generate(&self, spec: &MusicSpec) -> MusicBed {
        MusicBed {
            samples: Vec::new(),
            format: spec.format,
            backend: MusicBedBackend::None,
            duration_seconds: 0.0,
            error: "no music generator is configured on this device".into(),
        }
    }
}

/// Sine tones from a scale, mixed and enveloped.
///
/// DETERMINISTIC from the spec's seed, so the same spec makes the same bed -
/// which matters because a person who liked yesterday's clip should be able to
/// make it again.
#[derive(Debug, Default, Clone, Copy)]
pub struct ProceduralMusicBedGenerator;

impl ProceduralMusicBedGenerator {
    /// Attack and release, in seconds. Short enough to be inaudible as a fade
    /// and long enough to remove the click: a step at 22050 Hz is broadband and
    /// the ear hears it as a tick, which is the single most common defect in
    /// generated audio.
    pub const ENVELOPE_SECONDS: f32 = 0.02;

    /// A tiny LCG, so the bed does not depend on a shared generator that
    /// something unrelated may have drawn from.
    fn next_random(state: u32) -> (u32, f32) {
        let next = state.wrapping_mul(1_103_515_245).wrapping_add(12_345) & 0x7FFF_FFFF;
        (next, next as f32 / 0x7FFF_FFFF as f32)
    }

    fn envelope(index: usize, total: usize, rate: u32) -> f32 {
        let ramp = ((Self::ENVELOPE_SECONDS * rate as f32) as usize).max(1);
        if index < ramp {
            index as f32 / ramp as f32
        } else if index + ramp >= total {
            ((total - index) as f32 / ramp as f32).max(0.0)
        } else {
            1.0
        }
    }
}

impl MusicBedGenerator for ProceduralMusicBedGenerator {
    fn backend(&self) -> MusicBedBackend {
        MusicBedBackend::Procedural
    }

    fn is_available(&self) -> bool {
        true
    }

    fn generate(&self, spec: &MusicSpec) -> MusicBed {
        let rate = spec.format.sample_rate_hz;
        let total = (spec.duration_seconds * rate as f32) as usize;
        if total == 0 {
            return MusicBed {
                samples: Vec::new(),
                format: spec.format,
                backend: self.backend(),
                duration_seconds: 0.0,
                error: "a bed needs a duration".into(),
            };
        }

        let voices = spec.voices.max(1);
        let pool = spec.key.frequencies(3, (voices * 2).max(5));
        let mut state = if spec.seed == 0 { 1 } else { spec.seed };
        let mut samples = vec![0f32; total];
        let seconds_per_beat = 60.0 / spec.tempo_bpm.max(1) as f32;
        let note_frames = ((seconds_per_beat * 2.0 * rate as f32) as usize).max(1);

        for voice in 0..voices {
            // Each voice starts at a different point so they do not all change
            // note together - simultaneous changes sound like a chord machine
            // rather than a bed.
            let mut position = -((voice * note_frames / voices) as isize);
            while position < total as isize {
                let (next_state, drawn) = Self::next_random(state);
                state = next_state;
                let frequency = pool[(drawn * pool.len() as f32) as usize % pool.len()];
                let length = note_frames.min(total.saturating_sub(position.max(0) as usize));
                if length == 0 {
                    break;
                }
                for i in 0..length {
                    let n = position + i as isize;
                    if n < 0 || n as usize >= total {
                        continue;
                    }
                    let phase = 2.0 * std::f64::consts::PI * frequency * n as f64 / rate as f64;
                    samples[n as usize] += phase.sin() as f32 * Self::envelope(i, length, rate);
                }
                position += note_frames as isize;
            }
        }

        // SCALED BY THE VOICE COUNT. Without this, three voices each reaching
        // 1.0 sum to 3.0, clip, and come out as a buzz that sounds exactly like
        // a broken decoder.
        let scale = spec.level / voices as f32;
        for s in samples.iter_mut() {
            *s *= scale;
        }

        MusicBed {
            samples,
            format: spec.format,
            backend: self.backend(),
            duration_seconds: total as f32 / rate as f32,
            error: String::new(),
        }
    }
}

/// Picks a generator, preferring the one that is actually there.
///
/// PROCEDURAL IS THE FLOOR and never absent. A resolver that could return
/// nothing would make every caller handle a case that need not exist.
#[derive(Default)]
pub struct MusicBedGeneratorResolver {
    generators: Vec<Box<dyn MusicBedGenerator + Send + Sync>>,
}

impl MusicBedGeneratorResolver {
    pub fn new(generators: Vec<Box<dyn MusicBedGenerator + Send + Sync>>) -> Self {
        Self { generators }
    }

    pub fn resolve(&self, preferred: Option<MusicBedBackend>) -> &dyn MusicBedGenerator {
        if let Some(wanted) = preferred {
            if let Some(found) = self
                .generators
                .iter()
                .find(|g| g.backend() == wanted && g.is_available())
            {
                return found.as_ref();
            }
        }
        self.generators
            .iter()
            .find(|g| g.is_available() && g.backend() != MusicBedBackend::None)
            .map(|g| g.as_ref())
            .unwrap_or(&ProceduralMusicBedGenerator)
    }

    pub fn available_backends(&self) -> Vec<MusicBedBackend> {
        let mut out: Vec<MusicBedBackend> = self
            .generators
            .iter()
            .filter(|g| g.is_available())
            .map(|g| g.backend())
            .collect();
        if !out.contains(&MusicBedBackend::Procedural) {
            out.push(MusicBedBackend::Procedural);
        }
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// What the device is

/// Where a memory number came from.
///
/// THE POINT OF THE WHOLE TYPE. A runtime's heap figure and the device's
/// physical memory are both "a number of bytes about memory", and using one
/// where the other was meant is invisible until a model gets killed on a phone
/// that had plenty of room.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlatformMemory {
    /// Nothing measured. Not zero - UNKNOWN. A chooser must treat this as "do
    /// not know" and refuse to size anything by it.
    #[default]
    Unknown,
    /// Read from the operating system. The only source that answers the question
    /// actually being asked.
    Physical,
    /// The runtime's own heap. NEVER the device's memory, and named so that
    /// using it as such requires writing the word.
    ManagedHeap,
    /// What a container or cgroup will allow, which on a server is the real
    /// ceiling regardless of what the host machine has.
    CgroupLimit,
}

/// How much memory, and how we know.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RamMeasurement {
    pub total_bytes: u64,
    pub available_bytes: u64,
    pub source: PlatformMemory,
}

impl RamMeasurement {
    /// A measurement with a value and no source is REFUSED.
    ///
    /// An unsourced number is exactly the bug this type exists to prevent, and
    /// letting one be built would put it back.
    pub fn new(total_bytes: u64, available_bytes: u64, source: PlatformMemory) -> Option<Self> {
        if total_bytes > 0 && source == PlatformMemory::Unknown {
            return None;
        }
        Some(Self { total_bytes, available_bytes, source })
    }

    pub fn unknown() -> Self {
        Self::default()
    }

    pub fn physical(total: u64, available: u64) -> Self {
        Self {
            total_bytes: total,
            available_bytes: if available == 0 { total } else { available },
            source: PlatformMemory::Physical,
        }
    }

    /// Deliberately awkward to build and clearly named at every call site.
    pub fn managed_heap(total: u64) -> Self {
        Self { total_bytes: total, available_bytes: 0, source: PlatformMemory::ManagedHeap }
    }

    /// Whether a model chooser may size anything by this.
    ///
    /// A HEAP READING IS NOT USABLE, and that is the rule this whole section is
    /// built around: it describes the runtime's allocations, not the phone.
    pub fn is_usable_for_sizing(&self) -> bool {
        self.total_bytes > 0
            && matches!(
                self.source,
                PlatformMemory::Physical | PlatformMemory::CgroupLimit
            )
    }

    pub fn total_gb(&self) -> f64 {
        self.total_bytes as f64 / (1u64 << 30) as f64
    }

    pub fn describe(&self) -> String {
        if self.source == PlatformMemory::Unknown || self.total_bytes == 0 {
            return "this device's memory has not been measured".into();
        }
        if !self.is_usable_for_sizing() {
            return format!(
                "{:.1} GB of managed heap - this is NOT the device's memory and must not be used to choose a model",
                self.total_gb()
            );
        }
        format!("{:.1} GB of memory", self.total_gb())
    }
}

/// What the assistant knows about the hardware it is on.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SystemInfoDeviceContext {
    pub device_name: String,
    pub platform: String,
    pub ram: RamMeasurement,
    pub cpu_count: usize,
    /// `None` when unknown. NOT 100 - a device that assumes a full battery
    /// because it cannot read one will spend a flat phone's last minutes on
    /// inference.
    pub battery_percent: Option<u8>,
    pub is_charging: Option<bool>,
    pub thermal_status: String,
    pub free_storage_bytes: u64,
}

impl SystemInfoDeviceContext {
    pub fn can_size_models(&self) -> bool {
        self.ram.is_usable_for_sizing()
    }

    pub fn describe(&self) -> String {
        let mut parts = vec![
            if self.device_name.is_empty() {
                "this device".to_string()
            } else {
                self.device_name.clone()
            },
            self.ram.describe(),
            format!("{} cores", self.cpu_count.max(1)),
        ];
        if let Some(percent) = self.battery_percent {
            parts.push(format!(
                "{percent}% {}",
                if self.is_charging == Some(true) { "charging" } else { "on battery" }
            ));
        }
        parts.join(", ")
    }
}

/// The seam to whatever the host can actually ask.
///
/// Every probe may be absent. A build with none of them returns UNKNOWN
/// everywhere - which is correct, and better than a plausible number nobody can
/// trace.
#[derive(Default)]
pub struct PlatformInterop {
    #[allow(clippy::type_complexity)]
    pub physical_memory: Option<Box<dyn Fn() -> (u64, u64) + Send + Sync>>,
    pub cgroup_limit: Option<Box<dyn Fn() -> u64 + Send + Sync>>,
    pub cpu_count: Option<Box<dyn Fn() -> usize + Send + Sync>>,
    pub battery_percent: Option<Box<dyn Fn() -> Option<u8> + Send + Sync>>,
    pub is_charging: Option<Box<dyn Fn() -> Option<bool> + Send + Sync>>,
}

impl PlatformInterop {
    /// A cgroup limit BEATS the physical figure where both exist.
    ///
    /// On a container the host's memory is not what this process may use, and
    /// sizing a model by the host's figure gets the process killed by the thing
    /// that set the limit.
    pub fn measure_ram(&self) -> RamMeasurement {
        if let Some(limit) = self.cgroup_limit.as_ref().map(|f| f()) {
            if limit > 0 {
                return RamMeasurement {
                    total_bytes: limit,
                    available_bytes: limit,
                    source: PlatformMemory::CgroupLimit,
                };
            }
        }
        if let Some((total, available)) = self.physical_memory.as_ref().map(|f| f()) {
            if total > 0 {
                return RamMeasurement::physical(total, available);
            }
        }
        RamMeasurement::unknown()
    }

    /// At least 1. A zero here divides by zero in every thread calculation
    /// downstream.
    pub fn cpu_count(&self) -> usize {
        self.cpu_count.as_ref().map(|f| f()).unwrap_or(1).max(1)
    }
}

/// What a model does.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ModelModality {
    #[default]
    Text,
    Transcription,
    Speech,
    Vision,
    Embedding,
    /// Text and vision together. Its own value rather than a pair, because a
    /// model that takes both is not the same as two that each take one.
    Multimodal,
    Rerank,
}

/// Where a download has got to.
///
/// Verifying and Installing are separate from Downloading because they are what
/// a person waits through AFTER the progress bar reaches the end - and a bar
/// sitting at 100% with no explanation reads as a hang.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DownloadPhase {
    #[default]
    Idle,
    /// Working out what to fetch. Fast, and worth naming so the UI does not show
    /// 0% during it.
    Resolving,
    Downloading,
    /// Checking the digest. On a phone, hashing four gigabytes is a real wait.
    Verifying,
    Installing,
    Complete,
    Failed,
    /// Stopped on purpose. NOT a failure, and shown differently.
    Cancelled,
}

/// Where a model comes from.
///
/// NO MODEL NAME AND NO DEFAULT REPOSITORY. Both are supplied by the catalogue,
/// because a hardcoded either is a thing that cannot be changed without a
/// release.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModelSource {
    pub source_id: String,
    pub repository: String,
    pub revision: String,
    pub files: Vec<String>,
    /// Keyed by file name. A file with no digest is refused on import, so this
    /// being complete is the difference between a bundle that can be verified
    /// and one that cannot.
    pub digests: HashMap<String, String>,
    pub total_bytes: u64,
}

impl ModelSource {
    pub fn is_verifiable(&self) -> bool {
        !self.files.is_empty()
            && self
                .files
                .iter()
                .all(|f| self.digests.get(f).map(|d| !d.is_empty()).unwrap_or(false))
    }
}

/// Where model files live on this device.
///
/// EVERY PATH IS CONTAINED. A model id arrives from a catalogue, which is
/// fetched, which means it is input - and an id of `../../../etc` that joins
/// cleanly writes outside the model directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelPaths {
    pub root: String,
}

impl ModelPaths {
    pub fn new(root: &str) -> Option<Self> {
        (!root.is_empty()).then(|| Self { root: root.to_string() })
    }

    /// A single segment only. A slash makes it a path, and a path is the thing
    /// being defended against.
    pub fn is_safe_id(model_id: &str) -> bool {
        !model_id.is_empty()
            && model_id.len() <= 128
            && model_id
                .chars()
                .next()
                .map(|c| c.is_ascii_alphanumeric())
                .unwrap_or(false)
            && model_id
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-'))
    }

    pub fn model_directory(&self, model_id: &str) -> Option<String> {
        Self::is_safe_id(model_id).then(|| format!("{}/{model_id}", self.root))
    }

    /// Contained by NORMALISING the segments, not by inspecting the raw string.
    ///
    /// Searching for ".." in the text misses an absolute path that overrides the
    /// join entirely, and misses a backslash on a platform that treats it as a
    /// separator.
    pub fn file_path(&self, model_id: &str, file_name: &str) -> Option<String> {
        let directory = self.model_directory(model_id)?;
        if file_name.starts_with('/') || file_name.starts_with('\\') {
            return None;
        }
        if file_name.len() >= 2 && file_name.as_bytes()[1] == b':' {
            return None;
        }
        let mut segments: Vec<&str> = Vec::new();
        for part in file_name.split(['/', '\\']) {
            match part {
                "" | "." => continue,
                ".." => {
                    segments.pop()?;
                }
                other => segments.push(other),
            }
        }
        (!segments.is_empty()).then(|| format!("{directory}/{}", segments.join("/")))
    }

    /// Turns `org/model` into a single safe segment.
    ///
    /// The separator becomes `--`, which is reversible by eye and cannot be a
    /// directory boundary. Lower-cased so a case-insensitive filesystem cannot
    /// hold two directories a case-sensitive one would keep apart - which is how
    /// the same model gets downloaded twice on one platform and once on another.
    pub fn normalise_id(repository: &str) -> String {
        repository
            .trim()
            .chars()
            .map(|c| {
                if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') {
                    c
                } else if c == '/' {
                    '\u{1}'
                } else {
                    '-'
                }
            })
            .collect::<String>()
            .replace('\u{1}', "--")
            .trim_matches(['-', '.'])
            .to_lowercase()
            .chars()
            .take(128)
            .collect()
    }
}

/// Voice configuration that ships INSIDE the app.
///
/// THE PAD RULE is here: a blank in a model's symbol table means index 0 for the
/// MMS families and index 3 for Piper, and getting it wrong produces audio that
/// is silent, clipped, or a fraction of a second long - never an error.
pub struct EmbeddedVoiceConfigs;

impl EmbeddedVoiceConfigs {
    /// `(sample rate, pad index, declares its own rate)`.
    fn entry(family: &str) -> (u32, usize, bool) {
        match family.to_lowercase().as_str() {
            "mms" => (16_000, 0, true),
            "piper" => (22_050, 3, true),
            // Open JTalk voices do NOT declare their rate. Assuming the family
            // default plays Japanese at the wrong speed, which sounds like a
            // broken voice rather than a configuration error.
            "jsut-openjtalk" => (22_050, 0, false),
            "pocket" => (24_000, 0, true),
            _ => (22_050, 0, true),
        }
    }

    pub fn sample_rate_for(family: &str) -> u32 {
        Self::entry(family).0
    }

    /// 0 for MMS, 3 for Piper. A wrong pad is never an error, only bad audio -
    /// which is why it is a table and not a guess.
    pub fn pad_index_for(family: &str) -> usize {
        Self::entry(family).1
    }

    pub fn declares_rate(family: &str) -> bool {
        Self::entry(family).2
    }

    pub fn known_families() -> &'static [&'static str] {
        &["jsut-openjtalk", "mms", "piper", "pocket"]
    }
}

/// How much a claim about this code has actually been earned.
///
/// ORDERED, and the order is the entire point. Everything below `RanOnDevice` is
/// a claim about a compiler, not about the thing working.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum VerificationLevel {
    /// Written. Nobody has run it.
    #[default]
    Unverified,
    /// It compiles. Says nothing about behaviour, and is the level most often
    /// mistaken for the next one.
    Compiles,
    /// Unit tests pass on a development machine.
    TestedLocally,
    /// It ran on the target hardware and did the thing. THE ONLY LEVEL THAT
    /// COUNTS as done, because a desktop is a compile gate and a phone is the
    /// benchmark.
    RanOnDevice,
    /// Ran on device and the numbers were recorded.
    MeasuredOnDevice,
}

/// Records what has actually been verified about a piece of code.
///
/// A recorded value rather than a comment so it can be COLLECTED - a build can
/// list everything claiming `RanOnDevice` and check that against what actually
/// ran. A comment saying the same thing cannot be counted, and so drifts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CircleAiVerificationStatus {
    pub level: VerificationLevel,
    /// Required above `TestedLocally`: "ran on device" without naming the device
    /// is the claim this type exists to stop.
    pub device: String,
    pub verified_on: String,
    pub note: String,
}

impl CircleAiVerificationStatus {
    pub fn new(
        level: VerificationLevel,
        device: &str,
        verified_on: &str,
        note: &str,
    ) -> Option<Self> {
        if level >= VerificationLevel::RanOnDevice && device.trim().is_empty() {
            return None;
        }
        Some(Self {
            level,
            device: device.to_string(),
            verified_on: verified_on.to_string(),
            note: note.to_string(),
        })
    }

    pub fn is_done(&self) -> bool {
        self.level >= VerificationLevel::RanOnDevice
    }

    pub fn describe(&self) -> String {
        match self.level {
            VerificationLevel::Unverified => "not verified".into(),
            VerificationLevel::Compiles => {
                "compiles - which says nothing about whether it works".into()
            }
            VerificationLevel::TestedLocally => {
                "unit tested on a development machine, not on the target".into()
            }
            level => {
                let where_ = if self.device.is_empty() {
                    String::new()
                } else {
                    format!(" on {}", self.device)
                };
                let when = if self.verified_on.is_empty() {
                    String::new()
                } else {
                    format!(" ({})", self.verified_on)
                };
                if level == VerificationLevel::MeasuredOnDevice {
                    format!("ran and was measured{where_}{when}")
                } else {
                    format!("ran{where_}{when}")
                }
            }
        }
    }
}

/// The names a diagnostic counter is allowed to take.
///
/// A FIXED SET, because free-form outcome strings produce three spellings of the
/// same thing and a dashboard that undercounts all three.
pub struct Outcomes;

impl Outcomes {
    pub const SUCCESS: &'static str = "success";
    /// Refused on purpose. NOT a failure, and counting it as one makes a working
    /// safety gate look like an outage.
    pub const REFUSED: &'static str = "refused";
    pub const FAILED: &'static str = "failed";
    pub const TIMED_OUT: &'static str = "timed-out";
    pub const CANCELLED: &'static str = "cancelled";
    /// The device could not, and said so. Also not a failure.
    pub const UNAVAILABLE: &'static str = "unavailable";

    pub const ALL: &'static [&'static str] = &[
        Self::SUCCESS, Self::REFUSED, Self::FAILED,
        Self::TIMED_OUT, Self::CANCELLED, Self::UNAVAILABLE,
    ];

    /// Only two of the six mean something went wrong.
    pub fn is_bad(outcome: &str) -> bool {
        outcome == Self::FAILED || outcome == Self::TIMED_OUT
    }
}

/// Counters and timings, in memory, on the device.
///
/// NOTHING LEAVES. There is no exporter, no endpoint and no identifier here, and
/// that is deliberate: telemetry that reaches a server is a record of what
/// somebody asked their phone, however aggregated it claims to be.
#[derive(Debug, Default)]
pub struct CircleAiDiagnostics {
    counters: HashMap<String, u64>,
    durations: HashMap<String, Vec<f64>>,
}

impl CircleAiDiagnostics {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn count(&mut self, operation: &str, outcome: &str) -> Result<(), String> {
        if !Outcomes::ALL.contains(&outcome) {
            return Err(format!(
                "'{outcome}' is not a known outcome; use one of {}",
                Outcomes::ALL.join(", ")
            ));
        }
        *self
            .counters
            .entry(format!("{operation}.{outcome}"))
            .or_insert(0) += 1;
        Ok(())
    }

    pub fn observe(&mut self, operation: &str, milliseconds: f64) {
        self.durations
            .entry(operation.to_string())
            .or_default()
            .push(milliseconds);
    }

    pub fn counter(&self, operation: &str, outcome: &str) -> u64 {
        *self
            .counters
            .get(&format!("{operation}.{outcome}"))
            .unwrap_or(&0)
    }

    /// A real percentile from the samples held, or 0 when there are none.
    ///
    /// NEAREST-RANK, not interpolated: with the handful of samples a phone
    /// accumulates, an interpolated p95 reports a duration that never happened.
    pub fn percentile(&self, operation: &str, fraction: f64) -> f64 {
        let Some(samples) = self.durations.get(operation) else { return 0.0 };
        if samples.is_empty() {
            return 0.0;
        }
        let mut sorted = samples.clone();
        sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        let index = ((fraction * sorted.len() as f64 + 0.5).round() as usize)
            .saturating_sub(1)
            .min(sorted.len() - 1);
        sorted[index]
    }

    pub fn reset(&mut self) {
        self.counters.clear();
        self.durations.clear();
    }
}

/// What a UI component gets for free.
///
/// Not a framework base - there is no framework here. It carries the device
/// context and the diagnostics handle so a component never reaches for a global
/// to find either, which is what makes the same component testable and the same
/// code usable from a head that is not a UI at all.
#[derive(Debug, Default)]
pub struct CircleAiComponentBase {
    pub device: SystemInfoDeviceContext,
    pub diagnostics: CircleAiDiagnostics,
    disposed: bool,
}

impl CircleAiComponentBase {
    pub fn new(device: SystemInfoDeviceContext) -> Self {
        Self { device, diagnostics: CircleAiDiagnostics::new(), disposed: false }
    }

    pub fn is_disposed(&self) -> bool {
        self.disposed
    }

    /// IDEMPOTENT. A component is disposed by a navigation and by a parent
    /// teardown, and often by both within a frame of each other.
    pub fn dispose(&mut self) {
        self.disposed = true;
    }
}
