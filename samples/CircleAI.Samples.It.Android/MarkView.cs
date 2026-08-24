// MarkView.cs
//
// The brand mark, and the ONLY drawing of it in the app.
//
// WHY IT LIVES IN ITS OWN FILE. It was a private nested class inside
// HomeActivity, which meant home had the mark and every other screen had a
// substitute: Ui.HomeBar drew a plain filled dot with a comment claiming it
// "reads as the same object as the hero". It did not. The hero is a ring open on
// one side with three arcs leaving it; the header was a full stop. A person
// three screens deep was looking at a different logo and being told it was the
// same one.
//
// Sharing the class rather than redrawing the shape is the point. A second
// rendering would agree on the day it was written and drift on the first day
// somebody tuned the hero — and nobody would notice, because the two are never
// on screen together.

using System;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.Animations;

namespace CircleAI.Samples.It.Mobile;


/// <summary>
/// The brand mark: a ring with sound leaving it, at any size.
/// </summary>
/// <remarks>
/// THIS IS THE ONE DRAWING OF THE MARK. The hero on the home screen and the
/// 30dp one in every screen's header are the same class at two sizes — every
/// dimension is a fraction of the radius, so it scales without a second set of
/// numbers to keep in step.
/// <para>
/// The launcher icon (<c>ic_launcher_foreground.xml</c>) cannot share the class,
/// being a vector drawable, so it is GENERATED from the constants in OnDraw
/// instead. When those change, regenerate it — hand-drawing it is how it drifted
/// into a half circle open on the wrong side with three solid arcs where the
/// live mark fades them.
/// </para>
/// </remarks>
internal sealed class MarkView : View
{
    readonly Paint _ring = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
    readonly Paint _arc  = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
    readonly Paint _halo = new(PaintFlags.AntiAlias) { Color = Ui.Blue };
    bool _busy;

    MarkState _state = MarkState.Idle;
    float _level;          // 0..1, the voice arriving right now
    float _shown;          // smoothed, so the arcs move like breath not static
    long  _tick;           // frame counter, drives Thinking and Speaking

    public MarkView(Context c) : base(c)
    {
        _ring.SetStyle(Paint.Style.Stroke);
        _arc.SetStyle(Paint.Style.Stroke);
        _arc.StrokeCap = Paint.Cap.Round;
        _halo.SetStyle(Paint.Style.Fill);
        _halo.Alpha = 28;
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            var pulse = new AlphaAnimation(1f, 0.45f)
            {
                Duration = 700,
                RepeatCount = Animation.Infinite,
                RepeatMode = RepeatMode.Reverse,
            };
            StartAnimation(pulse);
        }
        else ClearAnimation();
        Invalidate();
    }

    /// <summary>Which part of the exchange this is.</summary>
    public void SetState(MarkState s)
    {
        if (_state == s) return;
        _state = s;
        _tick = 0;
        // Listening and Speaking both ride a real audio level — one coming in,
        // one going out — so only the states with no sound of their own start
        // from zero.
        if (s is not (MarkState.Listening or MarkState.Speaking)) _level = _shown = 0;
        ClearAnimation();           // the states animate themselves; alpha would fight them
        Invalidate();
    }

    /// <summary>
    /// How loud the microphone is hearing you, 0 to 1.
    /// </summary>
    /// <remarks>
    /// THE ANSWER TO "CAN IT HEAR ME", given without words. It is the question
    /// everybody asks silently the moment they start talking to a machine, and
    /// a spinner cannot answer it — a spinner spins whether the microphone is
    /// working or muted or pointed at a wall. Arcs that move with your own
    /// voice answer it instantly, in a way a child and a grandparent both read
    /// without being told.
    /// <para>
    /// It is a REAL level off the microphone, not a decorative animation. That
    /// distinction is the whole value: a fake meter would be lying about the
    /// one thing the person is trying to find out.
    /// </para>
    /// </remarks>
    public void SetLevel(float level)
    {
        _level = Math.Clamp(level, 0f, 1f);
        // Speaking redraws on its own clock already, so nudging it here would
        // just queue duplicate frames.
        if (_state == MarkState.Listening) Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width, h = Height;
        float cx = w / 2f, cy = h / 2f;
        float r = Math.Min(w, h) / 2f;

        _ring.StrokeWidth = r * 0.11f;
        _arc.StrokeWidth  = r * 0.085f;

        // Chase the level rather than snapping to it. Raw frame-to-frame RMS
        // jitters hard enough to look like a fault; easing turns the same data
        // into something that reads as breathing.
        _shown += (_level - _shown) * (_level > _shown ? 0.45f : 0.12f);

        _halo.Alpha = _state switch
        {
            MarkState.Listening => (int)(28 + 46 * _shown),
            MarkState.Speaking  => (int)(40 + 34 * _shown),
            _ => 28,
        };
        canvas.DrawCircle(cx, cy, r * 0.98f, _halo);

        // The ring, open at the TOP — an 80 degree gap. NOT on the right, which
        // is what this comment used to say: -50 sweeping 280 clockwise covers
        // -50..230, leaving 230..310 open, and that straddles 270, which is 12
        // o'clock in canvas angles. The launcher icon was hand-drawn from the
        // old wording and became a half circle open on the wrong side, so the
        // geometry is spelled out here in the terms the drawing actually uses.
        var ringR = r * 0.46f;
        var ringBox = new RectF(cx - ringR, cy - ringR, cx + ringR, cy + ringR);
        canvas.DrawArc(ringBox, -50f, 280f, false, _ring);

        // THREE PHASES, THREE DIFFERENT MOTIONS — not three brightnesses.
        //
        // They used to be told apart by alpha alone, and Speaking was not
        // animated at all: a flat Alpha=255 at the idle spread, which is a lit
        // circle sitting still. So "I am hearing you", "I am thinking" and "I am
        // answering" all looked like the same glowing mark, and the one moment a
        // person most needs feedback — the long wait — was the least legible.
        //
        // Motion is what the eye reads first, so each phase gets its own:
        //
        //   LISTENING  arcs SCALE with your voice. Reactive, driven by the mic —
        //              it moves because YOU are moving it.
        //   THINKING   a bright band TRAVELS outward through the arcs, over and
        //              over. Directional and repeating: work in progress, no
        //              claim about how much is left.
        //   SPEAKING   arcs FIRE in sequence from the inside out, riding the
        //              output level. Sound leaving the phone, the mirror image
        //              of Listening pulling it in.
        for (var i = 0; i < 3; i++)
        {
            var spread = _state switch
            {
                MarkState.Listening => 0.16f + 0.16f * i + 0.07f * _shown * (i + 1),
                // Speaking breathes outward on the beat rather than with input.
                MarkState.Speaking  => 0.16f + 0.16f * i + 0.05f * SpeakPulse(i),
                _ => 0.16f + 0.16f * i,
            };
            var ar = ringR + r * spread;
            var box = new RectF(cx - ar, cy - ar, cx + ar, cy + ar);

            _arc.Alpha = _state switch
            {
                MarkState.Listening => (int)Math.Clamp(70 + 185 * _shown * (1f - 0.22f * i), 40, 255),
                MarkState.Thinking  => (int)Math.Clamp(60 + 195 * TravelBand(i), 40, 255),
                MarkState.Speaking  => (int)Math.Clamp(80 + 175 * SpeakPulse(i), 60, 255),
                _ => _busy ? 255 : i switch { 0 => 235, 1 => 150, _ => 80 },
            };
            canvas.DrawArc(box, -34f, 68f, false, _arc);
        }

        // Thinking and Speaking both animate on their own clock, so they ask for
        // the next frame. Listening is redrawn by arriving audio levels and Idle
        // is static — self-scheduling there would spin the CPU for no gain.
        if (_state is MarkState.Thinking or MarkState.Speaking)
        {
            _tick++;
            PostInvalidateOnAnimation();
        }
    }

    /// <summary>
    /// A bright band sweeping outward through the three arcs, for THINKING.
    /// </summary>
    /// <remarks>
    /// Deliberately a travelling position rather than a per-arc sine. Three arcs
    /// fading in and out on their own phases read as a shimmer; one band moving
    /// through them reads as something being carried from here to there, which is
    /// what "working on it" should look like. Returns 0..1 for how lit arc
    /// <paramref name="i"/> is right now.
    /// </remarks>
    float TravelBand(int i)
    {
        // Sweeps 0 -> 3 then wraps, so the band leaves the outer arc and
        // re-enters at the ring, over and over.
        var head = (_tick * 0.045f) % 3.2f;
        var d = Math.Abs(head - i);
        return Math.Max(0f, 1f - d);           // triangular falloff either side
    }

    /// <summary>
    /// Arcs firing outward in sequence, for SPEAKING, scaled by output loudness.
    /// </summary>
    /// <remarks>
    /// The inner arc leads and the outer follows, so the motion pushes AWAY from
    /// the mark — the opposite direction to Listening, which pulls inward as you
    /// talk. When the player reports a real output level the pulse rides it, so
    /// the mark moves with the words rather than to a metronome; with no level it
    /// still beats steadily, because a silent-looking mark during a spoken answer
    /// is the bug this replaces.
    /// </remarks>
    float SpeakPulse(int i)
    {
        var phase = _tick * 0.11f - i * 0.8f;   // inner arc leads, outer trails
        var beat  = 0.5 + 0.5 * Math.Sin(phase);
        var gain  = 0.55f + 0.45f * _shown;     // louder speech, wider swing
        return (float)(beat * gain);
    }
}
