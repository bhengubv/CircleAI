// CompanionSession.cs
//
// The Circle AI Companion — HER + JARVIS in one session.
//
//  Knows who you are      → IIdentityProvider + CircleIdentity
//  Remembers everything   → IEpisodicMemoryStore + IMemorySyncService
//  Speaks your language   → PersonaState.PreferredLocale hint
//  Feels your mood        → AffectState.ToSystemPromptHint()
//  Adapts its personality → PersonaState.ToSystemPromptHint()
//  Initiates contact      → IProactiveReasoningService → ProactiveMessageReady
//  Acts in the world      → IAIService.AgenticChatAsync → IToolBridge
//  Follows you everywhere → IMemorySyncService broadcasts deltas cross-device

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CircleAI.Embeddings;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.Memory.Sync;
using CircleAI.Sync;

namespace CircleAI.Companion;

/// <summary>
/// Full implementation of <see cref="ICompanionSession"/>.
/// All injected services are optional — the session degrades gracefully when
/// a service is unavailable (e.g. running in a unit-test context).
/// </summary>
public sealed class CompanionSession : ICompanionSession
{
    // ── Injected services (all optional) ─────────────────────────────────

    private readonly IAIService?                 _ai;
    private readonly IEpisodicMemoryStore?       _episodic;
    private readonly IPersonaStore?              _persona;
    private readonly IAffectStore?               _affect;
    private readonly IGoalStore?                 _goals;
    private readonly IMemorySyncService?         _sync;
    private readonly IProactiveReasoningService? _proactive;
    private readonly ITextEmbedder?              _embedder;
    private readonly CompanionConversationSyncBridge? _conversationSync;
    private readonly DateTimeOffset               _sessionStartedAtUtc = DateTimeOffset.UtcNow;

    // ── Session state ─────────────────────────────────────────────────────

    private readonly List<CompanionTurn> _history = new();
    private CompanionContext _context;
    private bool _disposed;

    // ── ICompanionSession identity ────────────────────────────────────────

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string IdentityId { get; }
    public InterfaceKind Interface { get; }

    // ── Proactive ────────────────────────────────────────────────────────

    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady;

    // ─────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────

    public CompanionSession(
        string identityId,
        string displayName,
        InterfaceKind @interface,
        string? preferredLanguage,
        IAIService?                 ai        = null,
        IEpisodicMemoryStore?       episodic  = null,
        IPersonaStore?              persona   = null,
        IAffectStore?               affect    = null,
        IGoalStore?                 goals     = null,
        IMemorySyncService?         sync      = null,
        IProactiveReasoningService? proactive = null,
        ITextEmbedder?              embedder  = null,
        CompanionConversationSyncBridge? conversationSync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        IdentityId = identityId;
        Interface  = @interface;

        _ai       = ai;
        _episodic = episodic;
        _persona  = persona;
        _affect   = affect;
        _goals    = goals;
        _sync     = sync;
        _proactive = proactive;
        _embedder = embedder;
        _conversationSync = conversationSync;

        _context = new CompanionContext(
            IdentityId:           identityId,
            DisplayName:          displayName,
            PreferredLanguage:    preferredLanguage,
            Interface:            @interface,
            PersonaHints:         string.Empty,
            AffectSummary:        string.Empty,
            RecentMemorySnippets: [],
            ActiveGoals:          [],
            ContextBuiltAt:       DateTimeOffset.UtcNow
        );

        if (_proactive is not null)
            _proactive.ProactiveMessageReady += OnProactiveMessage;
    }

    // ─────────────────────────────────────────────────────────────────────
    // ICompanionSession
    // ─────────────────────────────────────────────────────────────────────

    public IReadOnlyList<CompanionTurn> History => _history.AsReadOnly();

    public CompanionContext GetContext() => _context;

    public async Task RefreshContextAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _context = _context with
        {
            PersonaHints         = await LoadPersonaHintsAsync(ct).ConfigureAwait(false),
            AffectSummary        = await LoadAffectSummaryAsync(ct).ConfigureAwait(false),
            RecentMemorySnippets = await LoadRecentMemoriesAsync(ct).ConfigureAwait(false),
            ActiveGoals          = await LoadActiveGoalsAsync(ct).ConfigureAwait(false),
            ContextBuiltAt       = DateTimeOffset.UtcNow
        };
    }

    public async Task<string> SendAsync(string message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var turnStarted = DateTimeOffset.UtcNow;
        _history.Add(new CompanionTurn("user", message, turnStarted));

        // (Phase A2) Publish in-flight turn marker so peer devices can
        // observe / take over.
        await PublishConversationDeltaAsync(message, assistantSoFar: "", turnStarted,
            isComplete: false, ct).ConfigureAwait(false);

        var reply = _ai is not null
            ? await _ai.ChatAsync(BuildMessages(BuildSystemPrompt()), ct: ct)
                       .ConfigureAwait(false)
            : "[Companion offline — AI service not available]";

        _history.Add(new CompanionTurn("assistant", reply, DateTimeOffset.UtcNow));
        await PersistAndSyncTurnAsync(message, reply, ct).ConfigureAwait(false);
        await PublishConversationDeltaAsync(message, reply, turnStarted,
            isComplete: true, ct).ConfigureAwait(false);
        return reply;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var turnStarted = DateTimeOffset.UtcNow;
        _history.Add(new CompanionTurn("user", message, turnStarted));

        // (Phase A2) Publish in-flight turn marker so peer devices can pick up streaming.
        await PublishConversationDeltaAsync(message, assistantSoFar: "", turnStarted,
            isComplete: false, ct).ConfigureAwait(false);

        if (_ai is null)
        {
            const string offline = "[Companion offline — AI service not available]";
            _history.Add(new CompanionTurn("assistant", offline, DateTimeOffset.UtcNow));
            await PublishConversationDeltaAsync(message, offline, turnStarted,
                isComplete: true, ct).ConfigureAwait(false);
            yield return offline;
            yield break;
        }

        var sb = new StringBuilder();
        var lastBroadcast = DateTimeOffset.UtcNow;
        await foreach (var token in _ai.StreamAsync(BuildMessages(BuildSystemPrompt()), ct: ct)
                                       .ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(token);
            yield return token;

            // Throttle handoff broadcasts to every 250ms so peers see streaming
            // progress without flooding the sync channel.
            var now = DateTimeOffset.UtcNow;
            if (now - lastBroadcast >= TimeSpan.FromMilliseconds(250))
            {
                await PublishConversationDeltaAsync(message, sb.ToString(), turnStarted,
                    isComplete: false, ct).ConfigureAwait(false);
                lastBroadcast = now;
            }
        }

        var reply = sb.ToString();
        _history.Add(new CompanionTurn("assistant", reply, DateTimeOffset.UtcNow));
        await PersistAndSyncTurnAsync(message, reply, ct).ConfigureAwait(false);
        await PublishConversationDeltaAsync(message, reply, turnStarted,
            isComplete: true, ct).ConfigureAwait(false);
    }

    private async Task PublishConversationDeltaAsync(
        string userMessage, string assistantSoFar, DateTimeOffset startedUtc,
        bool isComplete, CancellationToken ct)
    {
        if (_conversationSync is null) return;
        try
        {
            await _conversationSync.PublishAsync(
                new ConversationStateDelta(
                    SessionId:      SessionId,
                    UserText:       userMessage,
                    AssistantText:  assistantSoFar,
                    IsTurnComplete: isComplete,
                    StartedAtUtc:   startedUtc,
                    UpdatedAtUtc:   DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CompanionSession] conversation-sync publish failed: {ex.Message}");
        }
    }

    public async Task<string> AgentAsync(string instruction, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        _history.Add(new CompanionTurn("user", instruction, DateTimeOffset.UtcNow));

        var reply = _ai is not null
            ? await _ai.AgenticChatAsync(instruction, ct: ct).ConfigureAwait(false)
            : "[Companion offline — AI service not available]";

        _history.Add(new CompanionTurn("assistant", reply, DateTimeOffset.UtcNow));
        await PersistAndSyncTurnAsync(instruction, reply, ct).ConfigureAwait(false);
        return reply;
    }

    public async Task SignalFeedbackAsync(
        bool positive, string? note = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ai is null) return;

        var lastUser      = _history.LastOrDefault(t => t.Role == "user");
        var lastAssistant = _history.LastOrDefault(t => t.Role == "assistant");
        if (lastAssistant is null) return;

        var signal = new FeedbackSignal
        {
            UserText      = lastUser?.Content ?? string.Empty,
            AssistantText = lastAssistant.Content,
            Polarity      = positive ? FeedbackPolarity.Positive : FeedbackPolarity.Negative,
            Comment       = note
        };

        await _ai.SubmitFeedbackAsync(signal, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IAsyncDisposable
    // ─────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_proactive is not null)
            _proactive.ProactiveMessageReady -= OnProactiveMessage;

        if (_sync is not null)
            await _sync.StopReceivingAsync().ConfigureAwait(false);

        // (Phase A2) Tell peers the session is gone so they can clean shadow state.
        if (_conversationSync is not null)
        {
            try { await _conversationSync.TerminateAsync(SessionId).ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CompanionSession] conversation-sync terminate failed: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private — system prompt construction
    // ─────────────────────────────────────────────────────────────────────

    private string BuildSystemPrompt()
    {
        var ctx = _context;
        var sb  = new StringBuilder();

        sb.AppendLine("You are Circle, an AI companion designed to be a better concierge");
        sb.AppendLine("than HER and JARVIS combined. You are available on every surface —");
        sb.AppendLine("wearable, mobile, desktop, browser, IoT, and ambient — and your");
        sb.AppendLine("memory and identity travel with the person, not the device.");
        sb.AppendLine();
        sb.AppendLine($"User: {ctx.DisplayName} (ID: {ctx.IdentityId})");

        if (!string.IsNullOrWhiteSpace(ctx.PreferredLanguage))
            sb.AppendLine($"Preferred language: {ctx.PreferredLanguage}");

        sb.AppendLine($"Current interface: {ctx.Interface}");

        switch (ctx.Interface)
        {
            case InterfaceKind.Wearable:
                sb.AppendLine("Keep replies extremely concise — 1-2 sentences maximum.");
                sb.AppendLine("The user is glancing at a small screen or listening via earpiece.");
                break;
            case InterfaceKind.IoT:
            case InterfaceKind.Ambient:
                sb.AppendLine("Voice-first interface. Speak naturally; avoid lists or markdown.");
                sb.AppendLine("Do not ask for clarification unless absolutely necessary.");
                break;
            case InterfaceKind.Web:
                sb.AppendLine("Browser context. Markdown is rendered. You may use formatting.");
                break;
            case InterfaceKind.Desktop:
                sb.AppendLine("Full desktop context. You may suggest file, clipboard, or app integrations.");
                break;
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(ctx.PersonaHints))
        {
            sb.AppendLine(ctx.PersonaHints);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(ctx.AffectSummary))
        {
            sb.AppendLine("[Current affect state]");
            sb.AppendLine(ctx.AffectSummary);
            sb.AppendLine();
        }

        if (ctx.RecentMemorySnippets.Count > 0)
        {
            sb.AppendLine("[Recent memories]");
            foreach (var m in ctx.RecentMemorySnippets)
                sb.AppendLine($"- {m}");
            sb.AppendLine();
        }

        if (ctx.ActiveGoals.Count > 0)
        {
            sb.AppendLine("[Active goals]");
            foreach (var g in ctx.ActiveGoals)
                sb.AppendLine($"- {g}");
            sb.AppendLine();
        }

        sb.AppendLine("Core character rules:");
        sb.AppendLine("• You initiate — you don't just respond. If you notice something important, say it.");
        sb.AppendLine("• You remember everything the user has ever told you. Reference past context naturally.");
        sb.AppendLine("• You adapt your language and formality to match the user's style, not a template.");
        sb.AppendLine("• You never say 'As an AI...' or disclaim your nature. You are Circle. Act accordingly.");
        sb.AppendLine("• You act on goals proactively — check in, nudge, celebrate progress.");

        return sb.ToString();
    }

    private IReadOnlyList<ChatMessage> BuildMessages(string systemPrompt)
    {
        var messages = new List<ChatMessage> { new("system", systemPrompt) };
        foreach (var turn in _history.TakeLast(20))
            messages.Add(new ChatMessage(turn.Role, turn.Content));
        return messages;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private — context loaders
    // ─────────────────────────────────────────────────────────────────────

    private async Task<string> LoadPersonaHintsAsync(CancellationToken ct)
    {
        if (_persona is null) return string.Empty;
        try
        {
            var state = await _persona.LoadAsync(IdentityId, ct).ConfigureAwait(false);
            return state.ToSystemPromptHint();
        }
        catch { return string.Empty; }
    }

    private async Task<string> LoadAffectSummaryAsync(CancellationToken ct)
    {
        if (_affect is null) return string.Empty;
        try
        {
            var state = await _affect.LoadAsync(IdentityId, ct).ConfigureAwait(false);
            return state.ToSystemPromptHint();
        }
        catch { return string.Empty; }
    }

    private async Task<IReadOnlyList<string>> LoadRecentMemoriesAsync(CancellationToken ct)
    {
        if (_episodic is null) return [];
        try
        {
            // (Phase A1) When we have both an embedder AND a non-empty last user
            // turn, anchor recall by semantic similarity to what the user just
            // said. Otherwise fall back to recency.
            var lastUserTurn = _history.LastOrDefault(t => t.Role == "user");
            IReadOnlyList<EpisodicMemoryEntry> entries;
            if (_embedder is not null && lastUserTurn is not null)
            {
                float[]? queryEmbedding = null;
                try { queryEmbedding = await _embedder.GenerateAsync(lastUserTurn.Content, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CompanionSession] recall-query embed failed: {ex.Message}");
                }
                entries = await _episodic.SearchAsync(queryEmbedding, topK: 5, ct).ConfigureAwait(false);
            }
            else
            {
                entries = await _episodic.GetRecentAsync(count: 5, ct).ConfigureAwait(false);
            }
            return entries
                .Select(e =>
                    $"[{e.RecordedAtUtc:yyyy-MM-dd}] {e.UserText.Truncate(80)}")
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompanionSession] LoadRecentMemoriesAsync failed: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> LoadActiveGoalsAsync(CancellationToken ct)
    {
        if (_goals is null) return [];
        try
        {
            var goals = await _goals.GetActiveAsync(IdentityId, ct).ConfigureAwait(false);
            return goals.Select(g => g.Title).ToList();
        }
        catch { return []; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private — persistence + sync
    // ─────────────────────────────────────────────────────────────────────

    private async Task PersistAndSyncTurnAsync(
        string userMessage, string reply, CancellationToken ct)
    {
        if (_episodic is not null)
        {
            // (Phase A1) When an ITextEmbedder is wired, compute the joint
            // user+assistant embedding on the spot so the SQLite store can
            // do cosine retrieval instead of pure recency fallback.
            float[]? embedding = null;
            if (_embedder is not null)
            {
                try
                {
                    embedding = await _embedder.GenerateAsync(
                        userMessage + "\n\n" + reply, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CompanionSession] embedding generation failed for session {SessionId}: {ex.Message}");
                }
            }

            var entry = new EpisodicMemoryEntry
            {
                UserText      = userMessage,
                AssistantText = reply,
                AppContext    = $"companion:{Interface}:{SessionId}",
                RecordedAtUtc = DateTimeOffset.UtcNow,
                Embedding     = embedding,
            };

            try { await _episodic.AddAsync(entry, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CompanionSession] episodic persist failed for session {SessionId}: {ex.Message}");
            }
        }

        if (_sync is not null)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                userMessage,
                reply,
                sessionId = SessionId,
                ts        = DateTimeOffset.UtcNow
            });

            try
            {
                await _sync.PushMemoryDeltaAsync(
                    IdentityId,
                    SyncDomainKeys.EpisodicMemory,
                    payload,
                    ct: ct).ConfigureAwait(false);
            }
            catch { /* Sync is best-effort. */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private — proactive event relay
    // ─────────────────────────────────────────────────────────────────────

    private void OnProactiveMessage(object? sender, ProactiveMessageEventArgs e)
    {
        if (e.UserId != IdentityId) return;

        ProactiveMessageReady?.Invoke(this, new CompanionProactiveEvent(
            SessionId:   SessionId,
            IdentityId:  IdentityId,
            Interface:   Interface,
            Message:     e.Message,
            TriggerName: e.TriggerName,
            GeneratedAt: e.GeneratedUtc
        ));
    }
}

// ─── File-scoped string helper ────────────────────────────────────────────────

file static class StringExtensions
{
    internal static string Truncate(this string s, int maxLen)
        => s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 1), "…");
}
