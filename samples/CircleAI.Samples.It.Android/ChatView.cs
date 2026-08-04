#nullable enable

// ChatView.cs
//
// The conversation, as a conversation.
//
// WHAT THIS REPLACES: one TextView that everything appended to. Chat turns,
// download progress, SHA-256 mismatches, engine names, model file paths — all of
// it in one growing wall of monospaced-feeling text. A person opening the app saw
// "ggml-tiny.bin (1/1) verifying…" and "engine : OnnxTtsEngine on
// en_US-lessac-high.onnx (out-of-process espeak)" in the same place they were
// supposed to be talking to it.
//
// THE RULE THIS ENFORCES: the conversation area holds ONLY what was said. What
// the machine is doing goes to a single status line that replaces itself, and the
// detail goes to logcat where a developer can read it and nobody else has to. If
// a message would not make sense read aloud to the person holding the phone, it
// does not belong on the screen.
//
// BUILT FOR SOMEONE WHO HAS NEVER USED ONE. The shape is the shape of every
// messaging app already on the phone — your words on the right, its words on the
// left, in bubbles. That is not a style choice; it is the one interface a
// seventy-year-old and a seven-year-old have both already learned. Nothing here
// needs to be taught.
//
// NO SCROLLBAR, EVER. It scrolls, but the bar is off: a thin grey sliver
// appearing and vanishing at the edge is visual noise that tells a person nothing
// they cannot get from the content moving.

using System;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

/// <summary>The conversation area: bubbles, a status line, and nothing else.</summary>
public sealed class ChatView : LinearLayout
{
    readonly ScrollView _scroll;
    readonly LinearLayout _turns;
    readonly TextView _status;
    readonly Context _c;

    TextView? _openReply;      // the bubble currently being streamed into

    public ChatView(Context c) : base(c)
    {
        _c = c;
        Orientation = Orientation.Vertical;

        // ── the status line ──────────────────────────────────────────────
        // One line, replaced not appended, in plain words. Everything that
        // used to be a log entry becomes at most a change to this sentence.
        _status = Ui.Label(c, "", 13.5f, Ui.InkSoft);
        _status.SetPadding(Ui.Dp(c, 18), Ui.Dp(c, 8), Ui.Dp(c, 18), Ui.Dp(c, 8));
        _status.Gravity = GravityFlags.Center;
        _status.Visibility = ViewStates.Gone;
        AddView(_status, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

        _scroll = new ScrollView(c);
        _scroll.VerticalScrollBarEnabled = false;
        _scroll.OverScrollMode = OverScrollMode.Never;

        _turns = new LinearLayout(c) { Orientation = Orientation.Vertical };
        _turns.SetPadding(Ui.Dp(c, 12), Ui.Dp(c, 8), Ui.Dp(c, 12), Ui.Dp(c, 12));
        _scroll.AddView(_turns);

        AddView(_scroll, new LayoutParams(LayoutParams.MatchParent, 0, 1f));
    }

    /// <summary>What the machine is doing, in words a person would use.</summary>
    /// <remarks>
    /// Replaces itself rather than accumulating. Pass null or empty to clear it —
    /// a status line that stays on the last thing that happened is a status line
    /// that is lying within about a second.
    /// </remarks>
    public void Status(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Visibility = ViewStates.Gone;
            return;
        }
        _status.Text = text;
        _status.Visibility = ViewStates.Visible;
    }

    /// <summary>Something the person said.</summary>
    public void You(string text) => Bubble(text, mine: true);

    /// <summary>Begins a reply that will be streamed in.</summary>
    public void BeginReply()
    {
        _openReply = Bubble("", mine: false);
    }

    /// <summary>Adds to the reply in progress, opening one if needed.</summary>
    /// <remarks>
    /// STRIPS THE SPEAKER PREFIX. The session streams its answers with a routing
    /// marker in front — "IT! &gt; " — which made sense when every turn was a line
    /// in one text buffer and you needed to know who was talking. A bubble already
    /// says who is talking by which side it is on and what colour it is, so the
    /// prefix is a leftover from the old interface leaking into the new one. Left
    /// alone, the first thing the assistant appears to say is its own name and a
    /// piece of punctuation.
    /// </remarks>
    public void Reply(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        if (_openReply is null) BeginReply();

        _openReply!.Text += chunk;

        // Chunks arrive a few characters at a time, so the prefix may be split
        // across several. Re-check while the bubble is still short rather than
        // trying to catch it in one chunk.
        var text = _openReply.Text ?? string.Empty;
        if (text.Length <= 24)
        {
            var cut = text.IndexOf('>');
            if (cut >= 0 && cut <= 12 && text[..cut].Trim().Length <= 10)
                _openReply.Text = text[(cut + 1)..].TrimStart();
        }
        ScrollToEnd();
    }

    /// <summary>Closes the reply in progress.</summary>
    public void EndReply()
    {
        // An empty bubble means the model produced nothing. Leaving it is a grey
        // rectangle with no explanation, which reads as a bug rather than silence.
        if (_openReply is { } r && string.IsNullOrWhiteSpace(r.Text))
            _turns.RemoveView((View)r.Parent!);
        _openReply = null;
    }

    /// <summary>
    /// A short system aside — "Voice is on", "That did not work".
    /// </summary>
    /// <remarks>
    /// Centred and quiet so it reads as the app speaking about itself rather than
    /// as a turn in the conversation. Use sparingly: every one of these is a line
    /// the person has to read and decide to ignore.
    /// </remarks>
    public void Note(string text)
    {
        DismissWelcome();
        var t = Ui.Label(_c, text, 13f, Ui.InkSoft);
        t.Gravity = GravityFlags.Center;
        t.SetPadding(Ui.Dp(_c, 24), Ui.Dp(_c, 10), Ui.Dp(_c, 24), Ui.Dp(_c, 10));
        _turns.AddView(t, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        ScrollToEnd();
    }

    /// <summary>
    /// What fills the screen before anyone has said anything.
    /// </summary>
    /// <remarks>
    /// AN EMPTY CHAT IS A HARDER PROBLEM THAN A FULL ONE. Taking the logs out left
    /// a black rectangle and a box saying "Type a message", which asks the one
    /// question most people cannot answer on the spot: what do you say to it?
    /// Someone who has never used an assistant does not know what it is for, and
    /// someone who has is still being asked to invent an example.
    /// <para>
    /// So the examples are HERE, and they are TAPPABLE. Nobody has to think of
    /// anything or type anything to get the first answer — which is the whole
    /// difference between a seven-year-old or a seventy-year-old trying it and
    /// putting the phone down. They are also the fastest way to teach what it can
    /// do, without a paragraph explaining it.
    /// </para>
    /// </remarks>
    public void ShowWelcome(string greeting, params (string Label, Action OnTap)[] suggestions)
    {
        Clear();

        var wrap = new LinearLayout(_c) { Orientation = Orientation.Vertical };
        wrap.SetGravity(GravityFlags.Center);
        wrap.SetPadding(Ui.Dp(_c, 22), Ui.Dp(_c, 28), Ui.Dp(_c, 22), Ui.Dp(_c, 12));

        var hi = Ui.Label(_c, greeting, 19f, Ui.Ink, bold: true);
        hi.Gravity = GravityFlags.Center;
        wrap.AddView(hi, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

        var sub = Ui.Label(_c, "Tap one to start, or type your own.", 14.5f, Ui.InkSoft);
        sub.Gravity = GravityFlags.Center;
        sub.SetPadding(0, Ui.Dp(_c, 8), 0, Ui.Dp(_c, 20));
        wrap.AddView(sub, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

        foreach (var (label, onTap) in suggestions)
        {
            var chip = Ui.Label(_c, label, 16f, Ui.Blue, bold: true);
            chip.Gravity = GravityFlags.Center;
            chip.SetPadding(Ui.Dp(_c, 18), Ui.Dp(_c, 14), Ui.Dp(_c, 18), Ui.Dp(_c, 14));
            chip.Background = Ui.Outlined(_c, Ui.Blue, 14f);
            chip.Clickable = true;
            chip.Click += (_, _) => onTap();

            var lp = new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent);
            lp.BottomMargin = Ui.Dp(_c, 10);
            wrap.AddView(chip, lp);
        }

        _turns.AddView(wrap, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        _showingWelcome = true;
    }

    bool _showingWelcome;

    /// <summary>Removes the welcome, if it is up. Called before the first turn.</summary>
    void DismissWelcome()
    {
        if (!_showingWelcome) return;
        _turns.RemoveAllViews();
        _showingWelcome = false;
    }

    /// <summary>Clears the conversation.</summary>
    public void Clear()
    {
        _turns.RemoveAllViews();
        _openReply = null;
        _showingWelcome = false;
        Status(null);
    }

    /// <summary>True when nothing has been said yet.</summary>
    public bool IsEmpty => _turns.ChildCount == 0;

    TextView Bubble(string text, bool mine)
    {
        DismissWelcome();
        var row = new LinearLayout(_c) { Orientation = Orientation.Horizontal };
        row.SetGravity(mine ? GravityFlags.Right : GravityFlags.Left);
        row.SetPadding(0, Ui.Dp(_c, 4), 0, Ui.Dp(_c, 4));

        var t = new TextView(_c) { Text = text, TextSize = 16.5f };
        t.SetTextColor(mine ? Ui.White : Ui.Ink);
        t.SetLineSpacing(0f, 1.22f);
        t.SetPadding(Ui.Dp(_c, 15), Ui.Dp(_c, 11), Ui.Dp(_c, 15), Ui.Dp(_c, 11));
        t.SetTextIsSelectable(true);

        // Mine is solid blue; its is a bordered surface. The border matters — on a
        // dark background an unbordered card and the page behind it are the same
        // colour at a glance, and the reply stops looking like a distinct thing.
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(Ui.Dp(_c, 16));
        if (mine) bg.SetColor(Ui.Blue.ToArgb());
        else
        {
            bg.SetColor(Ui.Surface.ToArgb());
            bg.SetStroke(Ui.Dp(_c, 1), Ui.Blue);
        }
        t.Background = bg;

        // A bubble that runs the full width stops reading as a bubble, and long
        // lines are harder to follow — cap it and let the shape do its job.
        var lp = new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent);
        t.SetMaxWidth((int)(Resources!.DisplayMetrics!.WidthPixels * 0.82));
        row.AddView(t, lp);

        _turns.AddView(row, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        ScrollToEnd();
        return t;
    }

    void ScrollToEnd() => _scroll.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
}
