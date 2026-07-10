//! aether::context — Rust port of `CircleAI.Aether/IAetherContext.cs`.
//!
//! Contract 2 — Presence and Capability. Answers "Is Aether here, and at what
//! level?" Apps query this at startup; the bootstrap acts on the result.
//!
//! `System.Version?` maps to [`AetherVersion`] (four ordered components with
//! .NET's "-1 for unset build/revision" comparison semantics) wrapped in
//! `Option`. The trait is property-based in C#; the Rust port exposes the same
//! properties as getter methods with default derivations (`is_sufficient`,
//! `requires_auth`) provided on the trait so implementors only supply raw state.

use std::cmp::Ordering;

use serde::{Deserialize, Serialize};

use super::events::AetherInstallLevel;

// ─────────────────────────────────────────────────────────────────────────────
// AetherVersion — a faithful `System.Version` port
// ─────────────────────────────────────────────────────────────────────────────

/// A `major.minor[.build[.revision]]` version, comparable with the same rules
/// as .NET's `System.Version`: unset `build`/`revision` compare as `-1`, so
/// `1.0` (`build = -1`) sorts before `1.0.0` (`build = 0`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct AetherVersion {
    pub major: i32,
    pub minor: i32,
    /// `-1` when unset (two-component version).
    pub build: i32,
    /// `-1` when unset (fewer than four components).
    pub revision: i32,
}

impl AetherVersion {
    /// A two-component `major.minor` version (build and revision unset).
    pub fn new(major: i32, minor: i32) -> Self {
        Self {
            major,
            minor,
            build: -1,
            revision: -1,
        }
    }

    /// A three-component `major.minor.build` version (revision unset).
    pub fn with_build(major: i32, minor: i32, build: i32) -> Self {
        Self {
            major,
            minor,
            build,
            revision: -1,
        }
    }

    /// A full four-component `major.minor.build.revision` version.
    pub fn full(major: i32, minor: i32, build: i32, revision: i32) -> Self {
        Self {
            major,
            minor,
            build,
            revision,
        }
    }
}

impl PartialOrd for AetherVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for AetherVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        self.major
            .cmp(&other.major)
            .then_with(|| self.minor.cmp(&other.minor))
            .then_with(|| self.build.cmp(&other.build))
            .then_with(|| self.revision.cmp(&other.revision))
    }
}

impl std::fmt::Display for AetherVersion {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.revision >= 0 {
            write!(
                f,
                "{}.{}.{}.{}",
                self.major, self.minor, self.build, self.revision
            )
        } else if self.build >= 0 {
            write!(f, "{}.{}.{}", self.major, self.minor, self.build)
        } else {
            write!(f, "{}.{}", self.major, self.minor)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAetherContext trait
// ─────────────────────────────────────────────────────────────────────────────

/// Reports the presence, version, and capability of the Aether runtime on this
/// device. The platform adapter provides the concrete implementation.
///
/// `install_level`, `runtime_version`, `minimum_required`, and `is_enabled` are
/// the raw state an implementor supplies; `is_available`, `is_sufficient`, and
/// `requires_auth` are derived defaults matching the C# computed properties.
pub trait IAetherContext: Send + Sync {
    /// Where Aether is installed, if at all.
    fn install_level(&self) -> AetherInstallLevel;

    /// The installed Aether runtime version, or `None` when Aether is absent.
    fn runtime_version(&self) -> Option<AetherVersion>;

    /// The minimum Aether version declared as required by the consuming app.
    fn minimum_required(&self) -> Option<AetherVersion>;

    /// True when Aether is installed and currently enabled. An OS-managed
    /// instance that has been toggled off returns false here.
    fn is_enabled(&self) -> bool;

    /// True when Aether is installed and enabled.
    ///
    /// Default: available whenever the install level is not
    /// [`AetherInstallLevel::None`]. Implementors that need the C# "always true"
    /// behaviour (live in-process runtime) override this.
    fn is_available(&self) -> bool {
        self.install_level() != AetherInstallLevel::None
    }

    /// True when [`IAetherContext::runtime_version`] satisfies
    /// [`IAetherContext::minimum_required`]. Always true when the minimum is
    /// `None`.
    fn is_sufficient(&self) -> bool {
        match self.minimum_required() {
            None => true,
            Some(min) => matches!(self.runtime_version(), Some(rt) if rt >= min),
        }
    }

    /// True when the install level is [`AetherInstallLevel::Os`]. OS-managed
    /// instances require biometric + device admin auth before they can be
    /// toggled.
    fn requires_auth(&self) -> bool {
        self.install_level() == AetherInstallLevel::Os
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StaticAetherContext — a working, immutable implementation
// ─────────────────────────────────────────────────────────────────────────────

/// A fixed-state [`IAetherContext`]. Holds the four raw properties and derives
/// the rest via the trait defaults. Suitable for tests, servers, and any host
/// that knows its Aether state up front.
#[derive(Debug, Clone)]
pub struct StaticAetherContext {
    install_level: AetherInstallLevel,
    runtime_version: Option<AetherVersion>,
    minimum_required: Option<AetherVersion>,
    is_enabled: bool,
    /// When set, overrides the derived [`IAetherContext::is_available`].
    available_override: Option<bool>,
}

impl StaticAetherContext {
    /// Builds a context from raw state. `is_available` is derived from
    /// `install_level`.
    pub fn new(
        install_level: AetherInstallLevel,
        runtime_version: Option<AetherVersion>,
        minimum_required: Option<AetherVersion>,
        is_enabled: bool,
    ) -> Self {
        Self {
            install_level,
            runtime_version,
            minimum_required,
            is_enabled,
            available_override: None,
        }
    }

    /// A context reporting Aether absent (install level None, disabled).
    pub fn absent() -> Self {
        Self::new(AetherInstallLevel::None, None, None, false)
    }

    /// Forces [`IAetherContext::is_available`] to `value` regardless of install
    /// level (mirrors adapters like `AetherNetContextAdapter` that report an
    /// always-available in-process runtime).
    pub fn with_available_override(mut self, value: bool) -> Self {
        self.available_override = Some(value);
        self
    }
}

impl IAetherContext for StaticAetherContext {
    fn install_level(&self) -> AetherInstallLevel {
        self.install_level
    }

    fn runtime_version(&self) -> Option<AetherVersion> {
        self.runtime_version
    }

    fn minimum_required(&self) -> Option<AetherVersion> {
        self.minimum_required
    }

    fn is_enabled(&self) -> bool {
        self.is_enabled
    }

    fn is_available(&self) -> bool {
        match self.available_override {
            Some(v) => v,
            None => self.install_level != AetherInstallLevel::None,
        }
    }
}
