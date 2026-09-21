using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Teezy.App;

/// <summary>The floating pill shown while the assistant is listening and acting.</summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="HudWindow"/>, not a variant of it. It carries the same load-bearing
/// rule — <b>this window must never take focus</b> — enforced the same three ways, because any
/// one of them alone has a gap: <c>ShowActivated="False"</c> covers the initial show but not
/// later ones; <c>WS_EX_NOACTIVATE</c> tells Windows never to activate on click; and
/// <c>IsHitTestVisible="False"</c> means clicks pass through. <c>WS_EX_TOOLWINDOW</c> keeps it
/// out of Alt-Tab.
/// </para>
/// <para>
/// It only ever exists between a hotkey press and its result, and dismisses itself afterwards.
/// A pill that lingers on screen waiting to be useful is the failure mode this whole feature
/// was designed away from.
/// </para>
/// </remarks>
public partial class AssistantWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Bar widths at rest — the mark exactly as it ships.</summary>
    private const double Bar1Rest = 100;
    private const double Bar2Rest = 74;

    /// <summary>The two middle bars while working: short, so the pulse reads as typing.</summary>
    private const double BarWorking = 40;

    private enum Phase { Hidden, Listening, Working, Settled }

    private readonly DispatcherTimer _frames = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16),
    };

    private readonly DispatcherTimer _dismiss = new();

    private Phase _phase = Phase.Hidden;
    private double _t;
    private double _level;

    public AssistantWindow()
    {
        InitializeComponent();

        SizeChanged += (_, _) => PositionAtBottomCenter();
        _frames.Tick += OnFrame;
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); Dismiss(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtrW(handle, GWL_EXSTYLE);
        Native.SetWindowLongPtrW(handle, GWL_EXSTYLE,
            style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ---- what the assistant tells it ----

    /// <summary>The microphone is open.</summary>
    public void ShowListening()
    {
        Appear();
        _phase = Phase.Listening;
        _t = 0;
        _dismiss.Stop();

        RestoreBars();
        Say("Listening");
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.9, Ms(260)));
    }

    /// <summary>Key released; working out what was said.</summary>
    public void ShowWorking()
    {
        if (_phase == Phase.Hidden) Appear();
        _phase = Phase.Working;
        _t = 0;

        RestoreBars();
        Say("Thinking");
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, Ms(260)));
    }

    /// <summary>It did the thing, and this is what it did.</summary>
    public void ShowDone(string message)
    {
        if (_phase == Phase.Hidden) Appear();
        _phase = Phase.Settled;

        Say(message);
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(200)));
        DrawTick();
        DismissIn(2000);
    }

    /// <summary>
    /// It heard you clearly and cannot do that.
    /// </summary>
    /// <remarks>
    /// The transcript is shown deliberately. Without it there is no way to tell a
    /// misrecognition from an unsupported command, and the two have completely different
    /// responses — say it again, versus stop asking for that.
    /// </remarks>
    public void ShowUnknown(string heard)
    {
        if (_phase == Phase.Hidden) Appear();
        _phase = Phase.Settled;

        Say("I can’t do that yet", heard);
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(200)));
        FlattenBars();
        ShakeOnce();
        DismissIn(3200);
    }

    /// <summary>
    /// It answered a question. Prose, so the pill grows to hold it.
    /// </summary>
    /// <remarks>
    /// Given longer to be read than an action confirmation, and scaled to the length: "Volume
    /// 40%" is taken in at a glance, two sentences are not. The floor is generous because the
    /// pill cannot be summoned back — once it has gone, the answer is gone with it.
    /// </remarks>
    public void ShowAnswer(string answer)
    {
        if (_phase == Phase.Hidden) Appear();
        _phase = Phase.Settled;

        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(200)));
        RestoreBars();
        Say("TeezyFlow", answer: answer);

        // Roughly a comfortable reading pace, floored and capped.
        var words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        DismissIn(Math.Clamp(2200 + words * 320, 3500, 14000));
    }

    public void ShowError(string message)
    {
        if (_phase == Phase.Hidden) Appear();
        _phase = Phase.Settled;

        Say(message);
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(200)));
        FlattenBars();
        ShakeOnce();
        DismissIn(3200);
    }

    /// <summary>Drives the meter while listening.</summary>
    public void SetLevel(float level) => _level = level;

    public void Dismiss()
    {
        if (_phase == Phase.Hidden) return;
        _phase = Phase.Hidden;
        _dismiss.Stop();
        _frames.Stop();

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Ms(220)));
        PillShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 8, Ms(240)) { EasingFunction = ease });
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0.94, Ms(240)) { EasingFunction = ease });
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 0.94, Ms(240)) { EasingFunction = ease });

        // Hidden rather than closed: this window is built once and reused, and rebuilding it
        // would repay the interop and positioning cost on every single utterance.
        var gone = new DispatcherTimer { Interval = Ms(260) };
        gone.Tick += (_, _) => { gone.Stop(); if (_phase == Phase.Hidden) Hide(); };
        gone.Start();
    }

    // ---- appearing ----

    private void Appear()
    {
        if (!IsVisible)
        {
            Pill.Opacity = 0;
            PillScale.ScaleX = PillScale.ScaleY = 0.9;
            PillShift.Y = 10;
            PositionAtBottomCenter();
            Show();
        }

        _frames.Start();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(180)));
        PillShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, Ms(320)) { EasingFunction = ease });

        // Overshoot on the scale only. Overshooting the position as well reads bouncy and
        // toy-like; overshooting neither reads as a tooltip.
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(Ms(0))));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.025, KeyTime.FromTimeSpan(Ms(220)), ease));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(Ms(340)), ease));
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
    }

    private void DismissIn(int ms)
    {
        _dismiss.Stop();
        _dismiss.Interval = Ms(ms);
        _dismiss.Start();
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // ---- text ----

    /// <summary>
    /// Sets the label and grows the pill to fit it.
    /// </summary>
    /// <remarks>
    /// The width is animated rather than left to <c>SizeToContent</c>, which snaps. Snapping is
    /// the single thing that most makes a floating panel feel like a dialog box: the content
    /// changed, so the box jumped.
    /// </remarks>
    private void Say(string status, string? heard = null, string? answer = null)
    {
        StatusText.Text = status;

        HeardText.Text = heard is { Length: > 0 } ? $"“{heard}”" : string.Empty;
        HeardText.Visibility = heard is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        AnswerText.Text = answer ?? string.Empty;
        AnswerText.Visibility = answer is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        // An answer fixes the width — it wraps to a set measure rather than stretching the pill
        // across the screen — so only the other two states are measured.
        var answerWidth = (double)FindResource("AnswerWidth");
        var content = AnswerText.Visibility == Visibility.Visible
            ? answerWidth
            : Math.Max(Measure(StatusText), MeasureHeard());

        var target = 30 + 13 + content + 19 + 21;
        var from = Pill.ActualWidth > 0 ? Pill.ActualWidth : target;

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(from, target, Ms(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        // Read from the theme rather than repeated here: the shared height is the whole reason
        // the two pills cannot drift apart.
        var resting = (double)FindResource("PillHeight");
        var wanted = resting;

        if (AnswerText.Visibility == Visibility.Visible)
        {
            // Measure the wrapped text rather than guessing a line count, so a three-line
            // answer is not clipped and a one-line answer is not padded out.
            AnswerText.Measure(new Size(answerWidth, double.PositiveInfinity));
            wanted = Math.Max(resting, 34 + AnswerText.DesiredSize.Height);
        }
        else if (HeardText.Visibility == Visibility.Visible)
        {
            wanted = resting + 12;
        }

        Pill.BeginAnimation(HeightProperty,
            new DoubleAnimation(Pill.ActualHeight > 0 ? Pill.ActualHeight : resting, wanted, Ms(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private double MeasureHeard() =>
        HeardText.Visibility == Visibility.Visible ? Math.Min(Measure(HeardText), 420) : 0;

    private double Measure(System.Windows.Controls.TextBlock block)
    {
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);

        return new FormattedText(block.Text ?? string.Empty, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, block.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
    }

    // ---- the mark ----

    private void RestoreBars()
    {
        Bar1.BeginAnimation(WidthProperty, null);
        Bar2.BeginAnimation(WidthProperty, null);
        Bar1.BeginAnimation(OpacityProperty, null);
        Bar2.BeginAnimation(OpacityProperty, null);

        BarA.BeginAnimation(OpacityProperty, null);
        BarB.BeginAnimation(OpacityProperty, null);

        Bar1.Opacity = Bar2.Opacity = BarA.Opacity = BarB.Opacity = 1;
        Bar1.Width = Bar1Rest;
        Bar2.Width = Bar2Rest;
        Tick.Opacity = 0;
    }

    private void FlattenBars()
    {
        Bar1.BeginAnimation(WidthProperty, null);
        Bar2.BeginAnimation(WidthProperty, null);
        Bar1.Opacity = Bar2.Opacity = 1;
        Bar1.Width = Bar2.Width = BarWorking;
        Tick.Opacity = 0;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        _t += _frames.Interval.TotalSeconds;

        switch (_phase)
        {
            case Phase.Listening:
                // Snap up, fall slowly — the asymmetry the HUD meter uses, and for the same
                // reason: a syllable must register at once, but the bar must settle between
                // words rather than strobe.
                var rate = _level > _lastLevel ? 0.6 : 0.15;
                _lastLevel += (_level - _lastLevel) * rate;

                var ripple = 0.5 + 0.5 * Math.Sin(_t * 6.2);
                Bar1.Width = 40 + 60 * _lastLevel * (0.75 + 0.25 * ripple);
                Bar2.Width = 30 + 44 * _lastLevel * (0.75 + 0.25 * (1 - ripple));

                Breathe.ScaleX = Breathe.ScaleY = 1 + 0.05 * _lastLevel;
                Glow.Opacity = 0.35 + 0.55 * _lastLevel;
                break;

            case Phase.Working:
                // A typing indicator, not a spinner. Spinners say "wait"; this says "working".
                Bar1.Width = Bar2.Width = BarWorking;
                Bar1.Opacity = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin(_t * 3.4));
                Bar2.Opacity = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin(_t * 3.4 + Math.PI));
                Breathe.ScaleX = Breathe.ScaleY = 1 + 0.018 * (0.5 + 0.5 * Math.Sin(_t * 3.4));
                break;

            case Phase.Settled:
                Breathe.ScaleX = Breathe.ScaleY += (1 - Breathe.ScaleX) * 0.18;
                break;
        }
    }

    private double _lastLevel;

    private void DrawTick()
    {
        var fade = new DoubleAnimation(1, 0, Ms(130));
        Bar1.BeginAnimation(OpacityProperty, fade);
        Bar2.BeginAnimation(OpacityProperty, fade);
        BarA.BeginAnimation(OpacityProperty, fade);
        BarB.BeginAnimation(OpacityProperty, fade);

        Tick.Opacity = 1;
        Tick.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(6, 0, Ms(300))
            {
                BeginTime = Ms(110),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        // One confident pop, not a bounce. A bounce reads as celebration, which is the wrong
        // amount of feeling for having changed the volume.
        var pop = new DoubleAnimation(1.0, 1.1, Ms(160))
        {
            BeginTime = Ms(110),
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
    }

    private void ShakeOnce()
    {
        var shake = new DoubleAnimationUsingKeyFrames();
        foreach (var (at, to) in new[] { (0, 0.0), (55, -6.0), (115, 6.0), (175, -3.5), (235, 0.0) })
        {
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(Ms(at))));
        }
        PillShift.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    /// <summary>
    /// Centres the pill above the taskbar, exactly where the dictation HUD sits.
    /// </summary>
    /// <remarks>
    /// The 34 subtracts the bottom margin that exists to give the shadow room, so the visible
    /// pill — not the window around it — lands 72 px up, matching <see cref="HudWindow"/>.
    /// </remarks>
    private void PositionAtBottomCenter()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - (72 - 34);
    }
}
