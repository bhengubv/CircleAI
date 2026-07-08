//! proactive_reasoning.rs
//!
//! B!'s ability to initiate contact rather than merely respond. Ported from
//! `IProactiveReasoningService.cs` + `ProactiveReasoningService.cs`. The service
//! evaluates a prioritised list of [`ITriggerCondition`] instances and, when the
//! first one fires, generates a warm, goal-aware check-in via
//! [`IAIService::ask`], then invokes the registered "message ready" handlers.
//!
//! The C# `CheckAsync` is async and reads `DateTimeOffset.UtcNow`; the sync port
//! takes `now` explicitly so the whole flow (trigger evaluation → prompt build →
//! generation → handler dispatch) is deterministic. Goal / affect stores are
//! injected behind traits with in-memory defaults.

use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

use crate::memory::{AffectState, Goal};

use super::service::IAIService;
use super::triggers::{ITriggerCondition, ProactiveContext};

/// Loads the current [`AffectState`] for a user. Mirrors the fields the C#
/// `ProactiveReasoningService` reads off `IAffectStore` (`LoadAsync`).
pub trait IAffectStore: Send + Sync {
    /// Load the affect state for `user_id`. Returns `None` when none is stored.
    fn load(&self, user_id: &str) -> Option<AffectState>;
}

/// Loads the active goals for a user. Mirrors `IGoalStore.GetActiveAsync`.
pub trait IGoalStore: Send + Sync {
    /// Return every active (in-progress) goal for `user_id`.
    fn get_active(&self, user_id: &str) -> Vec<Goal>;
}

/// In-memory [`IAffectStore`] holding one state per user. Deterministic default
/// for tests / headless scenarios.
#[derive(Default)]
pub struct InMemoryAffectStore {
    states: Mutex<std::collections::HashMap<String, AffectState>>,
}

impl InMemoryAffectStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Store / replace the state for its user.
    pub fn set(&self, state: AffectState) {
        self.states
            .lock()
            .unwrap()
            .insert(state.user_id.clone(), state);
    }
}

impl IAffectStore for InMemoryAffectStore {
    fn load(&self, user_id: &str) -> Option<AffectState> {
        self.states.lock().unwrap().get(user_id).cloned()
    }
}

/// In-memory [`IGoalStore`] holding active goals per user.
#[derive(Default)]
pub struct InMemoryGoalStore {
    goals: Mutex<Vec<Goal>>,
}

impl InMemoryGoalStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a goal to the store.
    pub fn add(&self, goal: Goal) {
        self.goals.lock().unwrap().push(goal);
    }
}

impl IGoalStore for InMemoryGoalStore {
    fn get_active(&self, user_id: &str) -> Vec<Goal> {
        use crate::memory::GoalStatus;
        self.goals
            .lock()
            .unwrap()
            .iter()
            .filter(|g| g.user_id == user_id && g.status == GoalStatus::Active)
            .cloned()
            .collect()
    }
}

/// Emitted when B! generates a proactive message. 1:1 with the C#
/// `ProactiveMessageEventArgs` record.
#[derive(Debug, Clone)]
pub struct ProactiveMessageEventArgs {
    /// User this message targets.
    pub user_id: String,
    /// The generated check-in message.
    pub message: String,
    /// Name of the trigger condition that fired.
    pub trigger_name: String,
    /// When the message was generated (UTC).
    pub generated_utc: DateTime<Utc>,
}

/// A handler for [`ProactiveMessageEventArgs`]. Mirrors the C#
/// `EventHandler<ProactiveMessageEventArgs>`.
pub type ProactiveMessageHandler = Box<dyn Fn(&ProactiveMessageEventArgs) + Send + Sync>;

/// Evaluates trigger conditions and generates a proactive check-in message
/// unprompted by the user. 1:1 with the C# `IProactiveReasoningService`.
pub trait IProactiveReasoningService {
    /// Evaluate all trigger conditions and, when any fires, generate a
    /// proactive message and dispatch the "message ready" handlers. `now` is
    /// the reference UTC time. Returns the generated message when one fired.
    fn check(&self, user_id: &str, now: DateTime<Utc>) -> Option<String>;
}

/// Default [`IProactiveReasoningService`]. Evaluates a prioritised trigger list
/// and calls [`IAIService::ask`] to generate a warm, goal-aware check-in when
/// any condition fires (only the first-firing trigger per call). 1:1 with the C#
/// `ProactiveReasoningService`.
pub struct ProactiveReasoningService<'a> {
    butler: &'a dyn IAIService,
    goal_store: Option<&'a dyn IGoalStore>,
    affect_store: Option<&'a dyn IAffectStore>,
    triggers: Vec<Box<dyn ITriggerCondition>>,
    handlers: Mutex<Vec<ProactiveMessageHandler>>,
}

impl<'a> ProactiveReasoningService<'a> {
    /// Constructs the service.
    ///
    /// `triggers` is evaluated in order; the first that fires causes the
    /// check-in. Pass an empty vector to disable all proactive messaging.
    pub fn new(
        butler: &'a dyn IAIService,
        goal_store: Option<&'a dyn IGoalStore>,
        affect_store: Option<&'a dyn IAffectStore>,
        triggers: Vec<Box<dyn ITriggerCondition>>,
    ) -> Self {
        Self {
            butler,
            goal_store,
            affect_store,
            triggers,
            handlers: Mutex::new(Vec::new()),
        }
    }

    /// Registers a handler fired when B! has something to say unprompted.
    /// Mirrors subscribing to the C# `ProactiveMessageReady` event.
    pub fn on_proactive_message_ready(&self, handler: ProactiveMessageHandler) {
        self.handlers.lock().unwrap().push(handler);
    }

    /// Builds a proactive prompt. 1:1 with the C# `BuildProactivePrompt` —
    /// same wording, same singular/plural handling, same "away for N hours/
    /// minutes" threshold (> 5 minutes).
    pub fn build_proactive_prompt(
        _user_id: &str,
        time_since_last_interaction: Duration,
        active_goals: &[Goal],
    ) -> String {
        let mut sb = String::new();
        sb.push_str("You are B!. ");

        let total_minutes = time_since_last_interaction.num_seconds() as f64 / 60.0;
        if total_minutes > 5.0 {
            let hours = (time_since_last_interaction.num_seconds() / 3600) as i64;
            let minutes = ((time_since_last_interaction.num_seconds() / 60) % 60) as i64;
            if hours > 0 {
                sb.push_str(&format!(
                    "The user has been away for approximately {hours} hour{}. ",
                    if hours == 1 { "" } else { "s" }
                ));
            } else {
                sb.push_str(&format!(
                    "The user has been away for approximately {minutes} minute{}. ",
                    if minutes == 1 { "" } else { "s" }
                ));
            }
        }

        if !active_goals.is_empty() {
            let n = active_goals.len();
            sb.push_str(&format!(
                "They have {n} active goal{}: ",
                if n == 1 { "" } else { "s" }
            ));
            for (i, g) in active_goals.iter().enumerate() {
                sb.push('"');
                sb.push_str(&g.title);
                sb.push('"');
                if i < n - 1 {
                    sb.push_str(", ");
                }
            }
            sb.push_str(". ");
        }

        sb.push_str("Generate a brief, friendly check-in message (1-2 sentences). ");
        sb.push_str("Be warm, specific to their goals if you know them, and not intrusive.");
        sb
    }
}

impl IProactiveReasoningService for ProactiveReasoningService<'_> {
    fn check(&self, user_id: &str, now: DateTime<Utc>) -> Option<String> {
        assert!(!user_id.trim().is_empty(), "userId required");

        if self.triggers.is_empty() {
            return None;
        }

        // 1. Load affect state.
        let affect = self.affect_store.and_then(|s| s.load(user_id));

        // 2. Load active goals.
        let active_goals = self
            .goal_store
            .map(|s| s.get_active(user_id))
            .unwrap_or_default();

        // 3. Build context snapshot.
        let time_since_last = match affect.as_ref() {
            Some(a) => now - a.last_updated_at,
            None => Duration::zero(),
        };
        let context = ProactiveContext::new(
            user_id,
            now,
            time_since_last,
            affect.clone(),
            active_goals.clone(),
        );

        // 4. Check triggers in order — fire only the first one.
        for trigger in &self.triggers {
            // Error-isolate the trigger's evaluation (mirrors the C# try/catch
            // that logs and skips a throwing trigger).
            let met = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                trigger.is_met(&context)
            }))
            .unwrap_or(false);
            if !met {
                continue;
            }

            // 5. Build the proactive prompt + 6. generate the message.
            let prompt = Self::build_proactive_prompt(user_id, time_since_last, &active_goals);
            let message = match self.butler.ask(&prompt) {
                Ok(m) => m,
                // butler failure is non-fatal — abandon this call.
                Err(_) => return None,
            };

            // 7. Dispatch the "message ready" handlers (best-effort).
            let args = ProactiveMessageEventArgs {
                user_id: user_id.to_string(),
                message: message.clone(),
                trigger_name: trigger.name().to_string(),
                generated_utc: now,
            };
            let handlers = self.handlers.lock().unwrap();
            for h in handlers.iter() {
                let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(&args)));
            }

            // Only fire one trigger per call.
            return Some(message);
        }

        None
    }
}
