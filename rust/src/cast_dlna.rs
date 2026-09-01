//! Casting to a television, and the duplex realtime seam.
//!
//! THE THING EVERYONE GETS WRONG ABOUT DLNA: the renderer PULLS. You do not push
//! bytes to a television - you hand it a URL and it comes back and fetches from
//! you. So this device has to be an HTTP server, reachable from the television,
//! for as long as the thing is playing. A design that assumes a push works
//! perfectly against a mock and not once against a real television.
//!
//! Three more, each of which silently breaks against half the devices on a
//! network:
//!
//!   * SOAPACTION must be QUOTED. Unquoted is rejected by some renderers and
//!     accepted by others, so it works on the television you tested on.
//!
//!   * SSDP headers are matched CASE-INSENSITIVELY. Real hardware sends
//!     `LOCATION`, `Location` and `location`.
//!
//!   * A control URL in a device description is RELATIVE to the DOCUMENT's own
//!     base - not the search target, not the host root. Resolving it wrong sends
//!     every command to a 404.
//!
//! REALTIME IS DUPLEX. Audio goes up while audio comes down, and the caller can
//! be interrupted mid-sentence. A request/response shape cannot express that,
//! which is why it is a separate seam.

use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// What is being cast

/// What kind of thing is playing.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CastContentKind {
    #[default]
    Video,
    Audio,
    Image,
    /// A document rendered to images first. Kept separate from Image because a
    /// document has PAGES, and a renderer that treats it as one image shows only
    /// the first.
    Document,
}

/// How to reach a renderer.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CastProtocol {
    /// UPnP/DLNA. What almost every television on a local network speaks.
    #[default]
    Dlna,
    /// Chromecast. Needs Google services, so it is not the default anywhere here.
    GoogleCast,
    AirPlay,
}

/// Where playback has got to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CastPlaybackState {
    /// Nothing has been sent. The starting state, and distinct from Stopped.
    #[default]
    Idle,
    /// The URL is sent and the renderer has not started fetching yet.
    Buffering,
    Playing,
    Paused,
    Stopped,
    /// The renderer reported a problem. Its OWN wording is carried, because a
    /// television's error is usually more specific than anything this could
    /// infer.
    Error,
}

/// Something went wrong casting.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CastError {
    /// Something general.
    Cast {
        message: String,
        /// The renderer's own words, when it gave any. Never invented.
        renderer_message: String,
    },
    /// A control command was refused or failed.
    ///
    /// A SEPARATE variant because the two need opposite handling: a failed
    /// discovery is worth retrying, and a refused Play usually means the
    /// renderer cannot handle the format, which retrying will not fix.
    Control {
        message: String,
        action: String,
        /// The SOAP fault code, when the renderer sent one.
        fault_code: String,
        renderer_message: String,
    },
}

impl std::fmt::Display for CastError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Cast { message, .. } => write!(f, "{message}"),
            Self::Control { message, action, .. } => write!(f, "{message} ({action})"),
        }
    }
}

impl CastError {
    /// Whether trying again could help. A control fault usually cannot.
    pub fn is_retryable(&self) -> bool {
        matches!(self, Self::Cast { .. })
    }
}

/// A renderer's identity on this network.
///
/// The UDN, not the IP address. A television's address changes when the lease
/// renews and its UDN does not, so a target remembered by address stops working
/// overnight while a target remembered by UDN is found again.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CastTargetId {
    pub udn: String,
    pub friendly_name: String,
}

/// Where the bytes are, from the RENDERER's point of view.
///
/// The URL here must be reachable FROM THE TELEVISION, which is why a `file://`
/// path or `127.0.0.1` is useless however correct it looks on this device.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CastMediaSource {
    pub url: String,
    pub mime_type: String,
    pub size_bytes: u64,
}

/// One thing to cast.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CastMedia {
    pub title: String,
    pub kind: CastContentKind,
    pub source: CastMediaSource,
    pub duration_seconds: u32,
    /// A still to show while it buffers. Optional, and absent is fine.
    pub poster_url: Option<String>,
}

/// What a renderer says it is doing.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CastStatus {
    pub state: CastPlaybackState,
    pub position_seconds: u32,
    pub duration_seconds: u32,
    pub volume: f32,
    pub is_muted: bool,
    /// Populated only in the Error state, and only with the renderer's words.
    pub message: String,
}

/// A local file being served to a renderer.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CastFile {
    pub path: String,
    pub mime_type: String,
    pub size_bytes: u64,
}

// ─────────────────────────────────────────────────────────────────────────────
// SSDP

/// One reply to a search.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SsdpResponse {
    /// Where the device description lives. The only field that really matters.
    pub location: String,
    pub usn: String,
    pub search_target: String,
    pub server: String,
    pub headers: HashMap<String, String>,
}

/// Finds renderers by multicast search.
///
/// The socket is the host's. What is here is the parsing, which is where the
/// interoperability problems live.
pub struct SsdpClient;

impl SsdpClient {
    /// The multicast group and port SSDP uses. Not configurable; it is the spec.
    pub const ADDRESS: &'static str = "239.255.255.250";
    pub const PORT: u16 = 1900;
    pub const MEDIA_RENDERER: &'static str = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// MX is the maximum seconds a device may wait before replying, and it
    /// exists so that a hundred devices do not answer at once. Setting it to 1
    /// to be quick makes a busy network drop replies; 2 to 3 is what devices
    /// expect.
    ///
    /// The trailing blank line is REQUIRED - an HTTPU message without the
    /// terminating CRLF CRLF is ignored by most stacks, silently.
    pub fn build_search(target: &str, mx: u8) -> String {
        format!(
            "M-SEARCH * HTTP/1.1\r\nHOST: {}:{}\r\nMAN: \"ssdp:discover\"\r\nMX: {}\r\nST: {}\r\n\r\n",
            Self::ADDRESS,
            Self::PORT,
            mx,
            target
        )
    }

    /// Parses a reply.
    ///
    /// HEADER NAMES ARE LOWER-CASED before lookup, because the spec says they
    /// are case-insensitive and real devices genuinely send `LOCATION`,
    /// `Location` and `location`. A parser that matches one spelling finds two
    /// thirds of the televisions on a network and misses the rest.
    pub fn parse_response(raw: &str) -> Option<SsdpResponse> {
        let mut lines = raw.lines();
        let status = lines.next()?;
        if !status.to_uppercase().starts_with("HTTP/1.1 200") {
            return None;
        }
        let mut headers = HashMap::new();
        for line in lines {
            // Only the FIRST colon separates - the value always contains more.
            if let Some((key, value)) = line.split_once(':') {
                headers.insert(key.trim().to_lowercase(), value.trim().to_string());
            }
        }
        let location = headers.get("location")?.clone();
        if location.is_empty() {
            return None;
        }
        Some(SsdpResponse {
            usn: headers.get("usn").cloned().unwrap_or_default(),
            search_target: headers.get("st").cloned().unwrap_or_default(),
            server: headers.get("server").cloned().unwrap_or_default(),
            location,
            headers,
        })
    }

    /// Deduplicated on USN. A device answers a search several times on purpose,
    /// and a list that shows the same television four times looks broken.
    pub fn dedupe(messages: &[String]) -> Vec<SsdpResponse> {
        let mut seen = std::collections::HashSet::new();
        messages
            .iter()
            .filter_map(|m| Self::parse_response(m))
            .filter(|r| seen.insert(r.usn.clone()))
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The device description

/// One service a device offers.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RendererDescription {
    pub service_type: String,
    /// Absolute, resolved against the description document's own base.
    pub control_url: String,
    pub event_sub_url: String,
}

/// What a device says about itself.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DeviceDescription {
    pub udn: String,
    pub friendly_name: String,
    pub manufacturer: String,
    pub model_name: String,
    pub services: Vec<RendererDescription>,
}

/// Resolves a possibly-relative URL against a base.
///
/// THIS IS THE ONE THAT BREAKS EVERYTHING. A control URL in a description is
/// usually relative - `/AVTransport/ctrl` or even `ctrl` - and it resolves
/// against the URL the DESCRIPTION was fetched from, not against the root of the
/// host and not against the search target. Getting it wrong sends every
/// subsequent command to a 404, and the symptom is a television that is
/// discovered perfectly and then ignores everything.
pub fn resolve_against(base: &str, reference: &str) -> String {
    if reference.is_empty() {
        return base.to_string();
    }
    if reference.starts_with("http://") || reference.starts_with("https://") {
        return reference.to_string();
    }
    let scheme_end = match base.find("://") {
        Some(i) => i + 3,
        None => return reference.to_string(),
    };
    let authority_end = base[scheme_end..]
        .find('/')
        .map(|i| scheme_end + i)
        .unwrap_or(base.len());
    let origin = &base[..authority_end];
    if reference.starts_with('/') {
        return format!("{origin}{reference}");
    }
    let path = &base[authority_end..];
    let dir = match path.rfind('/') {
        Some(i) => &path[..=i],
        None => "/",
    };
    format!("{origin}{dir}{reference}")
}

/// Reads a UPnP device description.
///
/// Parsed by scanning for the three elements it needs rather than with a general
/// XML parser: the document is small, flat and machine-generated, and adding an
/// XML dependency to reach a television is not a trade worth making. What it
/// does NOT do is guess - anything it cannot find comes back empty.
pub struct DeviceDescriptionParser;

impl DeviceDescriptionParser {
    fn element(xml: &str, tag: &str) -> String {
        let open = format!("<{tag}>");
        let close = format!("</{tag}>");
        match (xml.find(&open), xml.find(&close)) {
            (Some(start), Some(end)) if start + open.len() <= end => {
                xml[start + open.len()..end].trim().to_string()
            }
            _ => String::new(),
        }
    }

    pub fn parse(xml: &str, base_url: &str) -> DeviceDescription {
        let mut services = Vec::new();
        let mut rest = xml;
        while let Some(start) = rest.find("<service>") {
            let Some(end) = rest[start..].find("</service>") else { break };
            let block = &rest[start..start + end];
            let service_type = Self::element(block, "serviceType");
            if !service_type.is_empty() {
                services.push(RendererDescription {
                    service_type,
                    control_url: resolve_against(base_url, &Self::element(block, "controlURL")),
                    event_sub_url: resolve_against(base_url, &Self::element(block, "eventSubURL")),
                });
            }
            rest = &rest[start + end..];
        }
        DeviceDescription {
            udn: Self::element(xml, "UDN"),
            friendly_name: Self::element(xml, "friendlyName"),
            manufacturer: Self::element(xml, "manufacturer"),
            model_name: Self::element(xml, "modelName"),
            services,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Control

/// The metadata a renderer wants alongside a URL.
///
/// MANY RENDERERS REFUSE TO PLAY WITHOUT IT, and the refusal is silent - the
/// television accepts SetAVTransportURI, reports success, and then does nothing.
/// So this is built and sent every time rather than only when convenient.
pub struct DidlLite;

impl DidlLite {
    /// XML-escaped. The whole document is escaped AGAIN when it goes into the
    /// SOAP body, because it travels as a string inside XML. Escaping once is
    /// the commonest DIDL bug and it breaks on the first title containing an
    /// ampersand - which is to say, on somebody's actual media.
    pub fn escape(text: &str) -> String {
        text.replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
            .replace('"', "&quot;")
            .replace('\'', "&apos;")
    }

    pub fn build(media: &CastMedia) -> String {
        let upnp_class = match media.kind {
            CastContentKind::Audio => "object.item.audioItem.musicTrack",
            CastContentKind::Image => "object.item.imageItem.photo",
            _ => "object.item.videoItem",
        };
        let size = if media.source.size_bytes > 0 {
            format!(" size=\"{}\"", media.source.size_bytes)
        } else {
            String::new()
        };
        format!(
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" \
             xmlns:dc=\"http://purl.org/dc/elements/1.1/\" \
             xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">\
             <item id=\"0\" parentID=\"-1\" restricted=\"1\">\
             <dc:title>{}</dc:title><upnp:class>{}</upnp:class>\
             <res protocolInfo=\"http-get:*:{}:*\"{}>{}</res></item></DIDL-Lite>",
            Self::escape(&media.title),
            upnp_class,
            media.source.mime_type,
            size,
            Self::escape(&media.source.url)
        )
    }
}

/// Sends SOAP actions to a renderer.
pub struct UpnpControlPoint {
    post: Option<Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>>,
}

impl UpnpControlPoint {
    pub const AV_TRANSPORT: &'static str = "urn:schemas-upnp-org:service:AVTransport:1";
    pub const RENDERING_CONTROL: &'static str = "urn:schemas-upnp-org:service:RenderingControl:1";

    pub fn new(
        post: Option<
            Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>,
        >,
    ) -> Self {
        Self { post }
    }

    /// The SOAPACTION header MUST be quoted.
    ///
    /// `SOAPACTION: "urn:...#Play"` works everywhere; the same header without
    /// quotes is accepted by some renderers and rejected by others, so a build
    /// that omits them works on the television it was tested against and fails
    /// on somebody else's.
    pub fn headers_for(service_type: &str, action: &str) -> HashMap<String, String> {
        HashMap::from([
            ("Content-Type".to_string(), "text/xml; charset=\"utf-8\"".to_string()),
            ("SOAPACTION".to_string(), format!("\"{service_type}#{action}\"")),
        ])
    }

    pub fn envelope(service_type: &str, action: &str, args: &[(&str, String)]) -> String {
        let body: String = args
            .iter()
            .map(|(k, v)| format!("<{k}>{v}</{k}>"))
            .collect();
        format!(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\
             <s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" \
             s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\
             <s:Body><u:{action} xmlns:u=\"{service_type}\">{body}</u:{action}></s:Body></s:Envelope>"
        )
    }

    pub fn invoke(
        &self,
        control_url: &str,
        service_type: &str,
        action: &str,
        args: &[(&str, String)],
    ) -> Result<String, CastError> {
        let Some(post) = &self.post else {
            return Err(CastError::Control {
                message: "no transport configured".into(),
                action: action.into(),
                fault_code: String::new(),
                renderer_message: String::new(),
            });
        };
        let reply = post(
            control_url,
            &Self::headers_for(service_type, action),
            &Self::envelope(service_type, action, args),
        )
        .map_err(|e| CastError::Control {
            message: format!("{action} could not be sent"),
            action: action.into(),
            fault_code: String::new(),
            renderer_message: e,
        })?;

        // A SOAP fault comes back with HTTP 500 AND a body. A transport that
        // errors on 500 loses the fault code, which is the only useful part - so
        // the fault is looked for in whatever body did arrive.
        if let Some(code) = Self::element(&reply, "errorCode") {
            return Err(CastError::Control {
                message: format!("the renderer refused {action}"),
                action: action.into(),
                fault_code: code,
                renderer_message: Self::element(&reply, "errorDescription").unwrap_or_default(),
            });
        }
        Ok(reply)
    }

    fn element(xml: &str, tag: &str) -> Option<String> {
        let open = format!("<{tag}>");
        let close = format!("</{tag}>");
        let start = xml.find(&open)? + open.len();
        let end = xml[start..].find(&close)? + start;
        Some(xml[start..end].trim().to_string())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Sessions and targets

/// A renderer this device can cast to.
pub trait CastTarget {
    fn id(&self) -> &CastTargetId;
    fn protocol(&self) -> CastProtocol;
    fn can_play(&self) -> bool;
}

/// Finds renderers.
pub trait CastDiscovery {
    fn discover(&self, timeout_ms: u64) -> Vec<Box<dyn CastTarget + Send + Sync>>;
}

/// Finds nothing. The default: a device does not scan a network unprompted.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCastDiscovery;

impl CastDiscovery for NullCastDiscovery {
    fn discover(&self, _timeout_ms: u64) -> Vec<Box<dyn CastTarget + Send + Sync>> {
        Vec::new()
    }
}

/// A live cast.
pub trait CastSession {
    fn play(&mut self, media: &CastMedia) -> Result<(), CastError>;
    fn pause(&mut self) -> Result<(), CastError>;
    fn resume(&mut self) -> Result<(), CastError>;
    fn stop(&mut self) -> Result<(), CastError>;
    fn seek(&mut self, seconds: u32) -> Result<(), CastError>;
    fn status(&mut self) -> CastStatus;
}

/// A DLNA renderer found on the network.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DlnaCastTarget {
    pub id: CastTargetId,
    pub location: String,
    pub description: Option<DeviceDescription>,
}

impl DlnaCastTarget {
    /// The AVTransport control URL, or empty when the device does not offer one.
    ///
    /// A device may answer a MediaRenderer search and not carry AVTransport - a
    /// renderer with only RenderingControl can change its own volume and cannot
    /// be given something to play. Discovering that at Play time is too late.
    pub fn control_url(&self) -> String {
        self.description
            .as_ref()
            .and_then(|d| {
                d.services
                    .iter()
                    .find(|s| s.service_type == UpnpControlPoint::AV_TRANSPORT)
            })
            .map(|s| s.control_url.clone())
            .unwrap_or_default()
    }
}

impl CastTarget for DlnaCastTarget {
    fn id(&self) -> &CastTargetId {
        &self.id
    }
    fn protocol(&self) -> CastProtocol {
        CastProtocol::Dlna
    }
    fn can_play(&self) -> bool {
        !self.control_url().is_empty()
    }
}

/// Discovers DLNA renderers.
pub struct DlnaCastDiscovery {
    search: Option<Box<dyn Fn(&str, u64) -> Vec<String> + Send + Sync>>,
    fetch_text: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
}

impl DlnaCastDiscovery {
    pub fn new(
        search: Option<Box<dyn Fn(&str, u64) -> Vec<String> + Send + Sync>>,
        fetch_text: Option<Box<dyn Fn(&str) -> Option<String> + Send + Sync>>,
    ) -> Self {
        Self { search, fetch_text }
    }

    pub fn discover_dlna(&self, timeout_ms: u64) -> Vec<DlnaCastTarget> {
        let Some(search) = &self.search else { return Vec::new() };
        let messages = search(
            &SsdpClient::build_search(SsdpClient::MEDIA_RENDERER, 2),
            timeout_ms,
        );
        SsdpClient::dedupe(&messages)
            .into_iter()
            .map(|reply| {
                let description = self
                    .fetch_text
                    .as_ref()
                    .and_then(|fetch| fetch(&reply.location))
                    .map(|xml| DeviceDescriptionParser::parse(&xml, &reply.location));
                DlnaCastTarget {
                    id: CastTargetId {
                        udn: description
                            .as_ref()
                            .map(|d| d.udn.clone())
                            .filter(|u| !u.is_empty())
                            .unwrap_or_else(|| reply.usn.clone()),
                        friendly_name: description
                            .as_ref()
                            .map(|d| d.friendly_name.clone())
                            .unwrap_or_default(),
                    },
                    location: reply.location,
                    // A device that answered and then would not describe itself
                    // is still LISTED, without a description - it may come back.
                    // Dropping it makes a television flicker in and out.
                    description,
                }
            })
            .collect()
    }
}

/// A live DLNA cast.
pub struct DlnaCastSession {
    target: DlnaCastTarget,
    control: UpnpControlPoint,
    last_known: CastStatus,
}

impl DlnaCastSession {
    /// `InstanceID` is 0 for every renderer in practice. It is in the protocol
    /// for devices with several transports and no consumer television has one.
    const INSTANCE: &'static str = "0";

    pub fn new(target: DlnaCastTarget, control: UpnpControlPoint) -> Self {
        Self { target, control, last_known: CastStatus::default() }
    }

    /// Seeking takes `REL_TIME` in `H:MM:SS`, not seconds.
    ///
    /// Hours are NOT zero-padded and minutes and seconds are. Padding the hour
    /// is rejected by some renderers, which is the sort of thing only a real
    /// television tells you.
    pub fn rel_time(seconds: u32) -> String {
        format!("{}:{:02}:{:02}", seconds / 3600, (seconds % 3600) / 60, seconds % 60)
    }
}

impl CastSession for DlnaCastSession {
    fn play(&mut self, media: &CastMedia) -> Result<(), CastError> {
        let url = self.target.control_url();
        if url.is_empty() {
            return Err(CastError::Control {
                message: format!(
                    "{} cannot be given something to play",
                    if self.target.id.friendly_name.is_empty() {
                        "that device"
                    } else {
                        &self.target.id.friendly_name
                    }
                ),
                action: "SetAVTransportURI".into(),
                fault_code: String::new(),
                renderer_message: String::new(),
            });
        }
        // SetAVTransportURI FIRST, then Play - two separate actions. A renderer
        // given Play without a URI plays whatever was there before, which on a
        // television somebody else was using is somebody else's video.
        self.control.invoke(
            &url,
            UpnpControlPoint::AV_TRANSPORT,
            "SetAVTransportURI",
            &[
                ("InstanceID", Self::INSTANCE.into()),
                ("CurrentURI", DidlLite::escape(&media.source.url)),
                // Escaped a SECOND time: the DIDL document travels as a string
                // inside this XML, so its own markup has to survive being XML.
                ("CurrentURIMetaData", DidlLite::escape(&DidlLite::build(media))),
            ],
        )?;
        self.control.invoke(
            &url,
            UpnpControlPoint::AV_TRANSPORT,
            "Play",
            &[("InstanceID", Self::INSTANCE.into()), ("Speed", "1".into())],
        )?;
        self.last_known = CastStatus {
            state: CastPlaybackState::Buffering,
            duration_seconds: media.duration_seconds,
            ..Default::default()
        };
        Ok(())
    }

    fn pause(&mut self) -> Result<(), CastError> {
        self.control.invoke(
            &self.target.control_url(),
            UpnpControlPoint::AV_TRANSPORT,
            "Pause",
            &[("InstanceID", Self::INSTANCE.into())],
        )?;
        self.last_known.state = CastPlaybackState::Paused;
        Ok(())
    }

    fn resume(&mut self) -> Result<(), CastError> {
        self.control.invoke(
            &self.target.control_url(),
            UpnpControlPoint::AV_TRANSPORT,
            "Play",
            &[("InstanceID", Self::INSTANCE.into()), ("Speed", "1".into())],
        )?;
        self.last_known.state = CastPlaybackState::Playing;
        Ok(())
    }

    fn stop(&mut self) -> Result<(), CastError> {
        self.control.invoke(
            &self.target.control_url(),
            UpnpControlPoint::AV_TRANSPORT,
            "Stop",
            &[("InstanceID", Self::INSTANCE.into())],
        )?;
        self.last_known = CastStatus { state: CastPlaybackState::Stopped, ..Default::default() };
        Ok(())
    }

    fn seek(&mut self, seconds: u32) -> Result<(), CastError> {
        self.control.invoke(
            &self.target.control_url(),
            UpnpControlPoint::AV_TRANSPORT,
            "Seek",
            &[
                ("InstanceID", Self::INSTANCE.into()),
                ("Unit", "REL_TIME".into()),
                ("Target", Self::rel_time(seconds)),
            ],
        )?;
        Ok(())
    }

    fn status(&mut self) -> CastStatus {
        match self.control.invoke(
            &self.target.control_url(),
            UpnpControlPoint::AV_TRANSPORT,
            "GetTransportInfo",
            &[("InstanceID", Self::INSTANCE.into())],
        ) {
            Ok(reply) => {
                if let Some(state) = UpnpControlPoint::element(&reply, "CurrentTransportState") {
                    self.last_known.state = match state.as_str() {
                        "PLAYING" => CastPlaybackState::Playing,
                        "PAUSED_PLAYBACK" => CastPlaybackState::Paused,
                        "STOPPED" => CastPlaybackState::Stopped,
                        _ => self.last_known.state,
                    };
                }
            }
            // A failed status poll returns the LAST KNOWN state rather than
            // Error. Televisions drop a poll routinely and a UI that flips to an
            // error on one missed reply looks broken while playing perfectly.
            Err(CastError::Control { fault_code, renderer_message, .. })
                if !fault_code.is_empty() =>
            {
                self.last_known = CastStatus {
                    state: CastPlaybackState::Error,
                    message: renderer_message,
                    ..Default::default()
                };
            }
            Err(_) => {}
        }
        self.last_known.clone()
    }
}

/// Casts to a renderer.
pub trait CastEngine {
    fn discover(&self, timeout_ms: u64) -> Vec<DlnaCastTarget>;
    fn connect(&self, target: DlnaCastTarget) -> DlnaCastSession;
}

/// The DLNA engine.
pub struct DlnaCastEngine {
    discovery: DlnaCastDiscovery,
    post: Option<Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>>,
}

impl DlnaCastEngine {
    pub fn new(
        discovery: DlnaCastDiscovery,
        post: Option<
            Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>,
        >,
    ) -> Self {
        Self { discovery, post }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Serving the bytes

/// This device's address ON THE NETWORK THE RENDERER IS ON.
///
/// Not `127.0.0.1`, which is the answer a naive lookup gives and which a
/// television cannot reach. Not the first interface either - a phone with a
/// mobile connection and Wi-Fi has two, and only one of them is where the
/// television is.
pub struct LocalAddress;

impl LocalAddress {
    /// Prefers a PRIVATE IPv4 address, because that is what a renderer on the
    /// same Wi-Fi can route to. A public address may be correct and is usually
    /// behind a NAT the television cannot traverse.
    pub fn is_private_v4(address: &str) -> bool {
        let parts: Vec<u8> = match address
            .split('.')
            .map(str::parse::<u8>)
            .collect::<Result<Vec<_>, _>>()
        {
            Ok(p) if p.len() == 4 => p,
            _ => return false,
        };
        matches!(parts[0], 10)
            || (parts[0] == 172 && (16..=31).contains(&parts[1]))
            || (parts[0] == 192 && parts[1] == 168)
    }

    pub fn is_loopback(address: &str) -> bool {
        address == "::1" || address.starts_with("127.")
    }

    /// Empty when nothing usable was offered, which a caller must handle.
    pub fn pick(candidates: &[String]) -> String {
        let usable: Vec<&String> = candidates
            .iter()
            .filter(|a| !a.is_empty() && !Self::is_loopback(a))
            .collect();
        usable
            .iter()
            .find(|a| Self::is_private_v4(a))
            .or_else(|| usable.first())
            .map(|a| (*a).clone())
            .unwrap_or_default()
    }
}

/// Serves a local file to a renderer over HTTP.
///
/// RANGE REQUESTS ARE NOT OPTIONAL. A television seeking in a video sends
/// `Range: bytes=...` and expects 206 with a `Content-Range`; a host that always
/// answers 200 with the whole file makes seeking either fail or restart from the
/// beginning, and on a large file it re-downloads the lot.
pub struct TcpMediaHost {
    address: String,
    port: u16,
    running: bool,
    served: HashMap<String, CastFile>,
    counter: u64,
}

impl TcpMediaHost {
    pub fn new(address: String, port: u16) -> Self {
        Self { address, port, running: false, served: HashMap::new(), counter: 0 }
    }

    pub fn is_running(&self) -> bool {
        self.running
    }

    /// Refused on loopback rather than started. A host on loopback serves a URL
    /// no television can fetch, and the failure appears much later as a
    /// television that buffers forever.
    pub fn start(&mut self) -> bool {
        if self.address.is_empty() || LocalAddress::is_loopback(&self.address) {
            return false;
        }
        self.running = true;
        true
    }

    pub fn stop(&mut self) {
        self.running = false;
        self.served.clear();
    }

    /// A file is served under an OPAQUE id, not its path.
    ///
    /// Putting the path in the URL would let anything on the network read any
    /// file this process can, by asking for it - and a television is not the
    /// only thing on a café's Wi-Fi.
    pub fn url_for(&mut self, file: &CastFile) -> String {
        if !self.running {
            return String::new();
        }
        let existing = self
            .served
            .iter()
            .find(|(_, f)| f.path == file.path)
            .map(|(id, _)| id.clone());
        let id = existing.unwrap_or_else(|| {
            self.counter += 1;
            format!("m{:x}", self.counter)
        });
        self.served.insert(id.clone(), file.clone());
        format!("http://{}:{}/{}", self.address, self.port, id)
    }

    pub fn file_for(&self, id: &str) -> Option<&CastFile> {
        self.served.get(id)
    }

    /// Parses a Range header into a byte span.
    ///
    /// `bytes=500-` means from 500 to the end, and `bytes=-500` means the LAST
    /// 500 bytes - not the first 500. Reading the second as the first serves the
    /// wrong part of the file and a television shows the middle of a video when
    /// asked for its end.
    pub fn parse_range(header: &str, size_bytes: u64) -> Option<(u64, u64)> {
        let spec = header.trim().strip_prefix("bytes=")?;
        let (start_text, end_text) = spec.split_once('-')?;
        if size_bytes == 0 || (start_text.is_empty() && end_text.is_empty()) {
            return None;
        }
        if start_text.is_empty() {
            let length = end_text.parse::<u64>().ok()?.min(size_bytes);
            return Some((size_bytes - length, size_bytes - 1));
        }
        let start = start_text.parse::<u64>().ok()?;
        if start >= size_bytes {
            return None;
        }
        // The end is INCLUSIVE in HTTP, which is one off from every slice in
        // this file - and getting it wrong drops the last byte of every
        // response.
        let end = if end_text.is_empty() {
            size_bytes - 1
        } else {
            end_text.parse::<u64>().ok()?.min(size_bytes - 1)
        };
        (end >= start).then_some((start, end))
    }
}

/// A document being cast, page by page.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CastDocument {
    pub title: String,
    pub page_count: usize,
    /// Rendered images, one per page, in order.
    pub page_urls: Vec<String>,
}

/// Turns a document into something castable.
pub trait DocumentCastAdapter {
    fn is_available(&self) -> bool;
    fn prepare(&self, path: &str) -> Option<CastDocument>;
}

/// Prepares nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullDocumentCastAdapter;

impl DocumentCastAdapter for NullDocumentCastAdapter {
    fn is_available(&self) -> bool {
        false
    }
    fn prepare(&self, _path: &str) -> Option<CastDocument> {
        None
    }
}

/// Serves a local file to a renderer.
pub trait LocalMediaHost {
    fn is_running(&self) -> bool;
    fn url_for(&mut self, file: &CastFile) -> String;
}

impl LocalMediaHost for TcpMediaHost {
    fn is_running(&self) -> bool {
        self.running
    }
    fn url_for(&mut self, file: &CastFile) -> String {
        TcpMediaHost::url_for(self, file)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Realtime

/// Which way audio is flowing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RealtimeDirection {
    Inbound,
    Outbound,
}

/// The wire format of a realtime stream.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RealtimeAudioFormat {
    pub sample_rate_hz: u32,
    pub channels: u8,
    pub bits_per_sample: u8,
}

impl Default for RealtimeAudioFormat {
    fn default() -> Self {
        Self { sample_rate_hz: 24_000, channels: 1, bits_per_sample: 16 }
    }
}

/// One frame of audio.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RealtimeAudioFrame {
    pub pcm: Vec<u8>,
    pub direction: RealtimeDirection,
    pub format: RealtimeAudioFormat,
    pub at_ms: u64,
}

/// One tool a realtime session may call.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RealtimeTool {
    pub name: String,
    pub description: String,
    pub input_schema_json: String,
}

/// How a realtime session is set up.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RealtimeSessionConfig {
    pub system_prompt: String,
    pub voice: String,
    pub language: String,
    pub tools: Vec<RealtimeTool>,
    pub audio_format: RealtimeAudioFormat,
}

/// Something that happened during a session.
#[derive(Debug, Clone, PartialEq)]
pub enum RealtimeEvent {
    /// The caller began talking.
    SpeechStarted { session_id: String, at_ms: u64 },
    /// The caller stopped. NOT the same as end of turn: stopping making noise
    /// and having finished a sentence are different facts.
    SpeechEnded { session_id: String, at_ms: u64 },
    /// A partial transcript. Deltas REPLACE each other for an utterance; they do
    /// not append. A consumer that appends renders the sentence growing by
    /// duplication.
    TranscriptDelta { session_id: String, delta: String, at_ms: u64 },
    /// The settled transcript for an utterance.
    TranscriptFinal {
        session_id: String,
        text: String,
        /// `None` when the engine did not say. Zero is a real answer meaning
        /// "no idea", and the two must not be confused.
        confidence: Option<f32>,
        at_ms: u64,
    },
    /// A whole turn finished.
    TurnComplete { session_id: String, duration_ms: u64 },
    /// The model asked for a tool.
    ToolCall { session_id: String, tool_name: String, args_json: String },
    /// Something went wrong.
    SessionError {
        session_id: String,
        code: String,
        message: String,
        /// Whether the session survives. A recoverable error and a dead session
        /// demand opposite reactions, and a caller that cannot tell reconnects
        /// on every hiccup or on none.
        fatal: bool,
    },
}

/// A duplex audio session.
pub trait RealtimeSession {
    fn send_audio(&mut self, frame: &RealtimeAudioFrame) -> bool;
    /// Barge-in. NOT optional and not a nicety: without it the service keeps
    /// speaking over somebody who has started talking, which is the single thing
    /// that makes a voice assistant feel broken.
    fn interrupt(&mut self) -> bool;
    fn close(&mut self);
    fn drain_events(&mut self) -> Vec<RealtimeEvent>;
}

/// Opens realtime sessions.
pub trait RealtimeService {
    fn open(&self, config: &RealtimeSessionConfig) -> Option<Box<dyn RealtimeSession + Send>>;
}

/// Accepts audio and produces nothing.
#[derive(Debug, Default)]
pub struct NullRealtimeSession;

impl RealtimeSession for NullRealtimeSession {
    fn send_audio(&mut self, _frame: &RealtimeAudioFrame) -> bool {
        false
    }
    fn interrupt(&mut self) -> bool {
        false
    }
    fn close(&mut self) {}
    fn drain_events(&mut self) -> Vec<RealtimeEvent> {
        Vec::new()
    }
}

/// Opens sessions that do nothing.
///
/// The DEFAULT, so a build with no realtime provider runs the local voice loop -
/// which is the intended behaviour rather than a degradation.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRealtimeService;

impl RealtimeService for NullRealtimeService {
    fn open(&self, _config: &RealtimeSessionConfig) -> Option<Box<dyn RealtimeSession + Send>> {
        Some(Box::new(NullRealtimeSession))
    }
}

/// Echoes audio back and emits the events a real session would.
///
/// What the loop is tested against: it exercises barge-in, transcript deltas and
/// turn completion without a network or a provider.
#[derive(Debug, Default)]
pub struct LoopbackRealtimeSession {
    pub session_id: String,
    frames: Vec<RealtimeAudioFrame>,
    events: Vec<RealtimeEvent>,
    interrupted: bool,
}

impl LoopbackRealtimeSession {
    pub fn new(session_id: String) -> Self {
        Self { session_id, ..Default::default() }
    }

    pub fn frames_received(&self) -> usize {
        self.frames.len()
    }

    pub fn was_interrupted(&self) -> bool {
        self.interrupted
    }
}

impl RealtimeSession for LoopbackRealtimeSession {
    fn send_audio(&mut self, frame: &RealtimeAudioFrame) -> bool {
        let first = self.frames.is_empty();
        self.frames.push(frame.clone());
        if first {
            self.events.push(RealtimeEvent::SpeechStarted {
                session_id: self.session_id.clone(),
                at_ms: frame.at_ms,
            });
        }
        true
    }

    fn interrupt(&mut self) -> bool {
        self.interrupted = true;
        self.events.push(RealtimeEvent::SpeechEnded {
            session_id: self.session_id.clone(),
            at_ms: 0,
        });
        true
    }

    fn close(&mut self) {
        self.events.push(RealtimeEvent::TurnComplete {
            session_id: self.session_id.clone(),
            duration_ms: 0,
        });
    }

    fn drain_events(&mut self) -> Vec<RealtimeEvent> {
        std::mem::take(&mut self.events)
    }
}

/// Opens loopback sessions.
#[derive(Debug, Default)]
pub struct LoopbackRealtimeService {
    counter: std::sync::atomic::AtomicU64,
}

impl RealtimeService for LoopbackRealtimeService {
    fn open(&self, _config: &RealtimeSessionConfig) -> Option<Box<dyn RealtimeSession + Send>> {
        let n = self
            .counter
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed)
            + 1;
        Some(Box::new(LoopbackRealtimeSession::new(format!("loopback-{n}"))))
    }
}

/// A duplex link to a realtime service.
pub trait RealtimeTransport {
    fn connect(&mut self) -> bool;
    fn send_audio(&mut self, pcm: &[u8]) -> bool;
    fn interrupt(&mut self) -> bool;
    fn close(&mut self);
}

/// Builds a transport for a provider.
pub trait RealtimeTransportFactory {
    fn create(&self, provider_id: &str) -> Option<Box<dyn RealtimeTransport + Send>>;
}

/// Creates nothing.
///
/// The default: a build with no realtime provider configured runs the local
/// loop.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRealtimeTransportFactory;

impl RealtimeTransportFactory for NullRealtimeTransportFactory {
    fn create(&self, _provider_id: &str) -> Option<Box<dyn RealtimeTransport + Send>> {
        None
    }
}

/// A realtime session over a websocket the host supplies.
///
/// NO SOCKET IS OPENED HERE. `send` and `close` are closures, so this is testable
/// without a network and cannot open a connection as a side effect of being
/// constructed - which is exactly the accident that would send audio before a
/// person agreed to it.
pub struct RealtimeWebSocketSession {
    pub session_id: String,
    pub provider_id: String,
    send: Option<Box<dyn Fn(&[u8]) -> bool + Send + Sync>>,
    closed: bool,
    sent_frames: usize,
}

impl RealtimeWebSocketSession {
    pub fn new(
        session_id: String,
        provider_id: String,
        send: Option<Box<dyn Fn(&[u8]) -> bool + Send + Sync>>,
    ) -> Self {
        Self { session_id, provider_id, send, closed: false, sent_frames: 0 }
    }

    pub fn is_open(&self) -> bool {
        !self.closed && self.send.is_some()
    }

    pub fn frames_sent(&self) -> usize {
        self.sent_frames
    }
}

impl RealtimeSession for RealtimeWebSocketSession {
    /// Refuses after close rather than failing loudly.
    ///
    /// Audio arrives from a capture thread that has not yet noticed the session
    /// ended; panicking there kills the capture and the microphone stays hot.
    fn send_audio(&mut self, frame: &RealtimeAudioFrame) -> bool {
        if !self.is_open() {
            return false;
        }
        let ok = self.send.as_ref().map(|f| f(&frame.pcm)).unwrap_or(false);
        if ok {
            self.sent_frames += 1;
        }
        ok
    }

    fn interrupt(&mut self) -> bool {
        if !self.is_open() {
            return false;
        }
        self.send
            .as_ref()
            .map(|f| f(b"{\"type\":\"response.cancel\"}"))
            .unwrap_or(false)
    }

    /// IDEMPOTENT. A session is closed by whichever of the peer, the user and
    /// the error path gets there first, and often by two of them.
    fn close(&mut self) {
        self.closed = true;
    }

    fn drain_events(&mut self) -> Vec<RealtimeEvent> {
        Vec::new()
    }
}

/// What every realtime cloud provider needs.
///
/// NO `derive(Debug)`. The hand-written one below REDACTS the key, because an
/// options struct reaches a log through `{:?}` far more often than through a
/// deliberate print - and a derived Debug would put the key in every one.
#[derive(Clone, Default)]
pub struct RealtimeCloudOptions {
    /// OFF. A build that carries a provider does not use it.
    pub enabled: bool,
    pub model: String,
    pub url: String,
    pub voice: String,
    pub sample_rate_hz: u32,
    /// Held opaque and never printed. A key reaches a log the ordinary way, and
    /// that is not a decision anybody made.
    key: String,
}

impl RealtimeCloudOptions {
    pub fn with_key(mut self, key: &str) -> Self {
        self.key = key.to_string();
        self
    }

    /// The ONE way out, named so it is visible at every call site.
    pub fn reveal_key(&self) -> &str {
        &self.key
    }

    pub fn is_configured(&self) -> bool {
        self.enabled && !self.key.is_empty() && !self.url.is_empty()
    }
}

/// Prints everything EXCEPT the key.
///
/// The provider, the model and the URL are useful in a log and the key never is.
impl std::fmt::Debug for RealtimeCloudOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RealtimeCloudOptions")
            .field("enabled", &self.enabled)
            .field("model", &self.model)
            .field("url", &self.url)
            .field("voice", &self.voice)
            .field("sample_rate_hz", &self.sample_rate_hz)
            .field("key", &if self.key.is_empty() { "<unset>" } else { "<set>" })
            .finish()
    }
}

macro_rules! realtime_provider {
    ($name:ident, $provider:literal, $url:literal, $model:literal) => {
        /// A realtime provider.
        ///
        /// Reports unavailable rather than failing when unconfigured, so the
        /// caller falls back to the on-device voice loop instead of failing the
        /// call.
        pub struct $name {
            pub options: RealtimeCloudOptions,
            connect: Option<Box<dyn Fn(&str, &str) -> Option<RealtimeWebSocketSession> + Send + Sync>>,
        }

        impl $name {
            pub const PROVIDER_ID: &'static str = $provider;

            pub fn new(
                options: RealtimeCloudOptions,
                connect: Option<
                    Box<dyn Fn(&str, &str) -> Option<RealtimeWebSocketSession> + Send + Sync>,
                >,
            ) -> Self {
                Self { options, connect }
            }

            pub fn defaults() -> RealtimeCloudOptions {
                RealtimeCloudOptions {
                    url: $url.into(),
                    model: $model.into(),
                    sample_rate_hz: 24_000,
                    ..Default::default()
                }
            }

            pub fn is_available(&self) -> bool {
                self.options.is_configured() && self.connect.is_some()
            }

            /// `None` rather than an error when unavailable, so the caller falls
            /// back to the on-device loop instead of failing the call.
            pub fn open(&self) -> Option<RealtimeWebSocketSession> {
                if !self.is_available() {
                    return None;
                }
                (self.connect.as_ref()?)(&self.options.url, self.options.reveal_key())
            }
        }
    };
}

realtime_provider!(
    OpenAiRealtimeService,
    "openai",
    "wss://api.openai.com/v1/realtime",
    "gpt-4o-realtime-preview"
);
realtime_provider!(
    GeminiLiveService,
    "gemini",
    "wss://generativelanguage.googleapis.com/ws",
    "gemini-2.0-flash-live"
);
realtime_provider!(
    NovaSonicService,
    "nova-sonic",
    "wss://bedrock-runtime.us-east-1.amazonaws.com",
    "amazon.nova-sonic-v1"
);
realtime_provider!(
    ElevenLabsConvService,
    "elevenlabs",
    "wss://api.elevenlabs.io/v1/convai/conversation",
    ""
);
realtime_provider!(
    UltravoxService,
    "ultravox",
    "wss://api.ultravox.ai/api/calls",
    "fixie-ai/ultravox"
);

/// Wires the realtime providers a host has consented to.
///
/// BOTH configured AND consented, not either. A configured provider nobody
/// agreed to is the failure this exists to prevent.
pub struct RealtimeCloudRegistration;

impl RealtimeCloudRegistration {
    pub fn consented(available: &[(String, bool)], consented: &[String]) -> Vec<String> {
        let allowed: Vec<String> = consented
            .iter()
            .map(|c| c.trim().to_lowercase())
            .filter(|c| !c.is_empty())
            .collect();
        available
            .iter()
            .filter(|(id, is_available)| *is_available && allowed.contains(&id.to_lowercase()))
            .map(|(id, _)| id.clone())
            .collect()
    }

    /// What a person is shown before any audio leaves the device.
    pub fn describe(providers: &[String]) -> String {
        if providers.is_empty() {
            return "no audio would leave this device".into();
        }
        format!(
            "if this device cannot hear or speak, it would ask: {}",
            providers.join(", ")
        )
    }
}
